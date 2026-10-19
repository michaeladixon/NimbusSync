using NimbusSync.Engine.Models;
using NimbusSync.Tests.Fakes;

namespace NimbusSync.Tests.Sync;

/// <summary>
/// Each test maps to a finding from the code review. Test names say which.
/// </summary>
public class SyncEngineTests
{
    // ---------------------------------------------------------------- initial scan (findings 4, 5)

    [Fact]
    public async Task InitialScan_DownloadsRemoteOnlyFiles_PreservingHierarchy()
    {
        using var h = new EngineHarness();
        var docs = h.Cloud.AddFolder("docs");
        var sub = h.Cloud.AddFolder("sub", docs.Id);
        h.Cloud.AddFile("README.md", "root readme");
        h.Cloud.AddFile("README.md", "docs readme", docs.Id);     // same name, different folder
        h.Cloud.AddFile("deep.txt", "deep", sub.Id);

        await h.SyncAsync();

        Assert.Equal("root readme", h.ReadLocal("README.md"));
        Assert.Equal("docs readme", h.ReadLocal("docs/README.md"));
        Assert.Equal("deep", h.ReadLocal("docs/sub/deep.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Equal(SyncItemStatus.Synced, (await h.ItemAsync("docs/sub/deep.txt"))!.Status);
    }

    [Fact]
    public async Task InitialScan_UploadsLocalOnlyFiles_CreatingRemoteFolders()
    {
        using var h = new EngineHarness();
        await h.WriteLocalAsync("notes/a.txt", "A");
        await h.WriteLocalAsync("notes/inner/b.txt", "B");

        await h.SyncAsync();

        Assert.Equal("A", h.Cloud.ContentOf("notes/a.txt"));
        Assert.Equal("B", h.Cloud.ContentOf("notes/inner/b.txt"));
        Assert.True(h.Cloud.FindByPath("notes")!.IsFolder);
        Assert.NotNull((await h.ItemAsync("notes/inner/b.txt"))!.RemoteId);
    }

    [Fact]
    public async Task InitialScan_IdenticalContentOnBothSides_AdoptsBaselineWithoutTransfer()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("same.txt", "identical");
        await h.WriteLocalAsync("same.txt", "identical");

        await h.SyncAsync();

        Assert.Equal(0, h.Cloud.DownloadCount);
        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Equal(SyncItemStatus.Synced, (await h.ItemAsync("same.txt"))!.Status);
    }

    [Fact]
    public async Task InitialScan_DifferentContentOnBothSides_RaisesConflict()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("both.txt", "remote version");
        await h.WriteLocalAsync("both.txt", "local version");

        await h.SyncAsync();

        Assert.Single(h.Conflicts);
        Assert.Equal("both.txt", h.Conflicts[0].RelativePath);
        Assert.Equal("local version", h.ReadLocal("both.txt"));            // nothing clobbered
        Assert.Equal("remote version", h.Cloud.ContentOf("both.txt"));
    }

    // ---------------------------------------------------------------- echo suppression (finding 2)

    [Fact]
    public async Task Download_ThenLocalEchoEvent_DoesNotReupload()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("r.txt", "remote");
        await h.SyncAsync();
        Assert.Equal(1, h.Cloud.DownloadCount);

