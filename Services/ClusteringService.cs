using System.Text.Json;
using System.Text.RegularExpressions;
using DevChronicle.Models;

namespace DevChronicle.Services;

/// <summary>
/// Groups commits into work units based on folders and keywords for LLM compression
/// </summary>
public class ClusteringService
{
    private static readonly Dictionary<string, string[]> KeywordMap = new()
    {
        ["bugfix"] = new[] { "fix", "bug", "crash", "null", "edge", "error", "issue" },
        ["performance"] = new[] { "perf", "speed", "cache", "memory", "optimize" },
        ["refactor"] = new[] { "refactor", "cleanup", "rename", "restructure" },
        ["testing"] = new[] { "test", "ci", "coverage", "spec" },
        ["ui"] = new[] { "ui", "qml", "wpf", "toolbar", "dialog", "button", "view" },
        ["documentation"] = new[] { "doc", "readme", "comment", "guide" }
    };

    public List<WorkUnit> ClusterCommits(List<Commit> commits)
    {
        var workUnits = new List<WorkUnit>();

        // Group by top-level folder first
        var folderGroups = commits
            .SelectMany(c =>
            {
                var files = JsonSerializer.Deserialize<List<CommitFile>>(c.FilesJson) ?? new List<CommitFile>();
                var folders = files.Select(f => GetTopLevelFolder(f.Path)).Distinct();
                return folders.Select(folder => new { Folder = folder, Commit = c });
            })
            .GroupBy(x => x.Folder)
            .OrderByDescending(g => g.Count());

        foreach (var folderGroup in folderGroups)
        {
            var folderCommits = folderGroup.Select(x => x.Commit).Distinct().ToList();

            // Further categorize by keyword if there are many commits
            if (folderCommits.Count > 10)
            {
                var categoryGroups = folderCommits
                    .GroupBy(c => CategorizeByKeyword(c.Subject))
                    .OrderByDescending(g => g.Count());

                foreach (var categoryGroup in categoryGroups)
                {
                    workUnits.Add(new WorkUnit
                    {
                        Folder = folderGroup.Key,
                        Category = categoryGroup.Key,
                        Commits = categoryGroup.OrderByDescending(c => c.Additions + c.Deletions).ToList(),
                        TotalAdditions = categoryGroup.Sum(c => c.Additions),
                        TotalDeletions = categoryGroup.Sum(c => c.Deletions)
                    });
                }
            }
            else
            {
                workUnits.Add(new WorkUnit
                {
                    Folder = folderGroup.Key,
                    Category = CategorizeByKeyword(string.Join(" ", folderCommits.Select(c => c.Subject))),
                    Commits = folderCommits.OrderByDescending(c => c.Additions + c.Deletions).ToList(),
                    TotalAdditions = folderCommits.Sum(c => c.Additions),
                    TotalDeletions = folderCommits.Sum(c => c.Deletions)
                });
            }
        }

        return workUnits.OrderByDescending(wu => wu.TotalAdditions + wu.TotalDeletions).ToList();
    }

    private string GetTopLevelFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "root";

        var normalized = filePath.Replace('\\', '/');
        var firstSlash = normalized.IndexOf('/');

        if (firstSlash > 0)
            return normalized.Substring(0, firstSlash);

        return "root";
    }

    private string CategorizeByKeyword(string subject)
    {
        var lowerSubject = subject.ToLowerInvariant();

        foreach (var category in KeywordMap)
        {
            if (category.Value.Any(keyword => lowerSubject.Contains(keyword)))
                return category.Key;
        }

        return "general";
    }

    public string GenerateWorkUnitSummary(WorkUnit workUnit, int maxCommits = 5)
    {
        var topCommits = workUnit.Commits.Take(maxCommits).ToList();
        var remaining = workUnit.Commits.Count - topCommits.Count;

        var summary = $"[{workUnit.Folder}] {workUnit.Category} ({workUnit.Commits.Count} commits, +{workUnit.TotalAdditions}/-{workUnit.TotalDeletions}):\n";

        foreach (var commit in topCommits)
        {
            summary += $"  - {commit.Sha.Substring(0, 7)}: {commit.Subject} (+{commit.Additions}/-{commit.Deletions})\n";
        }

        if (remaining > 0)
            summary += $"  ... and {remaining} more commits\n";

        return summary;
    }
}

public class WorkUnit
{
    public string Folder { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<Commit> Commits { get; set; } = new();
    public int TotalAdditions { get; set; }
    public int TotalDeletions { get; set; }
}
