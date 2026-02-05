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
        public SettingsPage()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<SettingsViewModel>();
        }
    }
}
