using DevChronicle.Services;

namespace DevChronicle.Tests;

internal sealed class TestDb : IDisposable
{
    private readonly string _dbPath;

    public DatabaseService Db { get; }

    private TestDb(string dbPath)
    {
        _dbPath = dbPath;
        Db = new DatabaseService(dbPath);
    }

    public static TestDb Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DevChronicleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "devchronicle.tests.db");
        return new TestDb(dbPath);
    }

    public void Dispose()
    {
        try
        {
            // Best-effort cleanup; some runners may keep handles around briefly.
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);

            var dbDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir) && Directory.Exists(dbDir))
                Directory.Delete(dbDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
