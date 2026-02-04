using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;
using DevChronicle.Views.Windows;

namespace DevChronicle.ViewModels;

public partial class SessionsViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly GitService _gitService;

    [ObservableProperty]
    private ObservableCollection<Session> sessions = new();

    [ObservableProperty]
    private Session? selectedSession;

    [ObservableProperty]
    private bool isLoading;

    public SessionsViewModel(DatabaseService databaseService, GitService gitService)
    {
        _databaseService = databaseService;
        _gitService = gitService;

        // Load sessions without blocking constructor
        _ = LoadSessionsAsync();
    }

    [RelayCommand]
    private async Task LoadSessionsAsync()
    {
        IsLoading = true;
        try
        {
            var sessionsList = await _databaseService.GetAllSessionsAsync();

            // Ensure UI thread for ObservableCollection operations
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in sessionsList)
                {
                    Sessions.Add(session);
                }
            });
        }
        catch (Exception ex)
        {
            // Log error or show message to user
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show($"Failed to load sessions: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateSessionAsync()
    {
        try
        {
            // Show dialog to create new session
            var dialog = new CreateSessionDialog
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                var session = dialog.Result;

                // Save to database
                var id = await _databaseService.CreateSessionAsync(session);
                session.Id = id;

                // Ensure UI thread for ObservableCollection operations
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Sessions.Insert(0, session);
                });
            }
        }
        catch (Exception ex)
        {
            // Show error message to user
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show($"Failed to create session: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            });
        }
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(Session? session)
    {
        if (session == null) return;

        try
        {
            await _databaseService.DeleteSessionAsync(session.Id);

            // Ensure UI thread for ObservableCollection operations
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Remove(session);
            });
        }
        catch (Exception ex)
        {
            // Show error message to user
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show($"Failed to delete session: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            });
        }
    }

    [RelayCommand]
    private void OpenSession(Session? session)
    {
        if (session == null) return;

        // Navigate to SessionDetailPage with session ID
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.NavigateToSessionDetail(session.Id);
        }
    }
}
