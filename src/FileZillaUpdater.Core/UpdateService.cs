namespace FileZillaUpdater;

/// <summary>Résultat d'une vérification : version installée et dernière version publiée.</summary>
/// <param name="Error">Message si Chocolatey n'a pas répondu.</param>
public sealed record UpdateCheck(InstalledFileZilla? Installed, FileZillaRelease? Latest, string? Error)
{
    public bool UpdateAvailable => Installed is not null && Latest is not null && Latest.Version > Installed.Version;

    /// <summary>FileZilla absent et une version publiée : première installation possible.</summary>
    public bool InstallAvailable => Installed is null && Latest is not null;

    /// <summary>Question posée quand une mise à jour ou une première installation est possible.</summary>
    public string OfferText => this switch
    {
        { UpdateAvailable: true } => L.T("FileZilla {0} is available (installed version: {1}). Install now?",
                                         VersionText.Display(Latest!.Version), VersionText.Display(Installed!.Version)),
        { InstallAvailable: true } => L.T("FileZilla is not installed. Install it now?"),
        _ => "",
    };
}

/// <summary>Issue d'une installation.</summary>
/// <param name="Cancelled">Élévation (UAC) refusée : rien à afficher.</param>
/// <param name="Message">Confirmation (succès) ou erreur, affichée sauf en cas d'annulation.</param>
public sealed record UpdateOutcome(bool Success, bool Cancelled, string Message);

