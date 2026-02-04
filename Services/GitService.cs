using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class GitService
{
    public async Task<bool> IsValidRepositoryAsync(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return false;

        var result = await RunGitCommandAsync(repoPath, "rev-parse --git-dir");
        return result.Success;
    }

    public async Task<GitCommandResult> FetchAllAsync(string repoPath)
    {
        return await RunGitCommandAsync(repoPath, "fetch --all --prune --tags");
    }

    public async Task<List<Commit>> GetCommitsAsync(
        string repoPath,
        int sessionId,
        DateTime? since = null,
        DateTime? until = null,
        List<AuthorFilter>? authorFilters = null,
        bool includeMerges = false)
    {
        var args = new StringBuilder("log --all --pretty=format:%H|%aI|%an|%ae|%s");

        if (!includeMerges)
            args.Append(" --no-merges");

        if (since.HasValue)
            args.Append($" --since=\"{since.Value:yyyy-MM-dd}\"");

        if (until.HasValue)
            args.Append($" --until=\"{until.Value:yyyy-MM-dd}\"");

        if (authorFilters != null && authorFilters.Count > 0)
        {
            foreach (var filter in authorFilters)
            {
                if (!string.IsNullOrWhiteSpace(filter.Email))
                    args.Append($" --author=\"{filter.Email}\"");
                else if (!string.IsNullOrWhiteSpace(filter.Name))
                    args.Append($" --author=\"{filter.Name}\"");
            }
        }

        var result = await RunGitCommandAsync(repoPath, args.ToString());
        if (!result.Success)
            return new List<Commit>();

        var commits = new List<Commit>();
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 5) continue;

            // Safe DateTime parsing with TryParse
            if (!DateTime.TryParse(parts[1], out var authorDate))
            {
                // Skip commits with invalid dates
                continue;
            }

            var commit = new Commit
            {
                SessionId = sessionId,
                Sha = parts[0].Trim(),
                AuthorDate = authorDate,
                AuthorName = parts[2],
                AuthorEmail = parts[3],
                Subject = parts[4]
            };

            // Enrich with file stats
            await EnrichCommitWithStatsAsync(repoPath, commit);
            commits.Add(commit);
        }

        return commits;
    }

    private async Task EnrichCommitWithStatsAsync(string repoPath, Commit commit)
    {
        var result = await RunGitCommandAsync(repoPath, $"show --numstat --pretty=format: {commit.Sha}");
        if (!result.Success) return;

        var files = new List<CommitFile>();
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);

            // Validate parts before accessing
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
                continue;

            if (int.TryParse(parts[0], out var additions) && int.TryParse(parts[1], out var deletions))
            {
                files.Add(new CommitFile
                {
                    Path = parts[2].Trim(),  // Trim whitespace
                    Additions = additions,
                    Deletions = deletions
                });

                commit.Additions += additions;
                commit.Deletions += deletions;
            }
        }

        commit.FilesJson = JsonSerializer.Serialize(files);
    }

    public async Task<GitCommandResult> RunGitCommandAsync(string repoPath, string arguments)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    output.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    error.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            return new GitCommandResult
            {
                Success = process.ExitCode == 0,
                Output = output.ToString(),
                Error = error.ToString(),
                ExitCode = process.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new GitCommandResult
            {
                Success = false,
                Error = ex.Message,
                ExitCode = -1
            };
        }
    }
}

public class GitCommandResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}
