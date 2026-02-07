using System.IO;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using DevChronicle.Models;
using Microsoft.Data.Sqlite;

namespace DevChronicle.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public DatabaseService(string? dbPath = null)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? GetDefaultDbPath() : dbPath;

        var dbDir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dbDir))
            Directory.CreateDirectory(dbDir);

        _connectionString = $"Data Source={_dbPath}";

        InitializeDatabase();
    }

    private static string GetDefaultDbPath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevChronicle");
        return Path.Combine(appDataPath, "devchronicle.db");
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
                committer_date TEXT,
                committer_name TEXT,
                committer_email TEXT,
                subject TEXT NOT NULL,
                additions INTEGER NOT NULL DEFAULT 0,
                deletions INTEGER NOT NULL DEFAULT 0,
                files_json TEXT NOT NULL DEFAULT '[]',
                is_merge INTEGER NOT NULL DEFAULT 0,
                reachable_from_main INTEGER,
                patch_id TEXT,
                PRIMARY KEY (session_id, sha),
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        EnsureColumnExists(connection, "commits", "committer_date", "TEXT");
        EnsureColumnExists(connection, "commits", "committer_name", "TEXT");
        EnsureColumnExists(connection, "commits", "committer_email", "TEXT");
        EnsureColumnExists(connection, "commits", "patch_id", "TEXT");

        // Create index on author_date for faster queries
        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_commits_date
            ON commits(session_id, author_date)");

        // Create index on author_email for faster queries
        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_commits_author_email_date
            ON commits(session_id, author_email, author_date)");

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

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_days_day
            ON days(session_id, day)");

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_days_status_day
            ON days(session_id, status, day)");

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

        // Create commit parents table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS commit_parents (
                session_id INTEGER NOT NULL,
                child_sha TEXT NOT NULL,
                parent_sha TEXT NOT NULL,
                parent_order INTEGER NOT NULL,
                PRIMARY KEY (session_id, child_sha, parent_order),
                FOREIGN KEY (session_id, child_sha) REFERENCES commits(session_id, sha) ON DELETE CASCADE
            )");

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_commit_parents_parent
            ON commit_parents(session_id, parent_sha)");

        // Create commit branch labels table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS commit_branch_labels (
                session_id INTEGER NOT NULL,
                sha TEXT NOT NULL,
                branch_name TEXT NOT NULL,
                is_primary INTEGER NOT NULL DEFAULT 0,
                label_method TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                PRIMARY KEY (session_id, sha, branch_name),
                FOREIGN KEY (session_id, sha) REFERENCES commits(session_id, sha) ON DELETE CASCADE
            )");

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_commit_branch_labels_branch
            ON commit_branch_labels(session_id, branch_name)");

        // Create integration events table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS integration_events (
                id TEXT PRIMARY KEY,
                session_id INTEGER NOT NULL,
                anchor_sha TEXT,
                occurred_at TEXT NOT NULL,
                method TEXT NOT NULL,
                confidence TEXT NOT NULL,
                details_json TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        // Create integration event commits table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS integration_event_commits (
                integration_event_id TEXT NOT NULL,
                session_id INTEGER NOT NULL,
                sha TEXT NOT NULL,
                PRIMARY KEY (integration_event_id, sha),
                FOREIGN KEY (integration_event_id) REFERENCES integration_events(id) ON DELETE CASCADE
            )");

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_integration_event_commits_sha
            ON integration_event_commits(session_id, sha)");

        // Create branch snapshots table
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS branch_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL,
                captured_at TEXT NOT NULL,
                ref_name TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                head_date TEXT,
                is_remote INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            )");

        connection.Execute(@"
            CREATE INDEX IF NOT EXISTS idx_branch_snapshots_session_captured
            ON branch_snapshots(session_id, captured_at)");
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
            (session_id, sha, author_date, author_name, author_email, committer_date, committer_name, committer_email, subject, additions, deletions, files_json, is_merge, reachable_from_main, patch_id)
            VALUES (@SessionId, @Sha, @AuthorDate, @AuthorName, @AuthorEmail, @CommitterDate, @CommitterName, @CommitterEmail, @Subject, @Additions, @Deletions, @FilesJson, @IsMerge, @ReachableFromMain, @PatchId)";

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
                (session_id, sha, author_date, author_name, author_email, committer_date, committer_name, committer_email, subject, additions, deletions, files_json, is_merge, reachable_from_main, patch_id)
                VALUES (@SessionId, @Sha, @AuthorDate, @AuthorName, @AuthorEmail, @CommitterDate, @CommitterName, @CommitterEmail, @Subject, @Additions, @Deletions, @FilesJson, @IsMerge, @ReachableFromMain, @PatchId)";

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

    public async Task<int> BatchInsertCommitParentsAsync(List<CommitParent> parents)
    {
        if (parents == null || parents.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT OR IGNORE INTO commit_parents
                (session_id, child_sha, parent_sha, parent_order)
                VALUES (@SessionId, @ChildSha, @ParentSha, @ParentOrder)";

            var rows = await connection.ExecuteAsync(sql, parents, transaction);
            transaction.Commit();
            return rows;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<int> BatchInsertCommitBranchLabelsAsync(List<CommitBranchLabel> labels)
    {
        if (labels == null || labels.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT OR REPLACE INTO commit_branch_labels
                (session_id, sha, branch_name, is_primary, label_method, captured_at)
                VALUES (@SessionId, @Sha, @BranchName, @IsPrimary, @LabelMethod, @CapturedAt)";

            var rows = await connection.ExecuteAsync(sql, labels, transaction);
            transaction.Commit();
            return rows;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<int> BatchInsertBranchSnapshotsAsync(List<BranchSnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT INTO branch_snapshots
                (session_id, captured_at, ref_name, head_sha, head_date, is_remote)
                VALUES (@SessionId, @CapturedAt, @RefName, @HeadSha, @HeadDate, @IsRemote)";

            var rows = await connection.ExecuteAsync(sql, snapshots, transaction);
            transaction.Commit();
            return rows;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<int> BatchInsertIntegrationEventsAsync(List<IntegrationEvent> events)
    {
        if (events == null || events.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT OR REPLACE INTO integration_events
                (id, session_id, anchor_sha, occurred_at, method, confidence, details_json)
                VALUES (@Id, @SessionId, @AnchorSha, @OccurredAt, @Method, @Confidence, @DetailsJson)";

            var rows = await connection.ExecuteAsync(sql, events, transaction);
            transaction.Commit();
            return rows;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<int> BatchInsertIntegrationEventCommitsAsync(List<IntegrationEventCommit> rows)
    {
        if (rows == null || rows.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                INSERT OR IGNORE INTO integration_event_commits
                (integration_event_id, session_id, sha)
                VALUES (@IntegrationEventId, @SessionId, @Sha)";

            var affected = await connection.ExecuteAsync(sql, rows, transaction);
            transaction.Commit();
            return affected;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<CommitBranchRow>> GetCommitBranchRowsForDayAsync(int sessionId, DateTime day)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        var dayStart = day.Date.ToString("yyyy-MM-dd");
        var sql = @"
            SELECT
                c.sha AS Sha,
                c.subject AS Subject,
                c.author_date AS AuthorDate,
                lbl.branch_name AS BranchName
            FROM commits c
            LEFT JOIN commit_branch_labels lbl
              ON lbl.session_id = c.session_id
             AND lbl.sha = c.sha
             AND lbl.is_primary = 1
            WHERE c.session_id = @SessionId
              AND DATE(c.author_date) = @Day
            ORDER BY c.author_date";

        var rows = await connection.QueryAsync<CommitBranchRow>(sql, new { SessionId = sessionId, Day = dayStart });
        return rows.ToList();
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
                committer_date AS CommitterDate,
                committer_name AS CommitterName,
                committer_email AS CommitterEmail,
                subject AS Subject,
                additions AS Additions,
                deletions AS Deletions,
                files_json AS FilesJson,
                is_merge AS IsMerge,
                reachable_from_main AS ReachableFromMain,
                patch_id AS PatchId
            FROM commits
            WHERE session_id = @SessionId AND DATE(author_date) = @Day
            ORDER BY author_date";
        return await connection.QueryAsync<Commit>(sql, new { SessionId = sessionId, Day = dayStart });
    }

    public async Task<HashSet<string>> GetCommitShasInRangeAsync(int sessionId, DateTime? start, DateTime? end)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                SELECT sha
                FROM commits
                WHERE session_id = @SessionId
                  AND DATE(author_date) BETWEEN @Start AND @End";
            var rows = await connection.QueryAsync<string>(sql, new
            {
                SessionId = sessionId,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
            return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var allRows = await connection.QueryAsync<string>(
            "SELECT sha FROM commits WHERE session_id = @SessionId",
            new { SessionId = sessionId });
        return allRows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<Commit>> GetCommitsByShasAsync(int sessionId, IEnumerable<string> shas)
    {
        var shaList = shas.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (shaList.Count == 0)
            return new List<Commit>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        var sql = @"
            SELECT
                session_id AS SessionId,
                sha AS Sha,
                author_date AS AuthorDate,
                author_name AS AuthorName,
                author_email AS AuthorEmail,
                committer_date AS CommitterDate,
                committer_name AS CommitterName,
                committer_email AS CommitterEmail,
                subject AS Subject,
                additions AS Additions,
                deletions AS Deletions,
                files_json AS FilesJson,
                is_merge AS IsMerge,
                reachable_from_main AS ReachableFromMain,
                patch_id AS PatchId
            FROM commits
            WHERE session_id = @SessionId
              AND sha IN @Shas";

        var rows = await connection.QueryAsync<Commit>(sql, new { SessionId = sessionId, Shas = shaList });
        return rows.ToList();
    }

    public async Task<List<(string PatchId, List<string> Shas)>> GetPatchIdGroupsAsync(int sessionId)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        var sql = @"
            SELECT patch_id AS PatchId,
                   GROUP_CONCAT(sha, '|') AS ShaList
            FROM commits
            WHERE session_id = @SessionId
              AND patch_id IS NOT NULL
              AND patch_id <> ''
            GROUP BY patch_id
            HAVING COUNT(*) > 1";

        var rows = await connection.QueryAsync(sql, new { SessionId = sessionId });
        var results = new List<(string PatchId, List<string> Shas)>();
        foreach (var row in rows)
        {
            var patchId = (string)row.PatchId;
            var shaList = ((string)row.ShaList)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            results.Add((patchId, shaList));
        }

        return results;
    }

    public async Task<int> UpdateCommitPatchIdsAsync(int sessionId, List<(string Sha, string PatchId)> updates)
    {
        if (updates.Count == 0)
            return 0;

        using var connection = GetConnection();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            var sql = @"
                UPDATE commits
                SET patch_id = @PatchId
                WHERE session_id = @SessionId AND sha = @Sha";

            var rows = await connection.ExecuteAsync(sql, updates.Select(u => new
            {
                SessionId = sessionId,
                Sha = u.Sha,
                PatchId = u.PatchId
            }), transaction);
            transaction.Commit();
            return rows;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string type)
    {
        var rows = connection.Query($"PRAGMA table_info({table})");
        var exists = rows.Any(row =>
        {
            var name = (string)row.name;
            return string.Equals(name, column, StringComparison.OrdinalIgnoreCase);
        });

        if (!exists)
        {
            connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
    }

    public async Task<int> DeleteCommitsAsync(int sessionId, DateTime? start, DateTime? end)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                DELETE FROM commits
                WHERE session_id = @SessionId
                  AND DATE(author_date) BETWEEN @Start AND @End";
            return await connection.ExecuteAsync(sql, new
            {
                SessionId = sessionId,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
        }

        return await connection.ExecuteAsync(
            "DELETE FROM commits WHERE session_id = @SessionId",
            new { SessionId = sessionId });
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

    public async Task<int> DeleteDaysAsync(int sessionId, DateTime? start, DateTime? end)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                DELETE FROM days
                WHERE session_id = @SessionId
                  AND day BETWEEN @Start AND @End";
            return await connection.ExecuteAsync(sql, new
            {
                SessionId = sessionId,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
        }

        return await connection.ExecuteAsync(
            "DELETE FROM days WHERE session_id = @SessionId",
            new { SessionId = sessionId });
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

    public async Task<List<DateTime>> GetDaySummaryDaysAsync(int sessionId, DateTime? start, DateTime? end)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                SELECT day
                FROM day_summaries
                WHERE session_id = @SessionId
                  AND day BETWEEN @Start AND @End";
            var rows = await connection.QueryAsync<string>(sql, new
            {
                SessionId = sessionId,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
            return rows
                .Select(d => DateTime.TryParse(d, out var parsed) ? parsed.Date : DateTime.MinValue)
                .Where(d => d != DateTime.MinValue)
                .ToList();
        }

        var allSql = @"
            SELECT day
            FROM day_summaries
            WHERE session_id = @SessionId";
        var allRows = await connection.QueryAsync<string>(allSql, new { SessionId = sessionId });
        return allRows
            .Select(d => DateTime.TryParse(d, out var parsed) ? parsed.Date : DateTime.MinValue)
            .Where(d => d != DateTime.MinValue)
            .ToList();
    }

    // Export-oriented batch queries (avoid N+1 patterns)
    public async Task<List<Session>> GetSessionsByIdsAsync(IEnumerable<int> sessionIds)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0)
            return new List<Session>();

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
            WHERE id IN @Ids";

        var rows = await connection.QueryAsync<Session>(sql, new { Ids = ids });
        return rows.ToList();
    }

    public async Task<List<Models.Day>> GetDaysInRangeForSessionsAsync(IEnumerable<int> sessionIds, DateTime? start, DateTime? end)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0)
            return new List<Models.Day>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                SELECT
                    session_id AS SessionId,
                    day AS Date,
                    commit_count AS CommitCount,
                    additions AS Additions,
                    deletions AS Deletions,
                    status AS Status
                FROM days
                WHERE session_id IN @SessionIds
                  AND day BETWEEN @Start AND @End";

            var rows = await connection.QueryAsync<Models.Day>(sql, new
            {
                SessionIds = ids,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });

            return rows.ToList();
        }
        else
        {
            var sql = @"
                SELECT
                    session_id AS SessionId,
                    day AS Date,
                    commit_count AS CommitCount,
                    additions AS Additions,
                    deletions AS Deletions,
                    status AS Status
                FROM days
                WHERE session_id IN @SessionIds";

            var rows = await connection.QueryAsync<Models.Day>(sql, new { SessionIds = ids });
            return rows.ToList();
        }
    }

    public async Task<List<Commit>> GetCommitsInRangeForSessionsAsync(IEnumerable<int> sessionIds, DateTime? start, DateTime? end)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0)
            return new List<Commit>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                SELECT
                    session_id AS SessionId,
                    sha AS Sha,
                    author_date AS AuthorDate,
                    author_name AS AuthorName,
                    author_email AS AuthorEmail,
                    committer_date AS CommitterDate,
                    committer_name AS CommitterName,
                    committer_email AS CommitterEmail,
                    subject AS Subject,
                    additions AS Additions,
                    deletions AS Deletions,
                    files_json AS FilesJson,
                    is_merge AS IsMerge,
                    reachable_from_main AS ReachableFromMain,
                    patch_id AS PatchId
                FROM commits
                WHERE session_id IN @SessionIds
                  AND DATE(author_date) BETWEEN @Start AND @End";

            var rows = await connection.QueryAsync<Commit>(sql, new
            {
                SessionIds = ids,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });

            return rows.ToList();
        }
        else
        {
            var sql = @"
                SELECT
                    session_id AS SessionId,
                    sha AS Sha,
                    author_date AS AuthorDate,
                    author_name AS AuthorName,
                    author_email AS AuthorEmail,
                    committer_date AS CommitterDate,
                    committer_name AS CommitterName,
                    committer_email AS CommitterEmail,
                    subject AS Subject,
                    additions AS Additions,
                    deletions AS Deletions,
                    files_json AS FilesJson,
                    is_merge AS IsMerge,
                    reachable_from_main AS ReachableFromMain,
                    patch_id AS PatchId
                FROM commits
                WHERE session_id IN @SessionIds";

            var rows = await connection.QueryAsync<Commit>(sql, new { SessionIds = ids });
            return rows.ToList();
        }
    }

    public async Task<List<DaySummary>> GetLatestDaySummariesInRangeForSessionsAsync(IEnumerable<int> sessionIds, DateTime? start, DateTime? end)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0)
            return new List<DaySummary>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        IEnumerable<DaySummary> rows;
        if (start.HasValue && end.HasValue)
        {
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
                WHERE session_id IN @SessionIds
                  AND day BETWEEN @Start AND @End
                ORDER BY created_at DESC";

            rows = await connection.QueryAsync<DaySummary>(sql, new
            {
                SessionIds = ids,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
        }
        else
        {
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
                WHERE session_id IN @SessionIds
                ORDER BY created_at DESC";

            rows = await connection.QueryAsync<DaySummary>(sql, new { SessionIds = ids });
        }

        // Take latest per (session_id, day) deterministically using created_at ordering from SQL.
        return rows
            .GroupBy(r => (r.SessionId, r.Day.Date))
            .Select(g => g.First())
            .ToList();
    }

    public async Task<(DateTime? Start, DateTime? End)> GetEffectiveSessionRangeAsync(int sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        if (session == null)
            return (null, null);

        if (session.RangeStart.HasValue && session.RangeEnd.HasValue)
            return (session.RangeStart.Value.Date, session.RangeEnd.Value.Date);

        using var connection = GetConnection();
        await connection.OpenAsync();

        var sql = @"
            SELECT MIN(day) AS MinDay, MAX(day) AS MaxDay
            FROM days
            WHERE session_id = @SessionId";

        var row = await connection.QuerySingleAsync(sql, new { SessionId = sessionId });
        var minDay = row.MinDay as string;
        var maxDay = row.MaxDay as string;

        DateTime? start = null;
        DateTime? end = null;

        if (!string.IsNullOrWhiteSpace(minDay) && DateTime.TryParse(minDay, out var parsedMin))
            start = parsedMin.Date;
        if (!string.IsNullOrWhiteSpace(maxDay) && DateTime.TryParse(maxDay, out var parsedMax))
            end = parsedMax.Date;

        return (start, end);
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

    public async Task<int> DeleteDaySummariesAsync(int sessionId, DateTime? start, DateTime? end)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        if (start.HasValue && end.HasValue)
        {
            var sql = @"
                DELETE FROM day_summaries
                WHERE session_id = @SessionId
                  AND day BETWEEN @Start AND @End";
            return await connection.ExecuteAsync(sql, new
            {
                SessionId = sessionId,
                Start = start.Value.Date.ToString("yyyy-MM-dd"),
                End = end.Value.Date.ToString("yyyy-MM-dd")
            });
        }

        return await connection.ExecuteAsync(
            "DELETE FROM day_summaries WHERE session_id = @SessionId",
            new { SessionId = sessionId });
    }
}
