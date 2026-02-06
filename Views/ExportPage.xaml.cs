using System.Windows.Controls;
using System.Windows;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle.Views
{
    /// <summary>
    /// Interaction logic for ExportPage.xaml
    /// </summary>
    public partial class ExportPage : Page
    {
        public ExportPage()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<ExportViewModel>();
        }

        private void SessionExportMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button || button.ContextMenu == null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }
}
