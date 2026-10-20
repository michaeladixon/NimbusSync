using Dapper;
using Microsoft.Data.Sqlite;
using NimbusSync.Engine.Abstractions;
using NimbusSync.Engine.Models;

namespace NimbusSync.Engine.State;

/// <summary>
/// SQLite + Dapper state store. Opens a connection per call: Microsoft.Data.Sqlite
/// pools connections and WAL mode allows concurrent readers, so this is both
/// thread-safe and cheap. A shared connection is not safe for concurrent commands.
/// </summary>
public sealed class SqliteSyncStateStore : ISyncStateStore
{
    private readonly string _connectionString;
    private static readonly object InitLock = new();
    private static bool _handlersRegistered;

    public SqliteSyncStateStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        lock (InitLock)
        {
            if (!_handlersRegistered)
            {
                SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
                SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
                _handlersRegistered = true;
            }
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS SyncProfiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                LocalRootPath TEXT NOT NULL,
                RemoteFolderId TEXT NOT NULL,
                SharedDriveId TEXT,
                Direction INTEGER NOT NULL DEFAULT 0,
                PollIntervalSeconds INTEGER NOT NULL DEFAULT 60,
                ChangeToken TEXT,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                NativeFilePolicy INTEGER NOT NULL DEFAULT 1,
                ExcludePatterns TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS SyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL REFERENCES SyncProfiles(Id) ON DELETE CASCADE,
                RelativePath TEXT NOT NULL COLLATE NOCASE,
                RemoteId TEXT,
                RemoteParentId TEXT,
                IsDirectory INTEGER NOT NULL DEFAULT 0,
                IsGoogleNative INTEGER NOT NULL DEFAULT 0,
                LastSyncedLocalHash TEXT,
                LastSyncedRemoteHash TEXT,
                LastSyncedLocalSize INTEGER,
                LastSyncedRemoteSize INTEGER,
                LastSyncedAt TEXT,
                LastSyncedLocalModified TEXT,
                LastSyncedRemoteModified TEXT,
                Status INTEGER NOT NULL DEFAULT 0,
                UNIQUE(ProfileId, RelativePath)
            );
            CREATE INDEX IF NOT EXISTS IX_SyncItems_Profile_Status ON SyncItems(ProfileId, Status);
            CREATE INDEX IF NOT EXISTS IX_SyncItems_Profile_RemoteId ON SyncItems(ProfileId, RemoteId);

            CREATE TABLE IF NOT EXISTS SyncConflicts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL REFERENCES SyncProfiles(Id) ON DELETE CASCADE,
                SyncItemId INTEGER NOT NULL REFERENCES SyncItems(Id) ON DELETE CASCADE,
                RelativePath TEXT NOT NULL,
                DetectedAt TEXT NOT NULL,
                LocalHash TEXT, RemoteHash TEXT,
                LocalSize INTEGER, RemoteSize INTEGER,
                LocalModified TEXT, RemoteModified TEXT,
                Resolution INTEGER, ResolvedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_SyncConflicts_Unresolved ON SyncConflicts(ProfileId) WHERE Resolution IS NULL;