/// <summary>Vérification et installation de FileZilla depuis le paquet Chocolatey.</summary>
public sealed class UpdateService(
    IRegistryStore registry,
    IFileZillaSource source,
    ISignatureVerifier signatures,
    IFileZillaProcess filezilla,
    IProcessRunner runner,
    string tempDir)
{
    /// <summary>
    /// Attentes entre les tentatives au démarrage de session (environ une minute en
    /// tout), seulement si le réseau n'est pas encore prêt : la requête n'a alors pas
    /// atteint Chocolatey.
    /// </summary>
    public static readonly TimeSpan[] StartupRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Attente entre deux tentatives ; remplacée dans les tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    /// <summary>Réponse de Chocolatey gardée pour la durée du programme : une seule requête par lancement.</summary>
    private FileZillaRelease? _latest;

    /// <summary>
    /// Lit la version installée et la dernière version publiée. Chocolatey limite le
    /// nombre de requêtes : sa réponse est réutilisée, sauf avec <paramref name="refresh"/>
    /// (vérification demandée par l'utilisateur). Nouvel essai après chacune des attentes
    /// de <paramref name="retryDelays"/> seulement si le réseau ne répond pas.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(IReadOnlyList<TimeSpan> retryDelays, bool refresh = false,
                                              CancellationToken cancel = default)
    {
        var installed = InstalledFileZilla.Find(registry);
        if (_latest is not null && !refresh)
            return new UpdateCheck(installed, _latest, null);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _latest = await source.GetLatestAsync(cancel);
                Log.Info($"FileZilla installé : {Describe(installed?.Version)}, dernière version Chocolatey : {Describe(_latest.Version)}");
                return new UpdateCheck(installed, _latest, null);
            }
            catch (Exception exc) when (IsNetworkDown(exc) && !cancel.IsCancellationRequested)
            {
                Log.Error($"Chocolatey, tentative {attempt + 1} : {exc.Message}");
                if (attempt >= retryDelays.Count)
                    return new UpdateCheck(installed, null, L.T("Chocolatey does not answer: {0}", exc.Message));
                await Wait(retryDelays[attempt], cancel);
            }
            catch (Exception exc) when (exc is HttpRequestException or InvalidDataException)
            {
                Log.Error($"Chocolatey : {exc.Message}");
                return new UpdateCheck(installed, null, exc.Message);
            }
        }
    }

    /// <summary>Réseau absent ou délai dépassé : la requête n'a pas reçu de réponse HTTP.</summary>
    private static bool IsNetworkDown(Exception exc) =>
        exc is HttpRequestException { StatusCode: null } or TaskCanceledException;

    private static string Describe(Version? version) => version is null ? L.T("none") : VersionText.Display(version);

    public bool FileZillaRunning => filezilla.IsRunning;

    /// <summary>Code Windows ERROR_CANCELLED : élévation (UAC) refusée.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>Codes de sortie d'une annulation : ERROR_CANCELLED seul ou en HRESULT.</summary>
    public static readonly int[] CancelCodes = [ErrorCancelled, unchecked((int)0x800704C7)];

    /// <summary>Code de sortie lisible : décimal, ou hexadécimal pour un HRESULT.</summary>
    public static string ExitCodeText(int code) => code is >= 0 and < 0x10000 ? code.ToString() : $"0x{code:X8}";

    /// <summary>
    /// Télécharge le paquet Chocolatey, en extrait l'installeur 64 bits, contrôle sa
    /// signature (valide, Tim Kosse) puis l'exécute en mode silencieux, avec la portée de
    /// l'installation en place (tous les utilisateurs pour une première installation).
    /// Signature refusée : rien n'est installé. Les fichiers téléchargés sont supprimés.
    /// </summary>
    public async Task<UpdateOutcome> InstallAsync(FileZillaRelease release, CancellationToken cancel = default)
    {
        var installed = InstalledFileZilla.Find(registry);
        var failed = installed is null ? L.T("The installation failed") : L.T("The update failed");
        var work = Path.Combine(tempDir, $"{AppPaths.AppName}_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(work);
            var package = Path.Combine(work, release.PackageFileName);
            Log.Info($"Téléchargement de {release.PackageUrl}");
            await source.DownloadAsync(release, package, cancel);
            var installer = FileZillaPackage.ExtractInstaller(package, release.Version, work);
            File.Delete(package);

            var signature = signatures.Verify(installer);
            Log.Info($"Signature de {Path.GetFileName(installer)} : {signature}");
            if (!signature.Accepted)
                return new UpdateOutcome(false, false, L.T(
                    "The FileZilla installer is not validly signed by {0} (signer: {1}). Nothing was installed.",
                    SignatureCheck.ExpectedSigner, signature.Signer ?? L.T("none")));

            return await RunInstallerAsync(release.Version, failed, NsisSetup.Command(installer, installed?.AllUsers ?? true), cancel);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException or InvalidDataException
                                           or UnauthorizedAccessException
                                           or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            Log.Error($"Préparation de l'installation : {exc}");
            return new UpdateOutcome(false, false, L.T("{0}: {1}", failed, exc.Message));
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Suppression impossible de {folder} : {exc.Message}");
        }
    }

    /// <summary>
    /// Ferme FileZilla, exécute l'installeur et attend sa fin. Succès : relit la version
    /// installée et la compare à <paramref name="expected"/>. FileZilla est relancé s'il
    /// tournait avant (et pas déjà relancé). Le résultat est noté dans le journal.
    /// </summary>
    private async Task<UpdateOutcome> RunInstallerAsync(Version expected, string failed,
                                                         System.Diagnostics.ProcessStartInfo command,
                                                         CancellationToken cancel)
    {
        var closedPath = filezilla.Close();
        UpdateOutcome outcome;
        try
        {
            Log.Info($"Lancement de {command.FileName} {command.Arguments}");
            var code = await runner.RunAsync(command, cancel);
            var text = ExitCodeText(code);
            outcome = code == 0
                ? new UpdateOutcome(true, false, InstalledText(expected, InstalledFileZilla.Find(registry)?.Version))
                : new UpdateOutcome(false, CancelCodes.Contains(code), L.T("{0} (code {1}).", failed, text));
            Log.Info($"Installation terminée, code {text} : {(outcome.Cancelled ? "annulée par l'utilisateur" : outcome.Message)}");
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            Log.Error($"Installation : {exc}");
            var cancelled = exc is System.ComponentModel.Win32Exception { NativeErrorCode: ErrorCancelled };
            outcome = new UpdateOutcome(false, cancelled, L.T("{0}: {1}", failed, exc.Message));
        }
        Restart(closedPath);
        return outcome;
    }

    /// <summary>Confirmation de fin, avec l'écart si la version relue n'est pas celle attendue.</summary>
    public static string InstalledText(Version expected, Version? installed) => installed == expected
        ? L.T("FileZilla {0} is installed.", VersionText.Display(expected))
        : L.T("The installation of FileZilla {0} has finished, but the installed version read back is {1}.",
              VersionText.Display(expected), Describe(installed));

    /// <summary>
    /// Relance FileZilla s'il tournait avant l'installation, depuis son dossier
    /// d'installation (relu dans le registre), sinon depuis son chemin avant fermeture.
    /// </summary>
    private void Restart(string? closedPath)
    {
        if (closedPath is null)
            return;
        var exe = InstalledFileZilla.Find(registry)?.ExePath is { } path && File.Exists(path) ? path : closedPath;
        if (filezilla.IsRunning)
            Log.Info("FileZilla tourne déjà : pas de relance");
        else if (File.Exists(exe))
            filezilla.Start(exe);
    }
}
