using System.Diagnostics;
using DevChronicle.Models;
using DevChronicle.ViewModels;

namespace DevChronicle.Services;

public class SummarizationRunnerService
{
    private const int InterDayDelayMilliseconds = 2000;
    private const int RateLimitBaseDelayMilliseconds = 3000;
    private const int NetworkRetryBaseDelayMilliseconds = 5000;
    private const int NetworkRetryMaxDelayMilliseconds = 30000;

    private readonly SummarizationService _summarizationService;
    private readonly SummarizationBatchService _summarizationBatchService;
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly HashSet<int> _activeBatchMonitors = new();
    private readonly object _batchMonitorLock = new();

    public int DaysSummarized { get; private set; }
    public int PendingDays { get; private set; }
    public string Status { get; private set; } = "Idle";
    public OperationState OperationState { get; private set; } = OperationState.Idle;
    public string RecoverActionText { get; private set; } = string.Empty;
    public bool IsSummarizing { get; private set; }
    public double Progress { get; private set; }
    public int ProgressCurrentStep { get; private set; }
    public int ProgressTotalSteps { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<DateTime>? DaySummarized;

    public SummarizationRunnerService(
        SummarizationService summarizationService,
        SummarizationBatchService summarizationBatchService,
        DatabaseService databaseService,
        SettingsService settingsService,
        SessionContextService sessionContext)
    {
        _summarizationService = summarizationService;
        _summarizationBatchService = summarizationBatchService;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _sessionContext = sessionContext;
    }

    public async Task SummarizePendingDaysAsync()
    {
        if (IsSummarizing) return;

        var pendingMode = await _settingsService.GetAsync(SettingsService.SummarizationPendingModeKey, "Batch");
        if (string.Equals(pendingMode, "Batch", StringComparison.OrdinalIgnoreCase))
        {
            await SummarizePendingDaysBatchAsync();
            return;
        }

        await SummarizePendingDaysLiveAsync();
    }

    private async Task SummarizePendingDaysLiveAsync()
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            RequireInput("No session selected.", "Open a session and retry.");
            return;
        }

        IsSummarizing = true;
        PublishState();
        BeginOperation("Summarizing pending days");
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            var pendingDaysList = allDays.Where(d => d.Status == DayStatus.Mined).ToList();

            if (pendingDaysList.Count == 0)
            {
                RequireInput("No pending days to summarize.", "Mine or select a session with pending days.");
                return;
            }

            PendingDays = pendingDaysList.Count;
            PublishState();
            var totalPending = pendingDaysList.Count;
            var currentIndex = 0;

            foreach (var day in pendingDaysList)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                currentIndex++;
                UpdateOperation("Summarizing pending days", currentIndex, totalPending);

                var maxBullets = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
                var result = await SummarizeDayWithRetryAsync(
                    session.Id,
                    day.Date,
                    maxBullets,
                    _cancellationTokenSource.Token);

