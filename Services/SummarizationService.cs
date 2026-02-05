using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class SummarizationService
{
    private readonly DatabaseService _databaseService;
    private readonly ClusteringService _clusteringService;
    private readonly SettingsService _settingsService;
    private string _modelName = "gpt-4o-mini";
    private const string PromptVersion = "v1";
    private const string DefaultMasterPrompt =
        "You are a concise developer diary generator. Output ONLY bullet points that start with '- '.";
    private static readonly HttpClient Http = new HttpClient
    {
        BaseAddress = new Uri("https://api.openai.com/v1/")
    };
    private string? _apiKey;

    public SummarizationService(DatabaseService databaseService, ClusteringService clusteringService, SettingsService settingsService)
    {
        _databaseService = databaseService;
        _clusteringService = clusteringService;
        _settingsService = settingsService;
    }

    public void ConfigureOpenAI(string apiKey, string? modelName = null)
    {
        _apiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(modelName))
            _modelName = modelName;
    }

    public async Task<SummarizationResult> SummarizeDayAsync(
        int sessionId,
        DateTime day,
        int maxBullets = 6,
        CancellationToken cancellationToken = default)
    {
        var result = new SummarizationResult { Day = day };

        try
        {
            var commits = (await _databaseService.GetCommitsForDayAsync(sessionId, day)).ToList();

            var options = await GetSessionOptionsAsync(sessionId);
            if (!options.IncludeMerges)
                commits = commits.Where(c => !c.IsMerge).ToList();

            if (commits.Count == 0)
            {
                result.ErrorMessage = "No commits found for this day";
                return result;
            }

            // Compute input hash for caching
            var inputHash = ComputeInputHash(commits, day);

            // Check cache
            // TODO: Implement cache lookup in database

            // Cluster commits into work units
            var workUnits = _clusteringService.ClusterCommits(commits);

            // Build prompt
            var prompt = BuildPrompt(day, commits, workUnits, maxBullets);

            // Call OpenAI or generate offline summary
            string bulletsText;
            var apiKey = await GetApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "Missing OPENAI_API_KEY. Set it in your environment to enable AI summarization.";
                return result;
            }

            var masterPrompt = await GetMasterPromptAsync();
            bulletsText = await CallOpenAIAsync(masterPrompt, prompt, apiKey, cancellationToken);
            result.UsedAI = true;

            // Validate and clean bullets
            var bullets = ValidateBullets(bulletsText, maxBullets);
            result.Bullets = bullets;

            // Store in database
            var summary = new DaySummary
            {
                SessionId = sessionId,
                Day = day,
                BulletsText = string.Join("\n", bullets),
                Model = _modelName,
                PromptVersion = PromptVersion,
                InputHash = inputHash,
                CreatedAt = DateTime.UtcNow
            };

            await _databaseService.UpsertDaySummaryAsync(summary);

            // Update day status
            var dayRecord = (await _databaseService.GetDaysAsync(sessionId))
                .FirstOrDefault(d => d.Date.Date == day.Date);

            if (dayRecord != null)
            {
                dayRecord.Status = DayStatus.Summarized;
                await _databaseService.UpsertDayAsync(dayRecord);
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private string BuildPrompt(DateTime day, List<Commit> commits, List<WorkUnit> workUnits, int maxBullets)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Date: {day:yyyy-MM-dd}");
        sb.AppendLine($"Commits: {commits.Count}");
        sb.AppendLine($"Changes: +{commits.Sum(c => c.Additions)}/-{commits.Sum(c => c.Deletions)} lines");
        sb.AppendLine();
        sb.AppendLine("Work done:");
        sb.AppendLine();

        foreach (var unit in workUnits.Take(5))
        {
            sb.AppendLine(_clusteringService.GenerateWorkUnitSummary(unit));
        }

        sb.AppendLine();
        sb.AppendLine($"Generate {maxBullets} concise bullet points (max 10) summarizing this day's development work.");
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Each bullet must start with '- '");
        sb.AppendLine("- Focus on WHAT was done, not HOW");
        sb.AppendLine("- Be specific and evidence-based (reference actual commits/files)");
        sb.AppendLine("- Use active voice and past tense");
        sb.AppendLine("- DO NOT invent features or functionality not evident in commits");
        sb.AppendLine("- If unclear, be conservative in descriptions");
        sb.AppendLine();
        sb.AppendLine("Output only the bullet points, no other text:");

        return sb.ToString();
    }

    private async Task<string> CallOpenAIAsync(string systemPrompt, string prompt, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _modelName,
            messages = new[]
            {
                new { role = "developer", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            max_completion_tokens = 600,
            store = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {json}");

        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? string.Empty;
    }

    private string GenerateOfflineSummary(List<Commit> commits, List<WorkUnit> workUnits)
    {
        var bullets = new List<string>();

        foreach (var unit in workUnits.Take(6))
        {
            var commitCount = unit.Commits.Count;
            var topCommit = unit.Commits.First();

            bullets.Add($"- Worked on {unit.Folder} ({unit.Category}): {topCommit.Subject} " +
                       $"(+{unit.TotalAdditions}/-{unit.TotalDeletions} lines, {commitCount} commits)");
        }

        return string.Join("\n", bullets);
    }

    private List<string> ValidateBullets(string bulletsText, int maxBullets)
    {
        var lines = bulletsText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var bullets = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- "))
            {
                bullets.Add(trimmed);
                if (bullets.Count >= maxBullets)
                    break;
            }
        }

        return bullets;
    }

    private async Task<string?> GetApiKeyAsync()
    {
        var apiKey = _apiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = await _settingsService.GetAsync(SettingsService.OpenAiApiKeyKey, string.Empty);
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        }
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    private async Task<string> GetMasterPromptAsync()
    {
        var prompt = await _settingsService.GetAsync(SettingsService.SummarizationMasterPromptKey, DefaultMasterPrompt);
        return string.IsNullOrWhiteSpace(prompt) ? DefaultMasterPrompt : prompt;
    }

    private async Task<SessionOptions> GetSessionOptionsAsync(int sessionId)
    {
        try
        {
            var defaults = await _settingsService.GetDefaultSessionOptionsAsync();
            var session = await _databaseService.GetSessionAsync(sessionId);
            if (session == null || string.IsNullOrWhiteSpace(session.OptionsJson))
                return defaults;

            var options = JsonSerializer.Deserialize<SessionOptions>(session.OptionsJson) ?? new SessionOptions();
            options.IncludeMerges = options.IncludeMerges;
            options.IncludeDiffs = options.IncludeDiffs;
            options.WindowSizeDays = options.WindowSizeDays > 0 ? options.WindowSizeDays : defaults.WindowSizeDays;
            options.MaxBulletsPerDay = options.MaxBulletsPerDay > 0 ? options.MaxBulletsPerDay : defaults.MaxBulletsPerDay;
            options.BackfillOrder = string.IsNullOrWhiteSpace(options.BackfillOrder) ? defaults.BackfillOrder : options.BackfillOrder;
            options.OverlapDays = options.OverlapDays > 0 ? options.OverlapDays : defaults.OverlapDays;
            options.RefScope = options.RefScope;
            options.TrackIntegrations = options.TrackIntegrations;
            return options;
        }
        catch
        {
            return new SessionOptions();
        }
    }

    private string ComputeInputHash(List<Commit> commits, DateTime day)
    {
        var input = $"{PromptVersion}|{_modelName}|{day:yyyy-MM-dd}|";
        input += string.Join("|", commits.OrderBy(c => c.Sha).Select(c => $"{c.Sha}:{c.Subject}"));

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }
}

public class SummarizationResult
{
    public bool Success { get; set; }
    public DateTime Day { get; set; }
    public List<string> Bullets { get; set; } = new();
    public bool UsedAI { get; set; }
    public string? ErrorMessage { get; set; }
}
