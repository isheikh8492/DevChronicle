using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

/// <summary>
/// ViewModel for Phase B: AI Summarization controls and progress.
/// </summary>
public partial class SummarizationViewModel : ObservableObject
{
    private readonly SummarizationService _summarizationService;
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private int daysSummarized;

    [ObservableProperty]
    private int pendingDays;

    [ObservableProperty]
    private string status = "Idle";

    [ObservableProperty]
    private OperationState operationState = OperationState.Idle;

    [ObservableProperty]
    private string recoverActionText = string.Empty;

    [ObservableProperty]
    private bool isSummarizing;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private int progressCurrentStep;

    [ObservableProperty]
    private int progressTotalSteps;

    public bool IsProgressIndeterminate => IsSummarizing && Progress <= 0;
    public bool HasRecoverableIssue => !string.IsNullOrWhiteSpace(RecoverActionText);
    public bool HasProgressCounter => ProgressTotalSteps > 0;
    public bool ShowStatusPanel => IsSummarizing || OperationState != OperationState.Idle;
    public string ProgressCounterText => ProgressTotalSteps > 0 ? $"{ProgressCurrentStep}/{ProgressTotalSteps}" : string.Empty;

    /// <summary>
    /// Event fired when a day is summarized.
    /// Allows coordinators to update day browser status badges.
    /// </summary>
    public event EventHandler<DateTime>? DaySummarized;

    public SummarizationViewModel(
        SummarizationService summarizationService,
        DatabaseService databaseService,
        SettingsService settingsService,
        SessionContextService sessionContext)
    {
        _summarizationService = summarizationService;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _sessionContext = sessionContext;
    }

    [RelayCommand]
    private async Task SummarizePendingDaysAsync()
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            RequireInput("No session selected.", "Open a session and retry.");
            return;
        }

        IsSummarizing = true;
        BeginOperation("Summarizing pending days");
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            // Get all days with status "mined" (pending summarization)
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            var pendingDaysList = allDays.Where(d => d.Status == DayStatus.Mined).ToList();

            if (pendingDaysList.Count == 0)
            {
                RequireInput("No pending days to summarize.", "Mine or select a session with pending days.");
                return;
            }

            PendingDays = pendingDaysList.Count;
            var totalPending = pendingDaysList.Count;
            var currentIndex = 0;

            foreach (var day in pendingDaysList)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                currentIndex++;
                UpdateOperation("Summarizing pending days", currentIndex, totalPending);

                var maxBullets = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
                var result = await _summarizationService.SummarizeDayAsync(
                    session.Id,
                    day.Date,
                    maxBullets: maxBullets,
                    cancellationToken: _cancellationTokenSource.Token);

                if (result.Success)
                {
                    // Update day status in database
                    day.Status = DayStatus.Summarized;
                    await _databaseService.UpsertDayAsync(day);

                    DaysSummarized++;
                    PendingDays--;
                    UpdateOperation("Summarizing pending days", currentIndex, totalPending);

                    // Notify listeners that this day was summarized
                    DaySummarized?.Invoke(this, day.Date);
                }
                else
                {
                    FailOperation($"Summarization failed: {result.ErrorMessage}", "Fix the summarization issue and retry.");
                    return;
                }
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                CancelOperation("Summarization canceled.");
            }
            else
            {
                CompleteOperation("Summarization complete.");
            }
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Summarization canceled.");
        }
        catch (Exception ex)
        {
            FailOperation($"Summarization failed: {ex.Message}", "Retry summarization.");
        }
        finally
        {
            IsSummarizing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task SummarizeSelectedDayAsync(DayViewModel? day)
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            RequireInput("No session selected.", "Open a session and retry.");
            return;
        }

        if (day == null)
        {
            RequireInput("No day selected.", "Select a day and retry.");
            return;
        }

        IsSummarizing = true;
        BeginOperation("Summarizing selected day");
        UpdateOperation("Summarizing selected day", 1, 1);
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var maxBullets = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
            var result = await _summarizationService.SummarizeDayAsync(
                session.Id,
                day.Date,
                maxBullets: maxBullets,
                cancellationToken: _cancellationTokenSource.Token);

            if (result.Success)
            {
                day.SetStatus(DayStatus.Summarized);
                await _databaseService.UpsertDayAsync(day.Day);

                DaysSummarized++;
                DaySummarized?.Invoke(this, day.Date);
                CompleteOperation($"Summarization complete for {day.Date:yyyy-MM-dd}.");
            }
            else
            {
                FailOperation($"Summarization failed: {result.ErrorMessage}", "Fix the issue and retry selected day.");
            }
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Summarization canceled.");
        }
        catch (Exception ex)
        {
            FailOperation($"Summarization failed: {ex.Message}", "Retry selected day summarization.");
        }
        finally
        {
            IsSummarizing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            await UpdatePendingCountAsync();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cancellationTokenSource?.Cancel();
        Status = "Stopping summarization...";
    }

    /// <summary>
    /// Updates the count of pending days (called when days are loaded or mined).
    /// </summary>
    public async Task UpdatePendingCountAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null) return;

        try
        {
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            PendingDays = allDays.Count(d => d.Status == DayStatus.Mined);
            DaysSummarized = allDays.Count(d => d.Status == DayStatus.Summarized || d.Status == DayStatus.Approved);
        }
        catch
        {
            // Ignore errors during count update
        }
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnIsSummarizingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ShowStatusPanel));
    }

    partial void OnProgressCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
    }

    partial void OnProgressTotalStepsChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
    }

    partial void OnRecoverActionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRecoverableIssue));
    }

    partial void OnOperationStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(ShowStatusPanel));
    }

    private void BeginOperation(string verb)
    {
        OperationState = OperationState.Running;
        RecoverActionText = string.Empty;
        ProgressCurrentStep = 0;
        ProgressTotalSteps = 0;
        Progress = 0;
        Status = OperationStatusFormatter.FormatProgress(verb, 0, 0);
    }

    private void UpdateOperation(string verb, int current, int total)
    {
        var clampedCurrent = Math.Max(0, current);
        var clampedTotal = Math.Max(0, total);
        if (clampedTotal > 0 && clampedCurrent > clampedTotal)
        {
            Debug.WriteLine($"[SummarizationProgress] Invalid progress pair emitted: {clampedCurrent}/{clampedTotal}. Clamping.");
            clampedCurrent = clampedTotal;
        }

        ProgressCurrentStep = clampedCurrent;
        ProgressTotalSteps = clampedTotal;
        Progress = clampedTotal > 0
            ? Math.Clamp((double)clampedCurrent / clampedTotal * 100, 0, 100)
            : 0;
        Status = OperationStatusFormatter.FormatProgress(verb, clampedCurrent, clampedTotal);
    }

    private void CompleteOperation(string successMessage)
    {
        OperationState = OperationState.Success;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Success, successMessage);
    }

    private void CancelOperation(string cancelMessage)
    {
        OperationState = OperationState.Canceled;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Canceled, cancelMessage);
    }

    private void FailOperation(string errorMessage, string recoverAction)
    {
        OperationState = OperationState.Error;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Error, errorMessage);
    }

    private void RequireInput(string message, string recoverAction)
    {
        OperationState = OperationState.NeedsInput;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.NeedsInput, message);
    }
}
