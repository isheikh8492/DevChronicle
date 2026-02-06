using System;
using System.Text.Json;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

public partial class MiningViewModel : ObservableObject
{
    private readonly MiningService _miningService;
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly SessionContextService _sessionContext;
    private readonly LoggerService _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private int daysMined;

    [ObservableProperty]
    private int commitsFound;

    [ObservableProperty]
    private string status = "Idle";

    [ObservableProperty]
    private bool isMining;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string backfillOrder = "OldestFirst";

    public string BackfillOrderLabel => $"Backfill Order: {GetBackfillOrderDisplay(BackfillOrder)}";

    /// <summary>
    /// Event fired when mining completes successfully.
    /// Allows coordinators (like SessionDetailViewModel) to refresh day browser.
    /// </summary>
    public event EventHandler? MiningCompleted;

    private const string LastMinedSinceKey = "last_mined_since";
    private const string LastMinedUntilKey = "last_mined_until";
    private bool _backfillOrderLoaded;

    public MiningViewModel(
        MiningService miningService,
        DatabaseService databaseService,
        SettingsService settingsService,
        SessionContextService sessionContext,
        LoggerService logger)
    {
        _miningService = miningService;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _sessionContext = sessionContext;
        _logger = logger;
    }

    private async Task RunMiningAsync(int days)
    {
        if (IsMining) return;

        // Get current session from context
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            IsMining = false; // FIX: Reset state before returning
            return;
        }

        IsMining = true;
        Progress = 0;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            var options = await GetSessionOptionsAsync(session);
            var authorFilters = GetAuthorFilters(session);

            var until = DateTime.Today;
            var since = until.AddDays(-(days - 1));

            var result = await MineRangeInternalAsync(
                session,
                since,
                until,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token,
                preferGaps: false);

            DaysMined = result.DaysMined;
            CommitsFound = result.TotalCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                await SaveMiningCheckpointAsync(session.Id, since, until);
                Progress = 100;
                MiningCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Error: {result.ErrorMessage}";

