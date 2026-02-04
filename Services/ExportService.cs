using System.IO;
using System.Text;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class ExportService
{
    private readonly DatabaseService _databaseService;

    public ExportService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<string> ExportDeveloperDiaryAsync(
        int sessionId,
        DateTime startDate,
        DateTime endDate,
        string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Developer Diary");
        sb.AppendLine();
        sb.AppendLine($"**Period:** {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        sb.AppendLine();

        var days = (await _databaseService.GetDaysAsync(sessionId))
            .Where(d => d.Date >= startDate && d.Date <= endDate)
            .OrderBy(d => d.Date);

        foreach (var day in days)
        {
            // TODO: Get day summary from database
            sb.AppendLine($"## {day.Date:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine($"**Commits:** {day.CommitCount} | **Changes:** +{day.Additions}/-{day.Deletions}");
            sb.AppendLine();

            var commits = await _databaseService.GetCommitsForDayAsync(sessionId, day.Date);
            foreach (var commit in commits)
            {
                sb.AppendLine($"- [{commit.Sha.Substring(0, 7)}] {commit.Subject}");
            }

            sb.AppendLine();
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString());
        return outputPath;
    }

    public async Task<string> ExportResumeBulletsAsync(
        int sessionId,
        DateTime startDate,
        DateTime endDate,
        string outputPath,
        int maxBullets = 12)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Resume Bullets");
        sb.AppendLine();
        sb.AppendLine($"**Period:** {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        sb.AppendLine();

        // TODO: Get summaries and extract top bullets
        // For now, generate from commits
        var days = (await _databaseService.GetDaysAsync(sessionId))
            .Where(d => d.Date >= startDate && d.Date <= endDate)
            .OrderByDescending(d => d.Additions + d.Deletions)
            .Take(maxBullets);

        foreach (var day in days)
        {
            var commits = (await _databaseService.GetCommitsForDayAsync(sessionId, day.Date)).ToList();
            if (commits.Any())
            {
                var topCommit = commits.OrderByDescending(c => c.Additions + c.Deletions).First();
                sb.AppendLine($"- {topCommit.Subject} (+{day.Additions}/-{day.Deletions} lines)");
            }
        }

        sb.AppendLine();

        await File.WriteAllTextAsync(outputPath, sb.ToString());
        return outputPath;
    }
}