            CREATE TABLE IF NOT EXISTS PendingOperations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL REFERENCES SyncProfiles(Id) ON DELETE CASCADE,
                Type INTEGER NOT NULL,
                RelativePath TEXT NOT NULL,
                TargetRelativePath TEXT,
                RemoteId TEXT,
                RemoteHash TEXT,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                CreatedAt TEXT NOT NULL,
                NextAttemptAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PendingOps_Profile_Next ON PendingOperations(ProfileId, NextAttemptAt);
            """);
    }

    // ---------------- Profiles ----------------

    public async Task<SyncProfile?> GetProfileAsync(long profileId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<SyncProfile>("SELECT * FROM SyncProfiles WHERE Id=@profileId", new { profileId });
    }

    public async Task<IReadOnlyList<SyncProfile>> GetAllProfilesAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<SyncProfile>("SELECT * FROM SyncProfiles ORDER BY Id")).ToList();
    }

    public async Task<long> UpsertProfileAsync(SyncProfile profile, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        if (profile.Id > 0)
        {
            await db.ExecuteAsync("""
                UPDATE SyncProfiles SET Name=@Name, LocalRootPath=@LocalRootPath, RemoteFolderId=@RemoteFolderId,
                    SharedDriveId=@SharedDriveId, Direction=@Direction, PollIntervalSeconds=@PollIntervalSeconds,
                    ChangeToken=@ChangeToken, IsEnabled=@IsEnabled, NativeFilePolicy=@NativeFilePolicy,
                    ExcludePatterns=@ExcludePatterns
                WHERE Id=@Id
                """, profile);
            return profile.Id;
        }
        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO SyncProfiles (Name, LocalRootPath, RemoteFolderId, SharedDriveId, Direction, PollIntervalSeconds,
                ChangeToken, IsEnabled, CreatedAt, NativeFilePolicy, ExcludePatterns)
            VALUES (@Name, @LocalRootPath, @RemoteFolderId, @SharedDriveId, @Direction, @PollIntervalSeconds,
                @ChangeToken, @IsEnabled, @CreatedAt, @NativeFilePolicy, @ExcludePatterns);
            SELECT last_insert_rowid();
            """, profile);
        profile.Id = id;
        return id;
    }

    public async Task DeleteProfileAsync(long profileId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("DELETE FROM SyncProfiles WHERE Id=@profileId", new { profileId });
    }

    public async Task UpdateChangeTokenAsync(long profileId, string changeToken, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("UPDATE SyncProfiles SET ChangeToken=@changeToken WHERE Id=@profileId", new { profileId, changeToken });
    }

    // ---------------- Items ----------------

    private const string ItemInsertSql = """
        INSERT INTO SyncItems (ProfileId, RelativePath, RemoteId, RemoteParentId, IsDirectory, IsGoogleNative,
            LastSyncedLocalHash, LastSyncedRemoteHash, LastSyncedLocalSize, LastSyncedRemoteSize,
            LastSyncedAt, LastSyncedLocalModified, LastSyncedRemoteModified, Status)
        VALUES (@ProfileId, @RelativePath, @RemoteId, @RemoteParentId, @IsDirectory, @IsGoogleNative,
            @LastSyncedLocalHash, @LastSyncedRemoteHash, @LastSyncedLocalSize, @LastSyncedRemoteSize,
            @LastSyncedAt, @LastSyncedLocalModified, @LastSyncedRemoteModified, @Status)
        ON CONFLICT(ProfileId, RelativePath) DO UPDATE SET
            RemoteId=excluded.RemoteId, RemoteParentId=excluded.RemoteParentId,
            IsDirectory=excluded.IsDirectory, IsGoogleNative=excluded.IsGoogleNative,
            LastSyncedLocalHash=excluded.LastSyncedLocalHash, LastSyncedRemoteHash=excluded.LastSyncedRemoteHash,
            LastSyncedLocalSize=excluded.LastSyncedLocalSize, LastSyncedRemoteSize=excluded.LastSyncedRemoteSize,
            LastSyncedAt=excluded.LastSyncedAt, LastSyncedLocalModified=excluded.LastSyncedLocalModified,
            LastSyncedRemoteModified=excluded.LastSyncedRemoteModified, Status=excluded.Status;
        SELECT Id FROM SyncItems WHERE ProfileId=@ProfileId AND RelativePath=@RelativePath;
        """;

    public async Task<SyncItem?> GetItemByPathAsync(long profileId, string relativePath, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<SyncItem>(
            "SELECT * FROM SyncItems WHERE ProfileId=@profileId AND RelativePath=@relativePath COLLATE NOCASE",
            new { profileId, relativePath });
    }

    public async Task<SyncItem?> GetItemByRemoteIdAsync(long profileId, string remoteId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.QueryFirstOrDefaultAsync<SyncItem>(
            "SELECT * FROM SyncItems WHERE ProfileId=@profileId AND RemoteId=@remoteId", new { profileId, remoteId });
    }

    public async Task<IReadOnlyList<SyncItem>> GetItemsByStatusAsync(long profileId, SyncItemStatus status, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<SyncItem>("SELECT * FROM SyncItems WHERE ProfileId=@profileId AND Status=@status",
            new { profileId, status = (int)status })).ToList();
    }

    public async Task<IReadOnlyList<SyncItem>> GetAllItemsAsync(long profileId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<SyncItem>("SELECT * FROM SyncItems WHERE ProfileId=@profileId ORDER BY RelativePath", new { profileId })).ToList();
    }

    public async Task<IReadOnlyList<SyncItem>> GetItemsUnderPathAsync(long profileId, string relativeDirPath, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var prefix = relativeDirPath.Length == 0 ? "" : relativeDirPath + "/";
        return (await db.QueryAsync<SyncItem>(
            "SELECT * FROM SyncItems WHERE ProfileId=@profileId AND RelativePath LIKE @pattern ESCAPE '\\' COLLATE NOCASE ORDER BY RelativePath",
            new { profileId, pattern = EscapeLike(prefix) + "%" })).ToList();
    }

    public async Task<long> UpsertItemAsync(SyncItem item, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var id = await db.ExecuteScalarAsync<long>(ItemInsertSql, item);
        item.Id = id;
        return id;
    }

    public async Task<int> BulkUpsertItemsAsync(IEnumerable<SyncItem> items, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var count = 0;
        foreach (var item in items)
        {
            item.Id = await db.ExecuteScalarAsync<long>(ItemInsertSql, item, tx);
            count++;
        }
        await tx.CommitAsync(ct);
        return count;
    }

    public async Task DeleteItemAsync(long itemId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("DELETE FROM SyncItems WHERE Id=@itemId", new { itemId });
    }

    public async Task<int> DeleteItemsUnderPathAsync(long profileId, string relativeDirPath, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        // Empty path is the sync root: delete every row for the profile. This mirrors
        // GetItemsUnderPathAsync, which treats "" as prefix "" (everything under root).
        if (string.IsNullOrEmpty(relativeDirPath))
            return await db.ExecuteAsync("DELETE FROM SyncItems WHERE ProfileId=@profileId", new { profileId });

        return await db.ExecuteAsync(
            "DELETE FROM SyncItems WHERE ProfileId=@profileId AND (RelativePath=@dir COLLATE NOCASE OR RelativePath LIKE @pattern ESCAPE '\\' COLLATE NOCASE)",
            new { profileId, dir = relativeDirPath, pattern = EscapeLike(relativeDirPath + "/") + "%" });
    }

    public async Task<SyncItem?> GetItemByIdAsync(long itemId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<SyncItem>("SELECT * FROM SyncItems WHERE Id=@itemId", new { itemId });
    }

    /// <summary>
    /// Rename a tracked path and everything under it in place, preserving row ids so
    /// conflicts referencing those rows survive. One transaction, deepest paths first.
    /// </summary>
    public async Task<int> RenameItemPathsAsync(long profileId, string fromPath, string toPath, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // The dir row (RelativePath=@fromPath) is matched by the equality branch and gets
        // @toPath directly, so substr() never sees it. For the children, RelativePath =
        // fromPath + "/" + tail. SQLite's substr() indexes by Unicode character (code point),
        // not UTF-16 code unit, so a surrogate pair (e.g. emoji) counts as 1, not 2.
        // We must therefore pass the character length of fromPath, not string.Length.
        // Each high surrogate starts a 2-unit pair that is 1 character, so subtract the
        // count of high surrogates from the unit length to get the character length.
        var prefixLen = fromPath.Length - fromPath.Count(c => char.IsHighSurrogate(c));
        var n = await db.ExecuteAsync("""
            UPDATE SyncItems SET RelativePath = @toPath || substr(RelativePath, @prefixLen + 1)
            WHERE ProfileId=@profileId AND (RelativePath=@fromPath OR RelativePath LIKE @pattern ESCAPE '\')
            """, new { profileId, fromPath, toPath, prefixLen, pattern = EscapeLike(fromPath + "/") + "%" }, tx);
        await db.ExecuteAsync("""
            UPDATE SyncConflicts SET RelativePath = @toPath || substr(RelativePath, @prefixLen + 1)
            WHERE ProfileId=@profileId AND Resolution IS NULL
              AND (RelativePath=@fromPath COLLATE NOCASE OR RelativePath LIKE @pattern ESCAPE '\' COLLATE NOCASE)
            """, new { profileId, fromPath, toPath, prefixLen, pattern = EscapeLike(fromPath + "/") + "%" }, tx);
        await tx.CommitAsync(ct);
        return n;
    }

    // ---------------- Conflicts ----------------

    public async Task<IReadOnlyList<SyncConflict>> GetUnresolvedConflictsAsync(long profileId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<SyncConflict>(
            "SELECT * FROM SyncConflicts WHERE ProfileId=@profileId AND Resolution IS NULL ORDER BY DetectedAt DESC", new { profileId })).ToList();
    }

    public async Task<SyncConflict?> GetConflictAsync(long conflictId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<SyncConflict>("SELECT * FROM SyncConflicts WHERE Id=@conflictId", new { conflictId });
    }

    public async Task<long> AddConflictAsync(SyncConflict conflict, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO SyncConflicts (ProfileId, SyncItemId, RelativePath, DetectedAt, LocalHash, RemoteHash,
                LocalSize, RemoteSize, LocalModified, RemoteModified)
            VALUES (@ProfileId, @SyncItemId, @RelativePath, @DetectedAt, @LocalHash, @RemoteHash,
                @LocalSize, @RemoteSize, @LocalModified, @RemoteModified);
            SELECT last_insert_rowid();
            """, conflict);
        conflict.Id = id;
        return id;
    }

    public async Task ResolveConflictAsync(long conflictId, ConflictResolution resolution, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("UPDATE SyncConflicts SET Resolution=@resolution, ResolvedAt=@resolvedAt WHERE Id=@conflictId",
            new { conflictId, resolution = (int)resolution, resolvedAt = DateTimeOffset.UtcNow });
    }

    // ---------------- Pending operations ----------------

    private const string OpInsertSql = """
        INSERT INTO PendingOperations (ProfileId, Type, RelativePath, TargetRelativePath, RemoteId, RemoteHash, Attempts, LastError, CreatedAt, NextAttemptAt)
        VALUES (@ProfileId, @Type, @RelativePath, @TargetRelativePath, @RemoteId, @RemoteHash, @Attempts, @LastError, @CreatedAt, @NextAttemptAt)
        """;

    public async Task<int> EnqueueOperationsAsync(IEnumerable<PendingOperation> operations, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var n = 0;
        foreach (var op in operations) { await db.ExecuteAsync(OpInsertSql, op, tx); n++; }
        await tx.CommitAsync(ct);
        return n;
    }

    public async Task<IReadOnlyList<PendingOperation>> GetDueOperationsAsync(long profileId, DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<PendingOperation>(
            """
            SELECT * FROM PendingOperations WHERE ProfileId=@profileId AND NextAttemptAt<=@now
            ORDER BY CASE Type
                WHEN 2 THEN 0 WHEN 3 THEN 0   -- CreateRemoteFolder, CreateLocalFolder
                WHEN 6 THEN 1 WHEN 7 THEN 1   -- MoveLocal, MoveRemote
                WHEN 4 THEN 2 WHEN 5 THEN 2   -- DeleteLocal, DeleteRemote
                ELSE 3 END, Id
            LIMIT @limit
            """,
            new { profileId, now = now.ToString("o"), limit })).ToList();
    }

    public async Task<int> CountPendingOperationsAsync(long profileId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PendingOperations WHERE ProfileId=@profileId", new { profileId });
    }

    public async Task CompleteOperationAsync(long operationId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync("DELETE FROM PendingOperations WHERE Id=@operationId", new { operationId });
    }

    public async Task FailOperationAsync(long operationId, string error, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync(
            "UPDATE PendingOperations SET Attempts=Attempts+1, LastError=@error, NextAttemptAt=@next WHERE Id=@operationId",
            new { operationId, error, next = nextAttemptAt.ToString("o") });
    }

    public async Task CommitCycleAsync(long profileId, IEnumerable<SyncItem> itemUpserts, IEnumerable<PendingOperation> operations,
        string? newChangeToken, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var item in itemUpserts)
            item.Id = await db.ExecuteScalarAsync<long>(ItemInsertSql, item, tx);
        foreach (var op in operations)
            await db.ExecuteAsync(OpInsertSql, op, tx);
        if (newChangeToken is not null)
            await db.ExecuteAsync("UPDATE SyncProfiles SET ChangeToken=@newChangeToken WHERE Id=@profileId", new { profileId, newChangeToken }, tx);
        await tx.CommitAsync(ct);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
