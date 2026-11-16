using System.Diagnostics;

namespace FileZillaUpdater;

/// <summary>FileZilla Client installé, lu dans son entrée de désinstallation.</summary>
/// <param name="Root">Branche de l'entrée : HKCU pour une installation pour l'utilisateur courant seul.</param>
public sealed record InstalledFileZilla(Version Version, string? InstallLocation, RegistryRoot Root)
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string KeyName = "FileZilla Client";
    public const string ExeName = "filezilla.exe";

    /// <summary>Installation pour tous les utilisateurs (entrée dans HKLM).</summary>
    public bool AllUsers => Root != RegistryRoot.CurrentUser;

    public string? ExePath => string.IsNullOrEmpty(InstallLocation) ? null : Path.Combine(InstallLocation, ExeName);

    /// <summary>
    /// Version la plus récente trouvée dans HKLM (vues 64 et 32 bits) et HKCU ; null si
    /// FileZilla n'est pas installé.
    /// </summary>
    public static InstalledFileZilla? Find(IRegistryStore registry)
    {
        InstalledFileZilla? found = null;
        foreach (var root in new[] { RegistryRoot.LocalMachine64, RegistryRoot.LocalMachine32, RegistryRoot.CurrentUser })
        {
            var path = $@"{UninstallPath}\{KeyName}";
            var version = VersionText.Parse(registry.GetString(root, path, "DisplayVersion"));
            if (version is not null && (found is null || version > found.Version))
                found = new InstalledFileZilla(version, registry.GetString(root, path, "InstallLocation"), root);
        }
        return found;
    }
}

/// <summary>Installeur NSIS de FileZilla, lancé en mode silencieux.</summary>
public static class NsisSetup
{
    /// <summary>Mode silencieux et portée : <c>/user=all</c> (tous les utilisateurs) ou <c>/user=current</c>.</summary>
    public static string Arguments(bool allUsers) => $"/S /user={(allUsers ? "all" : "current")}";

    /// <summary>ShellExecute : Windows demande lui-même l'élévation (UAC) si l'installeur l'exige.</summary>
    public static ProcessStartInfo Command(string installer, bool allUsers) =>
        new(installer, Arguments(allUsers))
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        };
}
