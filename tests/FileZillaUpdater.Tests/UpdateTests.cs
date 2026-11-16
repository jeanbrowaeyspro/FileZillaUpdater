using System.Net.Http;
using FileZillaUpdater.ViewModels;
using static FileZillaUpdater.Tests.Fixtures;

namespace FileZillaUpdater.Tests;

/// <summary>
/// Vérification auprès de Chocolatey, téléchargement, extraction, contrôle de signature et
/// installation silencieuse de FileZilla, sans réseau : paquet zip réel contenant un faux
/// installeur, jamais exécuté.
/// </summary>
public class UpdateTests
{
    /// <summary>FileZilla installé dans un dossier temporaire (exécutable factice).</summary>
    private static (FakeRegistry Registry, string Exe) FileZillaOnDisk(string version = "3.70.6",
                                                                        RegistryRoot root = RegistryRoot.LocalMachine32)
    {
        var folder = NewFolder();
        var exe = Path.Combine(folder, InstalledFileZilla.ExeName);
        File.WriteAllText(exe, "");
        return (new FakeRegistry().WithFileZilla(version, folder, root), exe);
    }

    private sealed record Bench(UpdateService Service, string TempDir);

    private static Bench New(FakeRegistry registry, FakeSource? source = null, FakeFileZilla? filezilla = null,
                             FakeRunner? runner = null, ISignatureVerifier? signatures = null,
                             List<TimeSpan>? waits = null)
    {
        var temp = NewFolder("temp");
        var service = new UpdateService(registry, source ?? new FakeSource(FakeSource.Latest), signatures ?? new FakeSignature(),
                                        filezilla ?? new FakeFileZilla(null), runner ?? new FakeRunner(), temp)
        {
            Wait = (delay, _) =>
            {
                waits?.Add(delay);
                return Task.CompletedTask;
            },
        };
        return new Bench(service, temp);
    }

    private static UpdateService Service(FakeRegistry registry, FakeSource? source = null, FakeFileZilla? filezilla = null,
                                         FakeRunner? runner = null, ISignatureVerifier? signatures = null,
                                         List<TimeSpan>? waits = null) =>
        New(registry, source, filezilla, runner, signatures, waits).Service;

    /// <summary>Registre mis à jour à la version publiée, comme le fait l'installeur.</summary>
    private static Action Installs(FakeRegistry registry, string exe, RegistryRoot root = RegistryRoot.LocalMachine32) =>
        () => registry.WithFileZilla("3.72.0", Path.GetDirectoryName(exe), root);

    // ------------------------------------------------------------- vérification

    [Fact]
    public void StartupRetriesLastAboutOneMinute() =>
        Assert.InRange(UpdateService.StartupRetryDelays.Sum(d => d.TotalSeconds), 50, 70);

    [Fact]
    public async Task NetworkNotReadyIsRetried()
    {
        var (registry, _) = FileZillaOnDisk();
        var waits = new List<TimeSpan>();
        var source = new FakeSource(FakeSource.Offline, FakeSource.Offline, FakeSource.Latest);
        var check = await Service(registry, source, waits: waits).CheckAsync(UpdateService.StartupRetryDelays);
        Assert.Equal(3, source.Calls);
        Assert.Equal(UpdateService.StartupRetryDelays[..2], waits);
        Assert.True(check.UpdateAvailable);
        Assert.Equal("FileZilla 3.72.0 est disponible (version installée : 3.70.6). L'installer maintenant ?", check.OfferText);
    }

    [Fact]
    public async Task NetworkAbsentGivesUpSilently()
    {
        var (registry, _) = FileZillaOnDisk();
        var waits = new List<TimeSpan>();
        var source = new FakeSource(FakeSource.Offline);
        var check = await Service(registry, source, waits: waits).CheckAsync(UpdateService.StartupRetryDelays);
        Assert.Equal(UpdateService.StartupRetryDelays.Length + 1, source.Calls);
        Assert.Equal(UpdateService.StartupRetryDelays, waits);
        Assert.False(check.UpdateAvailable);
        Assert.StartsWith("Chocolatey ne répond pas", check.Error);
    }

    [Fact]
    public async Task RateLimitIsNotRetried()
    {
        // Chocolatey limite les requêtes : une seule, jamais en boucle.
        var (registry, _) = FileZillaOnDisk();
        var waits = new List<TimeSpan>();
        var source = new FakeSource(FakeSource.TooManyRequests);
        var check = await Service(registry, source, waits: waits).CheckAsync(UpdateService.StartupRetryDelays);
        Assert.Equal(1, source.Calls);
        Assert.Empty(waits);
        Assert.Equal("Chocolatey limite le nombre de requêtes, réessayer plus tard.", check.Error);
    }

