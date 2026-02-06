using System.Threading.Tasks;
using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private string displayedApiKey = string.Empty;

    [ObservableProperty]
    private bool isApiKeyVisible;

    [ObservableProperty]
    private bool isEditingApiKey;

    [ObservableProperty]
    private string masterPromptText = string.Empty;

    [ObservableProperty]
    private string defaultExportDirectory = string.Empty;

    private string _actualApiKey = string.Empty;

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
        _actualApiKey = await _settingsService.GetAsync(SettingsService.OpenAiApiKeyKey, string.Empty);
        MasterPromptText = await _settingsService.GetAsync(SettingsService.SummarizationMasterPromptKey, string.Empty);
        var fallbackExportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DevChronicleExports");
        DefaultExportDirectory = await _settingsService.GetAsync(SettingsService.ExportDefaultDirectoryKey, fallbackExportDir);
        UpdateDisplayedApiKey();
    }

    [RelayCommand]
    private void ToggleApiKeyVisibility()
    {
        IsApiKeyVisible = !IsApiKeyVisible;
        UpdateDisplayedApiKey();
    }

    private void UpdateDisplayedApiKey()
    {
        if (IsEditingApiKey)
        {
            // Don't update while editing
            return;
        }

        DisplayedApiKey = IsApiKeyVisible ? _actualApiKey : new string('*', _actualApiKey.Length);
    }

    public void StartEditingApiKey()
    {
        IsEditingApiKey = true;
        DisplayedApiKey = string.Empty;
    }

    public void UpdateApiKeyInput(string input)
    {
        if (IsEditingApiKey)
        {
            DisplayedApiKey = input;
        }
    }

    partial void OnIncludeMergesChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.IncludeMergesKey, value);
    partial void OnIncludeDiffsChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.IncludeDiffsKey, value);
    partial void OnWindowSizeDaysChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MiningWindowSizeDaysKey, value);
    partial void OnMaxBulletsPerDayChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MaxBulletsPerDayKey, value);
    partial void OnOverlapDaysChanged(int value) => _ = _settingsService.SetAsync(SettingsService.MiningOverlapDaysKey, value);
    partial void OnFillGapsFirstChanged(bool value) => _ = _settingsService.SetAsync(SettingsService.MiningFillGapsFirstKey, value);
    partial void OnBackfillOrderChanged(string value) => _ = _settingsService.SetAsync(SettingsService.MiningBackfillOrderKey, value);
    partial void OnDefaultExportDirectoryChanged(string value) => _ = _settingsService.SetAsync(SettingsService.ExportDefaultDirectoryKey, value ?? string.Empty);

    public async Task SaveApiKeyAsync(string newApiKey)
    {
        _actualApiKey = newApiKey;
        await _settingsService.SetAsync(SettingsService.OpenAiApiKeyKey, newApiKey);
        IsEditingApiKey = false;
        UpdateDisplayedApiKey();
    }

    public async Task SaveMasterPromptAsync()
    {
        await _settingsService.SetAsync(SettingsService.SummarizationMasterPromptKey, MasterPromptText ?? string.Empty);
    }
}
