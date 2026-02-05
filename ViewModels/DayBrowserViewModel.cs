using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

/// <summary>
/// Wrapper class for Day model with UI-specific properties.
/// </summary>
public partial class DayViewModel : ObservableObject
{
    public Models.Day Day { get; }

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private DayStatus status;

    public DayViewModel(Models.Day day)
    {
        Day = day;
        status = day.Status;
    }

    public int SessionId => Day.SessionId;
    public DateTime Date => Day.Date;
    public int CommitCount => Day.CommitCount;
    public int Additions => Day.Additions;
    public int Deletions => Day.Deletions;
    public void SetStatus(DayStatus newStatus)
    {
        Day.Status = newStatus;
        Status = newStatus;
    }
}

/// <summary>
/// ViewModel for the Day Browser section that displays all days in the session.
/// </summary>
public partial class DayBrowserViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly SessionContextService _sessionContext;

    [ObservableProperty]
    private ObservableCollection<DayViewModel> days = new();

    [ObservableProperty]
    private DayViewModel? selectedDay;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string filterStatus = "All";

    public DayBrowserViewModel(
        DatabaseService databaseService,
        SessionContextService sessionContext)
    {
        _databaseService = databaseService;
        _sessionContext = sessionContext;
    }

    /// <summary>
    /// Loads all days for the current session.
    /// </summary>
    public async Task LoadDaysAsync(int sessionId)
    {
        IsLoading = true;

        try
        {
            var daysList = (await _databaseService.GetDaysAsync(sessionId))
                .OrderByDescending(d => d.Date)
                .ToList();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Days.Clear();
                foreach (var day in daysList)
                {
                    Days.Add(new DayViewModel(day));
                }

                // Auto-select the first (most recent) day
                if (Days.Count > 0)
                {
                    var firstDay = Days[0];
                    firstDay.IsExpanded = true;
                    SelectedDay = firstDay;
                }
            });
        }
        catch (Exception ex)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    $"Failed to load days: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Refreshes the day list (called after mining completes).
    /// </summary>
    public async Task RefreshAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null) return;

        await LoadDaysAsync(session.Id);
    }

    /// <summary>
    /// Updates a day's status (called after summarization).
    /// </summary>
    public void UpdateDayStatus(DateTime date, DayStatus newStatus)
    {
        var dayVM = Days.FirstOrDefault(d => d.Date.Date == date.Date);
        if (dayVM != null)
        {
            if (System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                dayVM.SetStatus(newStatus);
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => dayVM.SetStatus(newStatus));
            }
        }
    }

    [RelayCommand]
    private void SelectDay(DayViewModel day)
    {
        // Collapse previously selected day
        if (SelectedDay != null && SelectedDay != day)
        {
            SelectedDay.IsExpanded = false;
        }

        // Toggle selected day
        day.IsExpanded = !day.IsExpanded;
        SelectedDay = day.IsExpanded ? day : null;
    }

    [RelayCommand]
    private async Task ApproveDayAsync(DayViewModel day)
    {
        try
        {
            day.Day.Status = DayStatus.Approved;
            await _databaseService.UpsertDayAsync(day.Day);

            System.Windows.MessageBox.Show(
                $"Day {day.Date:yyyy-MM-dd} approved!",
                "Success",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            OnPropertyChanged(nameof(Days)); // Trigger UI update
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to approve day: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void FilterByStatus(string status)
    {
        FilterStatus = status;
        // TODO: Implement filtering logic
        // For now, this is a placeholder
    }
}
