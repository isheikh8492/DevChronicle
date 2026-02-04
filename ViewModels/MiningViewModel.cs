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

    /// <summary>
    /// Event fired when mining completes successfully.
    /// Allows coordinators (like SessionDetailViewModel) to refresh day browser.
    /// </summary>
    public event EventHandler? MiningCompleted;

    public MiningViewModel(
        MiningService miningService,
        DatabaseService databaseService,
        SessionContextService sessionContext)
    {
        _miningService = miningService;
        _databaseService = databaseService;
        _sessionContext = sessionContext;
    }

    [RelayCommand]
    private async Task MineLastDaysAsync(int days = 14)
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

            var result = await _miningService.MineCommitsAsync(
                session.Id,
                session.RepoPath,
                DateTime.Now.AddDays(-days),
                DateTime.Now,
                options,
                authorFilters,
                progressReporter,
                _cancellationTokenSource.Token);

            DaysMined = result.DaysMined;
            CommitsFound = result.StoredCommits;

            if (result.Success)
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
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

    [RelayCommand]
    private void Stop()
    {
        _cancellationTokenSource?.Cancel();
        Status = "Stopping...";
    }

    [RelayCommand]
    private async Task MineLast7DaysAsync()
    {
        await MineLastDaysAsync(7);
    }

    [RelayCommand]
    private async Task MineLast30DaysAsync()
    {
        await MineLastDaysAsync(30);
    }
}
