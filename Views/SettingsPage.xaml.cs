using System.Windows;
using System.Windows.Controls;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

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
    }
}
