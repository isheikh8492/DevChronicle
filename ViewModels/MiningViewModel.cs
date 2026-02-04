using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

public partial class MiningViewModel : ObservableObject
{
    private readonly MiningService _miningService;
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;
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
        SessionContextService sessionContext)
    {
        _miningService = miningService;
        _databaseService = databaseService;
        _sessionContext = sessionContext;
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
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            // Defensive JSON parsing with error handling
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

            List<AuthorFilter> authorFilters;
            try
            {
                authorFilters = string.IsNullOrEmpty(session.AuthorFiltersJson)
                    ? new List<AuthorFilter>()
                    : JsonSerializer.Deserialize<List<AuthorFilter>>(session.AuthorFiltersJson) ?? new List<AuthorFilter>();
            }
            catch (JsonException ex)
            {
                Status = $"Invalid author filters JSON: {ex.Message}. Using defaults.";
                authorFilters = new List<AuthorFilter>();
            }

            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            var since = DateTime.Today.AddDays(-(days - 1));
            var until = DateTime.Today;

            var result = await _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                since,
                until,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token);

            DaysMined = result.DaysMined;
            CommitsFound = result.StoredCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                await SaveMiningCheckpointAsync(session.Id, since, until);
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

        LoadBackfillOrderFromSession(session);

        if (session.RangeStart.HasValue && session.RangeEnd.HasValue)
        {
            await RunMiningRangeAsync(session.RangeStart.Value.Date, session.RangeEnd.Value.Date);
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

    public async Task RunMiningRangeAsync(DateTime since, DateTime until)
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
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
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

            List<AuthorFilter> authorFilters;
            try
            {
                authorFilters = string.IsNullOrEmpty(session.AuthorFiltersJson)
                    ? new List<AuthorFilter>()
                    : JsonSerializer.Deserialize<List<AuthorFilter>>(session.AuthorFiltersJson) ?? new List<AuthorFilter>();
            }
            catch (JsonException ex)
            {
                Status = $"Invalid author filters JSON: {ex.Message}. Using defaults.";
                authorFilters = new List<AuthorFilter>();
            }

            var progressReporter = new Progress<MiningProgress>(p =>
            {
                Status = p.Status;
            });

            Status = $"Mining {since:yyyy-MM-dd} to {until:yyyy-MM-dd}...";
            var result = await _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                since,
                until,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token);

            DaysMined = result.DaysMined;
            CommitsFound = result.StoredCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
                await SaveMiningCheckpointAsync(session.Id, since, until);
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
    private async Task MineLastDaysAsync(int days = 14)
    {
        await RunMiningAsync(days);
    }

    [RelayCommand]
    private async Task MineLastWindowAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        LoadBackfillOrderFromSession(session);

        if (session.RangeStart.HasValue && session.RangeEnd.HasValue)
        {
            Status = "Session uses a fixed range. Backfill not available.";
            return;
        }

        var windowDays = GetWindowSize(session);
        var until = DateTime.Today;
        var since = until.AddDays(-(windowDays - 1));
        await RunMiningRangeAsync(since, until);
    }

    [RelayCommand]
    private async Task BackfillPreviousWindowAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        LoadBackfillOrderFromSession(session);

        if (session.RangeStart.HasValue && session.RangeEnd.HasValue)
        {
            Status = "Session uses a fixed range. Backfill not available.";
            return;
        }

        var windowDays = GetWindowSize(session);
        var days = (await _databaseService.GetDaysAsync(session.Id)).ToList();
        if (days.Count == 0)
        {
            await RunMiningAsync(windowDays);
            return;
        }

        var oldestDay = days.Min(d => d.Date.Date);
        var until = oldestDay;
        var since = until.AddDays(-(windowDays - 1));

        await RunMiningRangeAsync(since, until);
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

    [RelayCommand]
    private async Task BackfillOrderAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        LoadBackfillOrderFromSession(session);

        BackfillOrder = BackfillOrder switch
        {
            "OldestFirst" => "NewestFirst",
            "NewestFirst" => "GappedFirst",
            _ => "OldestFirst"
        };

        var options = GetSessionOptions(session);
        options.BackfillOrder = BackfillOrder;
        session.OptionsJson = JsonSerializer.Serialize(options);
        await _databaseService.UpdateSessionOptionsAsync(session.Id, session.OptionsJson);

        Status = $"Backfill order set to {GetBackfillOrderDisplay(BackfillOrder)}.";
    }

    private int GetWindowSize(Session session)
    {
        var options = GetSessionOptions(session);
        return options.WindowSizeDays > 0 ? options.WindowSizeDays : 14;
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

    private SessionOptions GetSessionOptions(Session session)
    {
        try
        {
            return string.IsNullOrEmpty(session.OptionsJson)
                ? new SessionOptions()
                : JsonSerializer.Deserialize<SessionOptions>(session.OptionsJson) ?? new SessionOptions();
        }
        catch
        {
            return new SessionOptions();
        }
    }

    public void LoadBackfillOrderFromSession(Session session)
    {
        if (_backfillOrderLoaded)
            return;

        var options = GetSessionOptions(session);
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
