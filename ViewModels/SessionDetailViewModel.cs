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
    private bool isEditingSummary;

    [ObservableProperty]
    private string editedSummaryText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CommitEvidenceViewModel> selectedDayEvidence = new();

    [ObservableProperty]
    private bool hasSelectedDayEvidence;

    [ObservableProperty]
    private ObservableCollection<BranchEvidenceViewModel> selectedDayBranches = new();

    [ObservableProperty]
    private bool hasSelectedDayBranches;

    [ObservableProperty]
    private bool hasAnySelectedDayEvidence;

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
                EditedSummaryText = string.Empty;
                IsEditingSummary = false;
            });
            return;
        }

        var (summary, _) = await _databaseService.GetEffectiveDaySummaryAsync(session.Id, selectedDay.Date);
        if (summary == null || string.IsNullOrWhiteSpace(summary.BulletsText))
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelectedDayBullets.Clear();
                HasSelectedDaySummary = false;
                EditedSummaryText = string.Empty;
                IsEditingSummary = false;
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
            EditedSummaryText = string.Join(Environment.NewLine, SelectedDayBullets);
        });
    }

    [RelayCommand]
    private void StartEditSummary()
    {
        if (DayBrowser.SelectedDay == null)
            return;

        IsEditingSummary = true;
    }

    [RelayCommand]
    private async Task CancelEditSummaryAsync()
    {
        IsEditingSummary = false;
        await LoadSelectedDaySummaryAsync();
    }

    [RelayCommand]
    private async Task SaveEditedSummaryAsync()
    {
        var session = _sessionContext.CurrentSession;
        var selectedDay = DayBrowser.SelectedDay;
        if (session == null || selectedDay == null)
            return;

        var normalized = BulletText.NormalizeToDashBullets(EditedSummaryText);
        if (normalized.Count == 0)
        {
            System.Windows.MessageBox.Show("Summary cannot be empty.", "Edit Summary", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _databaseService.UpsertDaySummaryOverrideAsync(new DaySummaryOverride
        {
            SessionId = session.Id,
            Day = selectedDay.Date,
            BulletsText = string.Join("\n", normalized),
            UpdatedAt = DateTime.UtcNow
        });

        IsEditingSummary = false;
        await LoadSelectedDaySummaryAsync();
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
                SelectedDayBranches.Clear();
                HasSelectedDayBranches = false;
                HasAnySelectedDayEvidence = false;
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

        var branchRows = await _databaseService.GetCommitBranchRowsForDayAsync(session.Id, selectedDay.Date);
        var branchGroups = branchRows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.BranchName) ? "(unattributed)" : row.BranchName!)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToList();

        var branches = new List<BranchEvidenceViewModel>();
        foreach (var group in branchGroups)
        {
            var commitsInBranch = group
                .OrderBy(row => row.AuthorDate)
                .Select(row =>
                {
                    var shortSha = row.Sha.Length > 7 ? row.Sha.Substring(0, 7) : row.Sha;
                    return new BranchCommitViewModel(shortSha, row.Subject, row.AuthorDate);
                })
                .ToList();

            branches.Add(new BranchEvidenceViewModel(group.Key, commitsInBranch));
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SelectedDayEvidence.Clear();
            foreach (var item in evidence)
                SelectedDayEvidence.Add(item);

            HasSelectedDayEvidence = SelectedDayEvidence.Count > 0;

            SelectedDayBranches.Clear();
            foreach (var branch in branches)
                SelectedDayBranches.Add(branch);

            HasSelectedDayBranches = SelectedDayBranches.Count > 0;
            HasAnySelectedDayEvidence = HasSelectedDayEvidence || HasSelectedDayBranches;
        });
    }
}