                // Show detailed error message box
                System.Windows.MessageBox.Show(
                    $"Mining failed:\n\n{result.ErrorMessage}\n\n" +
                    $"Full Details:\n{result.ErrorDetails ?? "No additional details"}",
                    "Mining Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Status = $"Unexpected error: {ex.Message}";

            // Show unexpected error with full stack trace
            System.Windows.MessageBox.Show(
                $"Unexpected mining error:\n\n{ex.Message}\n\n" +
                $"Type: {ex.GetType().Name}\n\n" +
                $"Stack Trace:\n{ex.StackTrace}",
                "Unexpected Mining Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsMining = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public async Task MineInitialWindowAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        if (session.RangeStart.HasValue && session.RangeEnd.HasValue)
        {
            await RunMiningRangeAsync(session.RangeStart.Value.Date, session.RangeEnd.Value.Date, preferGaps: false);
            return;
        }

        SessionOptions options;
        try
        {
            options = string.IsNullOrEmpty(session.OptionsJson)
                ? new SessionOptions()
                : JsonSerializer.Deserialize<SessionOptions>(session.OptionsJson) ?? new SessionOptions();
        }
        catch (JsonException ex)
        {
            Status = $"Invalid session options JSON: {ex.Message}. Using defaults.";
            options = new SessionOptions();
        }

        var windowDays = options.WindowSizeDays > 0 ? options.WindowSizeDays : 14;
        await RunMiningAsync(windowDays);
    }

    public async Task RunMiningRangeAsync(DateTime since, DateTime until, bool preferGaps = false)
    {
        if (IsMining) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            IsMining = false;
            return;
        }

        IsMining = true;
        Progress = 0;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var options = await GetSessionOptionsAsync(session);
            var authorFilters = GetAuthorFilters(session);

            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            Status = $"Mining {since:yyyy-MM-dd} to {until:yyyy-MM-dd}...";
            var result = await MineRangeInternalAsync(
                session,
                since,
                until,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token,
                preferGaps);

            DaysMined = result.DaysMined;
            CommitsFound = result.TotalCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                await SaveMiningCheckpointAsync(session.Id, since, until);
                Progress = 100;
                MiningCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Error: {result.ErrorMessage}";
                System.Windows.MessageBox.Show(
                    $"Mining failed:\n\n{result.ErrorMessage}\n\n" +
                    $"Full Details:\n{result.ErrorDetails ?? "No additional details"}",
                    "Mining Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Status = $"Unexpected error: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Unexpected mining error:\n\n{ex.Message}\n\n" +
                $"Type: {ex.GetType().Name}\n\n" +
                $"Stack Trace:\n{ex.StackTrace}",
                "Unexpected Mining Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsMining = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task ReMineAsync()
    {
        if (IsMining) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        var rangeStart = session.RangeStart?.Date;
        var rangeEnd = session.RangeEnd?.Date;

        var scopeLabel = rangeStart.HasValue && rangeEnd.HasValue
            ? $"{rangeStart:yyyy-MM-dd} to {rangeEnd:yyyy-MM-dd}"
            : "ALL HISTORY";

        IsMining = true;
        Progress = 0;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            Status = $"Re-mining {scopeLabel}...";
            _logger.LogInfo($"[Mining] Re-mine start: session={session.Id} scope={scopeLabel}");

            await _databaseService.DeleteDaySummariesAsync(session.Id, rangeStart, rangeEnd);
            await _databaseService.DeleteDaysAsync(session.Id, rangeStart, rangeEnd);
            await _databaseService.DeleteCommitsAsync(session.Id, rangeStart, rangeEnd);

            var options = await GetSessionOptionsAsync(session);
            var authorFilters = GetAuthorFilters(session);

            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            var result = await RunMiningInBackgroundAsync(() => _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                rangeStart,
                rangeEnd,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token));

            DaysMined = result.DaysMined;
            CommitsFound = result.TotalCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                if (rangeStart.HasValue && rangeEnd.HasValue)
                    await SaveMiningCheckpointAsync(session.Id, rangeStart.Value, rangeEnd.Value);

                Progress = 100;
                MiningCompleted?.Invoke(this, EventArgs.Empty);
                _logger.LogInfo($"[Mining] Re-mine success: days={result.DaysMined} commits={result.StoredCommits}");
            }
            else
            {
                Status = $"Error: {result.ErrorMessage}";
                _logger.LogWarning($"[Mining] Re-mine failed: {result.ErrorMessage}");

                System.Windows.MessageBox.Show(
                    $"Mining failed:\n\n{result.ErrorMessage}\n\n" +
                    $"Full Details:\n{result.ErrorDetails ?? "No additional details"}",
                    "Mining Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Status = $"Unexpected error: {ex.Message}";
            _logger.LogError("[Mining] Re-mine unexpected error", ex);

            System.Windows.MessageBox.Show(
                $"Unexpected mining error:\n\n{ex.Message}\n\n" +
                $"Type: {ex.GetType().Name}\n\n" +
                $"Stack Trace:\n{ex.StackTrace}",
                "Unexpected Mining Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsMining = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _logger.LogInfo("[Mining] Re-mine end");
        }
    }

    [RelayCommand]
    private async Task ReMineKeepSummariesAsync()
    {
        if (IsMining) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        var rangeStart = session.RangeStart?.Date;
        var rangeEnd = session.RangeEnd?.Date;

        var scopeLabel = rangeStart.HasValue && rangeEnd.HasValue
            ? $"{rangeStart:yyyy-MM-dd} to {rangeEnd:yyyy-MM-dd}"
            : "ALL HISTORY";

        IsMining = true;
        Progress = 0;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            Status = $"Re-mining {scopeLabel} (keeping summaries)...";
            _logger.LogInfo($"[Mining] Re-mine (keep summaries) start: session={session.Id} scope={scopeLabel}");

            var existingDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            var scopedExisting = existingDays
                .Where(d => (!rangeStart.HasValue || d.Date.Date >= rangeStart.Value)
                            && (!rangeEnd.HasValue || d.Date.Date <= rangeEnd.Value))
                .ToList();

            var previousByDate = scopedExisting.ToDictionary(
                d => d.Date.Date,
                d => (d.CommitCount, d.Additions, d.Deletions, d.Status));

            var summaryDays = await _databaseService.GetDaySummaryDaysAsync(session.Id, rangeStart, rangeEnd);
            var summaryDaySet = new HashSet<DateTime>(summaryDays.Select(d => d.Date));
            _logger.LogInfo($"[Mining] Re-mine (keep summaries) summary days in scope: {summaryDaySet.Count}");

            var options = await GetSessionOptionsAsync(session);
            var authorFilters = GetAuthorFilters(session);

            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            var result = await RunMiningInBackgroundAsync(() => _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                rangeStart,
                rangeEnd,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token));

            var downgraded = 0;
            if (result.Success)
            {
                var afterDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
                var scopedAfter = afterDays
                    .Where(d => (!rangeStart.HasValue || d.Date.Date >= rangeStart.Value)
                                && (!rangeEnd.HasValue || d.Date.Date <= rangeEnd.Value))
                    .ToList();

                foreach (var day in scopedAfter)
                {
                    var date = day.Date.Date;
                    if (!summaryDaySet.Contains(date))
                        continue;

                    var previousExists = previousByDate.TryGetValue(date, out var previous);
                    var changed = !previousExists ||
                                  previous.CommitCount != day.CommitCount ||
                                  previous.Additions != day.Additions ||
                                  previous.Deletions != day.Deletions;

                    if (changed)
                    {
                        if (day.Status != DayStatus.Mined)
                        {
                            day.Status = DayStatus.Mined;
                            downgraded++;
                            await _databaseService.UpsertDayAsync(day);
                        }
                    }
                    else
                    {
                        if (previous.Status == DayStatus.Approved || previous.Status == DayStatus.Summarized)
                        {
                            if (day.Status != previous.Status)
                            {
                                day.Status = previous.Status;
                                await _databaseService.UpsertDayAsync(day);
                            }
                        }
                    }
                }
            }

            DaysMined = result.DaysMined;
            CommitsFound = result.TotalCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                if (rangeStart.HasValue && rangeEnd.HasValue)
                    await SaveMiningCheckpointAsync(session.Id, rangeStart.Value, rangeEnd.Value);

                Progress = 100;
                MiningCompleted?.Invoke(this, EventArgs.Empty);
                _logger.LogInfo($"[Mining] Re-mine (keep summaries) success: days={result.DaysMined} commits={result.StoredCommits} downgraded={downgraded}");
            }
            else
            {
                Status = $"Error: {result.ErrorMessage}";
                _logger.LogWarning($"[Mining] Re-mine (keep summaries) failed: {result.ErrorMessage}");

                System.Windows.MessageBox.Show(
                    $"Mining failed:\n\n{result.ErrorMessage}\n\n" +
                    $"Full Details:\n{result.ErrorDetails ?? "No additional details"}",
                    "Mining Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Status = $"Unexpected error: {ex.Message}";
            _logger.LogError("[Mining] Re-mine (keep summaries) unexpected error", ex);

            System.Windows.MessageBox.Show(
                $"Unexpected mining error:\n\n{ex.Message}\n\n" +
                $"Type: {ex.GetType().Name}\n\n" +
                $"Stack Trace:\n{ex.StackTrace}",
                "Unexpected Mining Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsMining = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _logger.LogInfo("[Mining] Re-mine (keep summaries) end");
        }
    }

    [RelayCommand]
    private async Task MineLastDaysAsync(int days = 14)
    {
        await RunMiningAsync(days);
    }


    [RelayCommand]
    private void Stop()
    {
        _cancellationTokenSource?.Cancel();
        Status = "Stopping...";
    }

    [RelayCommand]
    private async Task MineLast7DaysAsync()
    {
        await RunMiningAsync(7);
    }

    [RelayCommand]
    private async Task MineLast30DaysAsync()
    {
        await RunMiningAsync(30);
    }


    private async Task SaveMiningCheckpointAsync(int sessionId, DateTime since, DateTime until)
    {
        var now = DateTime.UtcNow;
        await _databaseService.UpsertCheckpointAsync(new Checkpoint
        {
            SessionId = sessionId,
            Phase = CheckpointPhase.Mine,
            CursorKey = LastMinedSinceKey,
            CursorValue = since.ToString("yyyy-MM-dd"),
            UpdatedAt = now
        });

        await _databaseService.UpsertCheckpointAsync(new Checkpoint
        {
            SessionId = sessionId,
            Phase = CheckpointPhase.Mine,
            CursorKey = LastMinedUntilKey,
            CursorValue = until.ToString("yyyy-MM-dd"),
            UpdatedAt = now
        });
    }

    private async Task<SessionOptions> GetSessionOptionsAsync(Session session)
    {
        try
        {
            var defaults = await _settingsService.GetDefaultSessionOptionsAsync();

            if (string.IsNullOrWhiteSpace(session.OptionsJson) || session.OptionsJson.Trim() == "{}")
                return defaults;

            var sessionOptions = JsonSerializer.Deserialize<SessionOptions>(session.OptionsJson) ?? new SessionOptions();

            sessionOptions.WindowSizeDays = sessionOptions.WindowSizeDays > 0 ? sessionOptions.WindowSizeDays : defaults.WindowSizeDays;
            sessionOptions.MaxBulletsPerDay = sessionOptions.MaxBulletsPerDay > 0 ? sessionOptions.MaxBulletsPerDay : defaults.MaxBulletsPerDay;
            sessionOptions.BackfillOrder = string.IsNullOrWhiteSpace(sessionOptions.BackfillOrder) ? defaults.BackfillOrder : sessionOptions.BackfillOrder;

            return sessionOptions;
        }
        catch
        {
            return new SessionOptions();
        }
    }

    private List<AuthorFilter> GetAuthorFilters(Session session)
    {
        try
        {
            return string.IsNullOrEmpty(session.AuthorFiltersJson)
                ? new List<AuthorFilter>()
                : JsonSerializer.Deserialize<List<AuthorFilter>>(session.AuthorFiltersJson) ?? new List<AuthorFilter>();
        }
        catch
        {
            return new List<AuthorFilter>();
        }
    }

    private async Task<MiningResult> MineRangeInternalAsync(
        Session session,
        DateTime since,
        DateTime until,
        SessionOptions options,
        List<AuthorFilter> authorFilters,
        IProgress<MiningProgress> progressReporter,
        CancellationToken cancellationToken,
        bool preferGaps)
    {
        if (preferGaps && options.FillGapsFirst)
        {
            var gapWindow = await TryGetGapWindowAsync(session, options);
            if (gapWindow.HasValue)
            {
                var (gapStart, gapEnd) = gapWindow.Value;
                Status = $"Mining gap {gapStart:yyyy-MM-dd} to {gapEnd:yyyy-MM-dd}...";
                return await RunMiningInBackgroundAsync(() => _miningService.MineCommitsAsync(
                    session.Id,
                    session.RepoPath,
                    gapStart,
                    gapEnd,
                    options,
                    authorFilters,
                    progressReporter,
                    cancellationToken));
            }
        }

        var totalDays = (until.Date - since.Date).Days + 1;
        var windowDays = options.WindowSizeDays > 0 ? options.WindowSizeDays : 14;

        if (totalDays <= windowDays)
        {
            return await RunMiningInBackgroundAsync(() => _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                since,
                until,
                options,
                authorFilters,
                progressReporter,
                cancellationToken));
        }

        var totalWindows = (int)Math.Ceiling(totalDays / (double)windowDays);
        var windowIndex = 0;
        var totalResult = new MiningResult { Success = true };
        var windowStart = since.Date;
        var overlapDays = Math.Max(0, options.OverlapDays);

        while (windowStart <= until.Date)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                totalResult.Success = false;
                totalResult.ErrorMessage = "Operation cancelled";
                break;
            }

            windowIndex++;
            var windowEnd = windowStart.AddDays(windowDays - 1);
            if (windowEnd > until.Date)
                windowEnd = until.Date;

            Status = $"Mining {windowStart:yyyy-MM-dd} to {windowEnd:yyyy-MM-dd} ({windowIndex}/{totalWindows})...";

            var result = await RunMiningInBackgroundAsync(() => _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                windowStart,
                windowEnd,
                options,
                authorFilters,
                progressReporter,
                cancellationToken));

