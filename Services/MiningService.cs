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
        DateTime? since,
        DateTime? until,
        SessionOptions options,
        List<AuthorFilter> authorFilters,
        IProgress<MiningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MiningResult();
        var rangeLabel = since.HasValue && until.HasValue
            ? $"{since.Value:yyyy-MM-dd} to {until.Value:yyyy-MM-dd}"
            : "ALL HISTORY";
        _logger.LogInfo($"Mining started for session {sessionId}, repo: {repoPath}, range: {rangeLabel}, scope: {options.RefScope}, includeMerges: {options.IncludeMerges}, trackIntegrations: {options.TrackIntegrations}");

        try
        {
            var normalizedStart = since?.Date;
            var normalizedEnd = until?.Date;
            var endExclusive = normalizedEnd?.AddDays(1);

            // Step 1: Fetch from remote
            _logger.LogInfo("Step 1: Fetching from remote...");
            progress?.Report(new MiningProgress
            {
                Status = "Fetching from remote...",
                ProcessedItems = 0,
                TotalItems = 4
            });
            var fetchResult = await _gitService.FetchAllAsync(repoPath);
            if (!fetchResult.Success)
            {
                _logger.LogError($"Git fetch failed: {fetchResult.Error}");
                result.ErrorMessage = $"Failed to fetch: {fetchResult.Error}";
                return result;
            }
            _logger.LogInfo("Git fetch completed successfully");

            // Step 2: Get commits from git (single-pass numstat)
            _logger.LogInfo("Step 2: Reading commits from Git (single-pass)...");
            progress?.Report(new MiningProgress
            {
                Status = "Reading commits from Git...",
                ProcessedItems = 1,
                TotalItems = 4
            });
            var includeMergesForMining = options.IncludeMerges || options.TrackIntegrations;
            var knownShas = await _databaseService.GetCommitShasInRangeAsync(sessionId, normalizedStart, normalizedEnd);
            var parsedCommits = await _gitService.GetCommitsWithNumstatAsync(
                repoPath,
                sessionId,
                normalizedStart,
                endExclusive,
                authorFilters,
                options.RefScope,
                includeMergesForMining,
                knownShas);

            var parsedBySha = parsedCommits.ToDictionary(p => p.Commit.Sha, StringComparer.OrdinalIgnoreCase);
            var reflogShas = await _gitService.GetReflogCommitsAsync(repoPath, normalizedStart, endExclusive);
            var missingReflogShas = reflogShas
                .Where(sha => !parsedBySha.ContainsKey(sha))
                .ToList();

            if (missingReflogShas.Count > 0)
            {
                _logger.LogInfo($"Reflog commits not in log output: {missingReflogShas.Count}");
                var reflogCommits = await _gitService.GetCommitsByShasWithNumstatAsync(repoPath, sessionId, missingReflogShas);
                foreach (var parsed in reflogCommits)
                {
                    parsedCommits.Add(parsed);
                    parsedBySha[parsed.Commit.Sha] = parsed;
                }
            }

            var commits = parsedCommits.Select(p => p.Commit).ToList();
            var existingCommits = await _databaseService.GetCommitsByShasAsync(sessionId, knownShas);
            var existingBySha = existingCommits.ToDictionary(c => c.Sha, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < commits.Count; i++)
            {
                var commit = commits[i];
                if (existingBySha.TryGetValue(commit.Sha, out var existing))
                {
                    commits[i] = existing;
                    parsedCommits[i].Commit = existing;
                }
            }
            var parentRows = parsedCommits
                .Where(p => p.Parents.Count > 0)
                .SelectMany(p => p.Parents.Select((parent, index) => new CommitParent
                {
                    SessionId = sessionId,
                    ChildSha = p.Commit.Sha,
                    ParentSha = parent,
                    ParentOrder = index
                }))
                .ToList();

            result.TotalCommits = commits.Count;
            _logger.LogInfo($"Found {commits.Count} commits");

            // Step 3: Store commits in database (deduplication handled by INSERT OR IGNORE)
            _logger.LogInfo("Step 3: Storing commits in database...");
            progress?.Report(new MiningProgress
            {
                Status = "Storing commits in database...",
                ProcessedItems = 2,
                TotalItems = 4
            });

            // Check for cancellation before batch insert
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Mining operation cancelled by user");
                result.ErrorMessage = "Operation cancelled";
                return result;
            }

            // Batch insert all commits in ONE transaction
            _logger.LogInfo($"Batch inserting {commits.Count} commits...");
            var storedCommits = await _databaseService.BatchInsertCommitsAsync(commits);
            result.StoredCommits = storedCommits;
            _logger.LogInfo($"Stored {storedCommits} new commits (parsed {commits.Count})");

            if (parentRows.Count > 0)
            {
                _logger.LogInfo($"Batch inserting {parentRows.Count} commit parents...");
                await _databaseService.BatchInsertCommitParentsAsync(parentRows);
            }

            // Branch snapshots + labels
            var capturedAt = DateTime.UtcNow;
            var snapshots = await _gitService.GetBranchTipsAsync(repoPath, sessionId, capturedAt);
            if (snapshots.Count > 0)
            {
                _logger.LogInfo($"Captured {snapshots.Count} branch tips");
                await _databaseService.BatchInsertBranchSnapshotsAsync(snapshots);
            }

            var labelsBySha = await _gitService.GetNameRevLabelsAsync(repoPath, commits.Select(c => c.Sha));
            var containsBySha = await _gitService.GetBranchContainmentAsync(repoPath, commits.Select(c => c.Sha));
            var labels = new List<CommitBranchLabel>();
            foreach (var commit in commits)
            {
                var labelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (labelsBySha.TryGetValue(commit.Sha, out var primaryLabel) && !string.IsNullOrWhiteSpace(primaryLabel))
                {
                    labels.Add(new CommitBranchLabel
                    {
                        SessionId = sessionId,
                        Sha = commit.Sha,
                        BranchName = primaryLabel!,
                        IsPrimary = true,
                        LabelMethod = "name-rev",
                        CapturedAt = capturedAt
                    });
                    labelSet.Add(primaryLabel!);
                }

                if (containsBySha.TryGetValue(commit.Sha, out var branches))
                {
                    foreach (var branch in branches)
                    {
                        if (!labelSet.Add(branch))
                            continue;

                        labels.Add(new CommitBranchLabel
                        {
                            SessionId = sessionId,
                            Sha = commit.Sha,
                            BranchName = branch,
                            IsPrimary = false,
                            LabelMethod = "branch-contains",
                            CapturedAt = capturedAt
                        });
                    }
                }

                if (labelSet.Count == 0)
                {
                    labels.Add(new CommitBranchLabel
                    {
                        SessionId = sessionId,
                        Sha = commit.Sha,
                        BranchName = "(unattributed)",
                        IsPrimary = true,
                        LabelMethod = "none",
                        CapturedAt = capturedAt
                    });
                }
            }

            if (labels.Count > 0)
            {
                _logger.LogInfo($"Persisting {labels.Count} commit branch labels");
                await _databaseService.BatchInsertCommitBranchLabelsAsync(labels);
            }

            if (options.TrackIntegrations)
            {
                var mergeCommits = parsedCommits
                    .Where(p => p.Parents.Count >= 2)
                    .ToList();

                _logger.LogInfo($"Integration tracking enabled. Merge commits in scope: {mergeCommits.Count}");
                var integrationEvents = new List<IntegrationEvent>();
                var integrationEventCommits = new List<IntegrationEventCommit>();

                foreach (var merge in mergeCommits)
                {
                    var parent1 = merge.Parents[0];
                    var parent2 = merge.Parents[1];
                    var integratedShas = await _gitService.GetIntegratedCommitsAsync(repoPath, parent1, parent2);

                    var evt = new IntegrationEvent
                    {
                        SessionId = sessionId,
                        AnchorSha = merge.Commit.Sha,
                        OccurredAt = merge.Commit.AuthorDate,
                        Method = "MergeCommit",
                        Confidence = "High",
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            parent1,
                            parent2,
                            integratedCount = integratedShas.Count
                        })
                    };

                    integrationEvents.Add(evt);
                    foreach (var sha in integratedShas)
                    {
                        integrationEventCommits.Add(new IntegrationEventCommit
                        {
                            IntegrationEventId = evt.Id,
                            SessionId = sessionId,
                            Sha = sha
                        });
                    }
                }

                if (integrationEvents.Count > 0)
                {
                    await _databaseService.BatchInsertIntegrationEventsAsync(integrationEvents);
                }

                if (integrationEventCommits.Count > 0)
                {
                    await _databaseService.BatchInsertIntegrationEventCommitsAsync(integrationEventCommits);
                }
            }

            var patchUpdates = new List<(string Sha, string PatchId)>();
            var missingPatchId = commits.Where(c => string.IsNullOrWhiteSpace(c.PatchId)).ToList();
            foreach (var commit in missingPatchId)
            {
                var patchId = await _gitService.GetPatchIdAsync(repoPath, commit.Sha);
                if (!string.IsNullOrWhiteSpace(patchId))
                {
                    commit.PatchId = patchId;
                    patchUpdates.Add((commit.Sha, patchId));
                }
            }

            if (patchUpdates.Count > 0)
            {
                await _databaseService.UpdateCommitPatchIdsAsync(sessionId, patchUpdates);
            }

            var patchGroups = await _databaseService.GetPatchIdGroupsAsync(sessionId);
            if (patchGroups.Count > 0)
            {
                var patchEvents = new List<IntegrationEvent>();
                var patchEventCommits = new List<IntegrationEventCommit>();
                foreach (var (patchId, shas) in patchGroups)
                {
                    var id = $"patch:{patchId}";
                    var evt = new IntegrationEvent
                    {
                        Id = id,
                        SessionId = sessionId,
                        AnchorSha = shas.LastOrDefault(),
                        OccurredAt = DateTime.UtcNow,
                        Method = "PatchMatch",
                        Confidence = "Medium",
                        DetailsJson = JsonSerializer.Serialize(new { patchId, count = shas.Count })
                    };
                    patchEvents.Add(evt);
                    foreach (var sha in shas)
                    {
                        patchEventCommits.Add(new IntegrationEventCommit
                        {
                            IntegrationEventId = id,
                            SessionId = sessionId,
                            Sha = sha
                        });
                    }
                }

                await _databaseService.BatchInsertIntegrationEventsAsync(patchEvents);
                await _databaseService.BatchInsertIntegrationEventCommitsAsync(patchEventCommits);
            }

            progress?.Report(new MiningProgress
            {
                Status = $"Stored {commits.Count} commits in database",
                ProcessedItems = 2,
                TotalItems = 4
            });

            // Step 4: Aggregate by day
            _logger.LogInfo("Step 4: Aggregating commits by day...");
            progress?.Report(new MiningProgress
            {
                Status = "Aggregating commits by day...",
                ProcessedItems = 3,
                TotalItems = 4
            });
            var aggregationCommits = options.IncludeMerges
                ? commits
                : commits.Where(c => !c.IsMerge).ToList();

            var dayGroups = aggregationCommits
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
                Status = $"Complete! Mined {result.DaysMined} days, {result.StoredCommits} commits.",
                ProcessedItems = 4,
                TotalItems = 4
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
                Status = $"Error: {ex.Message}",
                ProcessedItems = 0,
                TotalItems = 0
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
