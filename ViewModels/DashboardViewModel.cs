using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;
    private readonly SummarizationRunnerService _summarizationRunner;
    private bool _disposed;

    public ObservableCollection<DashboardSessionItem> RecentSessions { get; } = new();

    [ObservableProperty]
    private int totalSessions;

    [ObservableProperty]
    private int pendingDays;

    [ObservableProperty]
    private int summarizedDays;

    [ObservableProperty]
    private string activeSessionText = "No active session";

    [ObservableProperty]
    private string currentSummarizationStatus = "Idle";

    [ObservableProperty]
    private bool isSummarizationRunning;

    [ObservableProperty]
    private bool hasRecentSessions;

    public DashboardViewModel(
        DatabaseService databaseService,
        SessionContextService sessionContext,
        SummarizationRunnerService summarizationRunner)
    {
        _databaseService = databaseService;
        _sessionContext = sessionContext;
        _summarizationRunner = summarizationRunner;
        _sessionContext.CurrentSessionChanged += OnCurrentSessionChanged;
        _summarizationRunner.StateChanged += OnRunnerStateChanged;
        _ = RefreshDashboardDataAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _sessionContext.CurrentSessionChanged -= OnCurrentSessionChanged;
        _summarizationRunner.StateChanged -= OnRunnerStateChanged;
        _disposed = true;
    }

    [RelayCommand]
    private async Task RefreshDashboardDataAsync()
    {
        var sessions = (await _databaseService.GetAllSessionsAsync())
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        TotalSessions = sessions.Count;
        RecentSessions.Clear();
        foreach (var session in sessions.Take(5))
        {
            RecentSessions.Add(new DashboardSessionItem
            {
                SessionId = session.Id,
                Name = session.Name,
                RepoPath = session.RepoPath,
                CreatedAt = session.CreatedAt
            });
        }

        HasRecentSessions = RecentSessions.Count > 0;

        var pending = 0;
        var summarized = 0;
        foreach (var session in sessions)
        {
            var days = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            pending += days.Count(d => d.Status == DayStatus.Mined);
            summarized += days.Count(d => d.Status == DayStatus.Summarized || d.Status == DayStatus.Approved);
        }

        PendingDays = pending;
        SummarizedDays = summarized;
        UpdateActiveSessionText();
        SyncRunnerState();
    }

    [RelayCommand]
    private void ResumeLastSession()
    {
        var latest = RecentSessions.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        if (latest == null)
            return;

        OpenSession(latest.SessionId);
    }

    [RelayCommand]
    private async Task SummarizePendingCurrentSessionAsync()
    {
        if (!_sessionContext.HasActiveSession)
            return;

        await _summarizationRunner.SummarizePendingDaysAsync();
    }

    [RelayCommand]
    private void OpenRecentSession(DashboardSessionItem? session)
    {
        if (session == null)
            return;

        OpenSession(session.SessionId);
    }

    private void OpenSession(int sessionId)
    {
        if (System.Windows.Application.Current.MainWindow is not Views.MainWindow mainWindow)
            return;

        mainWindow.NavigateToSessionDetail(sessionId);
    }

    private void OnCurrentSessionChanged(object? sender, Session? e)
    {
        UpdateActiveSessionText();
    }

    private void OnRunnerStateChanged(object? sender, EventArgs e)
    {
        SyncRunnerState();
    }

    private void UpdateActiveSessionText()
    {
        var session = _sessionContext.CurrentSession;
        ActiveSessionText = session == null
            ? "No active session"
            : $"{session.Name} ({session.RepoPath})";
    }

    private void SyncRunnerState()
    {
        CurrentSummarizationStatus = _summarizationRunner.Status;
        IsSummarizationRunning = _summarizationRunner.IsSummarizing;
    }
}

public sealed class DashboardSessionItem
{
    public int SessionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string RepoPath { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
