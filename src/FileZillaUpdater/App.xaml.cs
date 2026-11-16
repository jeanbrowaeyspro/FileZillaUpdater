using System.Windows;
using System.Windows.Threading;
using FileZillaUpdater.Theme;
using FileZillaUpdater.ViewModels;

namespace FileZillaUpdater;

/// <summary>
/// Point d'entrée. Sans argument : fenêtre de réglages et d'installation. Avec
/// <c>/demarrage</c> : vérification silencieuse (une requête à Chocolatey), fenêtre
/// seulement si une mise à jour de FileZilla est disponible.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private IDialogs? _dialogs;
    /// <summary>Mise à jour de FileZillaUpdater lui-même, attendue avant la fermeture.</summary>
    private Task _selfUpdate = Task.CompletedTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.FromEnvironment();
        Log.Configure(paths.LogFile);
        var args = AppArguments.Parse(e.Args);
        var settings = AppSettings.Load(paths.SettingsFile);
        L.Use(settings.Language);

        _singleInstance = new Mutex(true, @"Local\" + AppPaths.AppName, out var first);
        if (!first)
        {
            Log.Info("Une instance tourne déjà : sortie");
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        // Boîtes centrées sur la fenêtre active (proposition de mise à jour au-dessus des réglages…).
        _dialogs = new WpfDialogs(() => Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow);
        DispatcherUnhandledException += OnUnhandledException;
        ThemeManager.Apply(this, settings.Theme);

        var services = new AppServices(paths, _dialogs);
        services.SelfUpdate.CleanUp();
        if (args.Startup)
            await StartupCheckAsync(services, args.AfterInstall);
        else
            ShowSettings(services, args, settings);
    }

    /// <summary>
    /// Démarrage de session : rien n'est affiché s'il n'y a rien à proposer. Juste après
    /// l'installation de l'outil (<paramref name="afterInstall"/>), l'installation de
    /// FileZilla est aussi proposée s'il est absent : jamais installé sans accord.
    /// </summary>
    private async Task StartupCheckAsync(AppServices services, bool afterInstall)
    {
        Log.Info("Vérification au démarrage");
        var check = await services.Updates.CheckAsync(UpdateService.StartupRetryDelays);
        // L'outil vérifie aussi sa propre version (GitHub), en parallèle.
        _selfUpdate = services.SelfUpdate.RunAsync();
        if (!check.UpdateAvailable && !(afterInstall && check.InstallAvailable))
        {
            ShutdownAfterSelfUpdate();
            return;
        }
        var window = CreateUpdatePrompt(check, services, owner: null);
        window.Closed += (_, _) => ShutdownAfterSelfUpdate();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Fenêtre de mise à jour. Dès que l'installation est lancée, sa fenêtre propriétaire
    /// (réglages) est masquée et elle devient une fenêtre d'attente jusqu'à la fin de
    /// l'installation silencieuse, puis le message de fin ; à sa fermeture, la fenêtre
    /// propriétaire est fermée, ce qui termine l'application.
    /// </summary>
    private static DialogWindow CreateUpdatePrompt(UpdateCheck check, AppServices services, Window? owner)
    {
        var viewModel = new UpdatePromptViewModel(check, services.Updates, services.Dialogs);
        var window = DialogWindow.Create(viewModel, owner);
        if (owner is not null)
        {
            // Masquer la propriétaire ne masque pas la fenêtre d'attente (fenêtre possédée).
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DialogViewModel.IsWaiting) && viewModel.IsWaiting)
                    owner.Hide();
            };
            window.Closed += (_, _) =>
            {
                if (viewModel.Outcome is not null)
                    owner.Close();
            };
        }
        return window;
    }

    /// <summary>
    /// Fenêtre de réglages. Changement de langue : elle est remplacée par une nouvelle,
    /// à la même place, dans la nouvelle langue.
    /// </summary>
    private void ShowSettings(AppServices services, AppArguments args, AppSettings settings, Window? previous = null)
    {
        var viewModel = new SettingsViewModel(services.Setup, services.Updates, settings,
                                              args.LauncherPath, services.Dialogs);
        var window = new SettingsWindow(viewModel);
        var reopening = false;
        viewModel.UpdateOffered += check => CreateUpdatePrompt(check, services, window).ShowDialog();
        viewModel.ThemeChanged += theme => ThemeManager.Apply(this, theme);
        viewModel.LanguageChanged += () =>
        {
            reopening = true;
            ShowSettings(services, args, settings, window);
            window.Close();
        };
        window.Closed += async (_, _) =>
        {
            if (reopening)
                return;
            await _selfUpdate;
            if (viewModel.PendingCommand is { } command)
            {
                // Instance unique libérée d'abord : la vérification lancée après
                // l'installation ne doit pas se croire en double.
                ReleaseSingleInstance();
                services.Runner.Start(command);
            }
            Shutdown();
        };
        if (previous is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = previous.Left;
            window.Top = previous.Top;
        }
        MainWindow = window;
        window.Show();
        _ = viewModel.OpenedAsync();
        if (!viewModel.InstallMode && previous is null)
            _selfUpdate = services.SelfUpdate.RunAsync();
    }

    /// <summary>
    /// Ferme l'application une fois la mise à jour de l'outil terminée, s'il y en a une en
    /// cours : le lanceur n'est jamais laissé à moitié remplacé.
    /// </summary>
    private async void ShutdownAfterSelfUpdate()
    {
        await _selfUpdate;
        Shutdown();
    }

    private void ReleaseSingleInstance()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception.ToString());
        _dialogs?.Error(L.T("Unexpected error:\n\n{0}", e.Exception.Message));
        e.Handled = true;
    }
}