                if (result.Success)
                {
                    day.Status = DayStatus.Summarized;
                    await _databaseService.UpsertDayAsync(day);

                    DaysSummarized++;
                    PendingDays--;
                    UpdateOperation("Summarizing pending days", currentIndex, totalPending);
                    DaySummarized?.Invoke(this, day.Date);

                    if (currentIndex < totalPending && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        Status = $"Cooling down before next day ({InterDayDelayMilliseconds / 1000}s)...";
                        PublishState();
                        await Task.Delay(InterDayDelayMilliseconds, _cancellationTokenSource.Token);
                    }
                }
                else
                {
                    FailOperation($"Summarization failed: {result.ErrorMessage}", "Fix the summarization issue and retry.");
                    return;
                }
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested)
                CancelOperation("Summarization canceled.");
            else
                CompleteOperation("Summarization complete.");
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
            PublishState();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            await UpdatePendingCountAsync();
        }
    }

    private async Task SummarizePendingDaysBatchAsync()
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            RequireInput("No session selected.", "Open a session and retry.");
            return;
        }

        IsSummarizing = true;
        PublishState();
        BeginOperation("Submitting pending days batch");
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            var pendingDaysList = allDays.Where(d => d.Status == DayStatus.Mined).ToList();
            if (pendingDaysList.Count == 0)
            {
                RequireInput("No pending days to summarize.", "Mine or select a session with pending days.");
                return;
            }

            PendingDays = pendingDaysList.Count;
            PublishState();
            var maxBullets = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
            var batchMaxDaysPerSubmit = await _settingsService.GetAsync(
                SettingsService.SummarizationBatchMaxDaysPerSubmitKey,
                15);
            batchMaxDaysPerSubmit = Math.Clamp(batchMaxDaysPerSubmit, 1, 60);
            var totalPendingAtStart = pendingDaysList.Count;
            var batchIndex = 0;

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var currentPending = (await _databaseService.GetDaysAsync(session.Id))
                    .Count(d => d.Status == DayStatus.Mined);
                PendingDays = currentPending;
                PublishState();
                if (currentPending == 0)
                {
                    CompleteOperation("Summarization complete.");
                    return;
                }

                batchIndex++;
                var done = Math.Max(0, totalPendingAtStart - currentPending);
                UpdateOperation("Submitting pending days batch", done, totalPendingAtStart);
                Status = $"Submitting batch {batchIndex} (up to {batchMaxDaysPerSubmit} days)...";
                PublishState();

                var batch = await Task.Run(
                    async () => await _summarizationBatchService.SubmitPendingDaysBatchAsync(
                        session.Id,
                        maxBullets,
                        batchMaxDaysPerSubmit,
                        _cancellationTokenSource.Token),
                    _cancellationTokenSource.Token);

                Status = $"Batch submitted ({batch.OpenAiBatchId}).";
                PublishState();
                await MonitorBatchUntilTerminalAsync(batch.Id, _cancellationTokenSource.Token);
            }

            CancelOperation("Summarization canceled.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Summarization canceled.");
        }
        catch (Exception ex)
        {
            FailOperation($"Batch summarization failed: {ex.Message}", "Fix the batch issue and retry.");
        }
        finally
        {
            IsSummarizing = false;
            PublishState();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            await UpdatePendingCountAsync();
        }
    }

    public async Task SummarizeSelectedDayAsync(DateTime day)
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            RequireInput("No session selected.", "Open a session and retry.");
            return;
        }

        IsSummarizing = true;
        PublishState();
        BeginOperation("Summarizing selected day");
        UpdateOperation("Summarizing selected day", 1, 1);
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var maxBullets = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
            var result = await SummarizeDayWithRetryAsync(
                session.Id,
                day,
                maxBullets,
                _cancellationTokenSource.Token);

            if (result.Success)
            {
                var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
                var matchingDay = allDays.FirstOrDefault(d => d.Date.Date == day.Date);
                if (matchingDay != null)
                {
                    matchingDay.Status = DayStatus.Summarized;
                    await _databaseService.UpsertDayAsync(matchingDay);
                }

                DaysSummarized++;
                DaySummarized?.Invoke(this, day);
                CompleteOperation($"Summarization complete for {day:yyyy-MM-dd}.");
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
            PublishState();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            await UpdatePendingCountAsync();
        }
    }

    public async Task StopAsync()
    {
        _cancellationTokenSource?.Cancel();
        Status = "Stopping summarization...";
        PublishState();

        try
        {
            var session = _sessionContext.CurrentSession;
            if (session != null)
            {
                var canceled = await _summarizationBatchService.CancelActiveBatchesForSessionAsync(
                    session.Id,
                    CancellationToken.None);

                if (canceled > 0)
                    CancelOperation($"Stopped and canceled {canceled} active batch job(s).");
                else
                    CancelOperation("Summarization canceled.");
            }
            else
            {
                CancelOperation("Summarization canceled.");
            }

            // If this stop request is handling resumed/background batch monitors,
            // there may be no local operation finally-block to flip IsSummarizing.
            if (_cancellationTokenSource == null)
            {
                IsSummarizing = false;
                PublishState();
            }
        }
        catch (Exception ex)
        {
            FailOperation($"Failed to cancel batch jobs: {ex.Message}", "Retry stop or wait for completion.");
        }
    }

    public async Task ResumeActiveBatchesAsync(CancellationToken cancellationToken = default)
    {
        var activeBatches = await _summarizationBatchService.GetActiveBatchesAsync();
        if (activeBatches.Count > 0)
        {
            IsSummarizing = true;
            Status = $"Resuming {activeBatches.Count} active batch job(s)...";
            OperationState = OperationState.Running;
            PublishState();
        }

        foreach (var batch in activeBatches)
        {
            if (!TryStartBatchMonitor(batch.Id))
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await MonitorBatchUntilTerminalAsync(batch.Id, cancellationToken);
                }
                catch
                {
                    // Ignore background resume failures; UI status will be updated on next explicit run.
                }
            }, cancellationToken);
        }
    }

    public async Task UpdatePendingCountAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null) return;

        try
        {
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            PendingDays = allDays.Count(d => d.Status == DayStatus.Mined);
            DaysSummarized = allDays.Count(d => d.Status == DayStatus.Summarized || d.Status == DayStatus.Approved);
            PublishState();
        }
        catch
        {
            // Ignore errors during count update.
        }
    }

    private async Task<SummarizationResult> SummarizeDayWithRetryAsync(
        int sessionId,
        DateTime day,
        int maxBullets,
        CancellationToken cancellationToken)
    {
        var configuredRetryWindowMinutes = await _settingsService.GetAsync(
            SettingsService.SummarizationNetworkRetryWindowMinutesKey,
            5);
        var networkRetryWindowMinutes = Math.Clamp(configuredRetryWindowMinutes, 1, 60);
        var configuredRateLimitWindowMinutes = await _settingsService.GetAsync(
            SettingsService.SummarizationRateLimitRetryWindowMinutesKey,
            10);
        var rateLimitRetryWindowMinutes = Math.Clamp(configuredRateLimitWindowMinutes, 1, 60);

        var rateLimitAttempt = 0;
        var networkAttempt = 0;
        var networkRetryDeadline = DateTime.UtcNow.AddMinutes(networkRetryWindowMinutes);
        var rateLimitRetryDeadline = DateTime.UtcNow.AddMinutes(rateLimitRetryWindowMinutes);

        while (true)
        {
            var result = await _summarizationService.SummarizeDayAsync(
                sessionId,
                day,
                maxBullets: maxBullets,
                cancellationToken: cancellationToken);

            if (result.Success)
                return result;

            if (IsRetryableNetworkError(result.ErrorMessage))
            {
                var now = DateTime.UtcNow;
                if (now >= networkRetryDeadline)
                    return result;

                var networkDelay = Math.Min(
                    NetworkRetryBaseDelayMilliseconds * (int)Math.Pow(2, Math.Min(networkAttempt, 4)),
                    NetworkRetryMaxDelayMilliseconds);
                var remaining = networkRetryDeadline - now;
                if (remaining.TotalMilliseconds < networkDelay)
                    networkDelay = Math.Max(1000, (int)remaining.TotalMilliseconds);

                var remainingMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                Status = $"Network issue detected. Retrying in {Math.Max(1, networkDelay / 1000)}s (up to {remainingMinutes} more min)...";
                PublishState();
                networkAttempt++;
                await Task.Delay(networkDelay, cancellationToken);
                continue;
            }

            if (!IsRetryableApiError(result.ErrorMessage))
                return result;

            var rateNow = DateTime.UtcNow;
            if (rateNow >= rateLimitRetryDeadline)
                return result;

            var rateLimitDelay = RateLimitBaseDelayMilliseconds * (int)Math.Pow(2, Math.Min(rateLimitAttempt, 20));
            var rateRemaining = rateLimitRetryDeadline - rateNow;
            if (rateRemaining.TotalMilliseconds < rateLimitDelay)
                rateLimitDelay = Math.Max(1000, (int)rateRemaining.TotalMilliseconds);

            var rateRemainingMinutes = Math.Max(1, (int)Math.Ceiling(rateRemaining.TotalMinutes));
            Status = $"Rate-limited by OpenAI. Retrying in {Math.Max(1, rateLimitDelay / 1000)}s (up to {rateRemainingMinutes} more min)...";
            PublishState();
            rateLimitAttempt++;
            await Task.Delay(rateLimitDelay, cancellationToken);
        }
    }

    private async Task MonitorBatchUntilTerminalAsync(int localBatchId, CancellationToken cancellationToken)
    {
        if (!TryStartBatchMonitor(localBatchId))
            return;

        try
        {
            var pollIntervalSeconds = await _settingsService.GetAsync(
                SettingsService.SummarizationBatchPollIntervalSecondsKey,
                30);
            pollIntervalSeconds = Math.Clamp(pollIntervalSeconds, 5, 300);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var batch = await _summarizationBatchService.RefreshBatchStatusAsync(localBatchId, cancellationToken);
                    Status = $"Batch {batch.Status} ({batch.OpenAiBatchId})";
                    PublishState();

                    if (string.Equals(batch.Status, SummarizationBatchStatuses.Completed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(batch.Status, SummarizationBatchStatuses.Applying, StringComparison.OrdinalIgnoreCase))
                    {
                        Status = "Applying batch results...";
                        PublishState();
                        var applyResult = await Task.Run(
                            async () => await _summarizationBatchService.ApplyBatchResultsAsync(localBatchId, cancellationToken),
                            cancellationToken);
                        Status = applyResult.Failed > 0
                            ? $"Partial Failure: {applyResult.Succeeded} succeeded, {applyResult.Failed} failed."
                            : $"Completed: {applyResult.Succeeded} days summarized.";
                        if (applyResult.Failed > 0)
                            OperationState = OperationState.Error;
                        else
                            OperationState = OperationState.Success;
                        PublishState();
                        return;
                    }

                    if (string.Equals(batch.Status, SummarizationBatchStatuses.Canceled, StringComparison.OrdinalIgnoreCase))
                    {
                        CancelOperation("Batch canceled.");
                        return;
                    }

                    if (string.Equals(batch.Status, SummarizationBatchStatuses.Failed, StringComparison.OrdinalIgnoreCase))
                    {
                        FailOperation($"Batch failed: {batch.LastError ?? "Unknown error"}", "Retry pending batch.");
                        return;
                    }
                }
                catch (Exception ex) when (IsRetryableNetworkError(ex.Message))
                {
                    Status = "Batch polling paused due to network issue. Retrying...";
                    PublishState();
                }

                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), cancellationToken);
            }
        }
        finally
        {
            StopBatchMonitor(localBatchId);
        }
    }

    private bool TryStartBatchMonitor(int localBatchId)
    {
        lock (_batchMonitorLock)
        {
            var added = _activeBatchMonitors.Add(localBatchId);
            if (added)
            {
                IsSummarizing = true;
                OperationState = OperationState.Running;
                PublishState();
            }

            return added;
        }
    }

    private void StopBatchMonitor(int localBatchId)
    {
        lock (_batchMonitorLock)
        {
            _activeBatchMonitors.Remove(localBatchId);
            if (_activeBatchMonitors.Count == 0 && _cancellationTokenSource == null)
            {
                IsSummarizing = false;
                PublishState();
            }
        }
    }

    private void PublishState() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void BeginOperation(string verb)
    {
        OperationState = OperationState.Running;
        RecoverActionText = string.Empty;
        ProgressCurrentStep = 0;
        ProgressTotalSteps = 0;
        Progress = 0;
        Status = OperationStatusFormatter.FormatProgress(verb, 0, 0);
        PublishState();
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
        PublishState();
    }

    private void CompleteOperation(string successMessage)
    {
        OperationState = OperationState.Success;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Success, successMessage);
        PublishState();
    }

    private void CancelOperation(string cancelMessage)
    {
        OperationState = OperationState.Canceled;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Canceled, cancelMessage);
        PublishState();
    }

    private void FailOperation(string errorMessage, string recoverAction)
    {
        OperationState = OperationState.Error;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Error, errorMessage);
        PublishState();
    }

    private void RequireInput(string message, string recoverAction)
    {
        OperationState = OperationState.NeedsInput;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.NeedsInput, message);
        PublishState();
    }

    private static bool IsRetryableApiError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var text = errorMessage.ToLowerInvariant();
        return text.Contains("429") ||
               text.Contains("rate limit") ||
               text.Contains("rate_limit") ||
               text.Contains("too many requests") ||
               text.Contains("temporarily unavailable") ||
               text.Contains("timeout");
    }

    private static bool IsRetryableNetworkError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var text = errorMessage.ToLowerInvariant();
        return text.Contains("no such host is known") ||
               text.Contains("name or service not known") ||
               text.Contains("temporary failure in name resolution") ||
               text.Contains("connection reset") ||
               text.Contains("connection refused") ||
               text.Contains("actively refused") ||
               text.Contains("network is unreachable") ||
               text.Contains("an error occurred while sending the request") ||
               text.Contains("httpclient") ||
               text.Contains("unable to read data from the transport connection");
    }
}
