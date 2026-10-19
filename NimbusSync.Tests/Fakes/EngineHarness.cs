using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.State;
using NimbusSync.Engine.Sync;

namespace NimbusSync.Tests.Fakes;

/// <summary>
/// Real engine + real SQLite store + real debouncing monitor (fed via Inject so tests
/// never depend on OS watcher timing) + in-memory cloud. One per test.
/// </summary>
public sealed class EngineHarness : IDisposable
{
    public string Root { get; }
    public FakeCloudProvider Cloud { get; } = new();
    public SqliteSyncStateStore Store { get; }
    public DebouncingFileSystemMonitor Monitor { get; }
    public SyncEngine Engine { get; }
    public SyncProfile Profile { get; }
    public List<SyncConflict> Conflicts { get; } = [];

    private readonly string _dbPath;

    public EngineHarness(SyncEngineOptions? options = null, NativeFilePolicy nativePolicy = NativeFilePolicy.LinkStub,
        SyncDirection direction = SyncDirection.Bidirectional)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "nimbus-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(baseDir, "sync");
        Directory.CreateDirectory(Root);
        _dbPath = Path.Combine(baseDir, "state.db");

        Store = new SqliteSyncStateStore(_dbPath);
        Monitor = new DebouncingFileSystemMonitor(TimeSpan.Zero);
        Engine = new SyncEngine(Cloud, Store, Monitor, NullLogger<SyncEngine>.Instance,
            options ?? new SyncEngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1) });
        Engine.ConflictDetected += (_, c) => Conflicts.Add(c);

        Profile = new SyncProfile
        {
            Name = "test", LocalRootPath = Root, RemoteFolderId = FakeCloudProvider.RootId,
            NativeFilePolicy = nativePolicy, Direction = direction
        };
    }

    // ---- local helpers ----

    public string Abs(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public async Task WriteLocalAsync(string rel, string content)
    {
        var abs = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllTextAsync(abs, content);
    }

    public void TouchLocal(string rel, ChangeType type = ChangeType.Modified) => Monitor.Inject(rel, type);

    public async Task WriteAndTouchAsync(string rel, string content)
    {
        var existed = File.Exists(Abs(rel));
        await WriteLocalAsync(rel, content);
        TouchLocal(rel, existed ? ChangeType.Modified : ChangeType.Created);
    }

    public void DeleteLocal(string rel)
    {
        var abs = Abs(rel);
        if (File.Exists(abs)) File.Delete(abs);
        else if (Directory.Exists(abs)) Directory.Delete(abs, true);
        TouchLocal(rel, ChangeType.Deleted);
    }

    public string ReadLocal(string rel) => File.ReadAllText(Abs(rel));
    public bool LocalExists(string rel) => File.Exists(Abs(rel)) || Directory.Exists(Abs(rel));

    public Task SyncAsync() => Engine.RunOnceAsync(Profile);

    public async Task<SyncItem?> ItemAsync(string rel) => await Store.GetItemByPathAsync(Profile.Id, rel);
    public Task<int> PendingCountAsync() => Store.CountPendingOperationsAsync(Profile.Id);

    public void Dispose()
    {
        Engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch { /* best effort */ }
    }
}
