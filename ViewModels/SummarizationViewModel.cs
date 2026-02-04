using System.Collections.ObjectModel;
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
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private int daysSummarized;

    [ObservableProperty]
    private int pendingDays;

    [ObservableProperty]
    private string status = "Idle";

    [ObservableProperty]
    private bool isSummarizing;

    [ObservableProperty]
    private double progress;

    /// <summary>
    /// Event fired when a day is summarized.
    /// Allows coordinators to update day browser status badges.
    /// </summary>
    public event EventHandler<DateTime>? DaySummarized;

    public SummarizationViewModel(
        SummarizationService summarizationService,
        DatabaseService databaseService,
        SessionContextService sessionContext)
    {
        _summarizationService = summarizationService;
        _databaseService = databaseService;
        _sessionContext = sessionContext;
    }

    [RelayCommand]
    private async Task SummarizePendingDaysAsync()
    {
        if (IsSummarizing) return;

        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        IsSummarizing = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            // Get all days with status "mined" (pending summarization)
            var allDays = (await _databaseService.GetDaysAsync(session.Id)).ToList();
            var pendingDaysList = allDays.Where(d => d.Status == DayStatus.Mined).ToList();

            if (pendingDaysList.Count == 0)
            {
                Status = "No pending days to summarize";
                return;
            }

            PendingDays = pendingDaysList.Count;
            var processed = 0;

            foreach (var day in pendingDaysList)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                Status = $"Summarizing day {day.Date:yyyy-MM-dd} ({processed + 1}/{PendingDays})...";

                var result = await _summarizationService.SummarizeDayAsync(
                    session.Id,
                    day.Date,
                    maxBullets: 6,
                    cancellationToken: _cancellationTokenSource.Token);

                if (result.Success)
                {
                    // Update day status in database
                    day.Status = DayStatus.Summarized;
                    await _databaseService.UpsertDayAsync(day);

                    processed++;
                    DaysSummarized++;
                    PendingDays--;
                    Progress = (processed / (double)pendingDaysList.Count) * 100;

                    // Notify listeners that this day was summarized
                    DaySummarized?.Invoke(this, day.Date);
                }
                else
                {
                    Status = $"Error summarizing {day.Date:yyyy-MM-dd}: {result.ErrorMessage}";
                    await Task.Delay(1000); // Brief pause before continuing
                }
            }

            Status = $"Complete! Summarized {processed} days.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
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
            Status = "No session selected";
            return;
        }

        if (day == null)
        {
            Status = "No day selected";
            return;
        }

        IsSummarizing = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            Status = $"Summarizing day {day.Date:yyyy-MM-dd}...";

            var result = await _summarizationService.SummarizeDayAsync(
                session.Id,
                day.Date,
                maxBullets: 6,
                cancellationToken: _cancellationTokenSource.Token);

            if (result.Success)
            {
                day.Day.Status = DayStatus.Summarized;
                await _databaseService.UpsertDayAsync(day.Day);

                DaysSummarized++;
                DaySummarized?.Invoke(this, day.Date);
                Status = $"Complete! Summarized {day.Date:yyyy-MM-dd}.";
            }
            else
            {
                Status = $"Error summarizing {day.Date:yyyy-MM-dd}: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
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
        Status = "Stopping...";
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
}