    [Fact]
    public async Task LatestVersionIsAskedOncePerSession()
    {
        var (registry, _) = FileZillaOnDisk();
        var source = new FakeSource(FakeSource.Latest);
        var service = Service(registry, source);
        await service.CheckAsync([]);
        var again = await service.CheckAsync([]);
        Assert.Equal(1, source.Calls);
        Assert.True(again.UpdateAvailable);
        // "Vérifier maintenant" : nouvelle requête.
        await service.CheckAsync([], refresh: true);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task NothingToOffer()
    {
        var (upToDate, _) = FileZillaOnDisk("3.72.0");
        var check = await Service(upToDate).CheckAsync([]);
        Assert.False(check.UpdateAvailable);
        Assert.False(check.InstallAvailable);
        Assert.Equal("", check.OfferText);

        // FileZilla absent : pas de mise à jour, mais une première installation.
        check = await Service(new FakeRegistry()).CheckAsync([]);
        Assert.Null(check.Installed);
        Assert.False(check.UpdateAvailable);
        Assert.True(check.InstallAvailable);
        Assert.Equal("FileZilla n'est pas installé. L'installer maintenant ?", check.OfferText);
    }

    // ------------------------------------------------------------- installation

    /// <summary>Proposition sur FileZilla 3.70.6 installé, 3.72.0 disponible.</summary>
    private static async Task<UpdatePromptViewModel> Prompt(UpdateService service, IDialogs? dialogs = null) =>
        new(await service.CheckAsync([]), service, dialogs ?? new FakeDialogs());

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "délai dépassé");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task UpdateDownloadsChecksTheSignatureThenRunsTheSilentInstaller()
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(exe);
        var installEnd = new TaskCompletionSource();
        var runner = new FakeRunner { During = Installs(registry, exe), Until = installEnd.Task };
        var source = new FakeSource(FakeSource.Latest);
        var signatures = new FakeSignature();
        var dialogs = new FakeDialogs();
        var bench = New(registry, source, filezilla, runner, signatures);
        var vm = await Prompt(bench.Service, dialogs);
        var closed = 0;
        vm.RequestClose += () => closed++;
        Assert.Equal(["Installer", "Plus tard"], vm.Buttons.Select(b => b.Text));

        var install = vm.InstallAsync();
        // FileZilla ouvert : fermeture demandée avant tout.
        Assert.Equal("FileZilla est ouvert. Le fermer pour l'installation ? Il sera relancé ensuite.", Assert.Single(dialogs.Shown));
        Assert.Equal(("Fermer FileZilla", "Annuler"), dialogs.Buttons[0]);
        // Fenêtre d'attente jusqu'à la fin de l'installeur, sans bouton ni fermeture possible.
        Assert.True(vm.IsWaiting);
        Assert.Equal(DialogKind.Info, vm.Kind);
        Assert.Equal("Téléchargement et installation de FileZilla 3.72.0…\n\nAutoriser les modifications de l'appareil si Windows le demande.", vm.Message);
        Assert.False(vm.CanClose);
        Assert.False(vm.InstallCommand.CanExecute(null));
        await WaitUntil(() => runner.Runs.Count == 1);
        Assert.Null(vm.Outcome);
        Assert.Equal(0, closed);

        // Paquet téléchargé depuis Chocolatey, installeur extrait puis contrôlé.
        var download = Assert.Single(source.Downloads);
        Assert.Equal("https://packages.chocolatey.org/filezilla.3.72.0.nupkg", download.Url);
        var run = Assert.Single(runner.Runs);
        Assert.Equal("FileZilla_3.72.0_win64-setup.exe", Path.GetFileName(run.FileName));
        Assert.Equal([run.FileName], signatures.Checked);
        Assert.Equal([FakeSource.FakeInstaller], runner.Installers);
        Assert.False(File.Exists(download.Destination));
        // NSIS silencieux, même portée que l'installation existante, UAC demandée par Windows.
        Assert.Equal("/S /user=all", run.Arguments);
        Assert.True(run.UseShellExecute);
        Assert.Equal(["close"], filezilla.Events);

