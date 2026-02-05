using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class SettingsService
{
    public const string MiningWindowSizeDaysKey = "mining.window_size_days";
    public const string MiningOverlapDaysKey = "mining.overlap_days";
    public const string MiningFillGapsFirstKey = "mining.fill_gaps_first";
    public const string MiningBackfillOrderKey = "mining.backfill_order";
    public const string IncludeMergesKey = "mining.include_merges";
    public const string IncludeDiffsKey = "summarization.include_diffs";
    public const string MaxBulletsPerDayKey = "summarization.max_bullets_per_day";
    public const string SummarizationMasterPromptKey = "summarization.master_prompt";
    public const string OpenAiApiKeyKey = "openai.api_key";

    private readonly DatabaseService _databaseService;

    public SettingsService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue)
    {
        var raw = await _databaseService.GetAppSettingAsync(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        try
        {
            return JsonSerializer.Deserialize<T>(raw) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var raw = JsonSerializer.Serialize(value);
        await _databaseService.UpsertAppSettingAsync(key, raw);
    }

    public async Task<SessionOptions> GetDefaultSessionOptionsAsync()
    {
        var options = new SessionOptions
        {
            IncludeMerges = await GetAsync(IncludeMergesKey, false),
            IncludeDiffs = await GetAsync(IncludeDiffsKey, false),
            WindowSizeDays = await GetAsync(MiningWindowSizeDaysKey, 14),
            MaxBulletsPerDay = await GetAsync(MaxBulletsPerDayKey, 6),
            BackfillOrder = await GetAsync(MiningBackfillOrderKey, "OldestFirst"),
            OverlapDays = await GetAsync(MiningOverlapDaysKey, 1),
            FillGapsFirst = await GetAsync(MiningFillGapsFirstKey, false),
            RefScope = RefScope.LocalBranchesOnly,
            TrackIntegrations = true
        };

        return options;
    }
}
