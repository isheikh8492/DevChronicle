using DevChronicle.Services;
using DevChronicle.Models;
using Xunit;

namespace DevChronicle.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task GetDefaultSessionOptionsAsync_ReturnsExpectedDefaults()
    {
        var db = new DatabaseService();
        var settings = new SettingsService(db);

        var options = await settings.GetDefaultSessionOptionsAsync();

        Assert.True(options.WindowSizeDays > 0);
        Assert.True(options.MaxBulletsPerDay > 0);
        Assert.True(options.OverlapDays >= 0);
        Assert.False(string.IsNullOrWhiteSpace(options.BackfillOrder));
        Assert.Equal(RefScope.LocalBranchesOnly, options.RefScope);
    }

    [Fact]
    public async Task AppSettings_RoundTripValue()
    {
        var db = new DatabaseService();
        var settings = new SettingsService(db);
        var key = $"tests.key.{Guid.NewGuid():N}";

        await settings.SetAsync(key, 123);
        var value = await settings.GetAsync(key, 0);

        Assert.Equal(123, value);
    }
}
