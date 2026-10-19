using Microsoft.Extensions.Logging.Abstractions;
using NimbusSync.Engine.Abstractions;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.Sync;
using NimbusSync.Tests.Fakes;

namespace NimbusSync.Tests.Sync;

/// <summary>
/// Independent validation tests written against the engine without reference to the author's
/// claims. Several are expected to fail; each failure is a reported engine bug, not a test bug.
/// </summary>
public class ValidatorTests
{
    /// <summary>Delegating cloud so a test can corrupt or fail specific calls the sealed fake cannot.</summary>
    private sealed class HookedCloud(FakeCloudProvider inner) : ICloudProvider
    {
        public Func<string, byte[]?>? CorruptDownload { get; set; }
        public Func<Exception?>? FailGetChanges { get; set; }

        public Task<string> GetStartChangeTokenAsync(string? d = null, CancellationToken ct = default) => inner.GetStartChangeTokenAsync(d, ct);
        public Task<(IReadOnlyList<RemoteChange> Changes, string NewToken)> GetChangesAsync(string t, string? d = null, CancellationToken ct = default)
        {
            if (FailGetChanges?.Invoke() is { } ex) throw ex;
            return inner.GetChangesAsync(t, d, ct);
        }
        public Task<IReadOnlyList<RemoteFileInfo>> ListFolderRecursiveAsync(string f, string? d = null, CancellationToken ct = default) => inner.ListFolderRecursiveAsync(f, d, ct);
        public async Task DownloadFileAsync(string id, Stream dest, CancellationToken ct = default)
        {
            if (CorruptDownload?.Invoke(id) is { } bytes) { await dest.WriteAsync(bytes, ct); return; }
            await inner.DownloadFileAsync(id, dest, ct);
        }
        public Task<RemoteFileInfo> UploadFileAsync(string p, string n, Stream c, string? m = null, CancellationToken ct = default) => inner.UploadFileAsync(p, n, c, m, ct);
        public Task<RemoteFileInfo> UpdateFileAsync(string id, Stream c, string? m = null, CancellationToken ct = default) => inner.UpdateFileAsync(id, c, m, ct);
        public Task<RemoteFileInfo> CreateFolderAsync(string p, string n, CancellationToken ct = default) => inner.CreateFolderAsync(p, n, ct);
        public Task<RemoteFileInfo> MoveAsync(string id, string? n, string? o, string? np, CancellationToken ct = default) => inner.MoveAsync(id, n, o, np, ct);
        public Task TrashAsync(string id, CancellationToken ct = default) => inner.TrashAsync(id, ct);
        public Task<RemoteFileInfo?> GetFileInfoAsync(string id, CancellationToken ct = default) => inner.GetFileInfoAsync(id, ct);
    }

