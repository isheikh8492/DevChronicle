using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class ExportService
{
    private readonly DatabaseService _databaseService;

    public ExportService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request)
    {
        if (request.SessionIds.Count == 0)
        {
            return new ExportResult
            {
                Succeeded = false,
                ErrorMessage = "No sessions selected."
            };
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            return new ExportResult
            {
                Succeeded = false,
                ErrorMessage = "No output directory selected."
            };
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var exportedAtUtc = DateTime.UtcNow;
        var timestamp = exportedAtUtc.ToString("yyyyMMdd_HHmmss");
        var result = new ExportResult { Succeeded = true };
        var shouldWriteDiary = request.ExportDiary && (request.Format == ExportFormat.MarkdownAndJson || request.Format == ExportFormat.MarkdownOnly);
        var shouldWriteArchive = request.ExportArchive && (request.Format == ExportFormat.MarkdownAndJson || request.Format == ExportFormat.JsonOnly);
        var totalSteps = 2 + (shouldWriteArchive ? 1 : 0) + (shouldWriteDiary ? 1 : 0) + 1;
        var currentStep = 0;

        void Report(string status)
        {
            currentStep++;
            request.ProgressReporter?.Report(new ExportProgress
            {
                Status = status,
                CurrentStep = currentStep,
                TotalSteps = totalSteps
            });
        }

        Report("Loading sessions...");
        var sessions = await _databaseService.GetSessionsByIdsAsync(request.SessionIds);
        var sessionsById = sessions.ToDictionary(s => s.Id);

        // Load export inputs in bulk (one row per day/summary, potentially many commits).
        Report("Loading day and summary data...");
        var days = await _databaseService.GetDaysInRangeForSessionsAsync(request.SessionIds, null, null);
        var summaries = await _databaseService.GetLatestEffectiveDaySummariesInRangeForSessionsAsync(request.SessionIds, null, null);
        var commits = new List<Commit>();
        if (shouldWriteArchive)
        {
            Report("Loading commit evidence...");
            commits = await _databaseService.GetCommitsInRangeForSessionsAsync(request.SessionIds, null, null);
        }

        request.CancellationToken.ThrowIfCancellationRequested();

        var diaryFileName = string.IsNullOrWhiteSpace(request.DiaryFileName)
            ? $"DevDiary_{timestamp}.md"
            : Path.GetFileName(request.DiaryFileName);
        var archiveFileName = string.IsNullOrWhiteSpace(request.ArchiveFileName)
            ? $"DevArchive_{timestamp}.json"
            : Path.GetFileName(request.ArchiveFileName);

        var diaryPath = Path.Combine(request.OutputDirectory, diaryFileName);
        var archivePath = Path.Combine(request.OutputDirectory, archiveFileName);

        try
        {
            if (shouldWriteDiary)
            {
                Report("Writing diary...");
                await WriteAtomicAsync(
                    diaryPath,
                    stream => WriteDiaryAsync(stream, sessions, days, summaries, exportedAtUtc, hideRepoPaths: request.HideRepoPathsInMarkdown, includePlaceholders: request.IncludePlaceholders, cancellationToken: request.CancellationToken),
                    request.CancellationToken);

                result.FilesWritten.Add(diaryPath);
            }

            if (shouldWriteArchive)
            {
                Report("Writing archive...");
                await WriteAtomicAsync(
                    archivePath,
                    stream => WriteArchiveAsync(stream, sessions, days, summaries, commits, exportedAtUtc, request.CancellationToken),
                    request.CancellationToken);

                result.FilesWritten.Add(archivePath);
            }
        }
        catch (OperationCanceledException)
        {
            return new ExportResult
            {
                Succeeded = false,
                Canceled = true,
                FilesWritten = result.FilesWritten,
                Warnings = result.Warnings
            };
        }
        catch (Exception ex)
        {
            return new ExportResult
            {
                Succeeded = false,
                ErrorMessage = ex.Message,
                FilesWritten = result.FilesWritten,
                Warnings = result.Warnings
            };
        }

        // Warn for missing sessions (if some IDs didn't resolve).
        foreach (var id in request.SessionIds)
        {
            if (!sessionsById.ContainsKey(id))
                result.Warnings.Add($"Session {id} not found.");
        }

        Report("Export complete.");

        return result;
    }

    public async Task<(DiaryDiff diff, ExportResult result)> UpdateDiaryAsync(UpdateDiaryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DiaryPath))
        {
            return (new DiaryDiff(), new ExportResult
            {
                Succeeded = false,
                ErrorMessage = "No diary path provided."
            });
        }

        var exportedAtUtc = DateTime.UtcNow;
        var totalSteps = 6;
        var currentStep = 0;

        void Report(string status)
        {
            currentStep++;
            request.ProgressReporter?.Report(new ExportProgress
            {
                Status = status,
                CurrentStep = currentStep,
                TotalSteps = totalSteps
            });
        }

        try
        {
            Report("Reading diary file...");
            var originalText = await File.ReadAllTextAsync(request.DiaryPath, request.CancellationToken);
            Report("Parsing manifest...");
            var manifest = TryParseManifest(originalText);
            if (manifest == null)
            {
                return (new DiaryDiff(), new ExportResult
                {
                    Succeeded = false,
                    ErrorMessage = "Selected diary is not a managed DevChronicle diary (missing DC:MANIFEST)."
                });
            }

            request.CancellationToken.ThrowIfCancellationRequested();

            Report("Loading session/day/summary data...");
            var sessionIds = manifest.SessionIds;
            var sessions = await _databaseService.GetSessionsByIdsAsync(sessionIds);
            var days = await _databaseService.GetDaysInRangeForSessionsAsync(sessionIds, null, null);
            var summaries = await _databaseService.GetLatestEffectiveDaySummariesInRangeForSessionsAsync(sessionIds, null, null);

            request.CancellationToken.ThrowIfCancellationRequested();

            Report("Computing diary diff...");
            var diff = ComputeDiaryDiff(originalText, manifest, days, summaries);

            // Regenerate the entire managed day/entry section deterministically and splice it into the file.
            Report("Rendering managed diary blocks...");
            var updatedManifest = manifest with { LastSyncedAtUtc = exportedAtUtc };
            var regeneratedBlocks = await RenderDiaryManagedBlocksToStringAsync(sessions, days, summaries, updatedManifest.Options.HideRepoPaths, updatedManifest.Options.IncludePlaceholders, request.CancellationToken);

            var updatedText = ReplaceDiaryManagedSectionAndManifest(originalText, updatedManifest.ToManifestLine(), regeneratedBlocks);

            Report("Writing updated diary...");
            await WriteAtomicAsync(
                request.DiaryPath,
                async stream =>
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
                    await writer.WriteAsync(updatedText);
                    await writer.FlushAsync();
                },
                request.CancellationToken,
                createBackup: true);

            return (diff, new ExportResult
            {
                Succeeded = true,
                FilesWritten = new List<string> { request.DiaryPath }
            });
        }
        catch (OperationCanceledException)
        {
            return (new DiaryDiff(), new ExportResult
            {
                Succeeded = false,
                Canceled = true
            });
        }
        catch (Exception ex)
        {
            return (new DiaryDiff(), new ExportResult
            {
                Succeeded = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public async Task<DiaryPreviewResult> PreviewDiaryUpdateAsync(UpdateDiaryRequest request)
    {
        var totalSteps = 4;
        var currentStep = 0;

        void Report(string status)
        {
            currentStep++;
            request.ProgressReporter?.Report(new ExportProgress
            {
                Status = status,
                CurrentStep = currentStep,
                TotalSteps = totalSteps
            });
        }

        try
        {
            Report("Reading diary file...");
            var diaryText = await File.ReadAllTextAsync(request.DiaryPath, request.CancellationToken);
            Report("Parsing manifest...");
            var manifest = TryParseManifest(diaryText);
            if (manifest == null)
            {
                return new DiaryPreviewResult
                {
                    Result = new ExportResult
                    {
                        Succeeded = false,
                        ErrorMessage = "Unmanaged diary (missing DC:MANIFEST)."
                    },
                    Diff = new DiaryDiff(),
                    BoundSessionIds = new List<int>()
                };
            }

            request.CancellationToken.ThrowIfCancellationRequested();

            Report("Loading day and summary data...");
            var sessionIds = manifest.SessionIds;
            var days = await _databaseService.GetDaysInRangeForSessionsAsync(sessionIds, null, null);
            var summaries = await _databaseService.GetLatestEffectiveDaySummariesInRangeForSessionsAsync(sessionIds, null, null);
            Report("Computing diff preview...");
            var diff = ComputeDiaryDiff(diaryText, manifest, days, summaries);

            return new DiaryPreviewResult
            {
                Result = new ExportResult { Succeeded = true },
                Diff = diff,
                BoundSessionIds = sessionIds.ToList()
            };
        }
        catch (OperationCanceledException)
        {
            return new DiaryPreviewResult
            {
                Result = new ExportResult { Succeeded = false, Canceled = true },
                Diff = new DiaryDiff(),
                BoundSessionIds = new List<int>()
            };
        }
        catch (Exception ex)
        {
            return new DiaryPreviewResult
            {
                Result = new ExportResult { Succeeded = false, ErrorMessage = ex.Message },
                Diff = new DiaryDiff(),
                BoundSessionIds = new List<int>()
            };
        }
    }

    public async Task<string> ConvertUnmanagedDiaryToManagedAsync(
        string inputDiaryPath,
        IReadOnlyList<int> sessionIds,
        bool hideRepoPaths,
        bool includePlaceholders,
        CancellationToken cancellationToken,
        IProgress<ExportProgress>? progressReporter = null)
    {
        if (sessionIds.Count == 0)
            throw new InvalidOperationException("No sessions provided for conversion.");

        var exportedAtUtc = DateTime.UtcNow;
        var totalSteps = 4;
        var currentStep = 0;

        void Report(string status)
        {
            currentStep++;
            progressReporter?.Report(new ExportProgress
            {
                Status = status,
                CurrentStep = currentStep,
                TotalSteps = totalSteps
            });
        }

        Report("Loading session/day/summary data...");
        var sessions = await _databaseService.GetSessionsByIdsAsync(sessionIds);
        var days = await _databaseService.GetDaysInRangeForSessionsAsync(sessionIds, null, null);
        var summaries = await _databaseService.GetLatestEffectiveDaySummariesInRangeForSessionsAsync(sessionIds, null, null);

        var inputDir = Path.GetDirectoryName(inputDiaryPath) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(inputDiaryPath);
        var outputPath = Path.Combine(inputDir, baseName + ".managed.md");

        if (File.Exists(outputPath))
            outputPath = Path.Combine(inputDir, baseName + ".managed_" + exportedAtUtc.ToString("yyyyMMdd_HHmmss") + ".md");

        Report("Rendering managed diary section...");
        var manifest = DiaryManifest.CreateMulti(sessionIds.ToList(), exportedAtUtc, hideRepoPaths, includePlaceholders);
        var managedBlocks = await RenderDiaryManagedBlocksToStringAsync(sessions, days, summaries, hideRepoPaths, includePlaceholders, cancellationToken);

        Report("Writing converted diary...");
        await WriteAtomicAsync(
            outputPath,
            async stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
                await writer.WriteLineAsync(manifest.ToManifestLine());
                await writer.WriteLineAsync();

                // Preserve the unmanaged file verbatim.
                using (var reader = new StreamReader(new FileStream(inputDiaryPath, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.UTF8))
                {
                    var unmanaged = await reader.ReadToEndAsync(cancellationToken);
                    await writer.WriteLineAsync(unmanaged.TrimEnd());
                }

                await writer.WriteLineAsync();
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("## DevChronicle Generated Section");
                await writer.WriteLineAsync();
                await writer.WriteAsync(managedBlocks);
                await writer.FlushAsync();
            },
            cancellationToken);

        Report("Conversion complete.");

        return outputPath;
    }

    private static async Task WriteAtomicAsync(string finalPath, Func<Stream, Task> writeFunc, CancellationToken cancellationToken, bool createBackup = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tmpPath = finalPath + ".tmp";
        var bakPath = finalPath + ".bak";

        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.SequentialScan))
            {
                await writeFunc(fs);
                await fs.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(finalPath))
            {
                if (createBackup)
                {
                    File.Replace(tmpPath, finalPath, bakPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Delete(finalPath);
                    File.Move(tmpPath, finalPath);
                }
            }
            else
            {
                File.Move(tmpPath, finalPath);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // ignore cleanup errors
            }

            throw;
        }
    }

    private static async Task WriteDiaryAsync(
        Stream stream,
        List<Session> sessions,
        List<DevChronicle.Models.Day> days,
        List<DaySummary> summaries,
        DateTime exportedAtUtc,
        bool hideRepoPaths,
        bool includePlaceholders,
        CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);

        var manifest = DiaryManifest.CreateMulti(sessions.Select(s => s.Id).ToList(), exportedAtUtc, hideRepoPaths, includePlaceholders);

        await writer.WriteLineAsync(manifest.ToManifestLine());
        await writer.WriteLineAsync("# Developer Diary");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"Exported: {exportedAtUtc:yyyy-MM-ddTHH:mm:ssZ}");
        await writer.WriteLineAsync();

        await WriteDiaryManagedBlocksAsync(writer, sessions, days, summaries, hideRepoPaths, includePlaceholders, cancellationToken);

        await writer.FlushAsync();
    }

    private static async Task<string> RenderDiaryManagedBlocksToStringAsync(
        List<Session> sessions,
        List<DevChronicle.Models.Day> days,
        List<DaySummary> summaries,
        bool hideRepoPaths,
        bool includePlaceholders,
        CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true))
        {
            await WriteDiaryManagedBlocksAsync(writer, sessions, days, summaries, hideRepoPaths, includePlaceholders, cancellationToken);
            await writer.FlushAsync();
        }
        ms.Position = 0;
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteDiaryManagedBlocksAsync(
        TextWriter writer,
        List<Session> sessions,
        List<DevChronicle.Models.Day> days,
        List<DaySummary> summaries,
        bool hideRepoPaths,
        bool includePlaceholders,
        CancellationToken cancellationToken)
    {
        var sessionMeta = sessions
            .Select(s => new SessionMeta(s))
            .OrderBy(s => s.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SessionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SessionId)
            .ToList();

        var daySetBySession = days
            .GroupBy(d => d.SessionId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Date.Date).ToHashSet());

        var summaryByKey = summaries.ToDictionary(s => (s.SessionId, s.Day.Date));

        var allDates = new HashSet<DateTime>();
        foreach (var kvp in daySetBySession)
            foreach (var d in kvp.Value)
                allDates.Add(d);
        foreach (var k in summaryByKey.Keys)
            allDates.Add(k.Item2);

        var orderedDates = allDates.OrderBy(d => d).ToList();

        foreach (var day in orderedDates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dayKey = day.ToString("yyyy-MM-dd");
            await writer.WriteLineAsync($"<!-- DC:DAY day={dayKey} -->");
            await writer.WriteLineAsync($"## {dayKey}");

            foreach (var s in sessionMeta)
            {
                var hasDay = daySetBySession.TryGetValue(s.SessionId, out var sessionDays) && sessionDays.Contains(day);
                var hasSummary = summaryByKey.TryGetValue((s.SessionId, day), out var summary);
                if (!hasDay && !hasSummary)
                    continue;

                var summaryCreatedAt = hasSummary ? summary!.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") : "none";
                await writer.WriteLineAsync($"<!-- DC:ENTRY day={dayKey} session={s.SessionId} summaryCreatedAt={summaryCreatedAt} -->");
                await writer.WriteLineAsync($"### {s.RepoName}");

                if (hasSummary && !string.IsNullOrWhiteSpace(summary!.BulletsText))
                {
                    await writer.WriteLineAsync(summary.BulletsText.TrimEnd());
                }
                else if (includePlaceholders)
                {
                    await writer.WriteLineAsync("(No summary yet)");
                }

                await writer.WriteLineAsync("<!-- /DC:ENTRY -->");
                await writer.WriteLineAsync();
            }

            await writer.WriteLineAsync("<!-- /DC:DAY -->");
            await writer.WriteLineAsync();
        }
    }

    private static async Task WriteArchiveAsync(
        Stream stream,
        List<Session> sessions,
        List<DevChronicle.Models.Day> days,
        List<DaySummary> summaries,
        List<Commit> commits,
        DateTime exportedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        var sessionMeta = sessions
            .Select(s => new SessionMeta(s))
            .OrderBy(s => s.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SessionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SessionId)
            .ToList();

        var daysBySession = days
            .GroupBy(d => d.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());

        var summaryByKey = summaries.ToDictionary(s => (s.SessionId, s.Day.Date));

        var commitsBySession = commits
            .GroupBy(c => c.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.AuthorDate).ThenBy(c => c.Sha, StringComparer.OrdinalIgnoreCase).ToList());

        jsonWriter.WriteStartObject();
        jsonWriter.WriteString("schemaVersion", "1.0");
        jsonWriter.WriteString("exportedAt", exportedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        jsonWriter.WriteStartArray("sessions");

        foreach (var s in sessionMeta)
        {
            cancellationToken.ThrowIfCancellationRequested();

            jsonWriter.WriteStartObject();
            jsonWriter.WriteStartObject("session");
            jsonWriter.WriteNumber("id", s.SessionId);
            jsonWriter.WriteString("name", s.SessionName);
            jsonWriter.WriteString("repoPath", s.RepoPath);
            jsonWriter.WriteString("repoName", s.RepoName);
            jsonWriter.WriteString("createdAt", s.CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            jsonWriter.WriteString("rangeDisplay", s.RangeDisplay);
            jsonWriter.WriteEndObject(); // session

            jsonWriter.WriteStartArray("days");

            var sessionDays = daysBySession.TryGetValue(s.SessionId, out var list) ? list : new List<DevChronicle.Models.Day>();
            var sessionCommits = commitsBySession.TryGetValue(s.SessionId, out var clist) ? clist : new List<Commit>();

            // Build lookup of commits by day for this session.
            var commitsByDay = sessionCommits
                .GroupBy(c => c.AuthorDate.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var day in sessionDays.OrderBy(d => d.Date))
            {
                cancellationToken.ThrowIfCancellationRequested();

                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("date", day.Date.ToString("yyyy-MM-dd"));
                jsonWriter.WriteString("status", day.Status.ToString());
                jsonWriter.WriteStartObject("stats");
                jsonWriter.WriteNumber("commitCount", day.CommitCount);
                jsonWriter.WriteNumber("additions", day.Additions);
                jsonWriter.WriteNumber("deletions", day.Deletions);
                jsonWriter.WriteEndObject();

                if (summaryByKey.TryGetValue((s.SessionId, day.Date.Date), out var summary))
                {
                    jsonWriter.WriteStartObject("summary");
                    jsonWriter.WriteString("createdAt", summary.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    jsonWriter.WriteString("promptVersion", summary.PromptVersion);
                    jsonWriter.WriteString("bulletsText", summary.BulletsText);
                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteStartArray("commits");
                if (commitsByDay.TryGetValue(day.Date.Date, out var dayCommits))
                {
                    foreach (var c in dayCommits.OrderBy(c => c.AuthorDate).ThenBy(c => c.Sha, StringComparer.OrdinalIgnoreCase))
                    {
                        jsonWriter.WriteStartObject();
                        jsonWriter.WriteString("sha", c.Sha);
                        jsonWriter.WriteString("authorDate", c.AuthorDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
                        jsonWriter.WriteString("authorName", c.AuthorName);
                        jsonWriter.WriteString("authorEmail", c.AuthorEmail);
                        jsonWriter.WriteString("subject", c.Subject);
                        jsonWriter.WriteNumber("additions", c.Additions);
                        jsonWriter.WriteNumber("deletions", c.Deletions);

                        jsonWriter.WriteStartArray("files");
                        foreach (var f in ParseFilesJson(c.FilesJson).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            jsonWriter.WriteStartObject();
                            jsonWriter.WriteString("path", f.Path);
                            jsonWriter.WriteNumber("additions", f.Additions);
                            jsonWriter.WriteNumber("deletions", f.Deletions);
                            jsonWriter.WriteEndObject();
                        }
                        jsonWriter.WriteEndArray();

                        jsonWriter.WriteEndObject();
                    }
                }
                jsonWriter.WriteEndArray(); // commits

                jsonWriter.WriteEndObject(); // day
            }

            jsonWriter.WriteEndArray(); // days
            jsonWriter.WriteEndObject(); // session wrapper
        }

        jsonWriter.WriteEndArray();
        jsonWriter.WriteEndObject();
        await jsonWriter.FlushAsync(cancellationToken);
    }

    private static List<CommitFile> ParseFilesJson(string filesJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filesJson))
                return new List<CommitFile>();

            var files = JsonSerializer.Deserialize<List<CommitFile>>(filesJson);
            return files ?? new List<CommitFile>();
        }
        catch
        {
            return new List<CommitFile>();
        }
    }

    private static DiaryManifest? TryParseManifest(string diaryText)
    {
        using var reader = new StringReader(diaryText);
        for (var i = 0; i < 20; i++)
        {
            var line = reader.ReadLine();
            if (line == null)
                break;

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("<!-- DC:MANIFEST ", StringComparison.Ordinal))
                continue;

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            var json = trimmed.Substring(start, end - start + 1);
            return DiaryManifest.FromJson(json);
        }

        return null;
    }

    private static DiaryDiff ComputeDiaryDiff(string diaryText, DiaryManifest manifest, List<DevChronicle.Models.Day> days, List<DaySummary> latestSummaries)
    {
        var entryRegex = new System.Text.RegularExpressions.Regex(
            @"<!--\\s*DC:ENTRY\\s+day=(?<day>\\d{4}-\\d{2}-\\d{2})\\s+session=(?<session>\\d+)\\s+summaryCreatedAt=(?<sca>[^\\s]+)\\s*-->",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var existing = entryRegex.Matches(diaryText)
            .Select(m => new
            {
                Day = DateTime.Parse(m.Groups["day"].Value).Date,
                SessionId = int.Parse(m.Groups["session"].Value),
                SummaryCreatedAt = m.Groups["sca"].Value
            })
            .ToDictionary(x => (x.SessionId, x.Day), x => x.SummaryCreatedAt);

        var bound = manifest.SessionIds.ToHashSet();
        var summaryCreatedByKey = latestSummaries
            .Where(s => bound.Contains(s.SessionId))
            .ToDictionary(s => (s.SessionId, s.Day.Date), s => s.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));

        var idealKeys = new HashSet<(int SessionId, DateTime Day)>();
        foreach (var d in days.Where(d => bound.Contains(d.SessionId)))
            idealKeys.Add((d.SessionId, d.Date.Date));
        foreach (var k in summaryCreatedByKey.Keys)
            idealKeys.Add((k.Item1, k.Item2.Date));

        string IdealCreatedAt((int SessionId, DateTime Day) k)
        {
            return summaryCreatedByKey.TryGetValue(k, out var createdAt) ? createdAt : "none";
        }

        var newCount = idealKeys.Count(k => !existing.ContainsKey(k));
        var updatedCount = idealKeys.Count(k => existing.TryGetValue(k, out var oldVal) && !string.Equals(oldVal, IdealCreatedAt(k), StringComparison.OrdinalIgnoreCase));
        var unchangedCount = idealKeys.Count(k => existing.TryGetValue(k, out var oldVal) && string.Equals(oldVal, IdealCreatedAt(k), StringComparison.OrdinalIgnoreCase));
        var extraCount = existing.Keys.Count(k => !idealKeys.Contains(k));

        var latestCreated = latestSummaries
            .Where(s => manifest.SessionIds.Contains(s.SessionId))
            .Select(s => s.CreatedAt.ToUniversalTime())
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var isStale = latestCreated > manifest.LastSyncedAtUtc;

        return new DiaryDiff
        {
            New = newCount,
            Updated = updatedCount,
            Unchanged = unchangedCount,
            Extra = extraCount,
            IsStale = isStale
        };
    }

    private static string ReplaceDiaryManagedSectionAndManifest(string originalText, string updatedManifestLine, string regeneratedManagedBlocks)
    {
        // Replace manifest line (single-line replacement) and replace the entire managed DC:DAY block section.
        var manifestRegex = new System.Text.RegularExpressions.Regex(@"^\\s*<!--\\s*DC:MANIFEST\\s+\\{.*\\}\\s*-->\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        var textWithManifest = manifestRegex.Replace(originalText, updatedManifestLine, count: 1);

        var firstDay = textWithManifest.IndexOf("<!-- DC:DAY", StringComparison.Ordinal);
        var lastDayClose = textWithManifest.LastIndexOf("<!-- /DC:DAY -->", StringComparison.Ordinal);
        if (firstDay < 0 || lastDayClose < 0)
        {
            // No managed section yet; append blocks at end.
            return textWithManifest.TrimEnd() + Environment.NewLine + Environment.NewLine + regeneratedManagedBlocks;
        }

        var end = lastDayClose + "<!-- /DC:DAY -->".Length;
        var prefix = textWithManifest.Substring(0, firstDay);
        var suffix = textWithManifest.Substring(end);
        return prefix + regeneratedManagedBlocks + suffix;
    }

    private sealed class SessionMeta
    {
        public SessionMeta(Session session)
        {
            SessionId = session.Id;
            SessionName = session.Name;
            RepoPath = session.RepoPath;
            CreatedAtUtc = session.CreatedAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Local).ToUniversalTime() : session.CreatedAt.ToUniversalTime();
            RepoName = GetRepoName(session.RepoPath);
            RangeDisplay = session.RangeStart.HasValue && session.RangeEnd.HasValue
                ? $"{session.RangeStart:yyyy-MM-dd} -> {session.RangeEnd:yyyy-MM-dd}"
                : "All history";
        }

        public int SessionId { get; }
        public string SessionName { get; }
        public string RepoPath { get; }
        public string RepoName { get; }
        public DateTime CreatedAtUtc { get; }
        public string RangeDisplay { get; }

        private static string GetRepoName(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                return "(unknown repo)";
            var trimmed = repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
    }

    private record DiaryManifest(string SchemaVersion, string DiaryType, List<int> SessionIds, DiaryOptions Options, DateTime CreatedAtUtc, DateTime LastSyncedAtUtc)
    {
        public static DiaryManifest CreateMulti(List<int> sessionIds, DateTime exportedAtUtc, bool hideRepoPaths, bool includePlaceholders)
        {
            sessionIds.Sort();
            return new DiaryManifest(
                SchemaVersion: "1.0",
                DiaryType: "multi",
                SessionIds: sessionIds,
                Options: new DiaryOptions(hideRepoPaths, includePlaceholders, "latestCreatedAt"),
                CreatedAtUtc: exportedAtUtc,
                LastSyncedAtUtc: exportedAtUtc);
        }

        public string ToManifestLine()
        {
            var payload = new
            {
                schemaVersion = SchemaVersion,
                diaryType = DiaryType,
                sessionIds = SessionIds,
                options = new
                {
                    hideRepoPaths = Options.HideRepoPaths,
                    includePlaceholders = Options.IncludePlaceholders,
                    summaryPolicy = Options.SummaryPolicy
                },
                createdAt = CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                lastSyncedAt = LastSyncedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            return $"<!-- DC:MANIFEST {JsonSerializer.Serialize(payload)} -->";
        }

        public static DiaryManifest FromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var schemaVersion = root.GetProperty("schemaVersion").GetString() ?? "1.0";
            var diaryType = root.GetProperty("diaryType").GetString() ?? "multi";

            var ids = root.GetProperty("sessionIds").EnumerateArray().Select(e => e.GetInt32()).OrderBy(x => x).ToList();

            var optionsEl = root.GetProperty("options");
            var hideRepoPaths = optionsEl.TryGetProperty("hideRepoPaths", out var hrp) && hrp.GetBoolean();
            var includePlaceholders = optionsEl.TryGetProperty("includePlaceholders", out var ip) && ip.GetBoolean();
            var summaryPolicy = optionsEl.TryGetProperty("summaryPolicy", out var sp) ? (sp.GetString() ?? "latestCreatedAt") : "latestCreatedAt";

            var createdAt = root.TryGetProperty("createdAt", out var ca) ? ParseUtc(ca.GetString()) : DateTime.UtcNow;
            var lastSyncedAt = root.TryGetProperty("lastSyncedAt", out var lsa) ? ParseUtc(lsa.GetString()) : createdAt;

            return new DiaryManifest(schemaVersion, diaryType, ids, new DiaryOptions(hideRepoPaths, includePlaceholders, summaryPolicy), createdAt, lastSyncedAt);
        }

        private static DateTime ParseUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DateTime.UtcNow;
            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            return DateTime.UtcNow;
        }
    }

    private record DiaryOptions(bool HideRepoPaths, bool IncludePlaceholders, string SummaryPolicy);
}
