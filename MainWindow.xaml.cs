using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DevChronicle
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            // Apply dark theme with Mica backdrop
            ApplicationThemeManager.Apply(
                ApplicationTheme.Dark,
                WindowBackdropType.Mica,
                true
            );

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Navigate to Dashboard on startup
            RootNavigationView.Navigate(typeof(DashboardPage));
        }
    }
}