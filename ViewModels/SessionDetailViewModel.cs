using System.Windows;
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
    }
}