    private static SyncEngine EngineOver(EngineHarness h, ICloudProvider cloud, SyncEngineOptions? o = null)
        => new(cloud, h.Store, h.Monitor, NullLogger<SyncEngine>.Instance,
            o ?? new SyncEngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1) });

    // ------------------------------------------------------------------ 1. remote folder rename + child modify, same cycle

    [Fact]
    public async Task RemoteFolderRename_AndChildModified_SameCycle_RenameFirstInFeed()
    {
        using var h = new EngineHarness();
        var dir = h.Cloud.AddFolder("dir");
        var a = h.Cloud.AddFile("a.txt", "a1", dir.Id);
        h.Cloud.AddFile("b.txt", "b1", dir.Id);
        await h.SyncAsync();

        h.Cloud.Rename(dir.Id, "renamed");
        h.Cloud.SetContent(a.Id, "a2");
        await h.SyncAsync();

        Assert.False(h.LocalExists("dir"));
        Assert.Equal("a2", h.ReadLocal("renamed/a.txt"));
        Assert.Equal("b1", h.ReadLocal("renamed/b.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Equal(dir.Id, (await h.ItemAsync("renamed"))!.RemoteId);
        Assert.Null(await h.ItemAsync("dir"));
        Assert.Equal(a.Id, (await h.ItemAsync("renamed/a.txt"))!.RemoteId);
    }

    [Fact]
    public async Task RemoteFolderRename_AndChildModified_SameCycle_ModifyFirstInFeed()
    {
        using var h = new EngineHarness();
        var dir = h.Cloud.AddFolder("dir");
        var a = h.Cloud.AddFile("a.txt", "a1", dir.Id);
        h.Cloud.AddFile("b.txt", "b1", dir.Id);
        await h.SyncAsync();

        h.Cloud.SetContent(a.Id, "a2");      // feed order: file change, then folder rename
        h.Cloud.Rename(dir.Id, "renamed");
        await h.SyncAsync();
        await Task.Delay(10);
        await h.Engine.FlushPendingAsync(h.Profile);

        Assert.False(h.LocalExists("dir"));
        Assert.Equal("a2", h.ReadLocal("renamed/a.txt"));
        Assert.Equal("b1", h.ReadLocal("renamed/b.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Null(await h.ItemAsync("dir"));
        Assert.Equal(dir.Id, (await h.ItemAsync("renamed"))!.RemoteId);
    }

    // ------------------------------------------------------------------ 2. local folder rename

    [Fact]
    public async Task LocalFolderRename_PreservesAllChildrenRemotely()
    {
        using var h = new EngineHarness();
        var old = h.Cloud.AddFolder("old");
        h.Cloud.AddFile("a.txt", "A", old.Id);
        h.Cloud.AddFile("b.txt", "B", old.Id);
        await h.SyncAsync();

        Directory.Move(h.Abs("old"), h.Abs("new"));
        h.TouchLocal("old", ChangeType.Deleted);
        h.TouchLocal("new", ChangeType.Created);
        await h.SyncAsync();

        Assert.Equal("A", h.Cloud.ContentOf("new/a.txt"));
        Assert.Equal("B", h.Cloud.ContentOf("new/b.txt"));
        Assert.Null(h.Cloud.FindByPath("old"));
        Assert.Null(await h.ItemAsync("old/a.txt"));
        Assert.NotNull(await h.ItemAsync("new/a.txt"));
        Assert.Equal(0, await h.PendingCountAsync());

        // Steady state afterwards: the echo of our own uploads must not re-download.
        var d = h.Cloud.DownloadCount;
        await h.SyncAsync();
        Assert.Equal(d, h.Cloud.DownloadCount);
    }

    // ------------------------------------------------------------------ 3. remote move out of root

    [Fact]
    public async Task RemoteMoveOutOfRoot_SoftDeletesLocal()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("m.txt", "payload");
        var outside = h.Cloud.AddFolder("outside", parentId: "some-other-root");
        await h.SyncAsync();

        h.Cloud.Reparent(f.Id, outside.Id);
        await h.SyncAsync();

        Assert.False(h.LocalExists("m.txt"));
        var trashed = Directory.GetFiles(Path.Combine(h.Root, ".nimbus-trash"), "m.txt", SearchOption.AllDirectories);
        Assert.Single(trashed);
        Assert.Equal("payload", File.ReadAllText(trashed[0]));
        Assert.Null(await h.ItemAsync("m.txt"));
    }

    // ------------------------------------------------------------------ 4. crash after commit, new engine over same store

    [Fact]
    public async Task CrashAfterCommit_NewEngineOverSameStore_CompletesQueuedWork()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();
        var f = h.Cloud.AddFile("later.txt", "durable");
        h.Cloud.FailDownload = _ => new IOException("process died here");
        await h.SyncAsync();
        Assert.Equal(1, await h.PendingCountAsync());
        Assert.False(h.LocalExists("later.txt"));

        // "Restart": fresh engine, fresh monitor, profile reloaded from the database.
        h.Cloud.FailDownload = null;
        var reloaded = await h.Store.GetProfileAsync(h.Profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(h.Profile.ChangeToken, reloaded!.ChangeToken);
        using var monitor2 = new DebouncingFileSystemMonitor(TimeSpan.Zero);
        using var engine2 = new SyncEngine(h.Cloud, h.Store, monitor2, NullLogger<SyncEngine>.Instance,
            new SyncEngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1) });

        await Task.Delay(10);
        await engine2.RunOnceAsync(reloaded);

        Assert.Equal("durable", h.ReadLocal("later.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Equal(f.Id, (await h.ItemAsync("later.txt"))!.RemoteId);
    }

    // ------------------------------------------------------------------ 5. rename heuristic with duplicate content

    [Fact]
    public async Task IdenticalContentFiles_DeleteOneCreateAnother_DoesNotTouchTheSurvivor()
    {
        using var h = new EngineHarness();
        var a = h.Cloud.AddFile("a.txt", "same");
        var b = h.Cloud.AddFile("b.txt", "same");
        await h.SyncAsync();

        h.DeleteLocal("a.txt");
        await h.WriteAndTouchAsync("c.txt", "same");
        await h.SyncAsync();

        Assert.False(h.Cloud.Files[b.Id].Trashed);
        Assert.Equal("b.txt", h.Cloud.Files[b.Id].Name);
        Assert.Equal("same", h.ReadLocal("b.txt"));
        Assert.NotNull(h.Cloud.FindByPath("c.txt"));
        Assert.Null(h.Cloud.FindByPath("a.txt"));
        Assert.Equal(0, h.Cloud.UploadCount); // a -> c was a rename
        Assert.Equal("c.txt", h.Cloud.Files[a.Id].Name);
        Assert.NotNull(await h.ItemAsync("b.txt"));
        Assert.Null(await h.ItemAsync("a.txt"));
    }

    [Fact]
    public async Task LocalMoveIntoNewFolder_WindowsEventShape_DoesNotDuplicateRemotely()
    {
        using var h = new EngineHarness();
        var a = h.Cloud.AddFile("a.txt", "payload");
        await h.SyncAsync();

        // Windows: Created(sub), Deleted(a.txt), Created(sub/a.txt) all inside one debounce window.
        Directory.CreateDirectory(h.Abs("sub"));
        File.Move(h.Abs("a.txt"), h.Abs("sub/a.txt"));
        h.TouchLocal("sub", ChangeType.Created);
        h.TouchLocal("a.txt", ChangeType.Deleted);
        h.TouchLocal("sub/a.txt", ChangeType.Created);
        await h.SyncAsync();

        var sub = h.Cloud.FindByPath("sub");
        Assert.NotNull(sub);
        var inSub = h.Cloud.Files.Values.Where(e => e.ParentId == sub!.Id && !e.Trashed).ToList();
        Assert.Single(inSub);
        Assert.Equal(a.Id, inSub[0].Id);           // moved, not re-uploaded
        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Equal(a.Id, (await h.ItemAsync("sub/a.txt"))!.RemoteId);
    }

    // ------------------------------------------------------------------ 6. download hash mismatch

    [Fact]
    public async Task DownloadHashMismatch_LeavesNoFile_AndRetries()
    {
        using var h = new EngineHarness();
        var hooked = new HookedCloud(h.Cloud);
        using var engine = EngineOver(h, hooked);
        await engine.RunOnceAsync(h.Profile);

        var f = h.Cloud.AddFile("v.txt", "real content");
        hooked.CorruptDownload = id => id == f.Id ? "corrupted on the wire"u8.ToArray() : null;
        await engine.RunOnceAsync(h.Profile);

        Assert.False(h.LocalExists("v.txt"));
        Assert.Empty(Directory.GetFiles(h.Root, "*.nimbus-tmp"));
        Assert.Equal(1, await h.PendingCountAsync());
        var ops = await h.Store.GetDueOperationsAsync(h.Profile.Id, DateTimeOffset.UtcNow.AddMinutes(1), 10);
        Assert.Equal(1, ops.Single().Attempts);
        Assert.Contains("hash", ops.Single().LastError, StringComparison.OrdinalIgnoreCase);

        hooked.CorruptDownload = null;
        await Task.Delay(10);
        await engine.FlushPendingAsync(h.Profile);
        Assert.Equal("real content", h.ReadLocal("v.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
    }

    // ------------------------------------------------------------------ 7. conflict resolved after remote moved on

    [Fact]
    public async Task KeepLocal_WhenRemoteChangedAgainAfterConflict_DoesNotSilentlyClobber()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "remote v2");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();
        Assert.Single(h.Conflicts);
        Assert.Equal(h.Conflicts[0].RemoteHash, h.Cloud.Files[f.Id].Md5);

        // Remote moves on before the user resolves. The user resolves having only seen v2.
        h.Cloud.SetContent(f.Id, "remote v3 (never shown to the user)");
        await h.Engine.ResolveConflictAsync(h.Conflicts[0].Id, ConflictResolution.KeepLocal);

        var remoteNow = h.Cloud.ContentOf("c.txt");
        var reconflicted = h.Conflicts.Count == 2 || (await h.Store.GetUnresolvedConflictsAsync(h.Profile.Id)).Count == 1;
        Assert.True(remoteNow.StartsWith("remote v3") || reconflicted,
            $"Remote v3 was overwritten by KeepLocal without the user ever seeing it. Remote is now '{remoteNow}'.");
    }

    // ------------------------------------------------------------------ 8. locked local file during download

    [Fact]
    public async Task LockedLocalFile_DownloadFailsAndRetries_OriginalUntouched()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("locked.txt", "v1");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "v2");

        await using (var hold = new FileStream(h.Abs("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await h.SyncAsync();
            Assert.Equal(1, await h.PendingCountAsync());
            Assert.Empty(Directory.GetFiles(h.Root, "*.nimbus-tmp"));
            Assert.Empty(h.Conflicts);
        }

        Assert.Equal("v1", h.ReadLocal("locked.txt"));
        await Task.Delay(10);
        await h.Engine.FlushPendingAsync(h.Profile);
        Assert.Equal("v2", h.ReadLocal("locked.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
    }

    // ------------------------------------------------------------------ 9. randomized alternating edits

    [Fact]
    public async Task TenCycles_AlternatingSides_ConvergesWithExactTransferCounts()
    {
        using var h = new EngineHarness();
        var files = Enumerable.Range(0, 5).Select(i => h.Cloud.AddFile($"f{i}.txt", $"init{i}")).ToList();
        await h.SyncAsync();
        Assert.Equal(5, h.Cloud.DownloadCount);

        var rng = new Random(20261019);
        int localEdits = 0, remoteEdits = 0;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var i = rng.Next(files.Count);
            var content = $"cycle{cycle}-file{i}-{rng.Next()}";
            if (cycle % 2 == 0) { await h.WriteAndTouchAsync($"f{i}.txt", content); localEdits++; }
            else { h.Cloud.SetContent(files[i].Id, content); remoteEdits++; }

            await h.SyncAsync();

            for (var k = 0; k < files.Count; k++)
                Assert.Equal(h.Cloud.ContentOf($"f{k}.txt"), h.ReadLocal($"f{k}.txt"));
            Assert.Empty(h.Conflicts);
            Assert.Equal(0, await h.PendingCountAsync());
        }

        Assert.Equal(5 + remoteEdits, h.Cloud.DownloadCount);
        Assert.Equal(localEdits, h.Cloud.UpdateCount);
        Assert.Equal(0, h.Cloud.UploadCount);
    }

    // ------------------------------------------------------------------ 10. initial scan: remote folder vs local file at same path

    [Fact]
    public async Task InitialScan_RemoteFolderCollidesWithLocalFile_RaisesConflictNotSilentError()
    {
        using var h = new EngineHarness(new SyncEngineOptions
        {
            MaxOperationAttempts = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1)
        });
        var x = h.Cloud.AddFolder("x");
        h.Cloud.AddFile("inner.txt", "inner", x.Id);
        await h.WriteLocalAsync("x", "I am a file named x");

        await h.SyncAsync();
        await Task.Delay(10);
        await h.Engine.FlushPendingAsync(h.Profile);

        // Policy (adjusted from the validator's original assertion): the local file is preserved as a
        // conflict copy and uploaded, the remote folder materializes, and a conflict is surfaced.
        // The original asserted the file stays at 'x', which would block the whole remote subtree.
        var survivors = Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p) == "I am a file named x").ToList();
        Assert.Single(survivors);                                                 // never clobbered
        Assert.True(Directory.Exists(h.Abs("x")));
        Assert.Equal("inner", h.ReadLocal("x/inner.txt"));
        var xItem = await h.ItemAsync("x");
        Assert.True(xItem is { IsDirectory: true, Status: SyncItemStatus.Synced }, "State must reflect that 'x' is now the synced directory.");
        Assert.NotEmpty(h.Conflicts);
        Assert.Equal("I am a file named x", h.Cloud.ContentOf(Path.GetFileName(survivors[0]))); // copy uploaded
        Assert.Equal(0, await h.PendingCountAsync());
    }

    // ------------------------------------------------------------------ extra: conflict record survives folder rename

    [Fact]
    public async Task ConflictInsideFolder_SurvivesRemoteFolderRename()
    {
        using var h = new EngineHarness();
        var dir = h.Cloud.AddFolder("dir");
        var c = h.Cloud.AddFile("c.txt", "base", dir.Id);
        await h.SyncAsync();
        h.Cloud.SetContent(c.Id, "remote edit");
        await h.WriteAndTouchAsync("dir/c.txt", "local edit");
        await h.SyncAsync();
        Assert.Single(await h.Store.GetUnresolvedConflictsAsync(h.Profile.Id));

        h.Cloud.Rename(dir.Id, "dir2");
        await h.SyncAsync();

        Assert.Equal("local edit", h.ReadLocal("dir2/c.txt"));
        Assert.Equal(SyncItemStatus.Conflict, (await h.ItemAsync("dir2/c.txt"))!.Status);
        var unresolved = await h.Store.GetUnresolvedConflictsAsync(h.Profile.Id);
        Assert.Single(unresolved); // FK cascade on the re-inserted SyncItem row must not drop it
        await h.Engine.ResolveConflictAsync(unresolved[0].Id, ConflictResolution.KeepRemote);
        Assert.Equal("remote edit", h.ReadLocal("dir2/c.txt"));
    }

    // ------------------------------------------------------------------ extra: remote folder trash with child entries (Drive emits both)

    [Fact]
    public async Task RemoteFolderTrash_WithChildTrashEntriesInFeed_SoftDeletesCleanly()
    {
        using var h = new EngineHarness();
        var dir = h.Cloud.AddFolder("dir");
        var a = h.Cloud.AddFile("a.txt", "a", dir.Id);
        await h.SyncAsync();

        h.Cloud.Trash(a.Id);   // Drive reports descendants trashed too
        h.Cloud.Trash(dir.Id);
        await h.SyncAsync();

        Assert.False(h.LocalExists("dir"));
        Assert.Single(Directory.GetFiles(Path.Combine(h.Root, ".nimbus-trash"), "a.txt", SearchOption.AllDirectories));
        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Null(await h.ItemAsync("dir"));
    }

    // ------------------------------------------------------------------ extra: local folder delete vs remote child edit

    [Fact]
    public async Task LocalFolderDelete_WhileRemoteEditedChild_RemoteEditWins()
    {
        using var h = new EngineHarness();
        var dir = h.Cloud.AddFolder("dir");
        var a = h.Cloud.AddFile("a.txt", "v1", dir.Id);
        await h.SyncAsync();

        h.Cloud.SetContent(a.Id, "v2 by a teammate");
        h.DeleteLocal("dir");
        await h.SyncAsync();

        Assert.False(h.Cloud.Files[dir.Id].Trashed, "Folder with a remotely edited child was trashed; 'remote edit beats local delete' does not hold for folders.");
        Assert.Equal("v2 by a teammate", h.ReadLocal("dir/a.txt"));
    }

    // ------------------------------------------------------------------ extra: MoveLocal over an untracked local file

    [Fact]
    public async Task RemoteRename_OntoUntrackedLocalFile_DoesNotOverwriteIt()
    {
        using var h = new EngineHarness();
        var a = h.Cloud.AddFile("a.txt", "A content");
        await h.SyncAsync();

        await h.WriteLocalAsync("b.txt", "user's unsynced b");  // watcher event not drained yet
        h.Cloud.Rename(a.Id, "b.txt");
        await h.SyncAsync();

        // The user's b.txt must still exist somewhere: in place, in .nimbus-trash, or as a conflict copy.
        var survivors = Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p) == "user's unsynced b").ToList();
        Assert.NotEmpty(survivors);
    }

    // ------------------------------------------------------------------ extra: local change drained, remote pull throws

    [Fact]
    public async Task LocalChangeDrained_ThenRemotePullThrows_ChangeIsNotLost()
    {
        using var h = new EngineHarness();
        var hooked = new HookedCloud(h.Cloud);
        using var engine = EngineOver(h, hooked);
        var f = h.Cloud.AddFile("e.txt", "v1");
        await engine.RunOnceAsync(h.Profile);

        await h.WriteAndTouchAsync("e.txt", "v2 local");
        hooked.FailGetChanges = () => new HttpRequestException("503 from Drive");
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.RunOnceAsync(h.Profile));

        hooked.FailGetChanges = null;
        await engine.RunOnceAsync(h.Profile);
        await engine.RunOnceAsync(h.Profile);

        Assert.Equal("v2 local", h.Cloud.ContentOf("e.txt"));
    }

    // ------------------------------------------------------------------ extra: pending download + later local edit

    [Fact]
    public async Task PendingDownloadThenLocalEdit_ConflictIsRaised_AndNothingIsTransferred()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("p.txt", "v1");
        await h.SyncAsync();

        h.Cloud.SetContent(f.Id, "remote v2");
        h.Cloud.FailDownload = _ => new IOException("blip");
        await h.SyncAsync();                                   // download queued, failed once
        Assert.Equal(1, await h.PendingCountAsync());

        h.Cloud.FailDownload = null;
        await h.WriteAndTouchAsync("p.txt", "local edit");
        await Task.Delay(10);
        await h.SyncAsync();

        Assert.Single(h.Conflicts);
        Assert.Equal("local edit", h.ReadLocal("p.txt"));
        Assert.Equal("remote v2", h.Cloud.ContentOf("p.txt"));  // must not be clobbered by the queued upload
        Assert.Equal(SyncItemStatus.Conflict, (await h.ItemAsync("p.txt"))!.Status);
    }

    // ------------------------------------------------------------------ extra: duplicate events for new folder children

    [Fact]
    public async Task NewLocalFolder_FolderAndChildEventsTogether_UploadsEachFileOnce()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();

        await h.WriteLocalAsync("pkg/one.txt", "1");
        await h.WriteLocalAsync("pkg/two.txt", "2");
        h.TouchLocal("pkg", ChangeType.Created);
        h.TouchLocal("pkg/one.txt", ChangeType.Created);
        h.TouchLocal("pkg/two.txt", ChangeType.Created);
        await h.SyncAsync();

        Assert.Equal("1", h.Cloud.ContentOf("pkg/one.txt"));
        Assert.Equal("2", h.Cloud.ContentOf("pkg/two.txt"));
        Assert.Equal(2, h.Cloud.UploadCount);
        Assert.Equal(0, h.Cloud.UpdateCount);
    }

    // ------------------------------------------------------------------ extra: case-variant names (Drive is case-sensitive)

    [Fact]
    public async Task RemoteFilesDifferingOnlyByCase_DoNotCrashTheEngine()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("Report.pdf", "upper");
        h.Cloud.AddFile("report.pdf", "lower");

        var ex = await Record.ExceptionAsync(() => h.SyncAsync());
        Assert.Null(ex);
        await h.SyncAsync();
        Assert.Equal(0, await h.PendingCountAsync());
    }

    [Fact]
    public async Task RemoteFilesWithSameNameInSameFolder_DoNotLoseEither()
    {
        using var h = new EngineHarness();
        var a = h.Cloud.AddFile("dup.txt", "first");
        var b = h.Cloud.AddFile("dup.txt", "second");

        await h.SyncAsync();
        await h.SyncAsync();

        var onDisk = Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToHashSet();
        Assert.Contains("first", onDisk);
        Assert.Contains("second", onDisk);
        Assert.False(h.Cloud.Files[a.Id].Trashed);
        Assert.False(h.Cloud.Files[b.Id].Trashed);
    }
    // ------------------------------------------------------------------ extra: overflow rescan vs items never downloaded

    [Fact]
    public async Task WatcherOverflow_WithPendingDownload_DoesNotTrashTheRemoteFile()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();
        var f = h.Cloud.AddFile("never-arrived.txt", "remote only so far");
        h.Cloud.FailDownload = _ => new IOException("offline");
        await h.SyncAsync();                       // item tracked as RemoteNew, download pending
        Assert.False(h.LocalExists("never-arrived.txt"));

        typeof(DebouncingFileSystemMonitor).GetField("_needsFullRescan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(h.Monitor, true);
        await h.SyncAsync();

        Assert.False(h.Cloud.Files[f.Id].Trashed, "Full rescan treated a never-downloaded file as a local delete and trashed it remotely.");
    }

    // ------------------------------------------------------------------ extra: user deletes their local copy of a conflicted file

    [Fact]
    public async Task LocalDeleteOfConflictedFile_DoesNotTrashTheRemoteEdit()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "teammate's edit");
        await h.WriteAndTouchAsync("c.txt", "my edit");
        await h.SyncAsync();
        Assert.Single(h.Conflicts);

        h.DeleteLocal("c.txt");   // user "resolves" by discarding their copy
        await h.SyncAsync();

        Assert.False(h.Cloud.Files[f.Id].Trashed, "Teammate's edit was trashed because the user deleted their own conflicted copy.");
    }

    // ------------------------------------------------------------------ extra: one locked file poisons the whole local batch

    [Fact]
    public async Task LockedFileInBatch_DoesNotLoseOtherLocalChanges()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("ok.txt", "v1");
        h.Cloud.AddFile("locked.pst", "v1");
        await h.SyncAsync();

        await h.WriteAndTouchAsync("ok.txt", "v2");
        h.TouchLocal("locked.pst", ChangeType.Modified);
        await using (var hold = new FileStream(h.Abs("locked.pst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Record.ExceptionAsync(() => h.SyncAsync());
        }
        await h.SyncAsync();
        await h.SyncAsync();

        Assert.Equal("v2", h.Cloud.ContentOf("ok.txt"));
    }

    // ------------------------------------------------------------------ extra: local folder rename cost

    [Fact]
    public async Task LocalFolderRename_IsNotTrashPlusReupload()
    {
        using var h = new EngineHarness();
        var old = h.Cloud.AddFolder("old");
        var a = h.Cloud.AddFile("big.bin", new string('x', 10_000), old.Id);
        await h.SyncAsync();

        Directory.Move(h.Abs("old"), h.Abs("new"));
        h.TouchLocal("old", ChangeType.Deleted);
        h.TouchLocal("new", ChangeType.Created);
        await h.SyncAsync();

        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.False(h.Cloud.Files[old.Id].Trashed);
        Assert.Equal("new", h.Cloud.Files[old.Id].Name);
        Assert.Equal(a.Id, (await h.ItemAsync("new/big.bin"))!.RemoteId);
    }

    // ------------------------------------------------------------------ extra: case-variant pair wedges the outbox

    [Fact]
    public async Task CaseVariantPair_DoesNotBlockUnrelatedOperations()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("Report.pdf", "upper");
        h.Cloud.AddFile("report.pdf", "lower");
        h.Cloud.AddFile("zzz-unrelated.txt", "should always arrive");

        for (var i = 0; i < 3; i++) { await Record.ExceptionAsync(() => h.SyncAsync()); await Task.Delay(5); }

        Assert.True(h.LocalExists("zzz-unrelated.txt"), "An unrelated file never synced because an earlier op throws out of the outbox loop every cycle.");
    }

    // ------------------------------------------------------------------ extra: remote edit during the upload retry window

    [Fact]
    public async Task RemoteEditDuringUploadRetryWindow_IsNotSilentlyOverwritten()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("w.txt", "base");
        await h.SyncAsync();

        await h.WriteAndTouchAsync("w.txt", "local edit");
        h.Cloud.FailUpload = _ => new IOException("503");
        await h.SyncAsync();                       // upload queued, failed once
        Assert.Equal(1, await h.PendingCountAsync());

        h.Cloud.SetContent(f.Id, "teammate's edit while we were offline");
        h.Cloud.FailUpload = null;
        await Task.Delay(10);
        await h.SyncAsync();                       // same cycle sees the remote edit AND retries the upload
        await h.SyncAsync();

        var remote = h.Cloud.ContentOf("w.txt");
        var conflicted = h.Conflicts.Count > 0;
        Assert.True(remote.StartsWith("teammate") || conflicted,
            $"Teammate's edit was overwritten by a retried upload with no conflict. Remote='{remote}', conflicts={h.Conflicts.Count}");
    }
}
