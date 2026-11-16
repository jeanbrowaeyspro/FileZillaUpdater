namespace FileZillaUpdater.ViewModels;

/// <summary>
/// Boîte "FileZilla x est disponible" : Installer / Plus tard. Si FileZilla est ouvert,
/// l'utilisateur accepte d'abord sa fermeture. Dès que l'installation est lancée, la
/// boîte devient une fenêtre d'attente (téléchargement, contrôle de signature,
/// installation silencieuse), puis un message de fin : "FileZilla x est installé." ou
/// l'erreur, avec OK, qui ferme. Annulation (UAC refusée) : fermée sans message.
/// "Plus tard" ne mémorise rien : la question revient au prochain démarrage. FileZilla
/// absent : même déroulement pour sa première installation, avec "Non".
/// </summary>
public sealed class UpdatePromptViewModel : DialogViewModel
{
    private readonly UpdateCheck _check;
    private readonly UpdateService _updates;
    private readonly IDialogs _dialogs;
    private bool _isBusy;

    public UpdatePromptViewModel(UpdateCheck check, UpdateService updates, IDialogs dialogs)
        : base(DialogKind.Question, check.OfferText)
    {
        _check = check;
        _updates = updates;
        _dialogs = dialogs;
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !IsBusy);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);
        // Première installation : proposée une seule fois, pas de "Plus tard".
        Buttons = [new(L.T("Install"), InstallCommand, true, false),
                   new(check.InstallAvailable ? L.T("No") : L.T("Later"), CloseCommand, false, true)];
    }

    /// <summary>Installation en cours : la fenêtre ne se ferme pas, les boutons sont inactifs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            InstallCommand.Refresh();
            CloseCommand.Refresh();
        }
    }

    public override bool CanClose => !IsBusy;

    /// <summary>Issue de la dernière installation ; null tant qu'aucune n'a été lancée.</summary>
    public UpdateOutcome? Outcome { get; private set; }

    public RelayCommand InstallCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Message de la fenêtre d'attente.</summary>
    public string WaitingText =>
        L.T("Downloading and installing FileZilla {0}…\n\nAllow the changes to the device if Windows asks.",
            VersionText.Display(_check.Latest!.Version));

    public async Task InstallAsync()
    {
        if (_updates.FileZillaRunning && !_dialogs.Confirm(
                L.T("FileZilla is open. Close it for the installation? It will be restarted afterwards."),
                L.T("Close FileZilla"), L.T("Cancel")))
            return;

        IsBusy = true;
        ShowWaiting(WaitingText);
        // Hors du fil de l'interface : téléchargement, contrôle de signature et fermeture
        // de FileZilla ne figent pas la fenêtre d'attente.
        Outcome = await Task.Run(() => _updates.InstallAsync(_check.Latest!));
        IsBusy = false;
        if (Outcome.Cancelled)
            Close();
        else
            ShowMessage(Outcome.Success ? DialogKind.Info : DialogKind.Error, Outcome.Message, L.T("OK"));
    }
}
