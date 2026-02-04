using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private int totalSessions;

    [ObservableProperty]
    private string welcomeMessage = "Welcome to DevChronicle";

    public DashboardViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        LoadDashboardDataAsync().ConfigureAwait(false);
    }

    private async Task LoadDashboardDataAsync()
    {
        var sessions = await _databaseService.GetAllSessionsAsync();
        TotalSessions = sessions.Count();
    }

    [RelayCommand]
    private void CreateNewSession()
    {
        // TODO: Navigate to sessions page and trigger create
    }
}
