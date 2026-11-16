using System.Diagnostics;

namespace FileZillaUpdater.ViewModels;

/// <summary>Élément d'une liste de choix : valeur mémorisée et texte affiché.</summary>
public sealed record Choice(string Key, string Label);

/// <summary>
/// Fenêtre de réglages (lancement manuel). Hors du dossier d'installation : options
/// d'installation et bouton "Installer" seulement. Une fois installé : application des
/// options, versions de FileZilla, vérification, désinstallation.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Setup _setup;
    private readonly UpdateService _updates;
    private readonly AppSettings _settings;
    private readonly string? _launcherPath;
    private readonly IDialogs _dialogs;
    private string _appliedTheme;
    private string _appliedLanguage;
    private bool _isBusy;
    private string _installedVersionText = "…";
    private string _latestVersionText = "…";
    private string _status = "";

    /// <param name="launcherPath">Lanceur transmis par <c>/lanceur</c> : fichier copié à l'installation.</param>
    public SettingsViewModel(Setup setup, UpdateService updates, AppSettings settings,
                             string? launcherPath, IDialogs dialogs)
    {
        _setup = setup;
        _updates = updates;
        _settings = settings;
        _launcherPath = launcherPath;
        _dialogs = dialogs;
        _appliedTheme = settings.Theme;
        _appliedLanguage = settings.Language;
        InstallMode = !setup.Paths.IsInInstallDir(launcherPath);
        ApplyCommand = new RelayCommand(Apply, () => !IsBusy && (!InstallMode || CanInstall));
        CheckCommand = new RelayCommand(() => _ = CheckAsync(offer: true), () => !IsBusy && !InstallMode);
        UninstallCommand = new RelayCommand(Uninstall, () => !IsBusy && !InstallMode && _setup.IsInstalled);
        if (InstallMode && !CanInstall)
            Status = L.T("Run FileZillaUpdater.exe to be able to install it.");
    }

    /// <summary>Une mise à jour de FileZilla est disponible après "Vérifier maintenant".</summary>
    public event Action<UpdateCheck>? UpdateOffered;

    /// <summary>Thème modifié par "Appliquer" : à appliquer tout de suite.</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>Langue modifiée par "Appliquer" : la fenêtre est rouverte dans la nouvelle langue.</summary>
    public event Action? LanguageChanged;

    public event Action? RequestClose;

    public string Title => AppInfo.Title;

    // ------------------------------------------------------------- options

    public bool RunAtStartup
    {
        get => _settings.RunAtStartup;
        set { _settings.RunAtStartup = value; OnPropertyChanged(); }
    }

    public bool DesktopShortcut
    {
        get => _settings.DesktopShortcut;
        set { _settings.DesktopShortcut = value; OnPropertyChanged(); }
    }

    public bool StartMenuShortcut
    {
        get => _settings.StartMenuShortcut;
        set { _settings.StartMenuShortcut = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<Choice> Themes { get; } =
        [new("system", L.T("Windows theme")), new("light", L.T("Light")), new("dark", L.T("Dark"))];

    public string Theme
    {
        get => _settings.Theme;
        set { _settings.Theme = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<Choice> Languages { get; } =
        [new(L.System, L.T("Windows language")), new(L.French, "Français"), new(L.English, "English")];

    public string Language
    {
        get => _settings.Language;
        set { _settings.Language = value; OnPropertyChanged(); }
    }

    // ------------------------------------------------------------- état

    /// <summary>Vrai si le lanceur n'est pas celui du dossier d'installation : parcours d'installation.</summary>
    public bool InstallMode { get; }

    /// <summary>Versions de FileZilla, vérification et désinstallation : seulement une fois installé.</summary>
    public bool ShowsSettings => !InstallMode;

    public string ApplyText => InstallMode ? L.T("Install") : L.T("Apply");

    private bool CanInstall => _launcherPath is not null && File.Exists(_launcherPath);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            ApplyCommand.Refresh();
            CheckCommand.Refresh();
            UninstallCommand.Refresh();
        }
    }

    public string InstalledVersionText
    {
        get => _installedVersionText;
        private set => Set(ref _installedVersionText, value);
    }

    public string LatestVersionText
    {
        get => _latestVersionText;
        private set => Set(ref _latestVersionText, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>
    /// Commande à lancer à la fermeture : première vérification par le lanceur installé
    /// après l'installation, suppression différée après la désinstallation.
    /// </summary>
    public ProcessStartInfo? PendingCommand { get; private set; }

    public RelayCommand ApplyCommand { get; }

    public RelayCommand CheckCommand { get; }

    public RelayCommand UninstallCommand { get; }

    // ------------------------------------------------------------- actions

    private void Apply()
    {
        Run(() =>
        {
            if (InstallMode)
            {
                _setup.Install(_launcherPath!, _settings);
                ApplyAppearance();
                _dialogs.Info(L.T("Installation finished."));
                PendingCommand = _setup.StartupCheck;
                RequestClose?.Invoke();
                return;
            }
            _setup.Apply(_settings);
            Status = L.T("Options applied.");
            ApplyAppearance();
        });
    }

    /// <summary>Signale le thème et la langue modifiés depuis la dernière application.</summary>
    private void ApplyAppearance()
    {
        if (_settings.Theme != _appliedTheme)
        {
            _appliedTheme = _settings.Theme;
            ThemeChanged?.Invoke(_appliedTheme);
        }
        if (_settings.Language != _appliedLanguage)
        {
            _appliedLanguage = _settings.Language;
            L.Use(_appliedLanguage);
            LanguageChanged?.Invoke();
        }
    }

    private void Uninstall()
    {
        if (!_dialogs.Confirm(L.T("Uninstall FileZillaUpdater?\n\nStarting with Windows and the shortcuts will be removed and the installation folder deleted. FileZilla stays installed."),
                              L.T("Uninstall"), L.T("Cancel")))
            return;
        Run(() =>
        {
            PendingCommand = _setup.Uninstall();
            _dialogs.Info(L.T("FileZillaUpdater is uninstalled."));
            RequestClose?.Invoke();
        });
    }

    /// <summary>Action sur fichiers ou registre : une erreur est affichée, pas propagée.</summary>
    private void Run(Action action)
    {
        IsBusy = true;
        try
        {
            action();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                           or System.Runtime.InteropServices.COMException)
        {
            Log.Error(exc.ToString());
            _dialogs.Error(L.T("Operation not possible:\n\n{0}", exc.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>À l'ouverture de la fenêtre : lecture des versions, seulement une fois installé.</summary>
    public Task OpenedAsync() => InstallMode ? Task.CompletedTask : CheckAsync(offer: false);

    /// <summary>
    /// Lit les versions (une seule tentative). Avec <paramref name="offer"/> (bouton
    /// "Vérifier maintenant") : nouvelle requête à Chocolatey, puis proposition de mise à
    /// jour si elle existe ; sinon la réponse déjà reçue pendant ce lancement est réutilisée.
    /// </summary>
    public async Task CheckAsync(bool offer)
    {
        IsBusy = true;
        LatestVersionText = L.T("Checking…");
        var check = await _updates.CheckAsync([], refresh: offer);
        IsBusy = false;
        InstalledVersionText = check.Installed is null ? L.T("not installed") : VersionText.Display(check.Installed.Version);
        LatestVersionText = check.Latest is null ? L.T("unknown") : VersionText.Display(check.Latest.Version);
        Status = check switch
        {
            { Error: not null } => check.Error,
            { Installed: null } => L.T("FileZilla is not installed."),
            { UpdateAvailable: true } => L.T("FileZilla {0} is available.", LatestVersionText),
            _ => L.T("FileZilla is up to date."),
        };
        if (offer && (check.UpdateAvailable || check.InstallAvailable))
            UpdateOffered?.Invoke(check);
    }
}
