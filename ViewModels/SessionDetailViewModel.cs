using System.Windows;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

/// <summary>
/// Composite ViewModel that orchestrates all phase ViewModels for a session.
/// This is the main ViewModel for SessionDetailPage.
/// </summary>
public partial class SessionDetailViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;

    [ObservableProperty]
    private Session? session;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private SessionDetailPanel selectedPanel = SessionDetailPanel.Summarization;

    [ObservableProperty]
    private ObservableCollection<string> selectedDayBullets = new();

    [ObservableProperty]
    private bool hasSelectedDaySummary;

    [ObservableProperty]
    private ObservableCollection<CommitEvidenceViewModel> selectedDayEvidence = new();

    [ObservableProperty]
    private bool hasSelectedDayEvidence;

    /// <summary>
    /// Phase A: Mining ViewModel
    /// </summary>
    public MiningViewModel Mining { get; }

    /// <summary>
    /// Phase B: Summarization ViewModel
    /// </summary>
    public SummarizationViewModel Summarization { get; }

    /// <summary>
    /// Phase C: Export ViewModel
    /// </summary>
    public ExportViewModel Export { get; }

    /// <summary>
    /// Day Browser ViewModel
    /// </summary>
    public DayBrowserViewModel DayBrowser { get; }

    public SessionDetailViewModel(
        DatabaseService databaseService,
        SessionContextService sessionContext,
        MiningViewModel miningViewModel,
        SummarizationViewModel summarizationViewModel,
        ExportViewModel exportViewModel,
        DayBrowserViewModel dayBrowserViewModel)
    {
        _databaseService = databaseService;
        _sessionContext = sessionContext;
        Mining = miningViewModel;
        Summarization = summarizationViewModel;
        Export = exportViewModel;
        DayBrowser = dayBrowserViewModel;

        // Subscribe to Mining completion to refresh day browser
        Mining.MiningCompleted += OnMiningCompleted;

        // Subscribe to Summarization events to update day status
        Summarization.DaySummarized += OnDaySummarized;

        DayBrowser.PropertyChanged += DayBrowserOnPropertyChanged;
    }

    /// <summary>
    /// Loads a session by ID and sets it as the current session context.
    /// </summary>
    /// <param name="sessionId">The session ID to load</param>
    public async Task LoadSessionAsync(int sessionId)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            Session = await _databaseService.GetSessionAsync(sessionId);

            if (Session == null)
            {
                ErrorMessage = "Session not found";
                return;
            }

            // Set as current session in context
            _sessionContext.SetCurrentSession(Session);

            // Load days for this session
            await DayBrowser.LoadDaysAsync(sessionId);

            // Update summarization pending count
            await Summarization.UpdatePendingCountAsync();

            await LoadSelectedDaySummaryAsync();
            await LoadSelectedDayEvidenceAsync();

            // Auto-start mining when a new session has no days yet
            if (DayBrowser.Days.Count == 0)
            {
                _ = Mining.MineInitialWindowAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load session: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BackToSessions()
    {
        // Clear session context when navigating away
        _sessionContext.ClearSession();

        // Navigate back to Sessions page
        // Note: Navigation will be implemented in MainWindow/SessionDetailPage
    }

    [RelayCommand]
    private void SetPanel(SessionDetailPanel panel)
    {
        SelectedPanel = panel;
    }

    private async void OnMiningCompleted(object? sender, EventArgs e)
    {
        // Refresh day browser when mining completes
        await DayBrowser.RefreshAsync();

        // Update summarization pending count
        await Summarization.UpdatePendingCountAsync();
    }

    private void OnDaySummarized(object? sender, DateTime day)
    {
        // Update day status badge in day browser
        DayBrowser.UpdateDayStatus(day, Models.DayStatus.Summarized);

        var selected = DayBrowser.SelectedDay;
        if (selected != null && selected.Date.Date == day.Date)
        {
            _ = LoadSelectedDaySummaryAsync();
            _ = LoadSelectedDayEvidenceAsync();
        }
    }

    private void DayBrowserOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DayBrowser.SelectedDay))
        {
            _ = LoadSelectedDaySummaryAsync();
            _ = LoadSelectedDayEvidenceAsync();
        }
    }

    private async Task LoadSelectedDaySummaryAsync()
    {
        var session = _sessionContext.CurrentSession;
        var selectedDay = DayBrowser.SelectedDay;

        if (session == null || selectedDay == null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelectedDayBullets.Clear();
                HasSelectedDaySummary = false;
            });
            return;
        }

        var summary = await _databaseService.GetDaySummaryAsync(session.Id, selectedDay.Date);
        if (summary == null || string.IsNullOrWhiteSpace(summary.BulletsText))
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelectedDayBullets.Clear();
                HasSelectedDaySummary = false;
            });
            return;
        }

        var bullets = summary.BulletsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SelectedDayBullets.Clear();
            foreach (var bullet in bullets)
                SelectedDayBullets.Add(bullet);

            HasSelectedDaySummary = SelectedDayBullets.Count > 0;
        });
    }

    private async Task LoadSelectedDayEvidenceAsync()
    {
        var session = _sessionContext.CurrentSession;
        var selectedDay = DayBrowser.SelectedDay;

        if (session == null || selectedDay == null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelectedDayEvidence.Clear();
                HasSelectedDayEvidence = false;
            });
            return;
        }

        var commits = (await _databaseService.GetCommitsForDayAsync(session.Id, selectedDay.Date)).ToList();
        var evidence = new List<CommitEvidenceViewModel>();

        foreach (var commit in commits)
        {
            var files = new List<FileEvidenceViewModel>();
            try
            {
                var parsed = JsonSerializer.Deserialize<List<CommitFile>>(commit.FilesJson) ?? new List<CommitFile>();
                foreach (var file in parsed)
                {
                    files.Add(new FileEvidenceViewModel(file.Path, file.Additions, file.Deletions));
                }
            }
            catch
            {
                // Ignore parse errors; keep files empty.
            }

            var shortSha = commit.Sha.Length > 7 ? commit.Sha.Substring(0, 7) : commit.Sha;
            evidence.Add(new CommitEvidenceViewModel(shortSha, commit.Subject, files));
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SelectedDayEvidence.Clear();
            foreach (var item in evidence)
                SelectedDayEvidence.Add(item);

            HasSelectedDayEvidence = SelectedDayEvidence.Count > 0;
        });
    }
}
