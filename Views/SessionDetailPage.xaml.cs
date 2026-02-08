using System.Windows.Controls;
using System.Windows.Threading;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle.Views;

/// <summary>
/// Interaction logic for SessionDetailPage.xaml
/// </summary>
public partial class SessionDetailPage : Page
{
    public SessionDetailPage()
    {
        InitializeComponent();
        Unloaded += SessionDetailPage_Unloaded;

        try
        {
            if (App.ServiceProvider != null)
            {
                DataContext = App.ServiceProvider.GetRequiredService<SessionDetailViewModel>();
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "ServiceProvider is null. Cannot initialize SessionDetailPage.",
                    "Initialization Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to initialize SessionDetailPage:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                "Initialization Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Called when page is navigated to with a session ID parameter.
    /// </summary>
    public async void LoadSession(int sessionId)
    {
        try
        {
            if (DataContext is SessionDetailViewModel viewModel)
            {
                await viewModel.LoadSessionAsync(sessionId);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    $"DataContext is not SessionDetailViewModel. Type: {DataContext?.GetType().Name ?? "null"}",
                    "Load Session Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to load session {sessionId}:\n\n{ex.Message}\n\nInner Exception:\n{ex.InnerException?.Message}\n\nStack Trace:\n{ex.StackTrace}",
                "Load Session Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void Button_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.NavigateBack();
        }
    }

    private void SessionDetailPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void EditSummaryButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Command runs first and toggles edit mode; focus textbox after state update.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SummaryEditorTextBox.Focus();
            SummaryEditorTextBox.CaretIndex = SummaryEditorTextBox.Text?.Length ?? 0;
        }));
    }
}
