using System.Windows;
using FileZillaUpdater.ViewModels;

namespace FileZillaUpdater;

/// <summary>Fenêtre de réglages et d'installation (lancement manuel).</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
