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

    public MiningViewModel(MiningService miningService, DatabaseService databaseService)
    {
        _miningService = miningService;
        _databaseService = databaseService;
    }

    [RelayCommand]
    private async Task MineLastDaysAsync(int days = 14)
    {
        if (IsMining) return;

        // TODO: Get current session from app state
        var sessions = await _databaseService.GetAllSessionsAsync();
        var session = sessions.FirstOrDefault();
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        IsMining = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var options = JsonSerializer.Deserialize<SessionOptions>(session.OptionsJson) ?? new SessionOptions();
            var authorFilters = JsonSerializer.Deserialize<List<AuthorFilter>>(session.AuthorFiltersJson) ?? new List<AuthorFilter>();

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
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.";
            else
                Status = $"Error: {result.ErrorMessage}";
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
}
