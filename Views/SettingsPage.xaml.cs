using System.Windows;
using System.Windows.Controls;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WinForms = System.Windows.Forms;

namespace DevChronicle.Views
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage : Page
    {
        private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<SettingsViewModel>();
        }

        private void OpenAiApiKeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsEditingApiKey)
            {
                ViewModel.StartEditingApiKey();
            }
        }

        private void OpenAiApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.UpdateApiKeyInput(OpenAiApiKeyBox.Text);
        }

        private async void SaveApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (OpenAiApiKeyBox == null)
                return;

            var apiKey = OpenAiApiKeyBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                System.Windows.MessageBox.Show("Please enter a valid OpenAI API key.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ViewModel.SaveApiKeyAsync(apiKey);

            System.Windows.MessageBox.Show("OpenAI API key saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void SaveMasterPrompt_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SaveMasterPromptAsync();
            System.Windows.MessageBox.Show("Master prompt saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BrowseExportDirectory_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Select default export output folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                return;

            ViewModel.DefaultExportDirectory = dialog.SelectedPath;
        }
    }
}