        installEnd.SetResult();
        await install.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["close", $"start {exe}"], filezilla.Events);
        // Dossier de travail supprimé.
        Assert.Empty(Directory.GetFileSystemEntries(bench.TempDir));

        // La fenêtre devient la confirmation, OK ferme.
        Assert.True(vm.Outcome!.Success);
        Assert.False(vm.IsWaiting);
        Assert.Equal(DialogKind.Info, vm.Kind);
        Assert.Equal("FileZilla 3.72.0 est installé.", vm.Message);
        var ok = Assert.Single(vm.Buttons);
        Assert.Equal("OK", ok.Text);
        Assert.True(vm.CanClose);
        ok.Command.Execute(null);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task OpenFileZillaKeptOpenMeansNothingIsDone()
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(exe);
        var source = new FakeSource(FakeSource.Latest);
        var runner = new FakeRunner();
        var vm = await Prompt(Service(registry, source, filezilla, runner), new FakeDialogs(answer: false));
        await vm.InstallAsync();
        Assert.Null(vm.Outcome);
        Assert.False(vm.IsWaiting);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.Empty(source.Downloads);
        Assert.Empty(runner.Runs);
        Assert.Empty(filezilla.Events);
    }

    [Theory]
    [InlineData(false, "Tim Kosse", "Tim Kosse")]
    [InlineData(true, "Autre Éditeur", "Autre Éditeur")]
    [InlineData(false, null, "aucune")]
    public async Task InstallerWithoutTheExpectedSignatureIsNeverRun(bool trusted, string? signer, string shown)
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(exe);
        var runner = new FakeRunner();
        var bench = New(registry, filezilla: filezilla, runner: runner, signatures: new FakeSignature(trusted, signer));
        var vm = await Prompt(bench.Service);
        await vm.InstallAsync();
        Assert.Empty(runner.Runs);
        // FileZilla n'est pas fermé pour rien.
        Assert.Empty(filezilla.Events);
        Assert.False(vm.Outcome!.Success);
        Assert.Equal(DialogKind.Error, vm.Kind);
        Assert.Equal($"L'installeur de FileZilla n'est pas signé valablement par Tim Kosse (signataire : {shown}). Rien n'a été installé.",
                     vm.Message);
        Assert.Empty(Directory.GetFileSystemEntries(bench.TempDir));
    }

    [Fact]
    public async Task RealSignatureCheckRefusesTheFakeInstaller()
    {
        // Vrai contrôle Authenticode sur le faux installeur extrait du paquet.
        var (registry, exe) = FileZillaOnDisk();
        var runner = new FakeRunner();
        var vm = await Prompt(Service(registry, runner: runner, signatures: new AuthenticodeVerifier()));
        await vm.InstallAsync();
        Assert.Empty(runner.Runs);
        Assert.EndsWith("(signataire : aucune). Rien n'a été installé.", vm.Message);
    }

    [Theory]
    [InlineData("tools/FileZilla_3.72.0_win32-setup.exe", "Le paquet ne contient pas l'installeur 64 bits de FileZilla.")]
    [InlineData("tools/FileZilla_3.71.0_win64-setup.exe", "Le paquet contient FileZilla_3.71.0_win64-setup.exe au lieu de la version 3.72.0.")]
    public async Task UnexpectedPackageIsRefused(string entry, string error)
    {
        var (registry, _) = FileZillaOnDisk();
        var runner = new FakeRunner();
        var signatures = new FakeSignature();
        var vm = await Prompt(Service(registry, new FakeSource(FakeSource.Latest) { InstallerEntry = entry }, runner: runner,
                                      signatures: signatures));
        await vm.InstallAsync();
        Assert.Empty(signatures.Checked);
        Assert.Empty(runner.Runs);
        Assert.Equal($"La mise à jour a échoué : {error}", vm.Message);
    }

    [Fact]
    public async Task DownloadErrorIsReported()
    {
        var source = new FakeSource(FakeSource.Latest) { DownloadError = new HttpRequestException("connexion perdue") };
        var vm = await Prompt(Service(new FakeRegistry(), source));
        await vm.InstallAsync();
        Assert.Equal(DialogKind.Error, vm.Kind);
        Assert.Equal("L'installation a échoué : connexion perdue", vm.Message);
    }

    [Fact]
    public async Task UnexpectedVersionIsReported()
    {
        // Installeur terminé sans erreur, mais le registre donne toujours 3.70.6.
        var (registry, exe) = FileZillaOnDisk();
        var vm = await Prompt(Service(registry, filezilla: new FakeFileZilla(null)));
        await vm.InstallAsync();
        Assert.True(vm.Outcome!.Success);
        Assert.Equal("L'installation de FileZilla 3.72.0 est terminée, mais la version installée relue est 3.70.6.", vm.Message);
        Assert.EndsWith("relue est aucune.", UpdateService.InstalledText(new Version(3, 72, 0, 0), null));
    }

    [Fact]
    public async Task FileZillaNotRunningIsNotStarted()
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(null);
        var dialogs = new FakeDialogs();
        var vm = await Prompt(Service(registry, filezilla: filezilla, runner: new FakeRunner { During = Installs(registry, exe) }),
                              dialogs);
        await vm.InstallAsync();
        Assert.Empty(dialogs.Shown);
        Assert.Equal(["close"], filezilla.Events);
        Assert.False(filezilla.IsRunning);
    }

    [Fact]
    public async Task FileZillaRestartedByItsInstallerIsNotStartedTwice()
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(exe);
        var runner = new FakeRunner { During = () => filezilla.StartedElsewhere(exe) };
        var vm = await Prompt(Service(registry, filezilla: filezilla, runner: runner));
        await vm.InstallAsync();
        Assert.Equal(["close"], filezilla.Events);
        Assert.True(filezilla.IsRunning);
    }

    [Fact]
    public async Task InstallationForTheCurrentUserStaysForTheCurrentUser()
    {
        var (registry, exe) = FileZillaOnDisk(root: RegistryRoot.CurrentUser);
        var runner = new FakeRunner { During = Installs(registry, exe, RegistryRoot.CurrentUser) };
        var vm = await Prompt(Service(registry, runner: runner));
        await vm.InstallAsync();
        Assert.Equal("/S /user=current", Assert.Single(runner.Runs).Arguments);
        Assert.Equal("FileZilla 3.72.0 est installé.", vm.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallerFailureShowsTheCode(bool wasRunning)
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(wasRunning ? exe : null);
        var vm = await Prompt(Service(registry, filezilla: filezilla, runner: new FakeRunner(2)));
        var closed = false;
        vm.RequestClose += () => closed = true;
        await vm.InstallAsync();
        Assert.False(vm.Outcome!.Success);
        // La fenêtre d'attente devient le message d'erreur.
        Assert.False(vm.IsWaiting);
        Assert.Equal(DialogKind.Error, vm.Kind);
        Assert.Equal("La mise à jour a échoué (code 2).", vm.Message);
        Assert.Equal("OK", Assert.Single(vm.Buttons).Text);
        Assert.False(closed);
        // FileZilla relancé seulement s'il tournait avant.
        Assert.Equal(wasRunning ? ["close", $"start {exe}"] : ["close"], filezilla.Events);
    }

    [Theory]
    [InlineData(1223, true)]
    [InlineData(unchecked((int)0x800704C7), true)]
    [InlineData(1223, false)]
    public async Task UacRefusalClosesWithoutMessage(int code, bool wasRunning)
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(wasRunning ? exe : null);
        var vm = await Prompt(Service(registry, filezilla: filezilla, runner: new FakeRunner(code)));
        var closed = false;
        vm.RequestClose += () => closed = true;
        await vm.InstallAsync();
        Assert.True(vm.Outcome!.Cancelled);
        Assert.True(closed);
        Assert.Equal(wasRunning ? ["close", $"start {exe}"] : ["close"], filezilla.Events);
    }

    [Fact]
    public async Task MissingFileZillaIsInstalledForAllUsers()
    {
        var registry = new FakeRegistry();
        var exe = Path.Combine(NewFolder(), InstalledFileZilla.ExeName);
        File.WriteAllText(exe, "");
        var runner = new FakeRunner { During = Installs(registry, exe) };
        var vm = await Prompt(Service(registry, runner: runner));
        Assert.Equal("FileZilla n'est pas installé. L'installer maintenant ?", vm.Message);
        Assert.Equal(["Installer", "Non"], vm.Buttons.Select(b => b.Text));

        await vm.InstallAsync();
        Assert.Equal("/S /user=all", Assert.Single(runner.Runs).Arguments);
        Assert.Equal("FileZilla 3.72.0 est installé.", vm.Message);
    }

    [Fact]
    public async Task FailedFirstInstallIsReported()
    {
        var vm = await Prompt(Service(new FakeRegistry(), runner: new FakeRunner(2)));
        await vm.InstallAsync();
        Assert.Equal("L'installation a échoué (code 2).", vm.Message);
    }

    [Fact]
    public async Task LaterClosesWithoutInstalling()
    {
        var (registry, exe) = FileZillaOnDisk();
        var filezilla = new FakeFileZilla(exe);
        var source = new FakeSource(FakeSource.Latest);
        var runner = new FakeRunner();
        var vm = await Prompt(Service(registry, source, filezilla, runner));
        Assert.Same(vm.CloseCommand, vm.Buttons[1].Command);
        var closed = false;
        vm.RequestClose += () => closed = true;
        vm.CloseCommand.Execute(null);
        Assert.True(closed);
        Assert.Empty(source.Downloads);
        Assert.Empty(runner.Runs);
        Assert.Empty(filezilla.Events);
    }
}
