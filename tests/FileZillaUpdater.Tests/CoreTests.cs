using System.Text;
using System.Text.RegularExpressions;
using static FileZillaUpdater.Tests.Fixtures;

namespace FileZillaUpdater.Tests;

/// <summary>Versions, Chocolatey, paquet, signature, registre, arguments, réglages, journal, traductions.</summary>
public class CoreTests
{
    [Theory]
    [InlineData("3.72.0", "3.72.0.0")]
    [InlineData("v1.2.0", "1.2.0.0")]
    [InlineData("FileZilla 3.70.6", "3.70.6.0")]
    [InlineData("3.69.1-beta", "3.69.1.0")]
    public void ParsesVersions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), VersionText.Parse(text));

    [Fact]
    public void ComparesVersionsWrittenDifferently()
    {
        Assert.Equal(VersionText.Parse("3.72"), VersionText.Parse("3.72.0.0"));
        Assert.True(VersionText.Parse("3.72.0") > VersionText.Parse("3.70.6"));
        Assert.True(VersionText.Parse("3.9.0") < VersionText.Parse("3.70.6"));
        Assert.Null(VersionText.Parse("aucune"));
        Assert.Equal("3.72.0", VersionText.Display(VersionText.Parse("3.72")!));
    }

    // ------------------------------------------------------------- Chocolatey

    [Fact]
    public void VersionIsReadInTheRedirect()
    {
        var release = ChocolateySource.ParseRedirect(new Uri("https://packages.chocolatey.org/filezilla.3.72.0.nupkg"));
        Assert.Equal(new Version(3, 72, 0, 0), release.Version);
        Assert.Equal("filezilla.3.72.0.nupkg", release.PackageFileName);
        // Adresse relative : rapportée à community.chocolatey.org.
        release = ChocolateySource.ParseRedirect(new Uri("/api/v2/package/filezilla.3.71.1.nupkg", UriKind.Relative));
        Assert.Equal("https://community.chocolatey.org/api/v2/package/filezilla.3.71.1.nupkg", release.PackageUrl);
    }

    [Theory]
    [InlineData("https://download.filezilla-project.org/client/filezilla.3.72.0.nupkg")]
    [InlineData("http://packages.chocolatey.org/filezilla.3.72.0.nupkg")]
    [InlineData("https://packages.chocolatey.org.exemple.com/filezilla.3.72.0.nupkg")]
    [InlineData("https://packages.chocolatey.org/filezilla.server.1.9.0.nupkg")]
    [InlineData("https://packages.chocolatey.org/filezilla.nupkg")]
    public void OnlyAChocolateyPackageIsAccepted(string address)
    {
        var error = Assert.Throws<InvalidDataException>(() => ChocolateySource.ParseRedirect(new Uri(address)));
        Assert.StartsWith("Adresse de paquet inattendue", error.Message);
    }

    [Fact]
    public void MissingRedirectIsReported() =>
        Assert.Equal("Chocolatey n'a pas donné l'adresse du paquet.",
                     Assert.Throws<InvalidDataException>(() => ChocolateySource.ParseRedirect(null)).Message);

    [Fact]
    public void NoAddressOfTheFileZillaSiteIsUsed()
    {
        // Le site de FileZilla refuse les téléchargements automatiques : jamais appelé.
        var sources = Directory.GetFiles(Path.Combine(SourceRoot, "src"), "*.cs", SearchOption.AllDirectories)
                               .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        foreach (var file in sources)
            Assert.DoesNotMatch(@"(?i)https?://[^\s""]*filezilla-project\.org", File.ReadAllText(file));
        Assert.StartsWith("https://community.chocolatey.org/", ChocolateySource.LatestUrl);
    }

    // ------------------------------------------------------------- paquet et signature

    [Fact]
    public void InstallerIsExtractedFromThePackage()
    {
        var folder = NewFolder();
        var package = Path.Combine(folder, "filezilla.3.72.0.nupkg");
        FakeSource.WritePackage(package, "tools/FileZilla_3.72.0_win64-setup.exe");
        var installer = FileZillaPackage.ExtractInstaller(package, new Version(3, 72, 0, 0), folder);
        Assert.Equal(Path.Combine(folder, "FileZilla_3.72.0_win64-setup.exe"), installer);
        Assert.Equal(FakeSource.FakeInstaller, File.ReadAllText(installer));
    }

    [Fact]
    public void UnsignedFileIsRefused()
    {
        var file = Path.Combine(NewFolder(), "FileZilla_3.72.0_win64-setup.exe");
        File.WriteAllText(file, FakeSource.FakeInstaller);
        var check = new AuthenticodeVerifier().Verify(file);
        Assert.False(check.Trusted);
        Assert.Null(check.Signer);
        Assert.False(check.Accepted);
    }

    [Fact]
    public void SignatureMustBeValidAndFromTimKosse()
    {
        Assert.True(new SignatureCheck(true, "Tim Kosse", "AB", null).Accepted);
        Assert.False(new SignatureCheck(false, "Tim Kosse", "AB", "0x800B0101").Accepted);
        Assert.False(new SignatureCheck(true, "Tim Kosse Ltd", "AB", null).Accepted);
        Assert.False(new SignatureCheck(true, null, null, null).Accepted);
    }

    /// <summary>
    /// Vrai paquet Chocolatey, s'il a été téléchargé à part (variable FILEZILLA_NUPKG) :
    /// extraction et signature réelles, installeur jamais lancé.
    /// </summary>
    [Fact]
    public void RealPackageGivesAnInstallerSignedByTimKosse()
    {
        var package = Environment.GetEnvironmentVariable("FILEZILLA_NUPKG");
        if (string.IsNullOrEmpty(package) || !File.Exists(package))
            return;
        var version = VersionText.Parse(Path.GetFileNameWithoutExtension(package))!;
        var installer = FileZillaPackage.ExtractInstaller(package, version, NewFolder());
        var check = new AuthenticodeVerifier().Verify(installer);
        Assert.True(check.Trusted, check.ToString());
        Assert.Equal("Tim Kosse", check.Signer);
        Assert.True(check.Accepted);
    }

    // ------------------------------------------------------------- registre

    [Fact]
    public void FindsInstalledFileZilla()
    {
        Assert.Null(InstalledFileZilla.Find(new FakeRegistry()));

        var found = InstalledFileZilla.Find(new FakeRegistry().WithFileZilla("3.70.6", @"C:\Program Files\FileZilla FTP Client"))!;
        Assert.Equal(new Version(3, 70, 6, 0), found.Version);
        Assert.True(found.AllUsers);
        Assert.Equal(@"C:\Program Files\FileZilla FTP Client\filezilla.exe", found.ExePath);

        // Plusieurs installations : la plus récente.
        var registry = new FakeRegistry()
            .WithFileZilla("3.69.0", @"C:\Program Files\FileZilla FTP Client", RegistryRoot.LocalMachine64)
            .WithFileZilla("3.71.0", @"C:\Users\x\AppData\Local\Programs\FileZilla", RegistryRoot.CurrentUser);
        found = InstalledFileZilla.Find(registry)!;
        Assert.Equal(RegistryRoot.CurrentUser, found.Root);
        Assert.False(found.AllUsers);

        // Sans dossier : pas d'exécutable connu.
        Assert.Null(InstalledFileZilla.Find(new FakeRegistry().WithFileZilla("3.70.6", null))!.ExePath);
    }

    [Fact]
    public void BuildsSilentInstallerCommand()
    {
        var command = NsisSetup.Command(@"C:\Temp\w\FileZilla_3.72.0_win64-setup.exe", allUsers: true);
        Assert.Equal("/S /user=all", command.Arguments);
        Assert.Equal(@"C:\Temp\w", command.WorkingDirectory);
        Assert.True(command.UseShellExecute);
        Assert.Equal("/S /user=current", NsisSetup.Arguments(false));
    }

    // ------------------------------------------------------------- divers

    [Theory]
    [InlineData(new string[0], false, null)]
    [InlineData(new[] { "/demarrage" }, true, null)]
    [InlineData(new[] { "/lanceur", @"C:\Téléchargements\FileZillaUpdater.exe" }, false, @"C:\Téléchargements\FileZillaUpdater.exe")]
    [InlineData(new[] { "/LANCEUR", @"C:\x.exe", "/Demarrage" }, true, @"C:\x.exe")]
    [InlineData(new[] { "/lanceur" }, false, null)]
    public void ParsesArguments(string[] args, bool startup, string? launcher) =>
        Assert.Equal(new AppArguments(startup, launcher), AppArguments.Parse(args));

    [Fact]
    public void ParsesAfterInstall() =>
        Assert.Equal(new AppArguments(true, @"C:\x.exe", true),
                     AppArguments.Parse(["/lanceur", @"C:\x.exe", "/demarrage", "/installation"]));

    [Fact]
    public void SettingsRoundTripInSnakeCase()
    {
        var paths = NewPaths();
        var defaults = AppSettings.Load(paths.SettingsFile);
        Assert.True(defaults.RunAtStartup);
        Assert.False(defaults.DesktopShortcut);
        Assert.True(defaults.StartMenuShortcut);
        Assert.Equal("system", defaults.Theme);
        Assert.Equal("system", defaults.Language);

        new AppSettings { RunAtStartup = false, DesktopShortcut = true, Theme = "dark", Language = "en" }.Save(paths.SettingsFile);
        var json = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"run_at_startup\": false", json);
        Assert.Contains("\"desktop_shortcut\": true", json);
        Assert.Contains("\"start_menu_shortcut\": true", json);
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.Contains("\"language\": \"en\"", json);
        var loaded = AppSettings.Load(paths.SettingsFile);
        Assert.False(loaded.RunAtStartup);
        Assert.True(loaded.DesktopShortcut);
        Assert.Equal("dark", loaded.Theme);
        // Le lanceur lit la langue sans désérialiser les réglages.
        Assert.Equal("en", L.LanguageIn(paths.SettingsFile));

        File.WriteAllText(paths.SettingsFile, "{ illisible");
        Assert.True(AppSettings.Load(paths.SettingsFile).RunAtStartup);
        Assert.Null(L.LanguageIn(paths.SettingsFile));
    }

    [Fact]
    public void LanguageCanBeForced()
    {
        try
        {
            L.Use(L.English);
            Assert.Equal("FileZilla is up to date.", L.T("FileZilla is up to date."));
            Assert.Equal("The update failed: x", L.T("{0}: {1}", L.T("The update failed"), "x"));
            L.Use(L.French);
            Assert.Equal("FileZilla est à jour.", L.T("FileZilla is up to date."));
            Assert.Equal("La mise à jour a échoué : x", L.T("{0}: {1}", L.T("The update failed"), "x"));
        }
        finally
        {
            L.Use(L.French);
        }
    }

    /// <summary>Dossier racine des sources (au-dessus de tests).</summary>
    private static string SourceRoot
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "FileZillaUpdater.slnx")))
                folder = folder.Parent;
            return folder?.FullName ?? throw new DirectoryNotFoundException("FileZillaUpdater.slnx");
        }
    }

    [Fact]
    public void EveryTextHasAFrenchTranslation()
    {
        var keys = new HashSet<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(SourceRoot, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var text = File.ReadAllText(file);
            if (file.EndsWith(".cs"))
                foreach (Match m in Regex.Matches(text, @"L\.T\(\s*""((?:\\.|[^""\\])*)"""))
                    keys.Add(Regex.Unescape(m.Groups[1].Value));
            else if (file.EndsWith(".xaml"))
                foreach (Match m in Regex.Matches(text, @"local:Tr '([^']*)'"))
                    keys.Add(m.Groups[1].Value);
        }
        Assert.True(keys.Count > 50, $"{keys.Count} textes trouvés");
        var missing = keys.Where(k => !FrenchCatalog.Texts.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Sans traduction :\n" + string.Join("\n", missing));
        var unused = FrenchCatalog.Texts.Keys.Where(k => !keys.Contains(k)).ToList();
        Assert.True(unused.Count == 0, "Traductions inutilisées :\n" + string.Join("\n", unused));
    }

    [Fact]
    public void InstallDirIsRecognised()
    {
        var paths = NewPaths();
        Assert.True(paths.IsInInstallDir(paths.InstalledLauncher));
        Assert.True(paths.IsInInstallDir(paths.InstalledLauncher.ToUpperInvariant()));
        Assert.False(paths.IsInInstallDir(paths.ExtractedApp));
        Assert.False(paths.IsInInstallDir(null));
    }

    [Fact]
    public void LogRotates()
    {
        var path = Path.Combine(NewFolder(), "journal.log");
        Log.Configure(path);
        try
        {
            File.WriteAllText(path, new string('x', (int)Log.MaxBytes + 1));
            Log.Info("après rotation");
            Assert.True(File.Exists(Path.ChangeExtension(path, ".old.log")));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} INFO après rotation", File.ReadAllText(path));
        }
        finally
        {
            Log.Configure("");
        }
    }

    [Fact]
    public void WritesRealShortcut()
    {
        // Vrai raccourci, écrit dans un dossier temporaire uniquement.
        var folder = NewFolder();
        var target = Path.Combine(folder, AppPaths.LauncherFileName);
        File.WriteAllText(target, "");
        var link = Path.Combine(folder, "menu", AppPaths.ShortcutFileName);
        new ShellLinkWriter().Create(link, target, Setup.ShortcutDescription);
        var bytes = File.ReadAllBytes(link);
        Assert.Equal(0x4C, bytes[0]);   // en-tête ShellLink
        Assert.Contains(AppPaths.LauncherFileName, Encoding.Unicode.GetString(bytes) + Encoding.Default.GetString(bytes));
    }
}