        // The watcher would fire for the file we just wrote.
        h.TouchLocal("r.txt", ChangeType.Created);
        await h.SyncAsync();
        h.TouchLocal("r.txt", ChangeType.Modified);
        await h.SyncAsync();

        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Equal(0, h.Cloud.UpdateCount);
        Assert.Equal(1, h.Cloud.DownloadCount);
    }

    [Fact]
    public async Task Upload_ThenRemoteFeedEcho_DoesNotRedownload()
    {
        using var h = new EngineHarness();
        await h.WriteLocalAsync("l.txt", "local");
        await h.SyncAsync();
        Assert.Equal(1, h.Cloud.UploadCount);

        // Next cycle sees our own upload in the change feed.
        await h.SyncAsync();
        await h.SyncAsync();

        Assert.Equal(0, h.Cloud.DownloadCount);
        Assert.Equal(1, h.Cloud.UploadCount);
    }

    [Fact]
    public async Task FiveCycleSteadyState_NoTransfersAtAll()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("a.txt", "A");
        await h.WriteLocalAsync("b.txt", "B");
        await h.SyncAsync();
        var (d, u, up) = (h.Cloud.DownloadCount, h.Cloud.UploadCount, h.Cloud.UpdateCount);

        for (var i = 0; i < 5; i++)
        {
            h.TouchLocal("a.txt"); h.TouchLocal("b.txt");
            await h.SyncAsync();
        }

        Assert.Equal((d, u, up), (h.Cloud.DownloadCount, h.Cloud.UploadCount, h.Cloud.UpdateCount));
    }

    // ---------------------------------------------------------------- incremental sync

    [Fact]
    public async Task LocalModify_PushesUpdate()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("f.txt", "v1");
        await h.SyncAsync();

        await h.WriteAndTouchAsync("f.txt", "v2");
        await h.SyncAsync();

        Assert.Equal("v2", h.Cloud.ContentOf("f.txt"));
        Assert.Equal(1, h.Cloud.UpdateCount);
    }

    [Fact]
    public async Task RemoteModify_PullsUpdate()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("f.txt", "v1");
        await h.SyncAsync();

        h.Cloud.SetContent(f.Id, "v2");
        await h.SyncAsync();

        Assert.Equal("v2", h.ReadLocal("f.txt"));
    }

    [Fact]
    public async Task NewRemoteFileInNewRemoteFolder_SameBatch_ResolvesPath()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();

        var folder = h.Cloud.AddFolder("later");
        h.Cloud.AddFile("x.txt", "X", folder.Id);
        await h.SyncAsync();

        Assert.Equal("X", h.ReadLocal("later/x.txt"));
    }

    [Fact]
    public async Task NewLocalFolderWithChildren_UploadsAll()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();

        await h.WriteLocalAsync("pkg/one.txt", "1");
        await h.WriteLocalAsync("pkg/nested/two.txt", "2");
        h.TouchLocal("pkg", ChangeType.Created); // Windows reports only the folder on a move-in
        await h.SyncAsync();

        Assert.Equal("1", h.Cloud.ContentOf("pkg/one.txt"));
        Assert.Equal("2", h.Cloud.ContentOf("pkg/nested/two.txt"));
    }

    [Fact]
    public async Task ChangeFeedWithDuplicateFileIds_DoesNotThrow_LastWins()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("dup.txt", "v1");
        await h.SyncAsync();

        h.Cloud.SetContent(f.Id, "v2");
        h.Cloud.SetContent(f.Id, "v3");
        h.Cloud.SetContent(f.Id, "v4"); // three feed entries, one file id
        await h.SyncAsync();

        Assert.Equal("v4", h.ReadLocal("dup.txt"));
        Assert.Equal(2, h.Cloud.DownloadCount); // initial + one for the batch, not three
    }

    // ---------------------------------------------------------------- conflicts

    [Fact]
    public async Task BothSidesModified_RaisesConflict_NeitherSideClobbered()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();

        h.Cloud.SetContent(f.Id, "remote edit");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();

        Assert.Single(h.Conflicts);
        Assert.Equal("local edit", h.ReadLocal("c.txt"));
        Assert.Equal("remote edit", h.Cloud.ContentOf("c.txt"));
        Assert.Equal(SyncItemStatus.Conflict, (await h.ItemAsync("c.txt"))!.Status);
    }

    [Fact]
    public async Task ConflictDetectedAcrossCycles_NotJustSameWindow()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();

        // Local edit happens but its watcher event has not been delivered yet; remote edits meanwhile.
        await h.WriteLocalAsync("c.txt", "local edit");
        h.Cloud.SetContent(f.Id, "remote edit");
        await h.SyncAsync();

        // The download must refuse to clobber a file that drifted from baseline.
        Assert.Single(h.Conflicts);
        Assert.Equal("local edit", h.ReadLocal("c.txt"));
        Assert.Equal(SyncItemStatus.Conflict, (await h.ItemAsync("c.txt"))!.Status);

        // The late watcher event changes nothing.
        h.TouchLocal("c.txt");
        await h.SyncAsync();
        Assert.Single(h.Conflicts);
        Assert.Equal("local edit", h.ReadLocal("c.txt"));
        Assert.Equal("remote edit", h.Cloud.ContentOf("c.txt"));
    }

    [Fact]
    public async Task UnresolvedConflict_FreezesItem_NoTransfersEitherDirection()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "remote edit");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();
        Assert.Single(h.Conflicts);
        var (d, u, up) = (h.Cloud.DownloadCount, h.Cloud.UploadCount, h.Cloud.UpdateCount);

        await h.WriteAndTouchAsync("c.txt", "local edit 2");
        h.Cloud.SetContent(f.Id, "remote edit 2");
        await h.SyncAsync();
        await h.SyncAsync();

        Assert.Single(h.Conflicts);
        Assert.Equal((d, u, up), (h.Cloud.DownloadCount, h.Cloud.UploadCount, h.Cloud.UpdateCount));
        Assert.Equal("local edit 2", h.ReadLocal("c.txt"));
        Assert.Equal("remote edit 2", h.Cloud.ContentOf("c.txt"));
    }

    [Fact]
    public async Task ResolveConflict_KeepLocal_Uploads()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "remote edit");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();

        await h.Engine.ResolveConflictAsync(h.Conflicts[0].Id, ConflictResolution.KeepLocal);

        Assert.Equal("local edit", h.Cloud.ContentOf("c.txt"));
        Assert.Empty(await h.Store.GetUnresolvedConflictsAsync(h.Profile.Id));
    }

    [Fact]
    public async Task ResolveConflict_KeepRemote_Downloads()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "remote edit");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();

        await h.Engine.ResolveConflictAsync(h.Conflicts[0].Id, ConflictResolution.KeepRemote);

        Assert.Equal("remote edit", h.ReadLocal("c.txt"));
    }

    [Fact]
    public async Task ResolveConflict_KeepBoth_CreatesConflictCopy()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("c.txt", "base");
        await h.SyncAsync();
        h.Cloud.SetContent(f.Id, "remote edit");
        await h.WriteAndTouchAsync("c.txt", "local edit");
        await h.SyncAsync();

        await h.Engine.ResolveConflictAsync(h.Conflicts[0].Id, ConflictResolution.KeepBoth);

        Assert.Equal("remote edit", h.ReadLocal("c.txt"));
        var copy = Directory.GetFiles(h.Root, "c (conflict*).txt").Single();
        Assert.Equal("local edit", File.ReadAllText(copy));
        Assert.Equal("local edit", h.Cloud.ContentOf(Path.GetFileName(copy)));
    }

    // ---------------------------------------------------------------- deletes (significant findings)

    [Fact]
    public async Task RemoteTrash_SoftDeletesLocal_IntoTrashFolder()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("gone.txt", "bye");
        await h.SyncAsync();

        h.Cloud.Trash(f.Id);
        await h.SyncAsync();

        Assert.False(h.LocalExists("gone.txt"));
        var trashed = Directory.GetFiles(Path.Combine(h.Root, ".nimbus-trash"), "gone.txt", SearchOption.AllDirectories);
        Assert.Single(trashed);
        Assert.Null(await h.ItemAsync("gone.txt"));
    }

    [Fact]
    public async Task RemoteRemoved_KeepsLocalFile_DropsTracking()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("revoked.txt", "mine");
        await h.SyncAsync();

        h.Cloud.Remove(f.Id); // access revoked / permanently deleted / moved out of scope
        await h.SyncAsync();

        Assert.True(h.LocalExists("revoked.txt"));
        Assert.Equal("mine", h.ReadLocal("revoked.txt"));
        Assert.Null(await h.ItemAsync("revoked.txt"));
    }

    [Fact]
    public async Task RemoteFolderTrashed_SoftDeletesWholeLocalSubtree()
    {
        using var h = new EngineHarness();
        var d = h.Cloud.AddFolder("dir");
        h.Cloud.AddFile("a.txt", "a", d.Id);
        await h.SyncAsync();
        Assert.True(h.LocalExists("dir/a.txt"));

        h.Cloud.Trash(d.Id);
        await h.SyncAsync();

        Assert.False(h.LocalExists("dir"));
        Assert.Null(await h.ItemAsync("dir/a.txt"));
        Assert.Null(await h.ItemAsync("dir"));
    }

    [Fact]
    public async Task LocalDelete_TrashesRemote_CleansState()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("del.txt", "x");
        await h.SyncAsync();

        h.DeleteLocal("del.txt");
        await h.SyncAsync();

        Assert.True(h.Cloud.Files[f.Id].Trashed);
        Assert.Null(await h.ItemAsync("del.txt"));
    }

    [Fact]
    public async Task LocalDeleteOfFolder_CleansAllChildState()
    {
        using var h = new EngineHarness();
        var d = h.Cloud.AddFolder("dir");
        h.Cloud.AddFile("a.txt", "a", d.Id);
        h.Cloud.AddFile("b.txt", "b", d.Id);
        await h.SyncAsync();

        h.DeleteLocal("dir"); // single watcher event for the folder, as on Windows
        await h.SyncAsync();

        Assert.True(h.Cloud.Files[d.Id].Trashed);
        Assert.Empty(await h.Store.GetItemsUnderPathAsync(h.Profile.Id, "dir"));
    }

    [Fact]
    public async Task LocalDelete_WhileRemoteModified_RemoteWins()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("f.txt", "v1");
        await h.SyncAsync();

        h.Cloud.SetContent(f.Id, "v2");
        h.DeleteLocal("f.txt");
        await h.SyncAsync();

        Assert.False(h.Cloud.Files[f.Id].Trashed);
        Assert.Equal("v2", h.ReadLocal("f.txt"));
    }

    // ---------------------------------------------------------------- renames

    [Fact]
    public async Task LocalRename_IsRemoteRename_NotReupload()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("old.txt", "payload");
        await h.SyncAsync();

        File.Move(h.Abs("old.txt"), h.Abs("new.txt"));
        h.TouchLocal("old.txt", ChangeType.Deleted);
        h.TouchLocal("new.txt", ChangeType.Created);
        await h.SyncAsync();

        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Equal("new.txt", h.Cloud.Files[f.Id].Name);
        Assert.False(h.Cloud.Files[f.Id].Trashed);
        Assert.Null(await h.ItemAsync("old.txt"));
        Assert.Equal(f.Id, (await h.ItemAsync("new.txt"))!.RemoteId);
    }

    [Fact]
    public async Task RemoteRename_MovesLocalFile()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("before.txt", "same");
        await h.SyncAsync();

        h.Cloud.Rename(f.Id, "after.txt");
        await h.SyncAsync();

        Assert.False(h.LocalExists("before.txt"));
        Assert.Equal("same", h.ReadLocal("after.txt"));
        Assert.Equal(1, h.Cloud.DownloadCount); // no re-download for a rename
    }

    [Fact]
    public async Task RemoteMoveIntoFolder_MovesLocalFile()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("m.txt", "mv");
        var d = h.Cloud.AddFolder("dest");
        await h.SyncAsync();

        h.Cloud.Reparent(f.Id, d.Id);
        await h.SyncAsync();

        Assert.Equal("mv", h.ReadLocal("dest/m.txt"));
        Assert.False(h.LocalExists("m.txt"));
    }

    // ---------------------------------------------------------------- outbox / retry (token-advance finding)

    [Fact]
    public async Task TransientDownloadFailure_IsRetried_TokenNotLost()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();
        var f = h.Cloud.AddFile("flaky.txt", "eventually");

        var calls = 0;
        h.Cloud.FailDownload = id => id == f.Id && ++calls <= 2 ? new IOException("network blip") : null;

        await h.SyncAsync();                         // fails, queued
        Assert.False(h.LocalExists("flaky.txt"));
        Assert.Equal(1, await h.PendingCountAsync());

        await Task.Delay(5);
        await h.Engine.FlushPendingAsync(h.Profile); // fails again
        await Task.Delay(5);
        await h.Engine.FlushPendingAsync(h.Profile); // succeeds

        Assert.Equal("eventually", h.ReadLocal("flaky.txt"));
        Assert.Equal(0, await h.PendingCountAsync());
    }

    [Fact]
    public async Task PartialDownload_LeavesNoCorruptFile()
    {
        using var h = new EngineHarness();
        await h.SyncAsync();
        var f = h.Cloud.AddFile("big.bin", "the full content");
        h.Cloud.FailDownload = _ => new IOException("cut off");

        await h.SyncAsync();

        Assert.False(h.LocalExists("big.bin"));
        Assert.Empty(Directory.GetFiles(h.Root, "*.nimbus-tmp"));
    }

    [Fact]
    public async Task MaxAttemptsExceeded_DropsOperation_MarksItemError()
    {
        using var h = new EngineHarness(new Engine.Sync.SyncEngineOptions
        {
            MaxOperationAttempts = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1)
        });
        await h.SyncAsync();
        h.Cloud.AddFile("dead.txt", "x");
        h.Cloud.FailDownload = _ => new IOException("permanent");

        await h.SyncAsync();
        await Task.Delay(5);
        await h.Engine.FlushPendingAsync(h.Profile);

        Assert.Equal(0, await h.PendingCountAsync());
        Assert.Equal(SyncItemStatus.Error, (await h.ItemAsync("dead.txt"))!.Status);
    }

    // ---------------------------------------------------------------- Google-native files

    [Fact]
    public async Task NativeDoc_LinkStubPolicy_WritesUrlFile_NeverUploads()
    {
        using var h = new EngineHarness();
        h.Cloud.AddNativeDoc("Plan");
        await h.SyncAsync();

        Assert.True(h.LocalExists("Plan.url"));
        Assert.Contains("docs.google.com", h.ReadLocal("Plan.url"));
        Assert.Equal(0, h.Cloud.DownloadCount);

        await h.WriteAndTouchAsync("Plan.url", "tampered");
        await h.SyncAsync();
        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Equal(0, h.Cloud.UpdateCount);
    }

    [Fact]
    public async Task NativeDoc_SkipPolicy_IgnoresEntirely()
    {
        using var h = new EngineHarness(nativePolicy: NativeFilePolicy.Skip);
        h.Cloud.AddNativeDoc("Plan");
        await h.SyncAsync();

        Assert.False(h.LocalExists("Plan.url"));
        Assert.Null(await h.ItemAsync("Plan.url"));
    }

    // ---------------------------------------------------------------- direction & exclusions

    [Fact]
    public async Task RemoteToLocalDirection_NeverUploads()
    {
        using var h = new EngineHarness(direction: SyncDirection.RemoteToLocal);
        h.Cloud.AddFile("r.txt", "remote");
        await h.WriteLocalAsync("l.txt", "local");
        await h.SyncAsync();

        Assert.Equal("remote", h.ReadLocal("r.txt"));
        Assert.Equal(0, h.Cloud.UploadCount);
        Assert.Null(h.Cloud.FindByPath("l.txt"));
    }

    [Fact]
    public async Task ExcludedPatterns_AreNeverUploaded()
    {
        using var h = new EngineHarness();
        await h.WriteLocalAsync("~$lock.docx", "office lock");
        await h.WriteLocalAsync("real.docx", "doc");
        await h.SyncAsync();

        Assert.Null(h.Cloud.FindByPath("~$lock.docx"));
        Assert.NotNull(h.Cloud.FindByPath("real.docx"));
    }

    [Fact]
    public async Task TrashFolder_IsNeverUploaded()
    {
        using var h = new EngineHarness();
        var f = h.Cloud.AddFile("gone.txt", "bye");
        await h.SyncAsync();
        h.Cloud.Trash(f.Id);
        await h.SyncAsync();

        h.TouchLocal(".nimbus-trash", ChangeType.Created);
        await h.SyncAsync();

        Assert.Null(h.Cloud.FindByPath(".nimbus-trash"));
    }

    // ---------------------------------------------------------------- watcher overflow

    [Fact]
    public async Task WatcherOverflow_FullRescanCatchesMissedChanges()
    {
        using var h = new EngineHarness();
        h.Cloud.AddFile("tracked.txt", "v1");
        await h.SyncAsync();

        await h.WriteLocalAsync("tracked.txt", "v2 made while watcher was down");
        File.SetLastWriteTimeUtc(h.Abs("tracked.txt"), DateTime.UtcNow.AddMinutes(1));
        await h.WriteLocalAsync("untracked.txt", "also missed");
        // No Inject calls: simulate lost events, then overflow.
        typeof(Engine.Sync.DebouncingFileSystemMonitor).GetField("_needsFullRescan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(h.Monitor, true);

        await h.SyncAsync();

        Assert.Equal("v2 made while watcher was down", h.Cloud.ContentOf("tracked.txt"));
        Assert.Equal("also missed", h.Cloud.ContentOf("untracked.txt"));
        Assert.False(h.Monitor.NeedsFullRescan);
    }
}