            if (!result.Success)
            {
                totalResult.Success = false;
                totalResult.ErrorMessage = result.ErrorMessage;
                totalResult.ErrorDetails = result.ErrorDetails;
                break;
            }

            totalResult.DaysMined += result.DaysMined;
            totalResult.StoredCommits += result.StoredCommits;
            totalResult.TotalCommits += result.TotalCommits;

            Progress = (windowIndex / (double)totalWindows) * 100;
            await SaveMiningCheckpointAsync(session.Id, windowStart, windowEnd);

            windowStart = windowEnd.AddDays(1 - overlapDays);
        }

        return totalResult;
    }

    private static Task<MiningResult> RunMiningInBackgroundAsync(Func<Task<MiningResult>> action)
    {
        return Task.Run(action);
    }

    private async Task<(DateTime start, DateTime end)?> TryGetGapWindowAsync(Session session, SessionOptions options)
    {
        var days = (await _databaseService.GetDaysAsync(session.Id)).ToList();
        if (days.Count < 2)
            return null;

        var dates = days.Select(d => d.Date.Date).Distinct().OrderBy(d => d).ToList();
        if (dates.Count < 2)
            return null;

        var gapStarts = new List<DateTime>();
        for (var i = 0; i < dates.Count - 1; i++)
        {
            var expectedNext = dates[i].AddDays(1);
            if (dates[i + 1] > expectedNext)
            {
                gapStarts.Add(expectedNext);
            }
        }

        if (gapStarts.Count == 0)
            return null;

        var windowDays = options.WindowSizeDays > 0 ? options.WindowSizeDays : 14;
        DateTime selectedGapStart;

        switch (options.BackfillOrder)
        {
            case "NewestFirst":
                selectedGapStart = gapStarts.Max();
                break;
            case "OldestFirst":
            case "GappedFirst":
            default:
                selectedGapStart = gapStarts.Min();
                break;
        }

        var gapEnd = selectedGapStart.AddDays(windowDays - 1);
        return (selectedGapStart, gapEnd);
    }

    public async Task LoadBackfillOrderFromSessionAsync(Session session)
    {
        if (_backfillOrderLoaded)
            return;

        var options = await GetSessionOptionsAsync(session);
        BackfillOrder = string.IsNullOrWhiteSpace(options.BackfillOrder) ? "OldestFirst" : options.BackfillOrder;
        OnPropertyChanged(nameof(BackfillOrderLabel));
        _backfillOrderLoaded = true;
    }

    partial void OnBackfillOrderChanged(string value)
    {
        OnPropertyChanged(nameof(BackfillOrderLabel));
    }

    private static string GetBackfillOrderDisplay(string order) =>
        order switch
        {
            "NewestFirst" => "Newest first",
            "GappedFirst" => "Fill gaps first",
            _ => "Oldest first"
        };
}
