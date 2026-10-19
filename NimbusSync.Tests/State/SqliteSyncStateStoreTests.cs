using Microsoft.Data.Sqlite;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.State;

namespace NimbusSync.Tests.State;

public class SqliteSyncStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nimbus_store_{Guid.NewGuid():N}.db");
    private readonly SqliteSyncStateStore _store;

    public SqliteSyncStateStoreTests() => _store = new SqliteSyncStateStore(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private async Task<long> NewProfileAsync()
    {
        await _store.InitializeAsync();
        return await _store.UpsertProfileAsync(new SyncProfile { Name = "p", LocalRootPath = "/tmp/x", RemoteFolderId = "root" });
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task Profile_RoundTrip_IncludingDateTimeOffsetAndEnums()
    {
        await _store.InitializeAsync();
        var created = new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.FromHours(-4));
        var id = await _store.UpsertProfileAsync(new SyncProfile
        {
            Name = "Work", LocalRootPath = @"C:\Users\m\Work", RemoteFolderId = "abc", SharedDriveId = "drv",
            Direction = SyncDirection.RemoteToLocal, NativeFilePolicy = NativeFilePolicy.Skip, CreatedAt = created
        });

        var p = await _store.GetProfileAsync(id);
        Assert.NotNull(p);
        Assert.Equal("Work", p.Name);
        Assert.Equal(SyncDirection.RemoteToLocal, p.Direction);
        Assert.Equal(NativeFilePolicy.Skip, p.NativeFilePolicy);
        Assert.Equal(created, p.CreatedAt);
        Assert.Equal("drv", p.SharedDriveId);
    }

    [Fact]
    public async Task Profile_Update_PersistsChanges()
    {
        var id = await NewProfileAsync();
        var p = (await _store.GetProfileAsync(id))!;
        p.Name = "renamed";
        await _store.UpsertProfileAsync(p);
        Assert.Equal("renamed", (await _store.GetProfileAsync(id))!.Name);
    }

    [Fact]
    public async Task Item_UpsertByPath_UpdatesInsteadOfDuplicating()
    {
        var pid = await NewProfileAsync();
        var first = await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "a/b.txt", RemoteId = "r1" });
        var second = await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "a/b.txt", RemoteId = "r2" });

        Assert.Equal(first, second);
        var all = await _store.GetAllItemsAsync(pid);
        Assert.Single(all);
        Assert.Equal("r2", all[0].RemoteId);
    }

    [Fact]
    public async Task Item_PathLookup_IsCaseInsensitive()
    {
        var pid = await NewProfileAsync();
        await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "Docs/File.TXT" });
        Assert.NotNull(await _store.GetItemByPathAsync(pid, "docs/file.txt"));
    }

    [Fact]
    public async Task Item_LookupByRemoteId_AndByStatus()
    {
        var pid = await NewProfileAsync();
        await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "x", RemoteId = "rx", Status = SyncItemStatus.Conflict });
        await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "y", RemoteId = "ry", Status = SyncItemStatus.Synced });

        Assert.Equal("x", (await _store.GetItemByRemoteIdAsync(pid, "rx"))!.RelativePath);
        Assert.Single(await _store.GetItemsByStatusAsync(pid, SyncItemStatus.Conflict));
    }

    [Fact]
    public async Task ItemsUnderPath_AndDeleteSubtree_RespectBoundaries()
    {
        var pid = await NewProfileAsync();
        foreach (var p in new[] { "dir", "dir/a.txt", "dir/sub/b.txt", "dir2/c.txt", "dirx.txt" })
            await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = p });

        var under = await _store.GetItemsUnderPathAsync(pid, "dir");
        Assert.Equal(["dir/a.txt", "dir/sub/b.txt"], under.Select(i => i.RelativePath).Order());

        var deleted = await _store.DeleteItemsUnderPathAsync(pid, "dir");
        Assert.Equal(3, deleted); // dir itself + two children
        var remaining = (await _store.GetAllItemsAsync(pid)).Select(i => i.RelativePath).Order().ToArray();
        Assert.Equal(["dir2/c.txt", "dirx.txt"], remaining);
    }

    [Fact]
    public async Task BulkUpsert_IsTransactionalAndIdempotent()
    {
        var pid = await NewProfileAsync();
        var items = Enumerable.Range(1, 250).Select(i => new SyncItem { ProfileId = pid, RelativePath = $"f{i:D4}.txt", RemoteId = $"r{i}" }).ToList();
        Assert.Equal(250, await _store.BulkUpsertItemsAsync(items));
        Assert.Equal(250, await _store.BulkUpsertItemsAsync(items));
        Assert.Equal(250, (await _store.GetAllItemsAsync(pid)).Count);
        Assert.All(items, i => Assert.True(i.Id > 0));
    }

    [Fact]
    public async Task Conflict_AddResolveGet()
    {
        var pid = await NewProfileAsync();
        var itemId = await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "c.txt" });
        var cid = await _store.AddConflictAsync(new SyncConflict { ProfileId = pid, SyncItemId = itemId, RelativePath = "c.txt", LocalHash = "a", RemoteHash = "b" });

        Assert.Single(await _store.GetUnresolvedConflictsAsync(pid));
        await _store.ResolveConflictAsync(cid, ConflictResolution.KeepBoth);
        Assert.Empty(await _store.GetUnresolvedConflictsAsync(pid));
        Assert.Equal(ConflictResolution.KeepBoth, (await _store.GetConflictAsync(cid))!.Resolution);
    }

    [Fact]
    public async Task PendingOperations_EnqueueDueFailComplete()
    {
        var pid = await NewProfileAsync();
        await _store.EnqueueOperationsAsync([
            new PendingOperation { ProfileId = pid, Type = SyncOperationType.Upload, RelativePath = "a" },
            new PendingOperation { ProfileId = pid, Type = SyncOperationType.Download, RelativePath = "b", RemoteId = "rb" }
        ]);

        var due = await _store.GetDueOperationsAsync(pid, DateTimeOffset.UtcNow, 10);
        Assert.Equal(2, due.Count);
        Assert.Equal(SyncOperationType.Upload, due[0].Type); // FIFO

        await _store.FailOperationAsync(due[0].Id, "boom", DateTimeOffset.UtcNow.AddHours(1));
        var dueNow = await _store.GetDueOperationsAsync(pid, DateTimeOffset.UtcNow, 10);
        Assert.Single(dueNow);
        Assert.Equal("b", dueNow[0].RelativePath);

        await _store.CompleteOperationAsync(dueNow[0].Id);
        Assert.Equal(1, await _store.CountPendingOperationsAsync(pid));

        var later = await _store.GetDueOperationsAsync(pid, DateTimeOffset.UtcNow.AddHours(2), 10);
        Assert.Single(later);
        Assert.Equal(1, later[0].Attempts);
        Assert.Equal("boom", later[0].LastError);
    }

    [Fact]
    public async Task CommitCycle_IsAtomic_ItemsOpsAndToken()
    {
        var pid = await NewProfileAsync();
        var items = new[] { new SyncItem { ProfileId = pid, RelativePath = "i.txt" } };
        var ops = new[] { new PendingOperation { ProfileId = pid, Type = SyncOperationType.Upload, RelativePath = "i.txt" } };

        await _store.CommitCycleAsync(pid, items, ops, "tok-2");

        Assert.True(items[0].Id > 0);
        Assert.Equal("tok-2", (await _store.GetProfileAsync(pid))!.ChangeToken);
        Assert.Equal(1, await _store.CountPendingOperationsAsync(pid));
    }

    [Fact]
    public async Task ConcurrentAccess_FromManyTasks_DoesNotThrow()
    {
        var pid = await NewProfileAsync();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            for (var j = 0; j < 10; j++)
            {
                await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = $"t{i}/f{j}" });
                _ = await _store.GetAllItemsAsync(pid);
            }
        }));
        await Task.WhenAll(tasks);
        Assert.Equal(200, (await _store.GetAllItemsAsync(pid)).Count);
    }

    [Fact]
    public async Task DeleteProfile_Cascades()
    {
        var pid = await NewProfileAsync();
        await _store.UpsertItemAsync(new SyncItem { ProfileId = pid, RelativePath = "x" });
        await _store.EnqueueOperationsAsync([new PendingOperation { ProfileId = pid, Type = SyncOperationType.Upload, RelativePath = "x" }]);

        await _store.DeleteProfileAsync(pid);

        Assert.Null(await _store.GetProfileAsync(pid));
        Assert.Empty(await _store.GetAllItemsAsync(pid));
        Assert.Equal(0, await _store.CountPendingOperationsAsync(pid));
    }
}
