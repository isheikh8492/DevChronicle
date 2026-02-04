using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class MiningService
{
    private readonly GitService _gitService;
    private readonly DatabaseService _databaseService;
    private readonly LoggerService _logger;

    public MiningService(GitService gitService, DatabaseService databaseService, LoggerService logger)
    {
        _gitService = gitService;
        _databaseService = databaseService;
        _logger = logger;
    }

    public async Task<MiningResult> MineCommitsAsync(
        int sessionId,
        string repoPath,
        DateTime since,
        DateTime until,
        SessionOptions options,
        List<AuthorFilter> authorFilters,
        IProgress<MiningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MiningResult();
        _logger.LogInfo($"Mining started for session {sessionId}, repo: {repoPath}, range: {since:yyyy-MM-dd} to {until:yyyy-MM-dd}");

        try
        {
            // Step 1: Fetch from remote
            _logger.LogInfo("Step 1: Fetching from remote...");
            progress?.Report(new MiningProgress { Status = "Fetching from remote..." });
            var fetchResult = await _gitService.FetchAllAsync(repoPath);
            if (!fetchResult.Success)
            {
                _logger.LogError($"Git fetch failed: {fetchResult.Error}");
                result.ErrorMessage = $"Failed to fetch: {fetchResult.Error}";
                return result;
            }
            _logger.LogInfo("Git fetch completed successfully");

            // Step 2: Get commits from git
            _logger.LogInfo("Step 2: Reading commits from Git...");
            progress?.Report(new MiningProgress { Status = "Reading commits from Git..." });
            var commits = await _gitService.GetCommitsAsync(
                repoPath,
                sessionId,
                since,
                until,
                authorFilters,
                options.IncludeMerges);

            result.TotalCommits = commits.Count;
            _logger.LogInfo($"Found {commits.Count} commits");

            // Step 3: Store commits in database (deduplication handled by INSERT OR IGNORE)
            _logger.LogInfo("Step 3: Storing commits in database...");
            progress?.Report(new MiningProgress { Status = "Storing commits in database..." });

            // Check for cancellation before batch insert
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Mining operation cancelled by user");
                result.ErrorMessage = "Operation cancelled";
                return result;
            }

            // Batch insert all commits in ONE transaction
            _logger.LogInfo($"Batch inserting {commits.Count} commits...");
            await _databaseService.BatchInsertCommitsAsync(commits);
            result.StoredCommits = commits.Count;
            _logger.LogInfo($"Successfully stored {commits.Count} commits");

            progress?.Report(new MiningProgress
            {
                Status = $"Stored {commits.Count} commits in database"
            });

            // Step 4: Aggregate by day
            _logger.LogInfo("Step 4: Aggregating commits by day...");
            progress?.Report(new MiningProgress { Status = "Aggregating commits by day..." });
            var dayGroups = commits
                .GroupBy(c => c.AuthorDate.Date)
                .OrderBy(g => g.Key);

            var daysToUpsert = new List<Models.Day>();
            foreach (var dayGroup in dayGroups)
            {
                daysToUpsert.Add(new Models.Day
                {
                    SessionId = sessionId,
                    Date = dayGroup.Key,
                    CommitCount = dayGroup.Count(),
                    Additions = dayGroup.Sum(c => c.Additions),
                    Deletions = dayGroup.Sum(c => c.Deletions),
                    Status = DayStatus.Mined
                });
            }

            // Batch upsert all days in ONE transaction
            _logger.LogInfo($"Batch upserting {daysToUpsert.Count} days...");
            await _databaseService.BatchUpsertDaysAsync(daysToUpsert);
            result.DaysMined = daysToUpsert.Count;
            _logger.LogInfo($"Successfully upserted {daysToUpsert.Count} days");

            progress?.Report(new MiningProgress
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits."
            });

            result.Success = true;
            _logger.LogInfo($"Mining completed successfully: {result.DaysMined} days, {result.StoredCommits} commits");
        }
        catch (Exception ex)
        {
            // Capture full exception including stack trace and inner exceptions
            result.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            result.ErrorDetails = ex.ToString();

            _logger.LogError($"Mining failed with exception: {ex.GetType().Name}", ex);

            // Also report to UI
            progress?.Report(new MiningProgress
            {
                Status = $"Error: {ex.Message}"
            });
        }

        return result;
    }
}

public class MiningResult
{
    public bool Success { get; set; }
    public int TotalCommits { get; set; }
    public int StoredCommits { get; set; }
    public int DaysMined { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }  // Full stack trace for debugging
}

public class MiningProgress
{
    public string Status { get; set; } = string.Empty;
    public int ProcessedItems { get; set; }
    public int TotalItems { get; set; }
}
