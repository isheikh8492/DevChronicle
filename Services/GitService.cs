using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class GitService
{
    private const string CommitPrefix = "COMMIT|";

    private static string BuildGitAuthorArg(AuthorFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Email))
        {
            // `--author` is a regex matched against "Name <email>". Treat input literally.
            // Wrapping with angle brackets makes "email" match only the email portion.
            var email = GitAuthorRegex.EscapeLiteral(filter.Email.Trim());
            return $" --author=\"<{email}>\"";
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var name = GitAuthorRegex.EscapeLiteral(filter.Name.Trim());
            return $" --author=\"{name}\"";
        }

        return string.Empty;
    }

    private static string BuildGitCommitterArg(AuthorFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Email))
        {
            var email = GitAuthorRegex.EscapeLiteral(filter.Email.Trim());
            return $" --committer=\"<{email}>\"";
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var name = GitAuthorRegex.EscapeLiteral(filter.Name.Trim());
            return $" --committer=\"{name}\"";
        }

        return string.Empty;
    }

    private static string BuildIdentityArg(AuthorFilter filter, IdentityMatchMode mode) =>
        mode switch
        {
            IdentityMatchMode.CommitterOnly => BuildGitCommitterArg(filter),
            _ => BuildGitAuthorArg(filter)
        };

    private static void AppendIdentityFilters(StringBuilder args, List<AuthorFilter>? filters, IdentityMatchMode mode)
    {
        if (filters == null || filters.Count == 0)
            return;

        foreach (var filter in filters)
        {
            args.Append(BuildIdentityArg(filter, mode));
        }
    }

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
        IdentityMatchMode identityMatchMode = IdentityMatchMode.AuthorOnly,
        bool includeMerges = false)
    {
        var args = new StringBuilder("log --all --pretty=format:%H|%aI|%an|%ae|%s");

        if (!includeMerges)
            args.Append(" --no-merges");

        if (since.HasValue)
            args.Append($" --since=\"{since.Value:yyyy-MM-dd}\"");

        if (until.HasValue)
            args.Append($" --until=\"{until.Value:yyyy-MM-dd}\"");

        AppendIdentityFilters(args, authorFilters, identityMatchMode);

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

    public async Task<List<ParsedCommit>> GetCommitsWithNumstatAsync(
        string repoPath,
        int sessionId,
        DateTime? since,
        DateTime? until,
        List<AuthorFilter>? authorFilters,
        IdentityMatchMode identityMatchMode,
        RefScope refScope,
        bool includeMerges,
        HashSet<string>? knownShas = null)
    {
        if (identityMatchMode == IdentityMatchMode.AuthorOrCommitter && authorFilters != null && authorFilters.Count > 0)
        {
            var author = await GetCommitsWithNumstatCoreAsync(
                repoPath,
                sessionId,
                since,
                until,
                authorFilters,
                IdentityMatchMode.AuthorOnly,
                refScope,
                includeMerges,
                knownShas);

            var committer = await GetCommitsWithNumstatCoreAsync(
                repoPath,
                sessionId,
                since,
                until,
                authorFilters,
                IdentityMatchMode.CommitterOnly,
                refScope,
                includeMerges,
                knownShas);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<ParsedCommit>(author.Count + committer.Count);
            foreach (var p in author)
            {
                if (seen.Add(p.Commit.Sha))
                    merged.Add(p);
            }
            foreach (var p in committer)
            {
                if (seen.Add(p.Commit.Sha))
                    merged.Add(p);
            }

            return merged;
        }

        return await GetCommitsWithNumstatCoreAsync(
            repoPath,
            sessionId,
            since,
            until,
            authorFilters,
            identityMatchMode,
            refScope,
            includeMerges,
            knownShas);
    }

    private async Task<List<ParsedCommit>> GetCommitsWithNumstatCoreAsync(
        string repoPath,
        int sessionId,
        DateTime? since,
        DateTime? until,
        List<AuthorFilter>? authorFilters,
        IdentityMatchMode identityMatchMode,
        RefScope refScope,
        bool includeMerges,
        HashSet<string>? knownShas)
    {
        var args = new StringBuilder("log ");
        args.Append(GetRefScopeArgs(refScope));
        args.Append(" --date=iso-strict --pretty=format:COMMIT|%H|%P|%aI|%an|%ae|%cI|%cn|%ce|%s --numstat");

        if (!includeMerges)
            args.Append(" --no-merges");

        if (since.HasValue)
            args.Append($" --since=\"{since.Value:yyyy-MM-ddTHH:mm:ss}\"");

        if (until.HasValue)
            args.Append($" --until=\"{until.Value:yyyy-MM-ddTHH:mm:ss}\"");

        AppendIdentityFilters(args, authorFilters, identityMatchMode);

        var result = await RunGitCommandAsync(repoPath, args.ToString());
        if (!result.Success)
            return new List<ParsedCommit>();

        var parsed = new List<ParsedCommit>();
        var lines = result.Output.Split('\n');
        ParsedCommit? current = null;
        var currentFiles = new List<CommitFile>();
        var skipNumstat = false;

        void FinalizeCurrent()
        {
            if (current == null)
                return;

            current.Commit.FilesJson = JsonSerializer.Serialize(currentFiles);
            parsed.Add(current);
            current = null;
            currentFiles = new List<CommitFile>();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith(CommitPrefix, StringComparison.Ordinal))
            {
                FinalizeCurrent();

                var parts = line.Split('|', 10);
                if (parts.Length < 10)
                    continue;

                if (!DateTime.TryParse(parts[3], out var authorDate))
                    continue;

                if (!DateTime.TryParse(parts[6], out var committerDate))
                    committerDate = authorDate;

                var parentShas = parts[2]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                var commit = new Commit
                {
                    SessionId = sessionId,
                    Sha = parts[1].Trim(),
                    AuthorDate = authorDate,
                    AuthorName = parts[4],
                    AuthorEmail = parts[5],
                    CommitterDate = committerDate,
                    CommitterName = parts[7],
                    CommitterEmail = parts[8],
                    Subject = parts[9],
                    IsMerge = parentShas.Count > 1
                };

                current = new ParsedCommit
                {
                    Commit = commit,
                    Parents = parentShas
                };

                skipNumstat = knownShas != null && knownShas.Contains(commit.Sha);
                continue;
            }

            if (current == null)
                continue;

            if (skipNumstat)
                continue;

            var statParts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (statParts.Length < 3)
                continue;

            var path = statParts[2].Trim();
            var additions = ParseNumstatValue(statParts[0]);
            var deletions = ParseNumstatValue(statParts[1]);

            currentFiles.Add(new CommitFile
            {
                Path = path,
                Additions = additions,
                Deletions = deletions
            });

            current.Commit.Additions += additions;
            current.Commit.Deletions += deletions;
        }

        FinalizeCurrent();
        return parsed;
    }

    public async Task<List<ParsedCommit>> GetCommitsByShasWithNumstatAsync(
        string repoPath,
        int sessionId,
        IEnumerable<string> shas)
    {
        var shaList = shas.Distinct().ToList();
        if (shaList.Count == 0)
            return new List<ParsedCommit>();

        var args = $"log --no-walk --date=iso-strict --pretty=format:COMMIT|%H|%P|%aI|%an|%ae|%cI|%cn|%ce|%s --numstat {string.Join(' ', shaList)}";
        var result = await RunGitCommandAsync(repoPath, args);
        if (!result.Success)
            return new List<ParsedCommit>();

        return ParseCommitsWithNumstatOutput(result.Output, sessionId, null);
    }

    public async Task<List<string>> GetReflogCommitsAsync(
        string repoPath,
        DateTime? since,
        DateTime? until,
        List<AuthorFilter>? authorFilters = null,
        IdentityMatchMode identityMatchMode = IdentityMatchMode.AuthorOnly)
    {
        if (identityMatchMode == IdentityMatchMode.AuthorOrCommitter && authorFilters != null && authorFilters.Count > 0)
        {
            var author = await GetReflogCommitsCoreAsync(repoPath, since, until, authorFilters, IdentityMatchMode.AuthorOnly);
            var committer = await GetReflogCommitsCoreAsync(repoPath, since, until, authorFilters, IdentityMatchMode.CommitterOnly);
            return author.Concat(committer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return await GetReflogCommitsCoreAsync(repoPath, since, until, authorFilters, identityMatchMode);
    }

    private async Task<List<string>> GetReflogCommitsCoreAsync(
        string repoPath,
        DateTime? since,
        DateTime? until,
        List<AuthorFilter>? authorFilters,
        IdentityMatchMode identityMatchMode)
    {
        var args = new StringBuilder("log -g --all --pretty=format:%H");

        if (since.HasValue)
            args.Append($" --since=\"{since.Value:yyyy-MM-ddTHH:mm:ss}\"");

        if (until.HasValue)
            args.Append($" --until=\"{until.Value:yyyy-MM-ddTHH:mm:ss}\"");

        AppendIdentityFilters(args, authorFilters, identityMatchMode);

        var result = await RunGitCommandAsync(repoPath, args.ToString());
        if (!result.Success)
            return new List<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Dictionary<string, List<string>>> GetBranchContainmentAsync(string repoPath, IEnumerable<string> shas)
    {
        var shaList = shas.Distinct().ToList();
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in shaList)
        {
            var result = await RunGitCommandAsync(repoPath, $"branch --contains {sha}");
            if (!result.Success)
                continue;

            var branches = result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => b.Trim().TrimStart('*').Trim())
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToList();

            if (branches.Count > 0)
                map[sha] = branches;
        }

        return map;
    }

    public async Task<string?> GetPatchIdAsync(string repoPath, string sha)
    {
        var args = $"/c git show {sha} --pretty=format: --unified=0 | git patch-id --stable";
        var result = await RunGitCommandAsync(repoPath, args, useShell: true);
        if (!result.Success)
            return null;

        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    public async Task<List<BranchSnapshot>> GetBranchTipsAsync(string repoPath, int sessionId, DateTime capturedAt)
    {
        var result = await RunGitCommandAsync(
            repoPath,
            "for-each-ref refs/heads --format=\"%(refname:short)|%(objectname)|%(committerdate:iso8601)\"");

        if (!result.Success)
            return new List<BranchSnapshot>();

        var snapshots = new List<BranchSnapshot>();
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
                continue;

            DateTime? headDate = null;
            if (parts.Length >= 3 && DateTime.TryParse(parts[2], out var parsedDate))
                headDate = parsedDate;

            snapshots.Add(new BranchSnapshot
            {
                SessionId = sessionId,
                CapturedAt = capturedAt,
                RefName = parts[0].Trim(),
                HeadSha = parts[1].Trim(),
                HeadDate = headDate,
                IsRemote = false
            });
        }

        return snapshots;
    }

    public async Task<Dictionary<string, string?>> GetNameRevLabelsAsync(string repoPath, IEnumerable<string> shas)
    {
        var shaList = shas.Distinct().ToList();
        var results = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (shaList.Count == 0)
            return results;

        const int chunkSize = 200;
        for (var i = 0; i < shaList.Count; i += chunkSize)
        {
            var chunk = shaList.Skip(i).Take(chunkSize).ToList();
            var args = $"name-rev --name-only --refs=refs/heads/* {string.Join(' ', chunk)}";
            var output = await RunGitCommandAsync(repoPath, args);
            if (!output.Success)
                continue;

            var lines = output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < chunk.Count; j++)
            {
                var label = j < lines.Length ? lines[j].Trim() : null;
                results[chunk[j]] = NormalizeBranchLabel(label);
            }
        }

        return results;
    }

    public async Task<List<string>> GetIntegratedCommitsAsync(string repoPath, string parent1Sha, string parent2Sha)
    {
        var result = await RunGitCommandAsync(repoPath, $"rev-list {parent2Sha} ^{parent1Sha}");
        if (!result.Success)
            return new List<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int ParseNumstatValue(string value)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;

        return 0;
    }

    private static string GetRefScopeArgs(RefScope scope) =>
        scope switch
        {
            RefScope.LocalPlusRemotes => "--branches --remotes",
            RefScope.AllRefs => "--all",
            _ => "--branches"
        };

    private static string? NormalizeBranchLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label == "undefined")
            return null;

        if (label.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            label = label.Substring("refs/heads/".Length);

        var tildeIndex = label.IndexOf('~');
        if (tildeIndex > 0)
            label = label.Substring(0, tildeIndex);

        var caretIndex = label.IndexOf('^');
        if (caretIndex > 0)
            label = label.Substring(0, caretIndex);

        return label;
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

    public async Task<GitCommandResult> RunGitCommandAsync(string repoPath, string arguments, bool useShell = false)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = useShell ? "cmd.exe" : "git",
                Arguments = useShell ? arguments : arguments,
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

    private static List<ParsedCommit> ParseCommitsWithNumstatOutput(string output, int sessionId, HashSet<string>? knownShas)
    {
        var parsed = new List<ParsedCommit>();
        var lines = output.Split('\n');
        ParsedCommit? current = null;
        var currentFiles = new List<CommitFile>();
        var skipNumstat = false;

        void FinalizeCurrent()
        {
            if (current == null)
                return;

            current.Commit.FilesJson = JsonSerializer.Serialize(currentFiles);
            parsed.Add(current);
            current = null;
            currentFiles = new List<CommitFile>();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith(CommitPrefix, StringComparison.Ordinal))
            {
                FinalizeCurrent();

                var parts = line.Split('|', 10);
                if (parts.Length < 10)
                    continue;

                if (!DateTime.TryParse(parts[3], out var authorDate))
                    continue;

                if (!DateTime.TryParse(parts[6], out var committerDate))
                    committerDate = authorDate;

                var parentShas = parts[2]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                var commit = new Commit
                {
                    SessionId = sessionId,
                    Sha = parts[1].Trim(),
                    AuthorDate = authorDate,
                    AuthorName = parts[4],
                    AuthorEmail = parts[5],
                    CommitterDate = committerDate,
                    CommitterName = parts[7],
                    CommitterEmail = parts[8],
                    Subject = parts[9],
                    IsMerge = parentShas.Count > 1
                };

                current = new ParsedCommit
                {
                    Commit = commit,
                    Parents = parentShas
                };

                skipNumstat = knownShas != null && knownShas.Contains(commit.Sha);
                continue;
            }

            if (current == null)
                continue;

            if (skipNumstat)
                continue;

            var statParts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (statParts.Length < 3)
                continue;

            var path = statParts[2].Trim();
            var additions = ParseNumstatValue(statParts[0]);
            var deletions = ParseNumstatValue(statParts[1]);

            currentFiles.Add(new CommitFile
            {
                Path = path,
                Additions = additions,
                Deletions = deletions
            });

            current.Commit.Additions += additions;
            current.Commit.Deletions += deletions;
        }

        FinalizeCurrent();
        return parsed;
    }
}

public static class GitAuthorRegex
{
    // Conservative escaping for git's `--author` regex (works for both BRE/ERE usage).
    // Git matches this regex against "Name <email>".
    public static string EscapeLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (IsRegexMeta(c))
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsRegexMeta(char c) =>
        c is '\\' or '.' or '^' or '$' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or ']' or '{' or '}';
}

public class GitCommandResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}
