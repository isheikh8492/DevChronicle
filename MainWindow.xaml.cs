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
        private int? _pendingSessionId;

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

        private void RootNavigationView_Navigated(NavigationView sender, NavigatedEventArgs args)
        {
            try
            {
                // Handle navigation with stored session ID parameter
                if (_pendingSessionId.HasValue && args.Page is SessionDetailPage detailPage)
                {
                    detailPage.LoadSession(_pendingSessionId.Value);
                    _pendingSessionId = null;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Navigation error:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "Navigation Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Navigates to SessionDetailPage with a session ID parameter.
        /// Called from SessionsViewModel when opening a session.
        /// </summary>
        public void NavigateToSessionDetail(int sessionId)
        {
            try
            {
                _pendingSessionId = sessionId;
                var success = RootNavigationView.Navigate(typeof(SessionDetailPage));
                if (!success)
                {
                    System.Windows.MessageBox.Show(
                        $"Failed to navigate to SessionDetailPage for session {sessionId}",
                        "Navigation Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error navigating to session {sessionId}:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "Navigation Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }
}