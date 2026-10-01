using System.Windows.Controls;
using PromptSaver.Desktop.ViewModels;

namespace PromptSaver.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnProviderApiKeyChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.ProviderApiKey = passwordBox.Password;
        }
    }
}
