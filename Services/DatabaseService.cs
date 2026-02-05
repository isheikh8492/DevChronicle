using System.IO;
using Dapper;
using DevChronicle.Models;
using Microsoft.Data.Sqlite;

namespace DevChronicle.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public DatabaseService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevChronicle");

        Directory.CreateDirectory(appDataPath);
        _dbPath = Path.Combine(appDataPath, "devchronicle.db");
        _connectionString = $"Data Source={_dbPath}";

        InitializeDatabase();
    }

    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private void InitializeDatabase()
    {
        using var connection = GetConnection();
        connection.Open();

        // Create sessions table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                repo_path TEXT NOT NULL,
                main_branch TEXT NOT NULL DEFAULT 'main',
                created_at TEXT NOT NULL,
                author_filters_json TEXT NOT NULL DEFAULT '[]',
                options_json TEXT NOT NULL DEFAULT '{}',
                range_start TEXT,
                range_end TEXT
            )");

        // Create app settings table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )");

        // Create checkpoints table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS checkpoints (
                session_id INTEGER NOT NULL,
                phase TEXT NOT NULL,
                cursor_key TEXT NOT NULL,
                cursor_value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (session_id, phase, cursor_key),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        // Create commits table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS commits (
                session_id INTEGER NOT NULL,
                sha TEXT NOT NULL,
                author_date TEXT NOT NULL,
                author_name TEXT NOT NULL,
                author_email TEXT NOT NULL,
                subject TEXT NOT NULL,
                additions INTEGER NOT NULL DEFAULT 0,
                deletions INTEGER NOT NULL DEFAULT 0,
                files_json TEXT NOT NULL DEFAULT '[]',
                is_merge INTEGER NOT NULL DEFAULT 0,
                reachable_from_main INTEGER,
                PRIMARY KEY (session_id, sha),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        // Create index on author_date for faster queries
        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_commits_date
            ON commits(session_id, author_date)");

        // Create days table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS days (
                session_id INTEGER NOT NULL,
                day TEXT NOT NULL,
                commit_count INTEGER NOT NULL DEFAULT 0,
                additions INTEGER NOT NULL DEFAULT 0,
                deletions INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'mined',
                PRIMARY KEY (session_id, day),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        // Create day_summaries table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS day_summaries (
                session_id INTEGER NOT NULL,
                day TEXT NOT NULL,
                bullets_text TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                input_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (session_id, day, prompt_version),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        // Create resume_summaries table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS resume_summaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL,
                range_start TEXT NOT NULL,
                range_end TEXT NOT NULL,
                bullets_text TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                input_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");
    }

    // Session operations
    public async Task<int> CreateSessionAsync(Session session)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO sessions (name, repo_path, main_branch, created_at, author_filters_json, options_json, range_start, range_end)
            VALUES (@Name, @RepoPath, @MainBranch, @CreatedAt, @AuthorFiltersJson, @OptionsJson, @RangeStart, @RangeEnd);
            SELECT last_insert_rowid();";

        return await connection.QuerySingleAsync<int>(sql, session);
    }

    public async Task<IEnumerable<Session>> GetAllSessionsAsync()
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            SELECT
                id AS Id,
                name AS Name,
                repo_path AS RepoPath,
                main_branch AS MainBranch,
                created_at AS CreatedAt,
                author_filters_json AS AuthorFiltersJson,
                options_json AS OptionsJson,
                range_start AS RangeStart,
                range_end AS RangeEnd
            FROM sessions
            ORDER BY created_at DESC";
        return await connection.QueryAsync<Session>(sql);
    }

    public async Task<Session?> GetSessionAsync(int id)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            SELECT
                id AS Id,
                name AS Name,
                repo_path AS RepoPath,
                main_branch AS MainBranch,
                created_at AS CreatedAt,
                author_filters_json AS AuthorFiltersJson,
                options_json AS OptionsJson,
                range_start AS RangeStart,
                range_end AS RangeEnd
            FROM sessions
            WHERE id = @Id";
        return await connection.QueryFirstOrDefaultAsync<Session>(sql, new { Id = id });
    }

    public async Task DeleteSessionAsync(int id)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM sessions WHERE id = @Id", new { Id = id });
    }

    public async Task UpdateSessionOptionsAsync(int sessionId, string optionsJson)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE sessions SET options_json = @OptionsJson WHERE id = @Id",
            new { OptionsJson = optionsJson, Id = sessionId });
    }

    public async Task<string?> GetAppSettingAsync(string key)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        return await connection.QueryFirstOrDefaultAsync<string?>(
            "SELECT value FROM app_settings WHERE key = @Key",
            new { Key = key });
    }

    public async Task UpsertAppSettingAsync(string key, string value)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(@"
            INSERT INTO app_settings (key, value)
            VALUES (@Key, @Value)
            ON CONFLICT(key) DO UPDATE SET value = @Value",
            new { Key = key, Value = value });
    }

    // Commit operations
    public async Task<int> InsertOrIgnoreCommitAsync(Commit commit)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            INSERT OR IGNORE INTO commits
            (session_id, sha, author_date, author_name, author_email, subject, additions, deletions, files_json, is_merge, reachable_from_main)
            VALUES (@SessionId, @Sha, @AuthorDate, @AuthorName, @AuthorEmail, @Subject, @Additions, @Deletions, @FilesJson, @IsMerge, @ReachableFromMain)";

        return await connection.ExecuteAsync(sql, commit);
    }

    public async Task<int> BatchInsertCommitsAsync(List<Commit> commits)
    {
        if (commits == null || commits.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT OR IGNORE INTO commits
                (session_id, sha, author_date, author_name, author_email, subject, additions, deletions, files_json, is_merge, reachable_from_main)
                VALUES (@SessionId, @Sha, @AuthorDate, @AuthorName, @AuthorEmail, @Subject, @Additions, @Deletions, @FilesJson, @IsMerge, @ReachableFromMain)";

            var rowsAffected = await connection.ExecuteAsync(sql, commits, transaction);
            transaction.Commit();
            return rowsAffected;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IEnumerable<Commit>> GetCommitsForDayAsync(int sessionId, DateTime day)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var dayStart = day.Date.ToString("yyyy-MM-dd");
        var sql = @"
            SELECT
                session_id AS SessionId,
                sha AS Sha,
                author_date AS AuthorDate,
                author_name AS AuthorName,
                author_email AS AuthorEmail,
                subject AS Subject,
                additions AS Additions,
                deletions AS Deletions,
                files_json AS FilesJson,
                is_merge AS IsMerge,
                reachable_from_main AS ReachableFromMain
            FROM commits
            WHERE session_id = @SessionId AND DATE(author_date) = @Day
            ORDER BY author_date";
        return await connection.QueryAsync<Commit>(sql, new { SessionId = sessionId, Day = dayStart });
    }

    // Day operations
    public async Task UpsertDayAsync(Models.Day day)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            INSERT INTO days (session_id, day, commit_count, additions, deletions, status)
            VALUES (@SessionId, @Date, @CommitCount, @Additions, @Deletions, @Status)
            ON CONFLICT(session_id, day) DO UPDATE SET
                commit_count = @CommitCount,
                additions = @Additions,
                deletions = @Deletions,
                status = @Status";

        await connection.ExecuteAsync(sql, day);
    }

    public async Task<int> BatchUpsertDaysAsync(List<Models.Day> days)
    {
        if (days == null || days.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT INTO days (session_id, day, commit_count, additions, deletions, status)
                VALUES (@SessionId, @Date, @CommitCount, @Additions, @Deletions, @Status)
                ON CONFLICT(session_id, day) DO UPDATE SET
                    commit_count = @CommitCount,
                    additions = @Additions,
                    deletions = @Deletions,
                    status = @Status";

            var rowsAffected = await connection.ExecuteAsync(sql, days, transaction);
            transaction.Commit();
            return rowsAffected;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IEnumerable<Models.Day>> GetDaysAsync(int sessionId)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            SELECT
                session_id AS SessionId,
                day AS Date,
                commit_count AS CommitCount,
                additions AS Additions,
                deletions AS Deletions,
                status AS Status
            FROM days
            WHERE session_id = @SessionId
            ORDER BY day DESC";
        return await connection.QueryAsync<Models.Day>(sql, new { SessionId = sessionId });
    }

    // Checkpoint operations
    public async Task<Checkpoint?> GetCheckpointAsync(int sessionId, CheckpointPhase phase, string cursorKey)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            SELECT
                session_id AS SessionId,
                phase AS Phase,
                cursor_key AS CursorKey,
                cursor_value AS CursorValue,
                updated_at AS UpdatedAt
            FROM checkpoints
            WHERE session_id = @SessionId AND phase = @Phase AND cursor_key = @CursorKey
            LIMIT 1";
        return await connection.QueryFirstOrDefaultAsync<Checkpoint>(sql, new
        {
            SessionId = sessionId,
            Phase = phase.ToString(),
            CursorKey = cursorKey
        });
    }

    public async Task UpsertCheckpointAsync(Checkpoint checkpoint)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            INSERT INTO checkpoints (session_id, phase, cursor_key, cursor_value, updated_at)
            VALUES (@SessionId, @Phase, @CursorKey, @CursorValue, @UpdatedAt)
            ON CONFLICT(session_id, phase, cursor_key) DO UPDATE SET
                cursor_value = @CursorValue,
                updated_at = @UpdatedAt";

        await connection.ExecuteAsync(sql, new
        {
            checkpoint.SessionId,
            Phase = checkpoint.Phase.ToString(),
            checkpoint.CursorKey,
            checkpoint.CursorValue,
            checkpoint.UpdatedAt
        });
    }

    // Day summary operations
    public async Task<DaySummary?> GetDaySummaryAsync(int sessionId, DateTime day)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var dayStart = day.Date.ToString("yyyy-MM-dd");
        var sql = @"
            SELECT
                session_id AS SessionId,
                day AS Day,
                bullets_text AS BulletsText,
                model AS Model,
                prompt_version AS PromptVersion,
                input_hash AS InputHash,
                created_at AS CreatedAt
            FROM day_summaries
            WHERE session_id = @SessionId AND day = @Day
            ORDER BY created_at DESC
            LIMIT 1";
        return await connection.QueryFirstOrDefaultAsync<DaySummary>(sql, new { SessionId = sessionId, Day = dayStart });
    }

    public async Task UpsertDaySummaryAsync(DaySummary summary)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var sql = @"
            INSERT INTO day_summaries
            (session_id, day, bullets_text, model, prompt_version, input_hash, created_at)
            VALUES (@SessionId, @Day, @BulletsText, @Model, @PromptVersion, @InputHash, @CreatedAt)
            ON CONFLICT(session_id, day, prompt_version) DO UPDATE SET
                bullets_text = @BulletsText,
                model = @Model,
                input_hash = @InputHash,
                created_at = @CreatedAt";

        await connection.ExecuteAsync(sql, new
        {
            summary.SessionId,
            Day = summary.Day.ToString("yyyy-MM-dd"),
            summary.BulletsText,
            summary.Model,
            summary.PromptVersion,
            summary.InputHash,
            summary.CreatedAt
        });
    }
}
