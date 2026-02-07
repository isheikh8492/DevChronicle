using DevChronicle.Services;
using DevChronicle.Models;
using Xunit;

namespace DevChronicle.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task GetDefaultSessionOptionsAsync_ReturnsExpectedDefaults()
    {
        using var testDb = TestDb.Create();
        var settings = new SettingsService(testDb.Db);

        var options = await settings.GetDefaultSessionOptionsAsync();

        Assert.True(options.WindowSizeDays > 0);
        Assert.True(options.MaxBulletsPerDay > 0);
        Assert.True(options.OverlapDays >= 0);
        Assert.False(string.IsNullOrWhiteSpace(options.BackfillOrder));
        Assert.Equal(RefScope.LocalBranchesOnly, options.RefScope);
        Assert.Equal(IdentityMatchMode.AuthorOrCommitter, options.IdentityMatchMode);
    }

    [Fact]
    public async Task AppSettings_RoundTripValue()
    {
        using var testDb = TestDb.Create();
        var settings = new SettingsService(testDb.Db);
        var key = $"tests.key.{Guid.NewGuid():N}";

        await settings.SetAsync(key, 123);
        var value = await settings.GetAsync(key, 0);

        Assert.Equal(123, value);
    }
}
