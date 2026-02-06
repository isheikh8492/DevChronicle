using System.Windows.Controls;
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
    }
}
