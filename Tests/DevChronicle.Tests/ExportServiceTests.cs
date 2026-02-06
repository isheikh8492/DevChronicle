using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class ExportServiceTests
{
    [Fact]
    public async Task ExportAsync_ReturnsError_WhenNoSessionsSelected()
    {
        var db = new DatabaseService();
        var service = new ExportService(db);

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
        var db = new DatabaseService();
        var service = new ExportService(db);

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
}
