using System.Windows.Controls;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle
{
    /// <summary>
    /// Interaction logic for MiningPage.xaml
    /// </summary>
    public partial class MiningPage : Page
    {
        public MiningPage()
        {
            InitializeComponent();

            if (App.ServiceProvider != null)
            {
                DataContext = App.ServiceProvider.GetRequiredService<MiningViewModel>();
            }
        }
    }
}
