using System.Globalization;
using Microsoft.Extensions.Logging;
using NimbusSync.Engine.Abstractions;
using NimbusSync.Engine.Models;

namespace NimbusSync.Engine.Sync;

/// <summary>
/// Core orchestrator: detect → verify against baseline → plan → commit (outbox + token) → execute.
///
/// Correctness properties:
///  - Every detected change is verified against the stored baseline before it becomes work,
///    so echoes of the engine's own writes are no-ops and the two sides cannot ping-pong.
///  - Every destructive write (upload-over, trash, move-over, download-over) re-checks the
///    other side's live state at execution time and raises a conflict instead of clobbering.
///  - Planned operations and the new change token are committed in one transaction before
///    anything executes, so a crash or API failure never loses work and never skips a token.
///  - Items in Conflict status are frozen in both directions until the user resolves them.
///  - Remote "removed" never deletes local data; remote "trashed" moves local data to
///    <c>.nimbus-trash</c> rather than deleting it.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly ICloudProvider _cloud;
    private readonly ISyncStateStore _store;
    private readonly IFileSystemMonitor _monitor;
    private readonly ILogger<SyncEngine> _logger;
    private readonly SyncEngineOptions _options;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    /// <summary>Folders known to be outside the synced root; avoids a metadata fetch per cycle.</summary>
    private readonly Dictionary<string, DateTimeOffset> _foreignFolderCache = new();
    private static readonly TimeSpan ForeignFolderTtl = TimeSpan.FromMinutes(10);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SyncProfile? _profile;
    private ExclusionMatcher _exclusions = new([]);

    public SyncEngine(ICloudProvider cloud, ISyncStateStore store, IFileSystemMonitor monitor,
        ILogger<SyncEngine> logger, SyncEngineOptions? options = null)
    {
        _cloud = cloud;
        _store = store;
        _monitor = monitor;
        _logger = logger;
        _options = options ?? new SyncEngineOptions();
    }

    /// <summary>Raised from background threads. UI consumers must marshal to their dispatcher.</summary>
    public event EventHandler<SyncStatusEventArgs>? StatusChanged;
    public event EventHandler<SyncConflict>? ConflictDetected;

    public SyncEngineState State { get; private set; } = SyncEngineState.Stopped;

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync(SyncProfile profile, CancellationToken ct = default)
    {
        if (State != SyncEngineState.Stopped)
            throw new InvalidOperationException($"Engine is {State}; must be Stopped.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await PrepareProfileAsync(profile, _cts.Token);

        if (string.IsNullOrEmpty(profile.ChangeToken))
        {
            SetState(SyncEngineState.InitialScan);
            await RunLockedAsync(() => PerformInitialScanAsync(profile, _cts.Token), _cts.Token);
        }

        _monitor.Start(profile.LocalRootPath, ExcludePatternsOf(profile));
        SetState(SyncEngineState.Watching);
        _loop = RunLoopAsync(profile, _cts.Token);
        _logger.LogInformation("Started profile '{Name}': {Local} ↔ {Remote}", profile.Name, profile.LocalRootPath, profile.RemoteFolderId);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _monitor.Stop();
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _loop = null;
        // Wait for any in-flight locked work (e.g. a conflict resolution) to finish.
        await _cycleLock.WaitAsync();
        _cycleLock.Release();
        SetState(SyncEngineState.Stopped);
    }

    /// <summary>One full cycle (initial scan if needed). Used by tests and manual "Sync now".</summary>
    public async Task RunOnceAsync(SyncProfile profile, CancellationToken ct = default)
    {
        // While the engine is running, StopAsync must be able to cancel a manual cycle
        // instead of waiting for it on the cycle lock.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct,
            State != SyncEngineState.Stopped && _cts is { } running ? running.Token : CancellationToken.None);
        ct = linked.Token;
        await PrepareProfileAsync(profile, ct);
        await RunLockedAsync(async () =>
        {
            if (string.IsNullOrEmpty(profile.ChangeToken))
                await PerformInitialScanAsync(profile, ct);
            else
                await RunCycleAsync(profile, ct);
        }, ct);
    }

    /// <summary>Retry any queued operations without pulling new changes.</summary>
    public Task FlushPendingAsync(SyncProfile profile, CancellationToken ct = default)
        => RunLockedAsync(() => ExecutePendingAsync(profile, ct), ct);

    private async Task PrepareProfileAsync(SyncProfile profile, CancellationToken ct)
    {
        await _store.InitializeAsync(ct);
        if (profile.Id == 0) await _store.UpsertProfileAsync(profile, ct);
        _profile = profile;
        _exclusions = new ExclusionMatcher(ExcludePatternsOf(profile));
        Directory.CreateDirectory(profile.LocalRootPath);
    }

    private static string[] ExcludePatternsOf(SyncProfile p)
        => p.ExcludePatterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task RunLockedAsync(Func<Task> work, CancellationToken ct)
    {
        await _cycleLock.WaitAsync(ct);
        try { await work(); }
        finally { _cycleLock.Release(); }
    }

    private async Task RunLoopAsync(SyncProfile profile, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(profile.PollIntervalSeconds), ct);
                await RunLockedAsync(() => RunCycleAsync(profile, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync cycle failed; retrying next interval.");
                SetState(SyncEngineState.Error);
            }
        }
    }

    // ------------------------------------------------------------------ initial scan

    private async Task PerformInitialScanAsync(SyncProfile profile, CancellationToken ct)
    {
        _logger.LogInformation("Initial scan for '{Name}'", profile.Name);

        // Token first, so anything that changes during the scan shows up in the first cycle.
        var token = await _cloud.GetStartChangeTokenAsync(profile.SharedDriveId, ct);
        var remote = await _cloud.ListFolderRecursiveAsync(profile.RemoteFolderId, profile.SharedDriveId, ct);
        var remoteById = remote.Where(r => !r.IsTrashed).ToDictionary(r => r.Id);
        var local = ScanLocal(profile);

        var plan = new CyclePlan();
        var matchedLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Folders first (shallowest first) so parents exist before children.
        var ordered = remoteById.Values
            .Select(r => (r, path: ResolvePathFromMap(r, remoteById, profile.RemoteFolderId)))
            .Where(t => t.path is not null && !_exclusions.IsExcluded(t.path!))
            .OrderByDescending(t => t.r.IsFolder).ThenBy(t => t.path!.Count(c => c == '/'));

        foreach (var (r, rawPath) in ordered)
        {
            var path = rawPath!;

            if (r.IsFolder)
            {
                path = await DedupPathAsync(profile, path, r.Id, plan, ct);
                if (local.TryGetValue(path, out var existingLocal) && existingLocal is FileInfo clashFile)
                {
                    await PlanTypeClashAsync(profile, path, clashFile, plan, ct); // local file where a folder must go
                    local.Remove(path);
                }
                matchedLocal.Add(path);
                plan.Add(NewItem(profile, path, r, isDir: true, SyncItemStatus.Synced));
                if (!local.ContainsKey(path))
                    plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, path, r.Id));
                continue;
            }

            if (r.IsGoogleNative)
            {
                if (profile.NativeFilePolicy == NativeFilePolicy.Skip) continue;
                var stubPath = await DedupPathAsync(profile, path + ".url", r.Id, plan, ct);
                matchedLocal.Add(stubPath);
                plan.Add(NewItem(profile, stubPath, r, isDir: false, SyncItemStatus.Synced, isNative: true));
                plan.Ops.Add(Op(profile, SyncOperationType.WriteNativeStub, stubPath, r.Id));
                continue;
            }

            path = await DedupPathAsync(profile, path, r.Id, plan, ct);
            matchedLocal.Add(path);

            if (local.TryGetValue(path, out var localInfo) && localInfo is FileInfo fi)
            {
                var hash = await FileHasher.ComputeMd5Async(fi.FullName, ct);
                var item = NewItem(profile, path, r, false, SyncItemStatus.Synced);
                SetLocalBaseline(item, fi, hash);
                if (!string.Equals(hash, r.Md5Hash, StringComparison.OrdinalIgnoreCase))
                {
                    // Both sides have content and no baseline exists: the user decides.
                    item.Status = SyncItemStatus.Conflict;
                    plan.Conflicts.Add(new PlannedConflict(item, hash, r.Md5Hash, fi.Length, r.Size,
                        new DateTimeOffset(fi.LastWriteTimeUtc), r.ModifiedTime));
                }
                plan.Add(item);
            }
            else if (local.TryGetValue(path, out var localDir) && localDir is DirectoryInfo)
            {
                await PlanTypeClashAsync(profile, path, localDir, plan, ct); // local folder where a file must go
                local.Remove(path);
                plan.Add(NewItem(profile, path, r, false, SyncItemStatus.RemoteNew));
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, r.Id, r.Md5Hash));
            }
            else
            {
                plan.Add(NewItem(profile, path, r, false, SyncItemStatus.RemoteNew));
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, r.Id, r.Md5Hash));
            }
        }

        foreach (var (path, info) in local.OrderBy(kv => kv.Key.Count(c => c == '/')))
        {
            if (matchedLocal.Contains(path) || plan.HasPath(path)) continue;
            if (info is DirectoryInfo)
            {
                plan.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, path));
            }
            else
            {
                plan.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.Upload, path));
            }
        }

        await CommitPlanAsync(profile, plan, token, ct);
        _logger.LogInformation("Initial scan: {Items} items, {Ops} operations, {Conflicts} conflicts",
            plan.Items.Count, plan.Ops.Count, plan.Conflicts.Count);

        await ExecutePendingAsync(profile, ct);
    }

    /// <summary>
    /// A file sits where the other side has a folder (or vice versa). Preserve the local
    /// item as a conflict copy, upload it, and record an auto-resolved KeepBoth conflict.
    /// </summary>
    private async Task PlanTypeClashAsync(SyncProfile profile, string path, FileSystemInfo localInfo, CyclePlan plan, CancellationToken ct)
    {
        var copyPath = ConflictCopyName(path);
        var from = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
        var to = PathNormalizer.ToAbsolute(profile.LocalRootPath, copyPath);
        if (localInfo is DirectoryInfo) Directory.Move(from, to); else File.Move(from, to);
        _logger.LogWarning("'{Path}' clashes with a remote item of a different type; kept local copy as '{Copy}'.", path, copyPath);

        var existing = await _store.GetItemByPathAsync(profile.Id, path, ct);
        if (existing is not null) await ForgetSubtreeAsync(profile, path, ct);

        if (localInfo is DirectoryInfo)
        {
            plan.Add(new SyncItem { ProfileId = profile.Id, RelativePath = copyPath, IsDirectory = true, Status = SyncItemStatus.LocalNew });
            plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, copyPath));
            await EnqueueUntrackedChildrenAsync(profile, copyPath, plan, [], ct);
        }
        else
        {
            var fi = new FileInfo(to);
            var item = new SyncItem { ProfileId = profile.Id, RelativePath = copyPath, Status = SyncItemStatus.LocalNew };
            var hash = await FileHasher.ComputeMd5Async(to, ct);
            SetLocalBaseline(item, fi, hash);
            plan.Add(item);
            plan.Ops.Add(Op(profile, SyncOperationType.Upload, copyPath));
            plan.Conflicts.Add(new PlannedConflict(item, hash, null, fi.Length, null, new DateTimeOffset(fi.LastWriteTimeUtc), null, AutoResolve: ConflictResolution.KeepBoth));
        }
    }

    // ------------------------------------------------------------------ cycle

    private async Task RunCycleAsync(SyncProfile profile, CancellationToken ct)
    {
        SetState(SyncEngineState.Syncing);

        // Remote first: if the pull fails nothing local has been drained yet.
        var token = profile.ChangeToken ?? throw new InvalidOperationException("No change token; initial scan required.");
        var (rawRemote, newToken) = await _cloud.GetChangesAsync(token, profile.SharedDriveId, ct);

        // The feed may report a file more than once; the last entry wins.
        var remoteByFileId = new Dictionary<string, RemoteChange>();
        foreach (var c in rawRemote) remoteByFileId[c.FileId] = c;

        var rescan = _monitor.NeedsFullRescan;
        IReadOnlyList<DetectedChange> localChanges;
        if (rescan)
        {
            _logger.LogWarning("File system watcher overflowed; performing full local rescan.");
            localChanges = await DiffLocalTreeAsync(profile, ct);
        }
        else
        {
            localChanges = _monitor.DrainChanges();
        }

        if (localChanges.Count > 0 || remoteByFileId.Count > 0)
            _logger.LogInformation("Cycle: {L} local, {R} remote changes", localChanges.Count, remoteByFileId.Count);

        try
        {
            var plan = await PlanAsync(profile, localChanges, remoteByFileId, ct);
            await CommitPlanAsync(profile, plan, newToken, ct);
        }
        catch
        {
            if (!rescan) _monitor.Requeue(localChanges); // nothing was committed; do not lose the drained events
            throw;
        }
        if (rescan) _monitor.AcknowledgeRescan();

        await ExecutePendingAsync(profile, ct);
        SetState(SyncEngineState.Watching);
    }

    private async Task CommitPlanAsync(SyncProfile profile, CyclePlan plan, string? newToken, CancellationToken ct)
    {
        ApplyDirectionFilter(profile, plan.Ops);
        await _store.CommitCycleAsync(profile.Id, plan.Items, plan.Ops, newToken, ct);
        if (newToken is not null) profile.ChangeToken = newToken;

        foreach (var id in plan.ItemDeletes) await _store.DeleteItemAsync(id, ct);
        foreach (var dir in plan.SubtreeDeletes) await ForgetSubtreeAsync(profile, dir, ct);

        foreach (var pc in plan.Conflicts)
            await RaiseConflictAsync(profile, pc.Item, pc.LocalHash, pc.RemoteHash, pc.LocalSize, pc.RemoteSize,
                pc.LocalModified, pc.RemoteModified, pc.AutoResolve, ct);
    }

    private async Task RaiseConflictAsync(SyncProfile profile, SyncItem item, string? localHash, string? remoteHash,
        long? localSize, long? remoteSize, DateTimeOffset? localModified, DateTimeOffset? remoteModified,
        ConflictResolution? autoResolve, CancellationToken ct)
    {
        var conflict = new SyncConflict
        {
            ProfileId = profile.Id, SyncItemId = item.Id, RelativePath = item.RelativePath,
            LocalHash = localHash, RemoteHash = remoteHash, LocalSize = localSize, RemoteSize = remoteSize,
            LocalModified = localModified, RemoteModified = remoteModified
        };
        await _store.AddConflictAsync(conflict, ct);
        if (autoResolve is { } r)
        {
            await _store.ResolveConflictAsync(conflict.Id, r, ct);
            conflict.Resolution = r;
            conflict.ResolvedAt = DateTimeOffset.UtcNow;
        }
        ConflictDetected?.Invoke(this, conflict);
    }

    private static void ApplyDirectionFilter(SyncProfile profile, List<PendingOperation> ops)
    {
        if (profile.Direction == SyncDirection.Bidirectional) return;
        ops.RemoveAll(op => profile.Direction switch
        {
            SyncDirection.LocalToRemote => op.Type is SyncOperationType.Download or SyncOperationType.CreateLocalFolder
                or SyncOperationType.DeleteLocal or SyncOperationType.MoveLocal or SyncOperationType.WriteNativeStub,
            SyncDirection.RemoteToLocal => op.Type is SyncOperationType.Upload or SyncOperationType.CreateRemoteFolder
                or SyncOperationType.DeleteRemote or SyncOperationType.MoveRemote,
            _ => false
        });
    }

    // ------------------------------------------------------------------ planning

    private async Task<CyclePlan> PlanAsync(SyncProfile profile, IReadOnlyList<DetectedChange> localChanges,
        Dictionary<string, RemoteChange> remoteByFileId, CancellationToken ct)
    {
        var plan = new CyclePlan();
        var consumedRemote = new HashSet<string>();
        var requeue = new List<DetectedChange>();

        // Pass 1: local deletions, indexed for rename detection.
        var deletedByHash = new Dictionary<string, SyncItem>(StringComparer.OrdinalIgnoreCase);
        var deletedDirs = new List<SyncItem>();
        var pendingDeletes = new List<SyncItem>();
        var creates = new List<DetectedChange>();

        foreach (var change in localChanges)
        {
            if (_exclusions.IsExcluded(change.RelativePath)) continue;
            var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, change.RelativePath);
            if (File.Exists(abs) || Directory.Exists(abs)) { creates.Add(change); continue; }

            var item = await _store.GetItemByPathAsync(profile.Id, change.RelativePath, ct);
            if (item is null) continue;
            pendingDeletes.Add(item);
            if (item.IsDirectory) deletedDirs.Add(item);
            else if (item.LastSyncedLocalHash is not null) deletedByHash.TryAdd(item.LastSyncedLocalHash, item);
        }

        // Pass 2: local creates/modifies, shallowest first so folder renames are seen before their children.
        var movedDirs = new List<string>(); // new paths of locally renamed folders; children need no separate work
        foreach (var change in creates.OrderBy(c => c.RelativePath.Count(ch => ch == '/')))
        {
            var path = change.RelativePath;
            if (plan.HasPath(path) || movedDirs.Any(d => PathNormalizer.IsUnder(path, d))) continue;

            var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
            var item = await _store.GetItemByPathAsync(profile.Id, path, ct);

            if (item?.IsGoogleNative == true) continue;              // link stubs are never uploaded
            if (item?.Status == SyncItemStatus.Conflict) continue;   // frozen until the user resolves it

            if (Directory.Exists(abs))
            {
                if (item is not null) continue; // directory "modified" events are noise

                var renamedFrom = await FindRenamedDirectoryAsync(profile, path, deletedDirs, ct);
                if (renamedFrom is not null)
                {
                    pendingDeletes.Remove(renamedFrom);
                    deletedDirs.Remove(renamedFrom);
                    foreach (var child in await _store.GetItemsUnderPathAsync(profile.Id, renamedFrom.RelativePath, ct))
                        pendingDeletes.RemoveAll(d => d.Id == child.Id);
                    plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveRemote, renamedFrom.RelativePath, path, renamedFrom.RemoteId));
                    movedDirs.Add(path);
                    continue;
                }

                plan.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, path));
                await EnqueueUntrackedChildrenAsync(profile, path, plan, deletedByHash, ct);
                continue;
            }

            try
            {
                await PlanLocalFileAsync(profile, path, item, plan, deletedByHash, pendingDeletes, remoteByFileId, consumedRemote, ct);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "'{Path}' could not be read (locked?); will retry next cycle.", path);
                requeue.Add(change);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "'{Path}' could not be read (access denied); will retry next cycle.", path);
                requeue.Add(change);
            }
        }

        // Sources of detected renames are not deletions.
        pendingDeletes.RemoveAll(d => plan.MovedSources.Contains(d.Id));

        // Pass 3: remaining local deletions.
        foreach (var item in pendingDeletes)
        {
            if (item.IsDirectory)
            {
                var children = await _store.GetItemsUnderPathAsync(profile.Id, item.RelativePath, ct);
                var protectedChildren = children.Where(c => !c.IsDirectory && (c.Status == SyncItemStatus.Conflict || RemoteContentChanged(c, remoteByFileId))).ToList();
                if (protectedChildren.Count > 0)
                {
                    // A teammate edited something inside: remote wins, restore the folder locally.
                    _logger.LogWarning("Local delete of '{Dir}' skipped: {N} file(s) inside changed remotely. Restoring.", item.RelativePath, protectedChildren.Count);
                    plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, item.RelativePath, item.RemoteId));
                    foreach (var c in children)
                    {
                        if (c.IsDirectory) plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, c.RelativePath, c.RemoteId));
                        else if (c.IsGoogleNative) plan.Ops.Add(Op(profile, SyncOperationType.WriteNativeStub, c.RelativePath, c.RemoteId));
                        else { if (c.RemoteId is not null) consumedRemote.Add(c.RemoteId); plan.Ops.Add(Op(profile, SyncOperationType.Download, c.RelativePath, c.RemoteId)); }
                    }
                    continue;
                }
            }
            else if (item.Status == SyncItemStatus.Conflict || RemoteContentChanged(item, remoteByFileId))
            {
                // Remote edit beats local delete: restore by downloading.
                if (item.RemoteId is not null) consumedRemote.Add(item.RemoteId);
                item.Status = SyncItemStatus.RemoteModified;
                plan.Add(item);
                plan.Ops.Add(Op(profile, SyncOperationType.Download, item.RelativePath, item.RemoteId));
                continue;
            }

            item.Status = SyncItemStatus.LocalDeleted;
            plan.Add(item);
            plan.Ops.Add(Op(profile, SyncOperationType.DeleteRemote, item.RelativePath, item.RemoteId));
        }

        // Pass 4: remote changes.
        var pathCache = new Dictionary<string, string?>();
        foreach (var (fileId, change) in remoteByFileId)
        {
            if (consumedRemote.Contains(fileId)) continue;
            var item = await _store.GetItemByRemoteIdAsync(profile.Id, fileId, ct);
            if (item?.Status == SyncItemStatus.Conflict && change.Type == RemoteChangeType.Modified) continue; // frozen

            if (change.Type == RemoteChangeType.Removed)
            {
                if (item is null) continue;
                _logger.LogWarning("'{Path}' is no longer visible remotely (deleted, access revoked, or moved out of scope). Keeping local copy and dropping it from tracking.", item.RelativePath);
                if (item.IsDirectory) plan.SubtreeDeletes.Add(item.RelativePath);
                plan.ItemDeletes.Add(item.Id);
                continue;
            }

            if (change.Type == RemoteChangeType.Trashed || change.File is null || change.File.IsTrashed)
            {
                if (item is null) continue;
                plan.Ops.Add(Op(profile, SyncOperationType.DeleteLocal, item.RelativePath, fileId));
                continue;
            }

            var file = change.File;
            var path = await ResolveRemotePathAsync(profile, file, remoteByFileId, pathCache, ct);
            if (path is null)
            {
                // Moved outside the synced folder (or under a trashed parent).
                if (item is not null) plan.Ops.Add(Op(profile, SyncOperationType.DeleteLocal, item.RelativePath, fileId));
                continue;
            }

            if (file.IsFolder)
            {
                if (_exclusions.IsExcluded(path)) continue;
                if (item is null)
                {
                    path = await DedupPathAsync(profile, path, fileId, plan, ct);
                    var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
                    if (File.Exists(abs))
                    {
                        await PlanTypeClashAsync(profile, path, new FileInfo(abs), plan, ct);
                    }
                    plan.Add(NewItem(profile, path, file, true, SyncItemStatus.RemoteNew));
                    plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, path, fileId));
                }
                else if (!string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    path = await DedupPathAsync(profile, path, fileId, plan, ct);
                    plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveLocal, item.RelativePath, path, fileId));
                }
                continue;
            }

            if (file.IsGoogleNative)
            {
                if (profile.NativeFilePolicy == NativeFilePolicy.Skip) continue;
                var stubPath = path + ".url";
                if (_exclusions.IsExcluded(stubPath)) continue;
                if (item is null)
                {
                    stubPath = await DedupPathAsync(profile, stubPath, fileId, plan, ct);
                    plan.Add(NewItem(profile, stubPath, file, false, SyncItemStatus.RemoteNew, isNative: true));
                    plan.Ops.Add(Op(profile, SyncOperationType.WriteNativeStub, stubPath, fileId));
                }
                else if (!string.Equals(item.RelativePath, stubPath, StringComparison.OrdinalIgnoreCase))
                {
                    stubPath = await DedupPathAsync(profile, stubPath, fileId, plan, ct);
                    plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveLocal, item.RelativePath, stubPath, fileId));
                }
                continue;
            }

            if (_exclusions.IsExcluded(path)) continue;

            if (item is null)
            {
                path = await DedupPathAsync(profile, path, fileId, plan, ct);
                var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
                if (Directory.Exists(abs))
                {
                    await PlanTypeClashAsync(profile, path, new DirectoryInfo(abs), plan, ct);
                }
                else if (File.Exists(abs))
                {
                    var localHash = await FileHasher.ComputeMd5Async(abs, ct);
                    var fi = new FileInfo(abs);
                    var newItem = NewItem(profile, path, file, false, SyncItemStatus.Synced);
                    SetLocalBaseline(newItem, fi, localHash);
                    if (!string.Equals(localHash, file.Md5Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        newItem.Status = SyncItemStatus.Conflict;
                        plan.Conflicts.Add(new PlannedConflict(newItem, localHash, file.Md5Hash, fi.Length, file.Size,
                            new DateTimeOffset(fi.LastWriteTimeUtc), file.ModifiedTime));
                    }
                    plan.Add(newItem); // identical content on both sides: adopt as baseline
                    continue;
                }
                plan.Add(NewItem(profile, path, file, false, SyncItemStatus.RemoteNew));
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, fileId, file.Md5Hash));
                continue;
            }

            var moved = !string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase);
            var contentChanged = !string.Equals(file.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase);

            if (moved)
            {
                path = await DedupPathAsync(profile, path, fileId, plan, ct);
                plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveLocal, item.RelativePath, path, fileId));
            }
            if (contentChanged)
            {
                item.Status = SyncItemStatus.RemoteModified;
                plan.Add(item);
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, fileId, file.Md5Hash));
            }
            // Neither moved nor changed: this is the echo of our own upload.
        }

        if (requeue.Count > 0) _monitor.Requeue(requeue);
        return plan;
    }

    private async Task PlanLocalFileAsync(SyncProfile profile, string path, SyncItem? item, CyclePlan plan,
        Dictionary<string, SyncItem> deletedByHash, List<SyncItem> pendingDeletes,
        Dictionary<string, RemoteChange> remoteByFileId, HashSet<string> consumedRemote, CancellationToken ct)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
        var fi = new FileInfo(abs);
        var hash = await FileHasher.ComputeMd5Async(abs, ct);

        if (item is null)
        {
            if (deletedByHash.Remove(hash, out var source) && pendingDeletes.Remove(source))
            {
                // Same content vanished elsewhere in this batch: a rename or move.
                plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveRemote, source.RelativePath, path, source.RemoteId));
                plan.MarkPath(path);
                plan.MovedSources.Add(source.Id);
                return;
            }
            var newItem = new SyncItem { ProfileId = profile.Id, RelativePath = path, Status = SyncItemStatus.LocalNew };
            SetLocalBaseline(newItem, fi, null);
            plan.Add(newItem);
            plan.Ops.Add(Op(profile, SyncOperationType.Upload, path));
            return;
        }

        if (string.Equals(hash, item.LastSyncedLocalHash, StringComparison.OrdinalIgnoreCase))
            return; // echo of our own download, or a touch with no content change

        if (item.RemoteId is not null && remoteByFileId.TryGetValue(item.RemoteId, out var rc)
            && rc.Type == RemoteChangeType.Modified && rc.File is { IsTrashed: false } rf
            && !string.Equals(rf.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rf.Md5Hash, hash, StringComparison.OrdinalIgnoreCase))
        {
            consumedRemote.Add(item.RemoteId);
            item.Status = SyncItemStatus.Conflict;
            plan.Add(item);
            plan.Conflicts.Add(new PlannedConflict(item, hash, rf.Md5Hash, fi.Length, rf.Size,
                new DateTimeOffset(fi.LastWriteTimeUtc), rf.ModifiedTime));
            return;
        }

        item.Status = SyncItemStatus.LocalModified;
        plan.Add(item);
        plan.Ops.Add(Op(profile, SyncOperationType.Upload, path, item.RemoteId));
    }

    private static bool RemoteContentChanged(SyncItem item, Dictionary<string, RemoteChange> batch)
        => item.RemoteId is not null && batch.TryGetValue(item.RemoteId, out var rc)
           && rc.Type == RemoteChangeType.Modified && rc.File is { IsTrashed: false, IsFolder: false }
           && !string.Equals(rc.File.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A new local directory is a rename of a deleted tracked directory when every tracked
    /// file under the old path exists under the new path with its baseline hash.
    /// </summary>
    private async Task<SyncItem?> FindRenamedDirectoryAsync(SyncProfile profile, string newPath, List<SyncItem> deletedDirs, CancellationToken ct)
    {
        foreach (var candidate in deletedDirs)
        {
            var children = (await _store.GetItemsUnderPathAsync(profile.Id, candidate.RelativePath, ct))
                .Where(c => !c.IsDirectory && c.LastSyncedLocalHash is not null).ToList();
            if (children.Count == 0) continue;

            var allMatch = true;
            foreach (var child in children)
            {
                var rel = newPath + child.RelativePath[candidate.RelativePath.Length..];
                var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, rel);
                if (!File.Exists(abs)) { allMatch = false; break; }
                var h = await FileHasher.ComputeMd5Async(abs, ct);
                if (!string.Equals(h, child.LastSyncedLocalHash, StringComparison.OrdinalIgnoreCase)) { allMatch = false; break; }
            }
            if (allMatch) return candidate;
        }
        return null;
    }

    private async Task EnqueueUntrackedChildrenAsync(SyncProfile profile, string dirPath, CyclePlan plan,
        Dictionary<string, SyncItem> deletedByHash, CancellationToken ct)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, dirPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(abs, "*", SearchOption.AllDirectories)
                     .OrderBy(e => e.Count(c => c == Path.DirectorySeparatorChar)))
        {
            var rel = PathNormalizer.ToRelative(profile.LocalRootPath, entry);
            if (_exclusions.IsExcluded(rel) || plan.HasPath(rel)) continue;
            if (await _store.GetItemByPathAsync(profile.Id, rel, ct) is not null) continue;

            if (Directory.Exists(entry))
            {
                plan.Add(new SyncItem { ProfileId = profile.Id, RelativePath = rel, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, rel));
                continue;
            }

            var hash = await FileHasher.ComputeMd5Async(entry, ct);
            if (deletedByHash.Remove(hash, out var source))
            {
                plan.Ops.Add(MoveOp(profile, SyncOperationType.MoveRemote, source.RelativePath, rel, source.RemoteId));
                plan.MarkPath(rel);
                plan.MovedSources.Add(source.Id);
                continue;
            }
            var item = new SyncItem { ProfileId = profile.Id, RelativePath = rel, Status = SyncItemStatus.LocalNew };
            SetLocalBaseline(item, new FileInfo(entry), null);
            plan.Add(item);
            plan.Ops.Add(Op(profile, SyncOperationType.Upload, rel));
        }
    }

    /// <summary>
    /// Returns a local path not already claimed by a different remote item. Drive allows
    /// duplicate and case-variant names in one folder; Windows does not.
    /// </summary>
    private async Task<string> DedupPathAsync(SyncProfile profile, string desired, string remoteId, CyclePlan plan, CancellationToken ct)
    {
        var candidate = desired;
        for (var n = 1; n < 1000; n++)
        {
            var existing = await _store.GetItemByPathAsync(profile.Id, candidate, ct);
            var planned = plan.ItemAt(candidate);
            var free = (existing is null || existing.RemoteId == remoteId) && (planned is null || planned.RemoteId == remoteId);
            if (free) return candidate;

            var dir = PathNormalizer.ParentOf(desired)!;
            var name = PathNormalizer.NameOf(desired);
            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            candidate = PathNormalizer.Combine(dir, $"{stem} ({n}){ext}");
        }
        throw new InvalidOperationException($"Could not find a free local name for '{desired}'.");
    }

    /// <summary>
    /// Builds the relative path of a remote file by walking its parent chain: tracked parent in
    /// state, parent in the same change batch, or a metadata fetch as a last resort.
    /// Returns null when the chain does not reach the profile's root folder or crosses a trashed folder.
    /// </summary>
    private async Task<string?> ResolveRemotePathAsync(SyncProfile profile, RemoteFileInfo file,
        Dictionary<string, RemoteChange> batch, Dictionary<string, string?> cache, CancellationToken ct)
    {
        if (file.ParentId is null) return null;
        var parentPath = await ResolveFolderPathAsync(profile, file.ParentId, batch, cache, 0, ct);
        return parentPath is null ? null : PathNormalizer.Combine(parentPath, file.Name);
    }

    private async Task<string?> ResolveFolderPathAsync(SyncProfile profile, string folderId,
        Dictionary<string, RemoteChange> batch, Dictionary<string, string?> cache, int depth, CancellationToken ct)
    {
        if (folderId == profile.RemoteFolderId) return string.Empty;
        if (depth > _options.MaxRemoteDepth) return null;
        if (cache.TryGetValue(folderId, out var cached)) return cached;

        string? result = null;
        RemoteFileInfo? info = null;

        if (batch.TryGetValue(folderId, out var change))
        {
            if (change.Type != RemoteChangeType.Modified || change.File is not { IsFolder: true, IsTrashed: false })
            {
                cache[folderId] = null; // trashed or removed parent: nothing under it is reachable
                return null;
            }
            info = change.File; // batch metadata is fresher than state (folder may have moved)
        }
        else if (await _store.GetItemByRemoteIdAsync(profile.Id, folderId, ct) is { } tracked)
        {
            result = tracked.RelativePath;
        }
        else if (_foreignFolderCache.TryGetValue(folderId, out var seenAt) && DateTimeOffset.UtcNow - seenAt < ForeignFolderTtl)
        {
            cache[folderId] = null;
            return null;
        }
        else
        {
            info = await _cloud.GetFileInfoAsync(folderId, ct);
        }

        if (result is null && info is { ParentId: not null, IsTrashed: false })
        {
            var parent = await ResolveFolderPathAsync(profile, info.ParentId, batch, cache, depth + 1, ct);
            result = parent is null ? null : PathNormalizer.Combine(parent, info.Name);
        }

        if (result is null && !batch.ContainsKey(folderId)) _foreignFolderCache[folderId] = DateTimeOffset.UtcNow;
        else _foreignFolderCache.Remove(folderId);

        cache[folderId] = result;
        return result;
    }

    private static string? ResolvePathFromMap(RemoteFileInfo file, Dictionary<string, RemoteFileInfo> all, string rootId)
    {
        var segments = new Stack<string>();
        segments.Push(file.Name);
        var parent = file.ParentId;
        var guard = 0;
        while (parent is not null && parent != rootId)
        {
            if (!all.TryGetValue(parent, out var p) || guard++ > 64) return null;
            segments.Push(p.Name);
            parent = p.ParentId;
        }
        return parent is null ? null : string.Join('/', segments);
    }

    // ------------------------------------------------------------------ execution

    private async Task ExecutePendingAsync(SyncProfile profile, CancellationToken ct)
    {
        var ops = await _store.GetDueOperationsAsync(profile.Id, DateTimeOffset.UtcNow, _options.OperationBatchSize, ct);
        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExecuteAsync(profile, op, ct);
                await _store.CompleteOperationAsync(op.Id, ct);
                RaiseStatus($"{op.Type}: {op.TargetRelativePath ?? op.RelativePath}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var attempts = op.Attempts + 1;
                if (attempts >= _options.MaxOperationAttempts)
                {
                    _logger.LogError(ex, "{Type} for '{Path}' failed {N} times; giving up.", op.Type, op.RelativePath, attempts);
                    await _store.CompleteOperationAsync(op.Id, ct);
                    var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
                    if (item is not null) { item.Status = SyncItemStatus.Error; await _store.UpsertItemAsync(item, ct); }
                    continue;
                }
                var delay = TimeSpan.FromTicks(Math.Min(_options.RetryMaxDelay.Ticks, _options.RetryBaseDelay.Ticks * (1L << Math.Min(attempts, 20))));
                _logger.LogWarning(ex, "{Type} for '{Path}' failed (attempt {N}); retry in {Delay}.", op.Type, op.RelativePath, attempts, delay);
                await _store.FailOperationAsync(op.Id, ex.Message, DateTimeOffset.UtcNow + delay, ct);
            }
        }
    }

    private Task ExecuteAsync(SyncProfile p, PendingOperation op, CancellationToken ct)
    {
        // A delete or move of "" would act on the whole synced folder; the root is never a valid target.
        if (string.IsNullOrEmpty(op.RelativePath) && op.Type is SyncOperationType.DeleteLocal or SyncOperationType.DeleteRemote
                or SyncOperationType.MoveLocal or SyncOperationType.MoveRemote)
            throw new InvalidOperationException($"{op.Type} has no path; refusing to act on the sync root.");
        return ExecuteCoreAsync(p, op, ct);
    }

    private Task ExecuteCoreAsync(SyncProfile p, PendingOperation op, CancellationToken ct) => op.Type switch
    {
        SyncOperationType.Upload => UploadAsync(p, op, ct),
        SyncOperationType.Download => DownloadAsync(p, op, ct),
        SyncOperationType.CreateRemoteFolder => EnsureRemoteFolderIdAsync(p, op.RelativePath, ct),
        SyncOperationType.CreateLocalFolder => CreateLocalFolderAsync(p, op, ct),
        SyncOperationType.DeleteLocal => DeleteLocalAsync(p, op, ct),
        SyncOperationType.DeleteRemote => DeleteRemoteAsync(p, op, ct),
        SyncOperationType.MoveLocal => MoveLocalAsync(p, op, ct),
        SyncOperationType.MoveRemote => MoveRemoteAsync(p, op, ct),
        SyncOperationType.WriteNativeStub => WriteNativeStubAsync(p, op, ct),
        _ => throw new InvalidOperationException($"Unknown operation {op.Type}")
    };

    private async Task UploadAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        if (!File.Exists(abs)) return; // vanished since planning; a Deleted event will follow

        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct)
                   ?? new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        if (item.IsGoogleNative) return;
        if (item.Status == SyncItemStatus.Conflict) return; // frozen; a later resolution will re-enqueue

        var hash = await FileHasher.ComputeMd5Async(abs, ct);
        var fi = new FileInfo(abs);

        // Pre-write check: never overwrite remote content the user has not seen.
        if (item.RemoteId is not null)
        {
            var live = await _cloud.GetFileInfoAsync(item.RemoteId, ct);
            if (live is null || live.IsTrashed)
            {
                item.RemoteId = null; // remote copy is gone; local edit wins and uploads as a new file
            }
            else
            {
                var expected = item.Status == SyncItemStatus.Pending && op.RemoteHash is not null ? op.RemoteHash : item.LastSyncedRemoteHash;
                if (!string.Equals(live.Md5Hash, expected, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(live.Md5Hash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = SyncItemStatus.Conflict;
                    SetLocalBaseline(item, fi, item.LastSyncedLocalHash); // keep baseline hash; refresh size/mtime only
                    await _store.UpsertItemAsync(item, ct);
                    await RaiseConflictAsync(profile, item, hash, live.Md5Hash, fi.Length, live.Size,
                        new DateTimeOffset(fi.LastWriteTimeUtc), live.ModifiedTime, null, ct);
                    _logger.LogWarning("Upload of '{Path}' skipped: remote changed since baseline; conflict raised.", op.RelativePath);
                    return;
                }
            }
        }

        var parentId = await EnsureRemoteFolderIdAsync(profile, PathNormalizer.ParentOf(op.RelativePath)!, ct);
        RemoteFileInfo result;
        await using (var stream = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
        {
            result = item.RemoteId is not null
                ? await _cloud.UpdateFileAsync(item.RemoteId, stream, ct: ct)
                : await _cloud.UploadFileAsync(parentId, PathNormalizer.NameOf(op.RelativePath), stream, ct: ct);
        }

        item.RemoteId = result.Id;
        item.RemoteParentId = result.ParentId ?? parentId;
        SetLocalBaseline(item, fi, hash);
        item.LastSyncedRemoteHash = result.Md5Hash ?? hash;
        item.LastSyncedRemoteSize = result.Size ?? fi.Length;
        item.LastSyncedRemoteModified = result.ModifiedTime;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        item.Status = SyncItemStatus.Synced;
        await _store.UpsertItemAsync(item, ct);
    }

    private async Task DownloadAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        if (op.RemoteId is null) return;
        var info = await _cloud.GetFileInfoAsync(op.RemoteId, ct);
        if (info is null || info.IsTrashed || info.IsFolder) return;

        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var tmp = abs + ExclusionMatcher.TempSuffix;

        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct)
                   ?? new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        if (item.Status == SyncItemStatus.Conflict) return; // frozen

        // Pre-write check: if the local file drifted from its baseline since this download was
        // planned (watcher event not yet drained), it is a conflict. Status Pending means the
        // engine itself intends this write (conflict resolution).
        if (item.Status != SyncItemStatus.Pending && File.Exists(abs))
        {
            var currentLocal = await FileHasher.ComputeMd5Async(abs, ct);
            var localDrifted = !string.Equals(currentLocal, item.LastSyncedLocalHash, StringComparison.OrdinalIgnoreCase);
            var alreadyHasRemote = string.Equals(currentLocal, info.Md5Hash, StringComparison.OrdinalIgnoreCase);
            if (localDrifted && !alreadyHasRemote)
            {
                var fiNow = new FileInfo(abs);
                item.Status = SyncItemStatus.Conflict;
                item.RemoteId ??= info.Id;
                await _store.UpsertItemAsync(item, ct);
                await RaiseConflictAsync(profile, item, currentLocal, info.Md5Hash, fiNow.Length, info.Size,
                    new DateTimeOffset(fiNow.LastWriteTimeUtc), info.ModifiedTime, null, ct);
                _logger.LogWarning("Download of '{Path}' skipped: local file changed since baseline; conflict raised.", op.RelativePath);
                return;
            }
        }

        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await _cloud.DownloadFileAsync(op.RemoteId, stream, ct);
                stream.Position = 0;
                var downloadedHash = await FileHasher.ComputeMd5Async(stream, ct);
                if (info.Md5Hash is not null && !string.Equals(downloadedHash, info.Md5Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Downloaded content hash {downloadedHash} does not match remote {info.Md5Hash}.");
            }
            File.Move(tmp, abs, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        if (info.ModifiedTime is { } mt) File.SetLastWriteTimeUtc(abs, mt.UtcDateTime);

        var fi = new FileInfo(abs);
        var hash = await FileHasher.ComputeMd5Async(abs, ct);
        item.RemoteId = info.Id;
        item.RemoteParentId = info.ParentId;
        SetLocalBaseline(item, fi, hash);
        item.LastSyncedRemoteHash = info.Md5Hash ?? hash;
        item.LastSyncedRemoteSize = info.Size;
        item.LastSyncedRemoteModified = info.ModifiedTime;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        item.Status = SyncItemStatus.Synced;
        await _store.UpsertItemAsync(item, ct);
    }

    private async Task CreateLocalFolderAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        if (File.Exists(abs)) MoveToTrash(profile, op.RelativePath, "a file blocked a folder of the same name");
        Directory.CreateDirectory(abs);
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct)
                   ?? new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        item.IsDirectory = true;
        item.RemoteId ??= op.RemoteId;
        item.Status = SyncItemStatus.Synced;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        await _store.UpsertItemAsync(item, ct);
    }

    private async Task<string> EnsureRemoteFolderIdAsync(SyncProfile profile, string relativeDir, CancellationToken ct)
    {
        if (relativeDir.Length == 0) return profile.RemoteFolderId;
        var item = await _store.GetItemByPathAsync(profile.Id, relativeDir, ct);
        if (item?.RemoteId is not null) return item.RemoteId;

        var parentId = await EnsureRemoteFolderIdAsync(profile, PathNormalizer.ParentOf(relativeDir)!, ct);
        var created = await _cloud.CreateFolderAsync(parentId, PathNormalizer.NameOf(relativeDir), ct);

        item ??= new SyncItem { ProfileId = profile.Id, RelativePath = relativeDir };
        item.IsDirectory = true;
        item.RemoteId = created.Id;
        item.RemoteParentId = parentId;
        item.Status = SyncItemStatus.Synced;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        await _store.UpsertItemAsync(item, ct);
        return created.Id;
    }

    private async Task DeleteLocalAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        MoveToTrash(profile, op.RelativePath, "removed remotely");
        await ForgetSubtreeAsync(profile, op.RelativePath, ct);
    }

    /// <summary>
    /// Drops the tracked rows for a path and everything under it. The store reads "" as the whole
    /// profile, which would erase every baseline and conflict, so the root is refused here.
    /// </summary>
    private Task<int> ForgetSubtreeAsync(SyncProfile profile, string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw new InvalidOperationException("Refusing to forget the tracked state of the whole sync root.");
        return _store.DeleteItemsUnderPathAsync(profile.Id, relativePath, ct);
    }

    /// <summary>Soft delete. Never removes user data; a unique destination avoids collisions within a cycle.</summary>
    private void MoveToTrash(SyncProfile profile, string relativePath, string reason)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, relativePath);
        if (!File.Exists(abs) && !Directory.Exists(abs)) return;

        var bucket = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var trashRoot = Path.Combine(profile.LocalRootPath, ExclusionMatcher.TrashDirName, bucket);
        var dest = PathNormalizer.ToAbsolute(trashRoot, relativePath);
        for (var n = 1; File.Exists(dest) || Directory.Exists(dest); n++)
            dest = PathNormalizer.ToAbsolute(Path.Combine(profile.LocalRootPath, ExclusionMatcher.TrashDirName, $"{bucket}-{n}"), relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(abs)) File.Move(abs, dest);
        else Directory.Move(abs, dest);
        _logger.LogInformation("Moved '{Path}' to {Trash} ({Reason})", relativePath, ExclusionMatcher.TrashDirName, reason);
    }

    private async Task DeleteRemoteAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
        var remoteId = item?.RemoteId ?? op.RemoteId;
        if (remoteId is not null)
        {
            var info = await _cloud.GetFileInfoAsync(remoteId, ct);
            if (info is { IsTrashed: false })
            {
                // Pre-write check: a remote edit since baseline wins over the local delete.
                if (item is not null && !info.IsFolder
                    && !string.Equals(info.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Trash of '{Path}' skipped: remote changed since baseline; restoring it locally.", op.RelativePath);
                    item.Status = SyncItemStatus.RemoteModified;
                    await _store.UpsertItemAsync(item, ct);
                    await DownloadAsync(profile, Op(profile, SyncOperationType.Download, op.RelativePath, remoteId, info.Md5Hash), ct);
                    return;
                }
                if (info.CanTrash == false)
                {
                    _logger.LogWarning("Cannot trash '{Path}' remotely (no permission); restoring it locally.", op.RelativePath);
                    if (info.IsFolder) await CreateLocalFolderAsync(profile, Op(profile, SyncOperationType.CreateLocalFolder, op.RelativePath, remoteId), ct);
                    else await DownloadAsync(profile, Op(profile, SyncOperationType.Download, op.RelativePath, remoteId, info.Md5Hash), ct);
                    return;
                }
                await _cloud.TrashAsync(remoteId, ct);
            }
        }
        await ForgetSubtreeAsync(profile, op.RelativePath, ct);
    }

    private async Task MoveLocalAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var target = op.TargetRelativePath ?? throw new InvalidOperationException("MoveLocal requires a target.");
        var from = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        var to = PathNormalizer.ToAbsolute(profile.LocalRootPath, target);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && from != to;

        if (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
        {
            var occupant = await _store.GetItemByPathAsync(profile.Id, target, ct);
            if (Directory.Exists(from) && Directory.Exists(to))
            {
                MergeDirectories(from, to); // a child download may have created the destination first
                await RenameTrackedPathsAsync(profile, op.RelativePath, target, ct);
                return;
            }
            if (occupant is null || occupant.RemoteId != op.RemoteId)
                MoveToTrash(profile, target, "displaced by a remote rename"); // never overwrite an untracked local item
        }

        if (Directory.Exists(from)) Directory.Move(from, to);
        else if (File.Exists(from)) File.Move(from, to, overwrite: caseOnly);
        else if (op.RemoteId is not null && !Directory.Exists(to) && !File.Exists(to))
        {
            // Source is gone; fall back to fetching the content at the new location.
            await RenameTrackedPathsAsync(profile, op.RelativePath, target, ct);
            await DownloadAsync(profile, Op(profile, SyncOperationType.Download, target, op.RemoteId), ct);
            return;
        }

        await RenameTrackedPathsAsync(profile, op.RelativePath, target, ct);
    }

    private static void MergeDirectories(string from, string to)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(dest)) MergeDirectories(entry, dest); else Directory.Move(entry, dest);
            }
            else File.Move(entry, dest, overwrite: true);
        }
        Directory.Delete(from, recursive: true);
    }

    private async Task MoveRemoteAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var target = op.TargetRelativePath ?? throw new InvalidOperationException("MoveRemote requires a target.");
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
        var remoteId = item?.RemoteId ?? op.RemoteId;
        if (remoteId is null) return;

        var live = await _cloud.GetFileInfoAsync(remoteId, ct);
        if (live is null || live.IsTrashed) { await ForgetSubtreeAsync(profile, op.RelativePath, ct); return; }

        var newParentId = await EnsureRemoteFolderIdAsync(profile, PathNormalizer.ParentOf(target)!, ct);
        var oldParentId = live.ParentId ?? item?.RemoteParentId;
        var newName = PathNormalizer.NameOf(target);
        var sameParent = oldParentId is null || oldParentId == newParentId;

        var result = await _cloud.MoveAsync(remoteId,
            newName == live.Name ? null : newName,
            sameParent ? null : oldParentId,
            sameParent ? null : newParentId, ct);

        await RenameTrackedPathsAsync(profile, op.RelativePath, target, ct);
        var moved = await _store.GetItemByPathAsync(profile.Id, target, ct);
        if (moved is not null)
        {
            moved.RemoteParentId = result.ParentId ?? newParentId;
            moved.LastSyncedRemoteModified = result.ModifiedTime;
            await _store.UpsertItemAsync(moved, ct);
        }
    }

    private async Task WriteNativeStubAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        if (op.RemoteId is null) return;
        var info = await _cloud.GetFileInfoAsync(op.RemoteId, ct);
        if (info is null || info.IsTrashed || info.WebViewLink is null) return;

        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
        if (item is null && (File.Exists(abs) || Directory.Exists(abs)))
            MoveToTrash(profile, op.RelativePath, "displaced by a link stub");

        var content = $"[InternetShortcut]\r\nURL={info.WebViewLink}\r\n";
        var tmp = abs + ExclusionMatcher.TempSuffix;
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, abs, overwrite: true);

        item ??= new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        item.IsGoogleNative = true;
        item.RemoteId = info.Id;
        item.RemoteParentId = info.ParentId;
        SetLocalBaseline(item, new FileInfo(abs), await FileHasher.ComputeMd5Async(abs, ct));
        item.LastSyncedRemoteModified = info.ModifiedTime;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        item.Status = SyncItemStatus.Synced;
        await _store.UpsertItemAsync(item, ct);
    }

    private Task RenameTrackedPathsAsync(SyncProfile profile, string from, string to, CancellationToken ct)
        => _store.RenameItemPathsAsync(profile.Id, from, to, ct); // in place: row ids and conflict references survive

    // ------------------------------------------------------------------ conflicts

    public Task ResolveConflictAsync(long conflictId, ConflictResolution resolution, CancellationToken ct = default)
        => RunLockedAsync(() => ResolveConflictCoreAsync(conflictId, resolution, ct), ct);

    private async Task ResolveConflictCoreAsync(long conflictId, ConflictResolution resolution, CancellationToken ct)
    {
        var profile = _profile ?? throw new InvalidOperationException("No active profile.");
        var conflict = await _store.GetConflictAsync(conflictId, ct) ?? throw new KeyNotFoundException($"Conflict {conflictId}");
        var item = await _store.GetItemByIdAsync(conflict.SyncItemId, ct)
                   ?? await _store.GetItemByPathAsync(profile.Id, conflict.RelativePath, ct)
                   ?? throw new InvalidOperationException($"No sync item for '{conflict.RelativePath}'.");

        var ops = new List<PendingOperation>();
        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                ops.Add(Op(profile, SyncOperationType.Upload, item.RelativePath, item.RemoteId, conflict.RemoteHash));
                break;

            case ConflictResolution.KeepRemote:
                ops.Add(Op(profile, SyncOperationType.Download, item.RelativePath, item.RemoteId, conflict.RemoteHash));
                break;

            case ConflictResolution.KeepBoth:
                var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, item.RelativePath);
                var copyPath = ConflictCopyName(item.RelativePath);
                var copyAbs = PathNormalizer.ToAbsolute(profile.LocalRootPath, copyPath);
                if (File.Exists(abs)) File.Move(abs, copyAbs, overwrite: false);
                ops.Add(Op(profile, SyncOperationType.Upload, copyPath));
                ops.Add(Op(profile, SyncOperationType.Download, item.RelativePath, item.RemoteId, conflict.RemoteHash));
                break;
        }

        ApplyDirectionFilter(profile, ops);
        item.Status = SyncItemStatus.Pending;
        await _store.UpsertItemAsync(item, ct);
        await _store.EnqueueOperationsAsync(ops, ct);
        await _store.ResolveConflictAsync(conflictId, resolution, ct);
        await ExecutePendingAsync(profile, ct);
        _logger.LogInformation("Conflict on '{Path}' resolved: {Resolution}", item.RelativePath, resolution);
    }

    private static string ConflictCopyName(string relativePath)
    {
        var dir = PathNormalizer.ParentOf(relativePath)!;
        var name = PathNormalizer.NameOf(relativePath);
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        return PathNormalizer.Combine(dir, $"{stem} (conflict {stamp}){ext}");
    }

    // ------------------------------------------------------------------ local scanning

    private Dictionary<string, FileSystemInfo> ScanLocal(SyncProfile profile)
    {
        var result = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        var root = profile.LocalRootPath;
        if (!Directory.Exists(root)) return result;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var rel = PathNormalizer.ToRelative(root, entry);
                if (_exclusions.IsExcluded(rel)) continue;
                if (Directory.Exists(entry)) { result[rel] = new DirectoryInfo(entry); stack.Push(entry); }
                else result[rel] = new FileInfo(entry);
            }
        }
        return result;
    }

    /// <summary>Full local diff against state, used after watcher overflow.</summary>
    private async Task<IReadOnlyList<DetectedChange>> DiffLocalTreeAsync(SyncProfile profile, CancellationToken ct)
    {
        var local = ScanLocal(profile);
        var tracked = await _store.GetAllItemsAsync(profile.Id, ct);
        var changes = new List<DetectedChange>();
        var trackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in tracked)
        {
            trackedPaths.Add(item.RelativePath);
            if (item.LastSyncedAt is null) continue; // never completed a sync; its absence on disk is expected
            if (!local.TryGetValue(item.RelativePath, out var info))
            {
                changes.Add(new DetectedChange { RelativePath = item.RelativePath, Type = ChangeType.Deleted });
                continue;
            }
            if (info is FileInfo fi && !item.IsDirectory &&
                (fi.Length != item.LastSyncedLocalSize || new DateTimeOffset(fi.LastWriteTimeUtc) != item.LastSyncedLocalModified))
                changes.Add(new DetectedChange { RelativePath = item.RelativePath, Type = ChangeType.Modified });
        }
        foreach (var path in local.Keys)
            if (!trackedPaths.Contains(path))
                changes.Add(new DetectedChange { RelativePath = path, Type = ChangeType.Created });

        return changes;
    }

    // ------------------------------------------------------------------ helpers

    private static SyncItem NewItem(SyncProfile p, string path, RemoteFileInfo r, bool isDir, SyncItemStatus status, bool isNative = false) => new()
    {
        ProfileId = p.Id, RelativePath = path, RemoteId = r.Id, RemoteParentId = r.ParentId,
        IsDirectory = isDir, IsGoogleNative = isNative,
        LastSyncedRemoteHash = r.Md5Hash, LastSyncedRemoteSize = r.Size, LastSyncedRemoteModified = r.ModifiedTime,
        LastSyncedAt = status == SyncItemStatus.Synced ? DateTimeOffset.UtcNow : null,
        Status = status
    };

    private static void SetLocalBaseline(SyncItem item, FileInfo fi, string? hash)
    {
        item.LastSyncedLocalHash = hash;
        item.LastSyncedLocalSize = fi.Length;
        item.LastSyncedLocalModified = new DateTimeOffset(fi.LastWriteTimeUtc);
    }

    private static PendingOperation Op(SyncProfile p, SyncOperationType type, string path, string? remoteId = null, string? remoteHash = null)
        => new() { ProfileId = p.Id, Type = type, RelativePath = path, RemoteId = remoteId, RemoteHash = remoteHash };

    private static PendingOperation MoveOp(SyncProfile p, SyncOperationType type, string from, string to, string? remoteId)
        => new() { ProfileId = p.Id, Type = type, RelativePath = from, TargetRelativePath = to, RemoteId = remoteId };

    private void SetState(SyncEngineState state)
    {
        State = state;
        RaiseStatus(state.ToString());
    }

    private void RaiseStatus(string message) => StatusChanged?.Invoke(this, new SyncStatusEventArgs(State, message));

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _monitor.Dispose();
        _cycleLock.Dispose();
    }

    private sealed class CyclePlan
    {
        private readonly Dictionary<string, SyncItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

        public List<SyncItem> Items { get; } = [];
        public List<PendingOperation> Ops { get; } = [];
        public List<PlannedConflict> Conflicts { get; } = [];
        public List<long> ItemDeletes { get; } = [];
        public List<string> SubtreeDeletes { get; } = [];
        public HashSet<long> MovedSources { get; } = [];

        public void Add(SyncItem item) { Items.Add(item); _byPath[item.RelativePath] = item; _claimed.Add(item.RelativePath); }
        public void MarkPath(string path) => _claimed.Add(path);
        public bool HasPath(string path) => _claimed.Contains(path);
        public SyncItem? ItemAt(string path) => _byPath.GetValueOrDefault(path);
    }

    private sealed record PlannedConflict(SyncItem Item, string? LocalHash, string? RemoteHash,
        long? LocalSize, long? RemoteSize, DateTimeOffset? LocalModified, DateTimeOffset? RemoteModified,
        ConflictResolution? AutoResolve = null);
}

public enum SyncEngineState { Stopped, InitialScan, Watching, Syncing, Error }

public sealed class SyncStatusEventArgs(SyncEngineState state, string message) : EventArgs
{
    public SyncEngineState State { get; } = state;
    public string Message { get; } = message;
}
