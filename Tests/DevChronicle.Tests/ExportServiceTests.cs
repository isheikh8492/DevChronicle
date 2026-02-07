using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class ExportServiceTests
{
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevChronicleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<int> CreateSessionAsync(DatabaseService db, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = $"TestSession_{Guid.NewGuid():N}";

        var session = new DevChronicle.Models.Session
        {
            Name = name,
            RepoPath = @"C:\Repo\Fake",
            MainBranch = "main",
            CreatedAt = DateTime.UtcNow,
            AuthorFiltersJson = "[]",
            OptionsJson = "{}",
            RangeStart = null,
            RangeEnd = null
        };

        return await db.CreateSessionAsync(session);
    }

    private static async Task CreateDayAsync(DatabaseService db, int sessionId, DateTime day, DevChronicle.Models.DayStatus status)
    {
        await db.UpsertDayAsync(new DevChronicle.Models.Day
        {
            SessionId = sessionId,
            Date = day,
            CommitCount = 1,
            Additions = 1,
            Deletions = 0,
            Status = status
        });
    }

    private static async Task CreateSummaryAsync(DatabaseService db, int sessionId, DateTime day, string bullets, DateTime createdAtUtc)
    {
        await db.UpsertDaySummaryAsync(new DevChronicle.Models.DaySummary
        {
            SessionId = sessionId,
            Day = day,
            BulletsText = bullets,
            Model = "test",
            PromptVersion = "v1",
            InputHash = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAtUtc
        });
    }

    [Fact]
    public async Task ExportAsync_ReturnsError_WhenNoSessionsSelected()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);

        var result = await service.ExportAsync(new ExportRequest
        {
            SessionIds = Array.Empty<int>(),
            ExportDiary = true,
            ExportArchive = false,
            Format = ExportFormat.MarkdownOnly,
            OutputDirectory = Path.GetTempPath(),
            HideRepoPathsInMarkdown = true,
            IncludePlaceholders = true,
            CancellationToken = CancellationToken.None
        });

        Assert.False(result.Succeeded);
        Assert.Equal("No sessions selected.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExportAsync_ReturnsError_WhenOutputDirectoryMissing()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);

        var result = await service.ExportAsync(new ExportRequest
        {
            SessionIds = new[] { 1 },
            ExportDiary = true,
            ExportArchive = false,
            Format = ExportFormat.MarkdownOnly,
            OutputDirectory = "",
            HideRepoPathsInMarkdown = true,
            IncludePlaceholders = true,
            CancellationToken = CancellationToken.None
        });

        Assert.False(result.Succeeded);
        Assert.Equal("No output directory selected.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExportAsync_WritesDiaryWithManifestHeader()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);

        var day = DateTime.UtcNow.Date;
        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Did something", DateTime.UtcNow);

        var outputDir = CreateTempDirectory();
        var result = await service.ExportAsync(new ExportRequest
        {
            SessionIds = new[] { sessionId },
            ExportDiary = true,
            ExportArchive = false,
            Format = ExportFormat.MarkdownOnly,
            OutputDirectory = outputDir,
            HideRepoPathsInMarkdown = true,
            IncludePlaceholders = true,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Succeeded);
        var diaryFile = result.FilesWritten.Single(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var firstLine = File.ReadLines(diaryFile).FirstOrDefault();
        Assert.NotNull(firstLine);
        Assert.Contains("DC:MANIFEST", firstLine);
    }

    [Fact]
    public async Task PreviewDiaryUpdateAsync_Succeeds_ForManagedDiary()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);

        var day = DateTime.UtcNow.Date;
        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Did something", DateTime.UtcNow);

        var outputDir = CreateTempDirectory();
        var export = await service.ExportAsync(new ExportRequest
        {
            SessionIds = new[] { sessionId },
            ExportDiary = true,
            ExportArchive = false,
            Format = ExportFormat.MarkdownOnly,
            OutputDirectory = outputDir,
            HideRepoPathsInMarkdown = true,
            IncludePlaceholders = true,
            CancellationToken = CancellationToken.None
        });

        var diaryPath = export.FilesWritten.Single(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var preview = await service.PreviewDiaryUpdateAsync(new UpdateDiaryRequest
        {
            DiaryPath = diaryPath,
            CancellationToken = CancellationToken.None
        });

        Assert.True(preview.Result.Succeeded);
    }

    [Fact]
    public async Task PreviewDiaryUpdateAsync_ReturnsUnmanagedError_WhenNoManifest()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "unmanaged.md");
        await File.WriteAllTextAsync(path, "# Notes\n\n- Just text");

        var preview = await service.PreviewDiaryUpdateAsync(new UpdateDiaryRequest
        {
            DiaryPath = path,
            CancellationToken = CancellationToken.None
        });

        Assert.False(preview.Result.Succeeded);
        Assert.Contains("Unmanaged diary", preview.Result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateDiaryAsync_UpdatesLastSyncedAt()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);

        var day = DateTime.UtcNow.Date;
        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Did something", DateTime.UtcNow.AddMinutes(-10));

        var outputDir = CreateTempDirectory();
        var export = await service.ExportAsync(new ExportRequest
        {
            SessionIds = new[] { sessionId },
            ExportDiary = true,
            ExportArchive = false,
            Format = ExportFormat.MarkdownOnly,
            OutputDirectory = outputDir,
            HideRepoPathsInMarkdown = true,
            IncludePlaceholders = true,
            CancellationToken = CancellationToken.None
        });

        var diaryPath = export.FilesWritten.Single(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var beforeLine = File.ReadLines(diaryPath).First();

        var update = await service.UpdateDiaryAsync(new UpdateDiaryRequest
        {
            DiaryPath = diaryPath,
            CancellationToken = CancellationToken.None
        });

        Assert.True(update.result.Succeeded);
        var afterLine = File.ReadLines(diaryPath).First();

        var before = ExtractLastSyncedAtUtc(beforeLine);
        var after = ExtractLastSyncedAtUtc(afterLine);
        Assert.True(after >= before);
    }

    [Fact]
    public async Task UpdateDiaryAsync_DiffCountsReflectChanges()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);
        var day = new DateTime(2020, 1, 1);

        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Updated summary", new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var outputDir = CreateTempDirectory();
        var diaryPath = Path.Combine(outputDir, "managed.md");

        var manifest = $"<!-- DC:MANIFEST {{\"schemaVersion\":\"1.0\",\"diaryType\":\"multi\",\"sessionIds\":[{sessionId}],\"options\":{{\"hideRepoPaths\":true,\"includePlaceholders\":true,\"summaryPolicy\":\"latestCreatedAt\"}},\"createdAt\":\"2020-01-01T00:00:00Z\",\"lastSyncedAt\":\"2020-01-01T00:00:00Z\"}} -->";
        var content = string.Join(Environment.NewLine, new[]
        {
            manifest,
            "<!-- DC:DAY day=2020-01-01 -->",
            "## 2020-01-01",
            $"<!-- DC:ENTRY day=2020-01-01 session={sessionId} summaryCreatedAt=2020-01-01T00:00:00Z -->",
            "- Old summary",
            "<!-- /DC:ENTRY -->",
            "<!-- /DC:DAY -->"
        });

        await File.WriteAllTextAsync(diaryPath, content);

        var preview = await service.PreviewDiaryUpdateAsync(new UpdateDiaryRequest
        {
            DiaryPath = diaryPath,
            CancellationToken = CancellationToken.None
        });

        Assert.True(preview.Result.Succeeded);
        Assert.True(preview.Diff.IsStale);
        Assert.True(preview.Diff.New + preview.Diff.Updated + preview.Diff.Unchanged > 0);
    }

    [Fact]
    public async Task ConvertUnmanagedDiaryToManagedAsync_CreatesNewFile()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);
        var day = DateTime.UtcNow.Date;

        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Did something", DateTime.UtcNow);

        var dir = CreateTempDirectory();
        var sourcePath = Path.Combine(dir, "notes.md");
        await File.WriteAllTextAsync(sourcePath, "# Notes\n\n- Manual notes");

        var outputPath = await service.ConvertUnmanagedDiaryToManagedAsync(
            sourcePath,
            new[] { sessionId },
            hideRepoPaths: true,
            includePlaceholders: true,
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(sourcePath, outputPath);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task ExportAsync_CancellationDoesNotLeaveFinalFile()
    {
        using var testDb = TestDb.Create();
        var service = new ExportService(testDb.Db);
        var sessionId = await CreateSessionAsync(testDb.Db);
        var day = DateTime.UtcNow.Date;

        await CreateDayAsync(testDb.Db, sessionId, day, DevChronicle.Models.DayStatus.Summarized);
        await CreateSummaryAsync(testDb.Db, sessionId, day, "- Did something", DateTime.UtcNow);

        var outputDir = CreateTempDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.ExportAsync(new ExportRequest
            {
                SessionIds = new[] { sessionId },
                ExportDiary = true,
                ExportArchive = false,
                Format = ExportFormat.MarkdownOnly,
                OutputDirectory = outputDir,
                HideRepoPathsInMarkdown = true,
                IncludePlaceholders = true,
                CancellationToken = cts.Token
            }));

        Assert.Empty(Directory.GetFiles(outputDir, "*.md"));
    }

    private static DateTime ExtractLastSyncedAtUtc(string manifestLine)
    {
        var start = manifestLine.IndexOf('{');
        var end = manifestLine.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Manifest JSON not found.");

        var json = manifestLine.Substring(start, end - start + 1);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var value = doc.RootElement.GetProperty("lastSyncedAt").GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("lastSyncedAt missing.");

        return DateTime.Parse(
            value,
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
