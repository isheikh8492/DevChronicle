using DevChronicle.Models;
using Xunit;

namespace DevChronicle.Tests;

public class DaySummaryOverrideTests
{
    [Fact]
    public async Task GetEffectiveDaySummaryAsync_PrefersOverride_WhenPresent()
    {
        using var testDb = TestDb.Create();
        var db = testDb.Db;

        var sessionId = await db.CreateSessionAsync(new Session
        {
            Name = $"TestSession_{Guid.NewGuid():N}",
            RepoPath = @"C:\Repo\Fake",
            MainBranch = "main",
            CreatedAt = DateTime.UtcNow,
            AuthorFiltersJson = "[]",
            OptionsJson = "{}"
        });

        var day = new DateTime(2020, 1, 1);
        await db.UpsertDaySummaryAsync(new DaySummary
        {
            SessionId = sessionId,
            Day = day,
            BulletsText = "- AI bullet",
            Model = "test",
            PromptVersion = "v1",
            InputHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        await db.UpsertDaySummaryOverrideAsync(new DaySummaryOverride
        {
            SessionId = sessionId,
            Day = day,
            BulletsText = "- Manual bullet",
            UpdatedAt = DateTime.UtcNow
        });

        var (effective, isOverride) = await db.GetEffectiveDaySummaryAsync(sessionId, day);

        Assert.True(isOverride);
        Assert.NotNull(effective);
        Assert.Equal("- Manual bullet", effective!.BulletsText);
    }

    [Fact]
    public async Task GetEffectiveDaySummaryAsync_FallsBackToAi_WhenOverrideDeleted()
    {
        using var testDb = TestDb.Create();
        var db = testDb.Db;

        var sessionId = await db.CreateSessionAsync(new Session
        {
            Name = $"TestSession_{Guid.NewGuid():N}",
            RepoPath = @"C:\Repo\Fake",
            MainBranch = "main",
            CreatedAt = DateTime.UtcNow,
            AuthorFiltersJson = "[]",
            OptionsJson = "{}"
        });

        var day = new DateTime(2020, 1, 1);
        await db.UpsertDaySummaryAsync(new DaySummary
        {
            SessionId = sessionId,
            Day = day,
            BulletsText = "- AI bullet",
            Model = "test",
            PromptVersion = "v1",
            InputHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow
        });

        await db.UpsertDaySummaryOverrideAsync(new DaySummaryOverride
        {
            SessionId = sessionId,
            Day = day,
            BulletsText = "- Manual bullet",
            UpdatedAt = DateTime.UtcNow
        });

        await db.DeleteDaySummaryOverrideAsync(sessionId, day);

        var (effective, isOverride) = await db.GetEffectiveDaySummaryAsync(sessionId, day);

        Assert.False(isOverride);
        Assert.NotNull(effective);
        Assert.Equal("- AI bullet", effective!.BulletsText);
    }
}

