using FileZillaUpdater.ViewModels;
using static FileZillaUpdater.Tests.Fixtures;

namespace FileZillaUpdater.Tests;

/// <summary>Installation, options, désinstallation et fenêtre de réglages (modèle de vue).</summary>
public class SetupTests
{
    private sealed record Bench(AppPaths Paths, FakeRegistry Registry, Setup Setup, string Launcher);

    /// <summary>Lanceur "téléchargé", application extraite par lui dans le dossier de données locales.</summary>
    private static Bench NewBench()
    {
        var paths = NewPaths();
        var downloads = Path.Combine(paths.TempDir, "Téléchargements");
        Directory.CreateDirectory(downloads);
        var launcher = Path.Combine(downloads, AppPaths.LauncherFileName);
        File.WriteAllText(launcher, "lanceur");
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.ExtractedApp, "application");
        var registry = new FakeRegistry();
        var setup = new Setup(paths, registry, new FakeShortcuts());
        return new Bench(paths, registry, setup, launcher);
    }

    private static string? RunEntry(Bench b) =>
        b.Registry.GetString(RegistryRoot.CurrentUser, Setup.RunPath, Setup.RunValue);

    private static UpdateService Updates(FakeRegistry registry, FakeSource? source = null) =>
        new(registry, source ?? new FakeSource(FakeSource.Latest), new FakeSignature(), new FakeFileZilla(null),
            new FakeRunner(), Path.GetTempPath());

    [Fact]
    public void InstallCopiesOnlyTheLauncherAndAppliesDefaults()
    {
        var b = NewBench();
        Assert.False(b.Setup.IsInstalled);
        b.Setup.Install(b.Launcher, new AppSettings());

        Assert.True(b.Setup.IsInstalled);
        Assert.Equal([b.Paths.InstalledLauncher], Directory.GetFileSystemEntries(b.Paths.InstallDir));
        Assert.Equal("lanceur", File.ReadAllText(b.Paths.InstalledLauncher));
        Assert.Equal($"\"{b.Paths.InstalledLauncher}\" /demarrage", RunEntry(b));
        Assert.False(File.Exists(b.Paths.DesktopShortcut));
        Assert.Equal(b.Paths.InstalledLauncher, File.ReadAllText(b.Paths.StartMenuShortcut));
        Assert.True(File.Exists(b.Paths.SettingsFile));
    }

    [Fact]
    public void ApplyFollowsTheOptions()
    {
        var b = NewBench();
        b.Setup.Install(b.Launcher, new AppSettings());
        var settings = new AppSettings
        {
            RunAtStartup = false,
            DesktopShortcut = true,
            StartMenuShortcut = false,
        };
        b.Setup.Apply(settings);
        Assert.Null(RunEntry(b));
        Assert.True(File.Exists(b.Paths.DesktopShortcut));
        Assert.False(File.Exists(b.Paths.StartMenuShortcut));
        Assert.False(AppSettings.Load(b.Paths.SettingsFile).RunAtStartup);
    }

    [Fact]
    public void InstallFromInstallDirDoesNotCopyOntoItself()
    {
        var b = NewBench();
        b.Setup.Install(b.Launcher, new AppSettings());
        b.Setup.Install(b.Paths.InstalledLauncher, new AppSettings());
        Assert.Equal("lanceur", File.ReadAllText(b.Paths.InstalledLauncher));
    }

    [Fact]
    public void UninstallRemovesEverything()
    {
        var b = NewBench();
        b.Setup.Install(b.Launcher, new AppSettings { DesktopShortcut = true });
        var cleanup = b.Setup.Uninstall();

        Assert.Null(RunEntry(b));
        Assert.False(File.Exists(b.Paths.DesktopShortcut));
        Assert.False(File.Exists(b.Paths.StartMenuShortcut));
        Assert.False(File.Exists(b.Paths.SettingsFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(b.Paths.SettingsFile)));
        Assert.NotNull(cleanup);
        Assert.Equal("cmd.exe", cleanup.FileName);
        Assert.Contains($"rmdir /s /q \"{b.Paths.InstallDir}\"", cleanup.Arguments);
        Assert.Contains($"del /f /q \"{b.Paths.ExtractedApp}\"", cleanup.Arguments);
        Assert.True(cleanup.CreateNoWindow);
        Assert.False(cleanup.UseShellExecute);
    }

    [Fact]
    public void DeferredDeletionRemovesTheFolderAndTheApp()
    {
        // Vraie commande cachée, sur des emplacements temporaires.
        var paths = NewPaths();
        Directory.CreateDirectory(paths.InstallDir);
        File.WriteAllText(paths.InstalledLauncher, "");
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.ExtractedApp, "");
        File.WriteAllText(paths.LogFile, "");
        using var process = System.Diagnostics.Process.Start(Setup.DeferredDeletion(paths.InstallDir, paths.ExtractedApp))!;
        Assert.True(process.WaitForExit(30_000));
        Assert.False(Directory.Exists(paths.InstallDir));
        Assert.False(File.Exists(paths.ExtractedApp));
        Assert.True(File.Exists(paths.LogFile));
    }

    // ------------------------------------------------------------- modèle de vue

    [Fact]
    public async Task InstallWindowInstallsThenStartsTheCheck()
    {
        var b = NewBench();
        b.Registry.WithFileZilla("3.70.6", null);
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), b.Launcher, dialogs);
        var closed = false;
        vm.RequestClose += () => closed = true;
        Assert.True(vm.InstallMode);
        Assert.False(vm.ShowsSettings);
        Assert.Equal("Installer", vm.ApplyText);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.UninstallCommand.CanExecute(null));
        Assert.StartsWith("FileZillaUpdater 1.0.0", vm.Title);

        // Pas de lecture des versions ni de proposition de mise à jour pendant l'installation.
        await vm.OpenedAsync();
        Assert.Equal("…", vm.InstalledVersionText);

        vm.DesktopShortcut = true;
        vm.ApplyCommand.Execute(null);
        Assert.True(File.Exists(b.Paths.DesktopShortcut));
        Assert.Equal(["Installation terminée."], dialogs.Shown);
        Assert.True(closed);
        // Lancée par l'application après la fermeture, une fois l'instance unique libérée.
        Assert.NotNull(vm.PendingCommand);
        Assert.Equal(b.Paths.InstalledLauncher, vm.PendingCommand.FileName);
        Assert.Equal("/demarrage /installation", vm.PendingCommand.Arguments);
        Assert.False(vm.PendingCommand.UseShellExecute);
    }

    [Fact]
    public async Task RunningFromInstallDirShowsSettingsAndApplies()
    {
        var b = NewBench();
        b.Registry.WithFileZilla("3.70.6", null);
        b.Setup.Install(b.Launcher, new AppSettings());
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), AppSettings.Load(b.Paths.SettingsFile),
                                       b.Paths.InstalledLauncher, dialogs);
        Assert.False(vm.InstallMode);
        Assert.True(vm.ShowsSettings);
        Assert.Equal("Appliquer", vm.ApplyText);
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.UninstallCommand.CanExecute(null));

        await vm.OpenedAsync();
        Assert.Equal("3.70.6", vm.InstalledVersionText);

        vm.DesktopShortcut = true;
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Options appliquées.", vm.Status);
        Assert.True(File.Exists(b.Paths.DesktopShortcut));
        Assert.Null(vm.PendingCommand);
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public void WithoutLauncherInstallIsImpossible()
    {
        var b = NewBench();
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), null, new FakeDialogs());
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Contains("FileZillaUpdater.exe", vm.Status);
    }

    [Fact]
    public void UninstallAsksAndPreparesCleanup()
    {
        var b = NewBench();
        b.Setup.Install(b.Launcher, new AppSettings());
        var closed = false;

        var refused = new FakeDialogs(answer: false);
        var refuse = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), b.Paths.InstalledLauncher,
                                           refused);
        refuse.UninstallCommand.Execute(null);
        Assert.Equal(("Désinstaller", "Annuler"), Assert.Single(refused.Buttons));
        Assert.Null(refuse.PendingCommand);
        Assert.NotNull(RunEntry(b));

        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), b.Paths.InstalledLauncher,
                                       dialogs);
        vm.RequestClose += () => closed = true;
        vm.UninstallCommand.Execute(null);
        Assert.Equal("cmd.exe", vm.PendingCommand?.FileName);
        Assert.Null(RunEntry(b));
        Assert.True(closed);
        Assert.Contains("FileZillaUpdater est désinstallé.", dialogs.Shown);
    }

    [Fact]
    public void FileErrorIsShownNotThrown()
    {
        var b = NewBench();
        var dialogs = new FakeDialogs();
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), b.Launcher, dialogs);
        // Dossier d'installation occupé par un fichier : la copie échoue.
        Directory.CreateDirectory(Path.GetDirectoryName(b.Paths.InstallDir)!);
        File.WriteAllText(b.Paths.InstallDir, "");
        vm.ApplyCommand.Execute(null);
        Assert.Single(dialogs.Shown);
        Assert.StartsWith("Opération impossible", dialogs.Shown[0]);
        Assert.True(vm.InstallMode);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckNowOffersTheUpdate()
    {
        var b = NewBench();
        b.Registry.WithFileZilla("3.70.6", @"C:\Program Files\FileZilla FTP Client");
        b.Setup.Install(b.Launcher, new AppSettings());
        var vm = new SettingsViewModel(b.Setup, Updates(b.Registry), new AppSettings(), b.Paths.InstalledLauncher,
                                       new FakeDialogs());
        UpdateCheck? offered = null;
        vm.UpdateOffered += check => offered = check;

        await vm.CheckAsync(offer: false);
        Assert.Equal("3.70.6", vm.InstalledVersionText);
        Assert.Equal("3.72.0", vm.LatestVersionText);
        Assert.Equal("FileZilla 3.72.0 est disponible.", vm.Status);
        Assert.Null(offered);

        vm.CheckCommand.Execute(null);
        await Task.Yield();
        Assert.NotNull(offered);
        Assert.True(offered.UpdateAvailable);
    }

    [Fact]
    public async Task CheckNowReportsState()
    {
        var upToDate = NewBench();
        upToDate.Registry.WithFileZilla("3.72.0", null);
        var vm = new SettingsViewModel(upToDate.Setup, Updates(upToDate.Registry), new AppSettings(), null, new FakeDialogs());
        await vm.CheckAsync(offer: true);
        Assert.Equal("FileZilla est à jour.", vm.Status);

        var missing = NewBench();
        vm = new SettingsViewModel(missing.Setup, Updates(missing.Registry), new AppSettings(), null, new FakeDialogs());
        UpdateCheck? offered = null;
        vm.UpdateOffered += check => offered = check;
        await vm.CheckAsync(offer: true);
        Assert.Equal("non installé", vm.InstalledVersionText);
        Assert.Equal("FileZilla n'est pas installé.", vm.Status);
        // FileZilla absent : sa première installation est proposée.
        Assert.True(offered?.InstallAvailable);

        var offline = NewBench();
        vm = new SettingsViewModel(offline.Setup, Updates(offline.Registry, new FakeSource(FakeSource.Offline)),
                                   new AppSettings(), null, new FakeDialogs());
        await vm.CheckAsync(offer: true);
        Assert.Equal("inconnue", vm.LatestVersionText);
        Assert.StartsWith("Chocolatey ne répond pas", vm.Status);
    }
}
