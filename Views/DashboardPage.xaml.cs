using System.Windows.Controls;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle.Views
{
    /// <summary>
    /// Interaction logic for DashboardPage.xaml
    /// </summary>
    public partial class DashboardPage : Page
    {
        public DashboardPage()
        {
            InitializeComponent();
            Loaded += DashboardPage_Loaded;
            Unloaded += DashboardPage_Unloaded;

            if (App.ServiceProvider != null)
            {
                DataContext = App.ServiceProvider.GetRequiredService<DashboardViewModel>();
            }
        }

        private void DashboardPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is DashboardViewModel viewModel)
            {
                _ = viewModel.RefreshDashboardDataCommand.ExecuteAsync(null);
            }
        }

        private void DashboardPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
