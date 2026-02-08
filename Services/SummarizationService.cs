using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class SummarizationService
{
    private const int DefaultMaxPromptCharsPerCall = 45000;
    private const int ChunkBulletCap = 12;
    private const int MaxAdaptiveSplitDepth = 8;
    private const int MinDiffCharsForSplit = 2000;
    private readonly DatabaseService _databaseService;
    private readonly ClusteringService _clusteringService;
    private readonly SettingsService _settingsService;
    private readonly GitService _gitService;
    private readonly IReadOnlyList<ISummarizationProvider> _providers;
    private readonly RateBudgetService _rateBudgetService;
    private string _modelName = "gpt-4o-mini";
    private const string PromptVersion = "v2";
    private const int DefaultMaxCompletionTokensPerCall = 3000;
    private const int DefaultMaxTotalBullets = 40;
    private const int MaxContinuationParts = 4;
    private const string DefaultMasterPrompt = """
You are an evidence-driven developer diary generator.

Output requirements:
- Output ONLY bullet lines starting with "- ".
- No headings, no numbering, no preface, no closing text, no code fences.
- No blank lines.
- Use inline backticks for files, classes, services, APIs, commands, config keys.

Quality requirements:
- Every bullet should include WHAT changed, WHERE it changed, HOW it was done, and WHY it was done when evidence supports it.
- If WHY is missing, do not invent it; keep the bullet factual and concise.
- If you infer beyond explicit evidence, keep it minimal and grounded in the provided evidence.
- Do not fabricate facts or claim completion without evidence.
- Prefer dense, specific, technically traceable bullets over vague summaries.
- Do not include churn/count noise (for example +X/-Y line counts) unless explicitly requested.
 - Do not use hedging filler such as "likely", "probably", "appears to", or "aimed to".
- Do not output standalone meta-only bullets such as "no evidence found", "missing evidence", or bracket-only notes.
- Do not output standalone provenance tags like "[multiple commits]" or "[file diff]"; fold provenance into normal sentence text if needed.

Coverage (include only if evidenced):
- feature/bug/maintenance work
- commit intent/themes
- file-level changes and risk areas
- architecture/tradeoffs
- UI/UX behavior changes
- backend/data/query/API impacts
- branch/integration context
- testing/debugging/validation
- blockers/friction and mitigations
- experiments/abandoned directions
- unresolved items and next steps
""";
    private string? _apiKey;

    public SummarizationService(
        DatabaseService databaseService,
        ClusteringService clusteringService,
        SettingsService settingsService,
        GitService gitService,
        RateBudgetService? rateBudgetService = null,
        IEnumerable<ISummarizationProvider>? providers = null)
    {
        _databaseService = databaseService;
        _clusteringService = clusteringService;
        _settingsService = settingsService;
        _gitService = gitService;
        _rateBudgetService = rateBudgetService ?? new RateBudgetService();
        var resolved = (providers ?? Enumerable.Empty<ISummarizationProvider>()).ToList();
        if (resolved.Count == 0)
        {
            resolved.Add(new OpenAiSummarizationProvider());
            resolved.Add(new AnthropicSummarizationProvider());
        }

        _providers = resolved;
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
            var commitDiffBySha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (options.IncludeDiffs && session != null && !string.IsNullOrWhiteSpace(session.RepoPath))
            {
                foreach (var commit in commits)
                {
                    var diff = await _gitService.GetCommitDiffAsync(session.RepoPath, commit.Sha);
                    commitDiffBySha[commit.Sha] = diff;
                }
            }

            // Call provider or generate offline summary
            string bulletsText;
            var masterPrompt = await GetMasterPromptAsync();
            _modelName = await GetModelNameAsync();
            var provider = ResolveProviderForModel(_modelName);
            var apiKey = await GetApiKeyForProviderAsync(provider.ProviderId);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = string.Equals(provider.ProviderId, "anthropic", StringComparison.OrdinalIgnoreCase)
                    ? "Missing Anthropic API key. Set it in Settings or ANTHROPIC_API_KEY."
                    : "Missing OPENAI_API_KEY. Set it in Settings or environment to enable AI summarization.";
                return result;
            }
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
                integrationEvents,
                options.IncludeDiffs,
                commitDiffBySha);

            var shouldChunk = ShouldUseChunkedSummarization(prompt, commits.Count);
            if (shouldChunk)
            {
                bulletsText = await SummarizeWithChunkingAsync(
                    day,
                    commits,
                    session,
                    dayRecord,
                    branchRows,
                    integrationEvents,
                    options.IncludeDiffs,
                    commitDiffBySha,
                    effectiveMaxBullets,
                    maxCompletionTokensPerCall,
                    masterPrompt,
                    provider,
                    apiKey,
                    cancellationToken);
            }
            else
            {
                try
                {
                    bulletsText = await CallProviderWithContinuationAsync(
                        masterPrompt,
                        prompt,
                        provider,
                        apiKey,
                        maxBullets: effectiveMaxBullets,
                        maxCompletionTokensPerCall: maxCompletionTokensPerCall,
                        cancellationToken);
                }
                catch (Exception ex) when (IsRequestTooLargeError(ex.Message))
                {
                    bulletsText = await SummarizeWithChunkingAsync(
                        day,
                        commits,
                        session,
                        dayRecord,
                        branchRows,
                        integrationEvents,
                        options.IncludeDiffs,
                        commitDiffBySha,
                        effectiveMaxBullets,
                        maxCompletionTokensPerCall,
                        masterPrompt,
                        provider,
                        apiKey,
                        cancellationToken);
                }
            }
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
        List<IntegrationEvent> integrationEvents,
        bool includeDiffs,
        IReadOnlyDictionary<string, string> commitDiffBySha)
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
            sb.AppendLine($"  - IsMerge: {commit.IsMerge}");

            var files = ParseCommitFiles(commit.FilesJson);
            foreach (var file in files)
            {
                sb.AppendLine($"  - File: `{file.Path}` (+{file.Additions}/-{file.Deletions})");
            }

            if (includeDiffs && commitDiffBySha.TryGetValue(commit.Sha, out var diff) && !string.IsNullOrWhiteSpace(diff))
            {
                sb.AppendLine("  - FullDiff:");
                sb.AppendLine("    <<<DIFF_START>>>");
                foreach (var diffLine in diff.Split('\n'))
                {
                    var clean = diffLine.TrimEnd('\r');
                    sb.AppendLine($"    {clean}");
                }
                sb.AppendLine("    <<<DIFF_END>>>");
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
        sb.AppendLine("If WHY is not evidenced, do not invent WHY; keep the bullet factual and skip speculative rationale.");
        sb.AppendLine("If you infer beyond explicit facts, keep it minimal and clearly grounded in evidence.");
        sb.AppendLine("Do not fabricate facts.");
        sb.AppendLine("Do not include churn/count noise (+X/-Y) unless explicitly requested.");
        sb.AppendLine("Avoid hedging filler like 'likely', 'probably', 'appears to', 'aimed to'.");
        sb.AppendLine("Do not output standalone bullets that only say evidence is missing; omit those categories instead.");
        sb.AppendLine("Do not output standalone bracket-only provenance notes (e.g. `[multiple commits]`, `[file diff]`).");
        sb.AppendLine("Coverage targets (include if evidenced): feature/bug/maintenance, commit intent, file-level changes/risk, architecture decisions/tradeoffs, UI/UX behavior, backend/data impacts, branch/integration context, testing/debugging/validation, blockers, experiments, unresolved next steps.");
        if (includeDiffs)
            sb.AppendLine("Diff policy: full commit diffs are provided for all commits; do not sample or ignore them when deriving HOW/WHY details.");
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

    private async Task<string> CallProviderWithContinuationAsync(
        string masterPrompt,
        string prompt,
        ISummarizationProvider provider,
        string apiKey,
        int maxBullets,
        int maxCompletionTokensPerCall,
        CancellationToken cancellationToken)
    {
        // Most days should fit in one response, but long diaries can hit token caps.
        // If the model stops due to length, ask it to continue and return only new bullets.
        var allBullets = new List<string>();
        string? pendingPartialBullet = null;

        for (var part = 0; part < MaxContinuationParts && allBullets.Count < maxBullets; part++)
        {
            var remaining = maxBullets - allBullets.Count;
            var continuationPrompt = part == 0
                ? prompt
                : pendingPartialBullet != null
                    ? BuildContinuationPromptWithPartial(prompt, allBullets, pendingPartialBullet, remaining)
                    : BuildContinuationPrompt(prompt, allBullets, remaining);

            var call = await CallProviderInternalAsync(
                masterPrompt,
                continuationPrompt,
                provider,
                apiKey,
                maxCompletionTokensPerCall,
                cancellationToken);

            // If we hit the token limit, the last bullet may be cut mid-sentence. Capture it so we can ask the
            // model to finish it first on the next continuation call.
            if (call.ReachedMaxTokens)
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
            if (!call.ReachedMaxTokens)
                break;
        }

        // Fall back to raw content if parsing failed unexpectedly.
        if (allBullets.Count == 0)
            return (await CallProviderInternalAsync(masterPrompt, prompt, provider, apiKey, maxCompletionTokensPerCall, cancellationToken)).Content;

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

    private static bool ShouldUseChunkedSummarization(string prompt, int commitCount)
    {
        return prompt.Length > DefaultMaxPromptCharsPerCall;
    }

    private static bool IsRequestTooLargeError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var text = errorMessage.ToLowerInvariant();
        return text.Contains("request too large", StringComparison.Ordinal) ||
               text.Contains("context_length_exceeded", StringComparison.Ordinal) ||
               text.Contains("maximum context length", StringComparison.Ordinal) ||
               text.Contains("tokens per minute", StringComparison.Ordinal);
    }

    private async Task<string> SummarizeWithChunkingAsync(
        DateTime day,
        List<Commit> commits,
        Session? session,
        Models.Day? dayRecord,
        List<CommitBranchRow> branchRows,
        List<IntegrationEvent> integrationEvents,
        bool includeDiffs,
        IReadOnlyDictionary<string, string> commitDiffBySha,
        int maxBullets,
        int maxCompletionTokensPerCall,
        string masterPrompt,
        ISummarizationProvider provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var chunks = BuildCommitChunks(commits, includeDiffs, commitDiffBySha, DefaultMaxPromptCharsPerCall);
        if (chunks.Count == 0)
            chunks.Add(commits.OrderBy(c => c.AuthorDate).ToList());

        var partialBullets = new List<string>();
        var chunkBulletBudget = Math.Max(maxBullets, ChunkBulletCap);

        foreach (var chunk in chunks)
        {
            var chunkParsed = await SummarizeChunkAdaptiveAsync(
                day,
                chunk,
                session,
                dayRecord,
                branchRows,
                integrationEvents,
                includeDiffs,
                commitDiffBySha,
                chunkBulletBudget,
                maxCompletionTokensPerCall,
                masterPrompt,
                provider,
                apiKey,
                cancellationToken,
                depth: 0);
            AppendUniqueBullets(partialBullets, chunkParsed);
        }

        if (partialBullets.Count == 0)
            return string.Empty;

        return await SynthesizePartialBulletsAdaptiveAsync(
            day,
            partialBullets,
            maxBullets,
            maxCompletionTokensPerCall,
            masterPrompt,
            provider,
            apiKey,
            cancellationToken,
            depth: 0);
    }

    private static List<List<Commit>> BuildCommitChunks(
        List<Commit> commits,
        bool includeDiffs,
        IReadOnlyDictionary<string, string> commitDiffBySha,
        int maxPromptChars)
    {
        var ordered = commits.OrderBy(c => c.AuthorDate).ToList();
        var chunks = new List<List<Commit>>();
        var current = new List<Commit>();
        var currentChars = 4000;

        foreach (var commit in ordered)
        {
            var estimate = EstimateCommitEvidenceChars(commit, includeDiffs, commitDiffBySha);
            if (current.Count > 0 && currentChars + estimate > maxPromptChars)
            {
                chunks.Add(current);
                current = new List<Commit>();
                currentChars = 4000;
            }

            current.Add(commit);
            currentChars += estimate;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    private static int EstimateCommitEvidenceChars(
        Commit commit,
        bool includeDiffs,
        IReadOnlyDictionary<string, string> commitDiffBySha)
    {
        var estimate = 400 + commit.Subject.Length + commit.FilesJson.Length;
        if (includeDiffs && commitDiffBySha.TryGetValue(commit.Sha, out var diff))
            estimate += diff.Length + 200;
        return estimate;
    }

    private async Task<List<string>> SummarizeChunkAdaptiveAsync(
        DateTime day,
        List<Commit> chunk,
        Session? session,
        Models.Day? dayRecord,
        List<CommitBranchRow> branchRows,
        List<IntegrationEvent> integrationEvents,
        bool includeDiffs,
        IReadOnlyDictionary<string, string> commitDiffBySha,
        int maxBullets,
        int maxCompletionTokensPerCall,
        string masterPrompt,
        ISummarizationProvider provider,
        string apiKey,
        CancellationToken cancellationToken,
        int depth)
    {
        var orderedChunk = chunk.OrderBy(c => c.AuthorDate).ToList();
        var chunkSet = new HashSet<string>(orderedChunk.Select(c => c.Sha), StringComparer.OrdinalIgnoreCase);
        var chunkWorkUnits = _clusteringService.ClusterCommits(orderedChunk);
        var chunkBranches = branchRows.Where(b => chunkSet.Contains(b.Sha)).ToList();
        var chunkIntegrations = integrationEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.AnchorSha) && chunkSet.Contains(e.AnchorSha!))
            .ToList();
        var chunkDiffs = commitDiffBySha
            .Where(kvp => chunkSet.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var chunkPrompt = BuildPrompt(
            day,
            orderedChunk,
            chunkWorkUnits,
            maxBullets,
            session,
            dayRecord,
            chunkBranches,
            chunkIntegrations,
            includeDiffs,
            chunkDiffs);

        try
        {
            var chunkText = await CallProviderWithContinuationAsync(
                masterPrompt,
                chunkPrompt,
                provider,
                apiKey,
                maxBullets: maxBullets,
                maxCompletionTokensPerCall: maxCompletionTokensPerCall,
                cancellationToken);
            return ValidateBullets(chunkText, maxBullets);
        }
        catch (Exception ex) when (IsRequestTooLargeError(ex.Message) && depth < MaxAdaptiveSplitDepth)
        {
            if (orderedChunk.Count > 1)
            {
                var (left, right) = SplitCommitListInHalf(orderedChunk);
                var merged = new List<string>();
                var leftBullets = await SummarizeChunkAdaptiveAsync(
                    day, left, session, dayRecord, branchRows, integrationEvents, includeDiffs, commitDiffBySha,
                    maxBullets, maxCompletionTokensPerCall, masterPrompt, provider, apiKey, cancellationToken, depth + 1);
                AppendUniqueBullets(merged, leftBullets);
                var rightBullets = await SummarizeChunkAdaptiveAsync(
                    day, right, session, dayRecord, branchRows, integrationEvents, includeDiffs, commitDiffBySha,
                    maxBullets, maxCompletionTokensPerCall, masterPrompt, provider, apiKey, cancellationToken, depth + 1);
                AppendUniqueBullets(merged, rightBullets);
                return merged;
            }

            if (!includeDiffs || orderedChunk.Count == 0)
                throw;

            var single = orderedChunk[0];
            if (!chunkDiffs.TryGetValue(single.Sha, out var fullDiff) || string.IsNullOrWhiteSpace(fullDiff) || fullDiff.Length < MinDiffCharsForSplit)
                throw;

            var (leftDiff, rightDiff) = SplitTextInHalf(fullDiff);
            if (string.IsNullOrWhiteSpace(leftDiff) || string.IsNullOrWhiteSpace(rightDiff))
                throw;

            var mergedFromDiffHalves = new List<string>();

            var leftOverrides = new Dictionary<string, string>(commitDiffBySha, StringComparer.OrdinalIgnoreCase)
            {
                [single.Sha] = leftDiff
            };
            var leftHalfBullets = await SummarizeChunkAdaptiveAsync(
                day,
                orderedChunk,
                session,
                dayRecord,
                branchRows,
                integrationEvents,
                includeDiffs,
                leftOverrides,
                maxBullets,
                maxCompletionTokensPerCall,
                masterPrompt,
                provider,
                apiKey,
                cancellationToken,
                depth + 1);
            AppendUniqueBullets(mergedFromDiffHalves, leftHalfBullets);

            var rightOverrides = new Dictionary<string, string>(commitDiffBySha, StringComparer.OrdinalIgnoreCase)
            {
                [single.Sha] = rightDiff
            };
            var rightHalfBullets = await SummarizeChunkAdaptiveAsync(
                day,
                orderedChunk,
                session,
                dayRecord,
                branchRows,
                integrationEvents,
                includeDiffs,
                rightOverrides,
                maxBullets,
                maxCompletionTokensPerCall,
                masterPrompt,
                provider,
                apiKey,
                cancellationToken,
                depth + 1);
            AppendUniqueBullets(mergedFromDiffHalves, rightHalfBullets);

            return mergedFromDiffHalves;
        }
    }

    private async Task<string> SynthesizePartialBulletsAdaptiveAsync(
        DateTime day,
        List<string> partialBullets,
        int maxBullets,
        int maxCompletionTokensPerCall,
        string masterPrompt,
        ISummarizationProvider provider,
        string apiKey,
        CancellationToken cancellationToken,
        int depth)
    {
        var synthesisPrompt = BuildChunkSynthesisPrompt(day, partialBullets, maxBullets);
        try
        {
            return await CallProviderWithContinuationAsync(
                masterPrompt,
                synthesisPrompt,
                provider,
                apiKey,
                maxBullets: maxBullets,
                maxCompletionTokensPerCall: maxCompletionTokensPerCall,
                cancellationToken);
        }
        catch (Exception ex) when (IsRequestTooLargeError(ex.Message) && depth < MaxAdaptiveSplitDepth && partialBullets.Count > 2)
        {
            var midpoint = partialBullets.Count / 2;
            var left = partialBullets.Take(midpoint).ToList();
            var right = partialBullets.Skip(midpoint).ToList();

            var leftText = await SynthesizePartialBulletsAdaptiveAsync(
                day,
                left,
                Math.Max(maxBullets, ChunkBulletCap),
                maxCompletionTokensPerCall,
                masterPrompt,
                provider,
                apiKey,
                cancellationToken,
                depth + 1);
            var rightText = await SynthesizePartialBulletsAdaptiveAsync(
                day,
                right,
                Math.Max(maxBullets, ChunkBulletCap),
                maxCompletionTokensPerCall,
                masterPrompt,
                provider,
                apiKey,
                cancellationToken,
                depth + 1);

            var merged = new List<string>();
            AppendUniqueBullets(merged, ValidateBullets(leftText, Math.Max(maxBullets, ChunkBulletCap)));
            AppendUniqueBullets(merged, ValidateBullets(rightText, Math.Max(maxBullets, ChunkBulletCap)));

            var finalPrompt = BuildChunkSynthesisPrompt(day, merged, maxBullets);
            return await CallProviderWithContinuationAsync(
                masterPrompt,
                finalPrompt,
                provider,
                apiKey,
                maxBullets: maxBullets,
                maxCompletionTokensPerCall: maxCompletionTokensPerCall,
                cancellationToken);
        }
    }

    private static (List<Commit> left, List<Commit> right) SplitCommitListInHalf(List<Commit> commits)
    {
        var midpoint = commits.Count / 2;
        if (midpoint <= 0)
            midpoint = 1;
        var left = commits.Take(midpoint).ToList();
        var right = commits.Skip(midpoint).ToList();
        if (right.Count == 0 && left.Count > 1)
        {
            right.Add(left[^1]);
            left.RemoveAt(left.Count - 1);
        }

        return (left, right);
    }

    private static (string left, string right) SplitTextInHalf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (string.Empty, string.Empty);

        var lines = text.Split('\n');
        if (lines.Length < 2)
            return (string.Empty, string.Empty);

        var midpoint = lines.Length / 2;
        if (midpoint <= 0)
            midpoint = 1;
        var left = string.Join('\n', lines.Take(midpoint));
        var right = string.Join('\n', lines.Skip(midpoint));
        return (left, right);
    }

    private static string BuildChunkSynthesisPrompt(DateTime day, List<string> partialBullets, int maxBullets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EVIDENCE_BUNDLE");
        sb.AppendLine($"- Day: {day:yyyy-MM-dd}");
        sb.AppendLine("- Source: chunked summaries from the same day.");
        sb.AppendLine();
        sb.AppendLine("Chunk bullets:");
        foreach (var bullet in partialBullets)
            sb.AppendLine(bullet);

        sb.AppendLine();
        sb.AppendLine($"Synthesize up to {maxBullets} final bullets.");
        sb.AppendLine("Deduplicate overlapping points, preserve chronology, and keep concrete WHAT/WHERE/HOW/WHY when evidenced.");
        sb.AppendLine("Output contract:");
        sb.AppendLine("- Each bullet must start with '- '");
        sb.AppendLine("- No headings, no numbering, no code fences, no preface/closing text");
        return sb.ToString();
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

    private async Task<SummarizationProviderResponse> CallProviderInternalAsync(
        string masterPrompt,
        string prompt,
        ISummarizationProvider provider,
        string apiKey,
        int maxCompletionTokens,
        CancellationToken cancellationToken)
    {
        var estimatedInputTokens = EstimateTokens(masterPrompt) + EstimateTokens(prompt);
        var reservedOutputTokens = Math.Max(1, maxCompletionTokens);

        await _rateBudgetService.WaitForCapacityAsync(
            provider.ProviderId,
            _modelName,
            estimatedInputTokens,
            reservedOutputTokens,
            cancellationToken);

        return await provider.CompleteAsync(
            new SummarizationProviderRequest(
                ApiKey: apiKey,
                Model: _modelName,
                SystemPrompt: masterPrompt,
                UserPrompt: prompt,
                Temperature: 0.2,
                MaxCompletionTokens: maxCompletionTokens),
            cancellationToken);
    }

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        // Rough heuristic for scheduling only. Keep conservative.
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private async Task<string?> GetApiKeyForProviderAsync(string providerId)
    {
        return string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? await GetAnthropicApiKeyAsync()
            : await GetOpenAiApiKeyAsync();
    }

    private async Task<string?> GetOpenAiApiKeyAsync()
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

    private async Task<string?> GetAnthropicApiKeyAsync()
    {
        var apiKey = await _settingsService.GetAsync(SettingsService.AnthropicApiKeyKey, string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    private async Task<string> GetMasterPromptAsync()
    {
        var prompt = await _settingsService.GetAsync(SettingsService.SummarizationMasterPromptKey, DefaultMasterPrompt);
        return string.IsNullOrWhiteSpace(prompt) ? DefaultMasterPrompt : prompt;
    }

    private async Task<string> GetModelNameAsync()
    {
        var configured = await _settingsService.GetAsync(SettingsService.SummarizationModelKey, _modelName);
        return string.IsNullOrWhiteSpace(configured) ? _modelName : configured.Trim();
    }

    private ISummarizationProvider ResolveProviderForModel(string modelName)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var matched = _providers.FirstOrDefault(p => p.CanHandleModel(modelName));
            if (matched != null)
                return matched;
        }

        return _providers.FirstOrDefault(p => string.Equals(p.ProviderId, "openai", StringComparison.OrdinalIgnoreCase))
            ?? _providers.FirstOrDefault()
            ?? throw new InvalidOperationException("No summarization providers are registered.");
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
