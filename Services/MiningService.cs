using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class MiningService
{
    private readonly GitService _gitService;
    private readonly DatabaseService _databaseService;

    public MiningService(GitService gitService, DatabaseService databaseService)
    {
        _gitService = gitService;
        _databaseService = databaseService;
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

        try
        {
            // Step 1: Fetch from remote
            progress?.Report(new MiningProgress { Status = "Fetching from remote..." });
            var fetchResult = await _gitService.FetchAllAsync(repoPath);
            if (!fetchResult.Success)
            {
                result.ErrorMessage = $"Failed to fetch: {fetchResult.Error}";
                return result;
            }

            // Step 2: Get commits from git
            progress?.Report(new MiningProgress { Status = "Reading commits from Git..." });
            var commits = await _gitService.GetCommitsAsync(
                repoPath,
                sessionId,
                since,
                until,
                authorFilters,
                options.IncludeMerges);

            result.TotalCommits = commits.Count;

            // Step 3: Store commits in database (deduplication handled by INSERT OR IGNORE)
            progress?.Report(new MiningProgress { Status = "Storing commits in database..." });
            var storedCount = 0;
            foreach (var commit in commits)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.ErrorMessage = "Operation cancelled";
                    return result;
                }

                await _databaseService.InsertOrIgnoreCommitAsync(commit);
                storedCount++;

                if (storedCount % 10 == 0)
                {
                    progress?.Report(new MiningProgress
                    {
                        Status = $"Stored {storedCount}/{commits.Count} commits..."
                    });
                }
            }

            result.StoredCommits = storedCount;

            // Step 4: Aggregate by day
            progress?.Report(new MiningProgress { Status = "Aggregating commits by day..." });
            var dayGroups = commits
                .GroupBy(c => c.AuthorDate.Date)
                .OrderBy(g => g.Key);

            foreach (var dayGroup in dayGroups)
            {
                var day = new Models.Day
                {
                    SessionId = sessionId,
                    Date = dayGroup.Key,
                    CommitCount = dayGroup.Count(),
                    Additions = dayGroup.Sum(c => c.Additions),
                    Deletions = dayGroup.Sum(c => c.Deletions),
                    Status = DayStatus.Mined
                };

                await _databaseService.UpsertDayAsync(day);
                result.DaysMined++;
            }

            progress?.Report(new MiningProgress
            {
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits."
            });

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
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
}

public class MiningProgress
{
    public string Status { get; set; } = string.Empty;
    public int ProcessedItems { get; set; }
    public int TotalItems { get; set; }
}
