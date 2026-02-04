using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

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
        LoadSessionsAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task LoadSessionsAsync()
    {
        IsLoading = true;
        try
        {
            var sessionsList = await _databaseService.GetAllSessionsAsync();
            Sessions.Clear();
            foreach (var session in sessionsList)
            {
                Sessions.Add(session);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateSessionAsync()
    {
        // TODO: Show dialog to create new session
        var session = new Session
        {
            Name = $"New Session {DateTime.Now:yyyy-MM-dd HH:mm}",
            RepoPath = @"C:\Path\To\Repo", // TODO: Browse for path
            CreatedAt = DateTime.UtcNow,
            MainBranch = "main"
        };

        var id = await _databaseService.CreateSessionAsync(session);
        session.Id = id;
        Sessions.Insert(0, session);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(Session? session)
    {
        if (session == null) return;

        await _databaseService.DeleteSessionAsync(session.Id);
        Sessions.Remove(session);
    }
}
