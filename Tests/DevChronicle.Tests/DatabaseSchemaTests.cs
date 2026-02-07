using Dapper;
using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class DatabaseSchemaTests
{
    [Fact]
    public void Database_HasCoreTables()
    {
        using var testDb = TestDb.Create();
        using var connection = testDb.Db.GetConnection();
        connection.Open();

        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table'")
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

        Assert.Contains("sessions", tables);
        Assert.Contains("days", tables);
        Assert.Contains("commits", tables);
        Assert.Contains("day_summaries", tables);
    }
}
