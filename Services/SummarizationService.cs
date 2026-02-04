using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class SummarizationService
{
    private readonly DatabaseService _databaseService;
    private readonly ClusteringService _clusteringService;
    private object? _openAIClient; // Placeholder for OpenAI client - implement when needed
    private string _modelName = "gpt-4o-mini";
    private const string PromptVersion = "v1";

    public SummarizationService(DatabaseService databaseService, ClusteringService clusteringService)
    {
        _databaseService = databaseService;
        _clusteringService = clusteringService;
    }

    public void ConfigureOpenAI(string apiKey, string? modelName = null)
    {
        // TODO: Initialize OpenAI client when package is properly configured
        // _openAIClient = new OpenAIClient(apiKey);
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
            // Get commits for the day
            var commits = (await _databaseService.GetCommitsForDayAsync(sessionId, day)).ToList();
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
            if (_openAIClient != null)
            {
                bulletsText = await CallOpenAIAsync(prompt, cancellationToken);
                result.UsedAI = true;
            }
            else
            {
                bulletsText = GenerateOfflineSummary(commits, workUnits);
                result.UsedAI = false;
            }

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

    private async Task<string> CallOpenAIAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_openAIClient == null)
            throw new InvalidOperationException("OpenAI client not configured");

        // TODO: Implement OpenAI API call when package is configured
        // For now, fall back to offline mode
        await Task.Delay(100, cancellationToken); // Simulate API call
        throw new InvalidOperationException("OpenAI integration not yet implemented - using offline mode");
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
