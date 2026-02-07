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
    private const string PromptVersion = "v2";
    private const int DefaultMaxCompletionTokensPerCall = 3000;
    private const int DefaultMaxTotalBullets = 40;
    private const int MaxContinuationParts = 4;
    private const string DefaultMasterPrompt =
        "You are an evidence-driven developer diary generator. Output ONLY bullet points starting with '- ' and stay strictly grounded in provided evidence.";
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
            var session = await _databaseService.GetSessionAsync(sessionId);
            var dayRecord = (await _databaseService.GetDaysAsync(sessionId))
                .FirstOrDefault(d => d.Date.Date == day.Date);
            var branchRows = await _databaseService.GetCommitBranchRowsForDayAsync(sessionId, day);
            var integrationEvents = await _databaseService.GetIntegrationEventsForDayAsync(sessionId, day);

            // Call OpenAI or generate offline summary
            string bulletsText;
            var apiKey = await GetApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "Missing OPENAI_API_KEY. Set it in your environment to enable AI summarization.";
                return result;
            }

            var masterPrompt = await GetMasterPromptAsync();
            var maxCompletionTokensPerCall = await _settingsService.GetAsync(
                SettingsService.SummarizationMaxCompletionTokensPerCallKey,
                DefaultMaxCompletionTokensPerCall);
            var maxTotalBullets = await _settingsService.GetAsync(
                SettingsService.SummarizationMaxTotalBulletsPerDayKey,
                DefaultMaxTotalBullets);

            maxCompletionTokensPerCall = Clamp(maxCompletionTokensPerCall, min: 256, max: 8000);
            maxTotalBullets = Clamp(maxTotalBullets, min: 1, max: 200);
            var effectiveMaxBullets = Clamp(Math.Min(maxBullets, maxTotalBullets), 1, 200);

            // Build prompt
            var prompt = BuildPrompt(
                day,
                commits,
                workUnits,
                effectiveMaxBullets,
                session,
                dayRecord,
                branchRows,
                integrationEvents);

            bulletsText = await CallOpenAIWithContinuationAsync(
                masterPrompt,
                prompt,
                apiKey,
                maxBullets: effectiveMaxBullets,
                maxCompletionTokensPerCall: maxCompletionTokensPerCall,
                cancellationToken);
            result.UsedAI = true;

            // Validate and clean bullets
            var bullets = ValidateBullets(bulletsText, effectiveMaxBullets);
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

    private string BuildPrompt(
        DateTime day,
        List<Commit> commits,
        List<WorkUnit> workUnits,
        int maxBullets,
        Session? session,
        Models.Day? dayRecord,
        List<CommitBranchRow> branchRows,
        List<IntegrationEvent> integrationEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EVIDENCE_BUNDLE");
        sb.AppendLine();
        sb.AppendLine("Session metadata:");
        sb.AppendLine($"- SessionId: {session?.Id ?? 0}");
        sb.AppendLine($"- SessionName: {session?.Name ?? "(unknown)"}");
        sb.AppendLine($"- RepoPath: {session?.RepoPath ?? "(unknown)"}");
        sb.AppendLine($"- MainBranch: {session?.MainBranch ?? "(unknown)"}");
        sb.AppendLine($"- Range: {(session?.RangeStart.HasValue == true ? session.RangeStart.Value.ToString("yyyy-MM-dd") : "(unset)")} to {(session?.RangeEnd.HasValue == true ? session.RangeEnd.Value.ToString("yyyy-MM-dd") : "(unset)")}");
        sb.AppendLine();

        sb.AppendLine("Day:");
        sb.AppendLine($"- Date: {day:yyyy-MM-dd}");
        sb.AppendLine($"- Status: {dayRecord?.Status.ToString() ?? "unknown"}");
        sb.AppendLine($"- CommitCount: {commits.Count}");
        sb.AppendLine($"- Additions: {commits.Sum(c => c.Additions)}");
        sb.AppendLine($"- Deletions: {commits.Sum(c => c.Deletions)}");
        sb.AppendLine();

        sb.AppendLine("Work-unit summaries:");
        sb.AppendLine();

        foreach (var unit in workUnits)
        {
            sb.AppendLine(_clusteringService.GenerateWorkUnitSummary(unit));
        }

        sb.AppendLine();
        sb.AppendLine("Commit evidence:");
        foreach (var commit in commits.OrderBy(c => c.AuthorDate))
        {
            var shortSha = commit.Sha.Length > 10 ? commit.Sha[..10] : commit.Sha;
            sb.AppendLine($"- Commit `{shortSha}`: {commit.Subject}");
            sb.AppendLine($"  - Author: {commit.AuthorName} <{commit.AuthorEmail}> at {commit.AuthorDate:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"  - Committer: {commit.CommitterName} <{commit.CommitterEmail}> at {commit.CommitterDate:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"  - Churn: +{commit.Additions}/-{commit.Deletions}, IsMerge: {commit.IsMerge}");

            var files = ParseCommitFiles(commit.FilesJson);
            foreach (var file in files)
            {
                sb.AppendLine($"  - File: `{file.Path}` (+{file.Additions}/-{file.Deletions})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Branch evidence:");
        if (branchRows.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var row in branchRows)
            {
                var shortSha = row.Sha.Length > 10 ? row.Sha[..10] : row.Sha;
                sb.AppendLine($"- `{shortSha}` -> Branch `{(string.IsNullOrWhiteSpace(row.BranchName) ? "(unattributed)" : row.BranchName)}`");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Integration evidence:");
        if (integrationEvents.Count == 0)
        {
            sb.AppendLine("- (none)");
        }
        else
        {
            foreach (var evt in integrationEvents.OrderBy(e => e.OccurredAt))
            {
                sb.AppendLine($"- Event `{evt.Id}` Method `{evt.Method}` Confidence `{evt.Confidence}` Anchor `{evt.AnchorSha ?? "(none)"}` Occurred `{evt.OccurredAt:yyyy-MM-ddTHH:mm:ss}`");
                if (!string.IsNullOrWhiteSpace(evt.DetailsJson))
                    sb.AppendLine($"  - Details: {evt.DetailsJson}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Generate up to {maxBullets} dense, technical diary bullets from this evidence.");
        sb.AppendLine("Per bullet, include WHAT + WHERE + HOW + WHY when evidence supports it.");
        sb.AppendLine("If WHY is not evidenced, include `[uncertain]` and say what evidence is missing.");
        sb.AppendLine("If you infer beyond explicit facts, tag bullet with `[inferred]`.");
        sb.AppendLine("Do not fabricate facts.");
        sb.AppendLine("Coverage targets (include if evidenced): feature/bug/maintenance, commit intent, file churn/risk, architecture decisions/tradeoffs, UI/UX behavior, backend/data impacts, branch/integration context, testing/debugging/validation, blockers, experiments, unresolved next steps.");
        sb.AppendLine("Output contract:");
        sb.AppendLine("- Each bullet must start with '- '");
        sb.AppendLine("- No headings, no numbering, no code fences, no preface/closing text");
        sb.AppendLine("- Use inline backticks for files, symbols, commands, APIs, and config keys where relevant");

        return sb.ToString();
    }

    private static List<CommitFile> ParseCommitFiles(string filesJson)
    {
        if (string.IsNullOrWhiteSpace(filesJson))
            return new List<CommitFile>();

        try
        {
            return JsonSerializer.Deserialize<List<CommitFile>>(filesJson) ?? new List<CommitFile>();
        }
        catch
        {
            return new List<CommitFile>();
        }
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
            max_completion_tokens = DefaultMaxCompletionTokensPerCall,
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

    private async Task<string> CallOpenAIWithContinuationAsync(
        string masterPrompt,
        string prompt,
        string apiKey,
        int maxBullets,
        int maxCompletionTokensPerCall,
        CancellationToken cancellationToken)
    {
        // Most days should fit in one response, but long diaries can hit token caps.
        // If the model stops due to length, ask it to continue and return only new bullets.
        var allBullets = new List<string>();
        var lastFinishReason = string.Empty;
        string? pendingPartialBullet = null;

        for (var part = 0; part < MaxContinuationParts && allBullets.Count < maxBullets; part++)
        {
            var remaining = maxBullets - allBullets.Count;
            var continuationPrompt = part == 0
                ? prompt
                : pendingPartialBullet != null
                    ? BuildContinuationPromptWithPartial(prompt, allBullets, pendingPartialBullet, remaining)
                    : BuildContinuationPrompt(prompt, allBullets, remaining);

            var call = await CallOpenAIInternalAsync(
                masterPrompt,
                continuationPrompt,
                apiKey,
                maxCompletionTokensPerCall,
                cancellationToken);

            lastFinishReason = call.FinishReason ?? string.Empty;

            // If we hit the token limit, the last bullet may be cut mid-sentence. Capture it so we can ask the
            // model to finish it first on the next continuation call.
            if (string.Equals(lastFinishReason, "length", StringComparison.OrdinalIgnoreCase))
                pendingPartialBullet = ExtractLastBulletLine(call.Content);
            else
                pendingPartialBullet = null;

            var newBullets = ValidateBullets(call.Content, remaining);

            // If we captured a "possibly partial" last bullet line, don't persist it from this chunk; we'll ask the
            // model to re-emit it completed next time.
            if (pendingPartialBullet != null && newBullets.Count > 0)
            {
                var last = newBullets[^1];
                if (string.Equals(last, pendingPartialBullet, StringComparison.OrdinalIgnoreCase))
                    newBullets.RemoveAt(newBullets.Count - 1);
            }

            var appended = AppendUniqueBullets(allBullets, newBullets);

            // If we didn't extract any new bullets, continuing won't help.
            if (appended == 0)
                break;

            // Only continue if we know we were cut off by length.
            if (!string.Equals(lastFinishReason, "length", StringComparison.OrdinalIgnoreCase))
                break;
        }

        // Fall back to raw content if parsing failed unexpectedly.
        if (allBullets.Count == 0)
            return (await CallOpenAIInternalAsync(masterPrompt, prompt, apiKey, maxCompletionTokensPerCall, cancellationToken)).Content;

        return string.Join("\n", allBullets);
    }

    private static string BuildContinuationPrompt(string originalPrompt, List<string> bulletsSoFar, int remaining)
    {
        var sb = new StringBuilder();
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("Already generated bullets (do not repeat these):");
        foreach (var b in bulletsSoFar)
            sb.AppendLine(b);
        sb.AppendLine();
        sb.AppendLine($"Continue with {remaining} more bullets.");
        sb.AppendLine("Output ONLY new bullet points that start with '- '.");
        sb.AppendLine("Do not repeat earlier bullets. Do not add headings or paragraphs.");
        return sb.ToString();
    }

    private static string BuildContinuationPromptWithPartial(string originalPrompt, List<string> bulletsSoFar, string partialBullet, int remaining)
    {
        var sb = new StringBuilder();
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("Already generated bullets (do not repeat these):");
        foreach (var b in bulletsSoFar)
            sb.AppendLine(b);
        sb.AppendLine();
        sb.AppendLine("The last response was truncated mid-bullet.");
        sb.AppendLine("First: output 1 bullet that completes the following partial bullet.");
        sb.AppendLine("That first bullet MUST start exactly with this text (including '- '):");
        sb.AppendLine(partialBullet);
        sb.AppendLine();
        if (remaining > 1)
            sb.AppendLine($"Then: output up to {remaining - 1} additional new bullets.");
        else
            sb.AppendLine("Then: stop (no additional bullets).");
        sb.AppendLine("Output ONLY bullet points that start with '- '.");
        sb.AppendLine("Do not repeat earlier bullets. Do not add headings or paragraphs.");
        return sb.ToString();
    }

    private static string? ExtractLastBulletLine(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // Find the last line that looks like a bullet start. When the model is truncated, this tends to be the
        // partial bullet we want to complete.
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                return trimmed;
        }

        return null;
    }

    private static int AppendUniqueBullets(List<string> destination, List<string> toAppend)
    {
        var seen = new HashSet<string>(destination, StringComparer.OrdinalIgnoreCase);
        var appended = 0;
        foreach (var bullet in toAppend)
        {
            if (seen.Add(bullet))
            {
                destination.Add(bullet);
                appended++;
            }
        }

        return appended;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
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

    private async Task<OpenAiChatResult> CallOpenAIInternalAsync(
        string masterPrompt,
        string prompt,
        string apiKey,
        int maxCompletionTokens,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _modelName,
            messages = new[]
            {
                new { role = "developer", content = masterPrompt },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            max_completion_tokens = maxCompletionTokens,
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
        var choice0 = doc.RootElement.GetProperty("choices")[0];
        var content = choice0.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

        string? finishReason = null;
        if (choice0.TryGetProperty("finish_reason", out var finishReasonProp) && finishReasonProp.ValueKind == JsonValueKind.String)
            finishReason = finishReasonProp.GetString();

        return new OpenAiChatResult(content, finishReason);
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

internal sealed record OpenAiChatResult(string Content, string? FinishReason);

public class SummarizationResult
{
    public bool Success { get; set; }
    public DateTime Day { get; set; }
    public List<string> Bullets { get; set; } = new();
    public bool UsedAI { get; set; }
    public string? ErrorMessage { get; set; }
}
