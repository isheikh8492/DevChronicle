using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private bool includeMerges;

    [ObservableProperty]
    private bool includeDiffs;

    [ObservableProperty]
    private int windowSizeDays;

    [ObservableProperty]
    private int maxBulletsPerDay;

    [ObservableProperty]
    private int overlapDays;

    [ObservableProperty]
    private bool fillGapsFirst;

    [ObservableProperty]
    private string backfillOrder = "OldestFirst";

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IncludeMerges = await _settingsService.GetAsync(SettingsService.IncludeMergesKey, false);
        IncludeDiffs = await _settingsService.GetAsync(SettingsService.IncludeDiffsKey, false);
        WindowSizeDays = await _settingsService.GetAsync(SettingsService.MiningWindowSizeDaysKey, 14);
        MaxBulletsPerDay = await _settingsService.GetAsync(SettingsService.MaxBulletsPerDayKey, 6);
        OverlapDays = await _settingsService.GetAsync(SettingsService.MiningOverlapDaysKey, 1);
        FillGapsFirst = await _settingsService.GetAsync(SettingsService.MiningFillGapsFirstKey, false);
        BackfillOrder = await _settingsService.GetAsync(SettingsService.MiningBackfillOrderKey, "OldestFirst");
    }

    partial void OnIncludeMergesChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.IncludeMergesKey, value);
    partial void OnIncludeDiffsChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.IncludeDiffsKey, value);
    partial void OnWindowSizeDaysChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MiningWindowSizeDaysKey, value);
    partial void OnMaxBulletsPerDayChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MaxBulletsPerDayKey, value);
    partial void OnOverlapDaysChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MiningOverlapDaysKey, value);
    partial void OnFillGapsFirstChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.MiningFillGapsFirstKey, value);
    partial void OnBackfillOrderChanged(string value) => _ = _settingsService.SetAsync(SettingsService.MiningBackfillOrderKey, value);
}
