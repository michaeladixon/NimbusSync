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
///  - Planned operations and the new change token are committed in one transaction before
///    anything executes, so a crash or API failure never loses work and never skips a token.
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
        SetState(SyncEngineState.Stopped);
    }

    /// <summary>One full cycle (initial scan if needed). Used by tests and manual "Sync now".</summary>
    public async Task RunOnceAsync(SyncProfile profile, CancellationToken ct = default)
    {
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

        // Folders first so parents exist before children.
        foreach (var r in remoteById.Values.OrderByDescending(r => r.IsFolder))
        {
            var path = ResolvePathFromMap(r, remoteById, profile.RemoteFolderId);
            if (path is null || _exclusions.IsExcluded(path)) continue;

            if (r.IsFolder)
            {
                matchedLocal.Add(path);
                plan.Items.Add(NewItem(profile, path, r, isDir: true, SyncItemStatus.Synced));
                if (!local.ContainsKey(path))
                    plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, path, r.Id));
                continue;
            }

            if (r.IsGoogleNative)
            {
                if (profile.NativeFilePolicy == NativeFilePolicy.Skip) continue;
                var stubPath = path + ".url";
                matchedLocal.Add(stubPath);
                plan.Items.Add(NewItem(profile, stubPath, r, isDir: false, SyncItemStatus.Synced, isNative: true));
                plan.Ops.Add(Op(profile, SyncOperationType.WriteNativeStub, stubPath, r.Id));
                continue;
            }

            matchedLocal.Add(path);
            if (local.TryGetValue(path, out var localInfo) && localInfo is FileInfo fi)
            {
                var hash = await FileHasher.ComputeMd5Async(fi.FullName, ct);
                if (string.Equals(hash, r.Md5Hash, StringComparison.OrdinalIgnoreCase))
                {
                    var item = NewItem(profile, path, r, false, SyncItemStatus.Synced);
                    SetLocalBaseline(item, fi, hash);
                    plan.Items.Add(item);
                }
                else
                {
                    // Both sides have content and no baseline exists: the user decides.
                    var item = NewItem(profile, path, r, false, SyncItemStatus.Conflict);
                    SetLocalBaseline(item, fi, hash);
                    plan.Items.Add(item);
                    plan.Conflicts.Add(new PlannedConflict(item, hash, r.Md5Hash, fi.Length, r.Size,
                        new DateTimeOffset(fi.LastWriteTimeUtc), r.ModifiedTime));
                }
            }
            else
            {
                plan.Items.Add(NewItem(profile, path, r, false, SyncItemStatus.RemoteNew));
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, r.Id, r.Md5Hash));
            }
        }

        foreach (var (path, info) in local.OrderBy(kv => kv.Key.Count(c => c == '/')))
        {
            if (matchedLocal.Contains(path)) continue;
            if (info is DirectoryInfo)
            {
                plan.Items.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, path));
            }
            else
            {
                plan.Items.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.Upload, path));
            }
        }

        await CommitPlanAsync(profile, plan, token, ct);
        _logger.LogInformation("Initial scan: {Items} items, {Ops} operations, {Conflicts} conflicts",
            plan.Items.Count, plan.Ops.Count, plan.Conflicts.Count);

        await ExecutePendingAsync(profile, ct);
    }

    // ------------------------------------------------------------------ cycle

    private async Task RunCycleAsync(SyncProfile profile, CancellationToken ct)
    {
        SetState(SyncEngineState.Syncing);

        IReadOnlyList<DetectedChange> localChanges;
        if (_monitor.NeedsFullRescan)
        {
            _logger.LogWarning("File system watcher overflowed; performing full local rescan.");
            localChanges = await DiffLocalTreeAsync(profile, ct);
            _monitor.AcknowledgeRescan();
        }
        else
        {
            localChanges = _monitor.DrainChanges();
        }

        var token = profile.ChangeToken ?? throw new InvalidOperationException("No change token; initial scan required.");
        var (rawRemote, newToken) = await _cloud.GetChangesAsync(token, profile.SharedDriveId, ct);

        // The feed may report a file more than once; the last entry wins.
        var remoteByFileId = new Dictionary<string, RemoteChange>();
        foreach (var c in rawRemote) remoteByFileId[c.FileId] = c;

        if (localChanges.Count > 0 || remoteByFileId.Count > 0)
            _logger.LogInformation("Cycle: {L} local, {R} remote changes", localChanges.Count, remoteByFileId.Count);

        var plan = await PlanAsync(profile, localChanges, remoteByFileId, ct);
        await CommitPlanAsync(profile, plan, newToken, ct);
        await ExecutePendingAsync(profile, ct);

        SetState(SyncEngineState.Watching);
    }

    private async Task CommitPlanAsync(SyncProfile profile, CyclePlan plan, string? newToken, CancellationToken ct)
    {
        ApplyDirectionFilter(profile, plan);
        await _store.CommitCycleAsync(profile.Id, plan.Items, plan.Ops, newToken, ct);
        if (newToken is not null) profile.ChangeToken = newToken;

        foreach (var id in plan.ItemDeletes) await _store.DeleteItemAsync(id, ct);
        foreach (var dir in plan.SubtreeDeletes) await _store.DeleteItemsUnderPathAsync(profile.Id, dir, ct);

        foreach (var pc in plan.Conflicts)
        {
            var conflict = new SyncConflict
            {
                ProfileId = profile.Id, SyncItemId = pc.Item.Id, RelativePath = pc.Item.RelativePath,
                LocalHash = pc.LocalHash, RemoteHash = pc.RemoteHash, LocalSize = pc.LocalSize, RemoteSize = pc.RemoteSize,
                LocalModified = pc.LocalModified, RemoteModified = pc.RemoteModified
            };
            await _store.AddConflictAsync(conflict, ct);
            ConflictDetected?.Invoke(this, conflict);
        }
    }

    private static void ApplyDirectionFilter(SyncProfile profile, CyclePlan plan)
    {
        if (profile.Direction == SyncDirection.Bidirectional) return;
        plan.Ops.RemoveAll(op => profile.Direction switch
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

        // Pass 1: local deletions, indexed by baseline hash for rename detection.
        var deletedByHash = new Dictionary<string, SyncItem>(StringComparer.OrdinalIgnoreCase);
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
            if (!item.IsDirectory && item.LastSyncedLocalHash is not null)
                deletedByHash.TryAdd(item.LastSyncedLocalHash, item);
        }

        // Pass 2: local creates/modifies, verified against baseline.
        foreach (var change in creates)
        {
            var path = change.RelativePath;
            var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
            var item = await _store.GetItemByPathAsync(profile.Id, path, ct);

            if (item?.IsGoogleNative == true) continue;              // link stubs are never uploaded
            if (item?.Status == SyncItemStatus.Conflict) continue;   // frozen until the user resolves it

            if (Directory.Exists(abs))
            {
                if (item is not null) continue; // directory "modified" events are noise
                plan.Items.Add(new SyncItem { ProfileId = profile.Id, RelativePath = path, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, path));
                await EnqueueUntrackedChildrenAsync(profile, path, plan, ct);
                continue;
            }

            var fi = new FileInfo(abs);
            var hash = await FileHasher.ComputeMd5Async(abs, ct);

            if (item is null)
            {
                if (deletedByHash.Remove(hash, out var source) && pendingDeletes.Remove(source))
                {
                    // Same content vanished elsewhere in this batch: a rename or move.
                    plan.Ops.Add(new PendingOperation
                    {
                        ProfileId = profile.Id, Type = SyncOperationType.MoveRemote,
                        RelativePath = source.RelativePath, TargetRelativePath = path, RemoteId = source.RemoteId
                    });
                    continue;
                }
                var newItem = new SyncItem { ProfileId = profile.Id, RelativePath = path, Status = SyncItemStatus.LocalNew };
                SetLocalBaseline(newItem, fi, null);
                plan.Items.Add(newItem);
                plan.Ops.Add(Op(profile, SyncOperationType.Upload, path));
                continue;
            }

            if (string.Equals(hash, item.LastSyncedLocalHash, StringComparison.OrdinalIgnoreCase))
                continue; // echo of our own download, or a touch with no content change

            var remoteChanged = item.RemoteId is not null
                && remoteByFileId.TryGetValue(item.RemoteId, out var rc)
                && rc.Type == RemoteChangeType.Modified && rc.File is { IsTrashed: false }
                && !string.Equals(rc.File.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase);

            if (remoteChanged)
            {
                var rf = remoteByFileId[item.RemoteId!].File!;
                consumedRemote.Add(item.RemoteId!);
                item.Status = SyncItemStatus.Conflict;
                plan.Items.Add(item);
                plan.Conflicts.Add(new PlannedConflict(item, hash, rf.Md5Hash, fi.Length, rf.Size,
                    new DateTimeOffset(fi.LastWriteTimeUtc), rf.ModifiedTime));
                continue;
            }

            item.Status = SyncItemStatus.LocalModified;
            plan.Items.Add(item);
            plan.Ops.Add(Op(profile, SyncOperationType.Upload, path, item.RemoteId));
        }

        // Pass 3: remaining local deletions.
        foreach (var item in pendingDeletes)
        {
            if (item.RemoteId is not null && remoteByFileId.TryGetValue(item.RemoteId, out var rc)
                && rc.Type == RemoteChangeType.Modified && rc.File is { IsTrashed: false, IsFolder: false }
                && !string.Equals(rc.File.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase))
            {
                // Remote edit beats local delete: restore by downloading.
                consumedRemote.Add(item.RemoteId);
                plan.Ops.Add(Op(profile, SyncOperationType.Download, item.RelativePath, item.RemoteId, rc.File.Md5Hash));
                continue;
            }
            item.Status = SyncItemStatus.LocalDeleted;
            plan.Items.Add(item);
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
                // Moved outside the synced folder.
                if (item is not null) plan.Ops.Add(Op(profile, SyncOperationType.DeleteLocal, item.RelativePath, fileId));
                continue;
            }

            if (file.IsFolder)
            {
                if (_exclusions.IsExcluded(path)) continue;
                if (item is null)
                {
                    plan.Items.Add(NewItem(profile, path, file, true, SyncItemStatus.RemoteNew));
                    plan.Ops.Add(Op(profile, SyncOperationType.CreateLocalFolder, path, fileId));
                }
                else if (!string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Ops.Add(new PendingOperation { ProfileId = profile.Id, Type = SyncOperationType.MoveLocal,
                        RelativePath = item.RelativePath, TargetRelativePath = path, RemoteId = fileId });
                }
                continue;
            }

            if (file.IsGoogleNative)
            {
                if (profile.NativeFilePolicy == NativeFilePolicy.Skip) continue;
                var stubPath = path + ".url";
                if (_exclusions.IsExcluded(stubPath)) continue;
                if (item is not null && !string.Equals(item.RelativePath, stubPath, StringComparison.OrdinalIgnoreCase))
                    plan.Ops.Add(new PendingOperation { ProfileId = profile.Id, Type = SyncOperationType.MoveLocal,
                        RelativePath = item.RelativePath, TargetRelativePath = stubPath, RemoteId = fileId });
                if (item is null)
                {
                    plan.Items.Add(NewItem(profile, stubPath, file, false, SyncItemStatus.RemoteNew, isNative: true));
                    plan.Ops.Add(Op(profile, SyncOperationType.WriteNativeStub, stubPath, fileId));
                }
                continue;
            }

            if (_exclusions.IsExcluded(path)) continue;

            if (item is null)
            {
                var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, path);
                if (File.Exists(abs))
                {
                    var localHash = await FileHasher.ComputeMd5Async(abs, ct);
                    var fi = new FileInfo(abs);
                    var newItem = NewItem(profile, path, file, false, SyncItemStatus.Synced);
                    SetLocalBaseline(newItem, fi, localHash);
                    if (string.Equals(localHash, file.Md5Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Items.Add(newItem); // identical content on both sides: adopt as baseline
                    }
                    else
                    {
                        newItem.Status = SyncItemStatus.Conflict;
                        plan.Items.Add(newItem);
                        plan.Conflicts.Add(new PlannedConflict(newItem, localHash, file.Md5Hash, fi.Length, file.Size,
                            new DateTimeOffset(fi.LastWriteTimeUtc), file.ModifiedTime));
                    }
                    continue;
                }
                plan.Items.Add(NewItem(profile, path, file, false, SyncItemStatus.RemoteNew));
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, fileId, file.Md5Hash));
                continue;
            }

            var moved = !string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase);
            var contentChanged = !string.Equals(file.Md5Hash, item.LastSyncedRemoteHash, StringComparison.OrdinalIgnoreCase);

            if (moved)
                plan.Ops.Add(new PendingOperation { ProfileId = profile.Id, Type = SyncOperationType.MoveLocal,
                    RelativePath = item.RelativePath, TargetRelativePath = path, RemoteId = fileId });

            if (contentChanged)
            {
                item.Status = SyncItemStatus.RemoteModified;
                plan.Items.Add(item);
                plan.Ops.Add(Op(profile, SyncOperationType.Download, path, fileId, file.Md5Hash));
            }
            // Neither moved nor changed: this is the echo of our own upload.
        }

        return plan;
    }

    private async Task EnqueueUntrackedChildrenAsync(SyncProfile profile, string dirPath, CyclePlan plan, CancellationToken ct)
    {
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, dirPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(abs, "*", SearchOption.AllDirectories))
        {
            var rel = PathNormalizer.ToRelative(profile.LocalRootPath, entry);
            if (_exclusions.IsExcluded(rel)) continue;
            if (await _store.GetItemByPathAsync(profile.Id, rel, ct) is not null) continue;
            if (plan.Items.Any(i => string.Equals(i.RelativePath, rel, StringComparison.OrdinalIgnoreCase))) continue;

            if (Directory.Exists(entry))
            {
                plan.Items.Add(new SyncItem { ProfileId = profile.Id, RelativePath = rel, IsDirectory = true, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.CreateRemoteFolder, rel));
            }
            else
            {
                plan.Items.Add(new SyncItem { ProfileId = profile.Id, RelativePath = rel, Status = SyncItemStatus.LocalNew });
                plan.Ops.Add(Op(profile, SyncOperationType.Upload, rel));
            }
        }
    }

    /// <summary>
    /// Builds the relative path of a remote file by walking its parent chain: tracked parent in
    /// state, parent in the same change batch, or a metadata fetch as a last resort.
    /// Returns null when the chain does not reach the profile's root folder.
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
        var tracked = await _store.GetItemByRemoteIdAsync(profile.Id, folderId, ct);
        RemoteFileInfo? info = null;

        if (batch.TryGetValue(folderId, out var change) && change.File is { IsFolder: true, IsTrashed: false })
            info = change.File; // batch metadata is fresher than state (folder may have moved)
        else if (tracked is not null)
            result = tracked.RelativePath;
        else
            info = await _cloud.GetFileInfoAsync(folderId, ct);

        if (result is null && info is { ParentId: not null })
        {
            var parent = await ResolveFolderPathAsync(profile, info.ParentId, batch, cache, depth + 1, ct);
            result = parent is null ? null : PathNormalizer.Combine(parent, info.Name);
        }

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
        var now = DateTimeOffset.UtcNow;
        var ops = await _store.GetDueOperationsAsync(profile.Id, now, _options.OperationBatchSize, ct);
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

    private Task ExecuteAsync(SyncProfile p, PendingOperation op, CancellationToken ct) => op.Type switch
    {
        SyncOperationType.Upload => UploadAsync(p, op, ct),
        SyncOperationType.Download => DownloadAsync(p, op, ct),
        SyncOperationType.CreateRemoteFolder => EnsureRemoteFolderAsync(p, op.RelativePath, ct),
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
        var parentId = await EnsureRemoteFolderIdAsync(profile, PathNormalizer.ParentOf(op.RelativePath)!, ct);

        var hash = await FileHasher.ComputeMd5Async(abs, ct);
        var fi = new FileInfo(abs);
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

        // Last line of defense against clobbering: if the local file drifted from its baseline
        // since this download was planned (watcher event not yet drained), it is a conflict.
        // Status Pending means the engine itself intends this write (conflict resolution).
        if (item.Status != SyncItemStatus.Pending && File.Exists(abs))
        {
            var currentLocal = await FileHasher.ComputeMd5Async(abs, ct);
            var localDrifted = !string.Equals(currentLocal, item.LastSyncedLocalHash, StringComparison.OrdinalIgnoreCase);
            var alreadyHasRemote = string.Equals(currentLocal, info.Md5Hash, StringComparison.OrdinalIgnoreCase);
            if (localDrifted && !alreadyHasRemote)
            {
                var fiNow = new FileInfo(abs);
                item.Status = SyncItemStatus.Conflict;
                if (item.Id == 0) item.RemoteId = info.Id;
                await _store.UpsertItemAsync(item, ct);
                var conflict = new SyncConflict
                {
                    ProfileId = profile.Id, SyncItemId = item.Id, RelativePath = item.RelativePath,
                    LocalHash = currentLocal, RemoteHash = info.Md5Hash, LocalSize = fiNow.Length, RemoteSize = info.Size,
                    LocalModified = new DateTimeOffset(fiNow.LastWriteTimeUtc), RemoteModified = info.ModifiedTime
                };
                await _store.AddConflictAsync(conflict, ct);
                ConflictDetected?.Invoke(this, conflict);
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
        Directory.CreateDirectory(PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath));
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct)
                   ?? new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        item.IsDirectory = true;
        item.RemoteId = op.RemoteId;
        item.Status = SyncItemStatus.Synced;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        await _store.UpsertItemAsync(item, ct);
    }

    private Task EnsureRemoteFolderAsync(SyncProfile profile, string relativeDir, CancellationToken ct)
        => EnsureRemoteFolderIdAsync(profile, relativeDir, ct);

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
        var abs = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        if (File.Exists(abs) || Directory.Exists(abs))
        {
            var trashRoot = Path.Combine(profile.LocalRootPath, ExclusionMatcher.TrashDirName,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            var dest = PathNormalizer.ToAbsolute(trashRoot, op.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(abs)) File.Move(abs, dest, overwrite: true);
            else Directory.Move(abs, dest);
            _logger.LogInformation("Moved '{Path}' to {Trash}", op.RelativePath, ExclusionMatcher.TrashDirName);
        }
        await _store.DeleteItemsUnderPathAsync(profile.Id, op.RelativePath, ct);
    }

    private async Task DeleteRemoteAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
        var remoteId = item?.RemoteId ?? op.RemoteId;
        if (remoteId is not null)
        {
            var info = await _cloud.GetFileInfoAsync(remoteId, ct);
            if (info is { IsTrashed: false }) await _cloud.TrashAsync(remoteId, ct);
        }
        await _store.DeleteItemsUnderPathAsync(profile.Id, op.RelativePath, ct);
    }

    private async Task MoveLocalAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var target = op.TargetRelativePath ?? throw new InvalidOperationException("MoveLocal requires a target.");
        var from = PathNormalizer.ToAbsolute(profile.LocalRootPath, op.RelativePath);
        var to = PathNormalizer.ToAbsolute(profile.LocalRootPath, target);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(from)) Directory.Move(from, to);
        else if (File.Exists(from)) File.Move(from, to, overwrite: true);
        else if (op.RemoteId is not null && !Directory.Exists(to) && !File.Exists(to))
        {
            // Source is gone; fall back to fetching the content at the new location.
            await DownloadAsync(profile, new PendingOperation { ProfileId = profile.Id, Type = SyncOperationType.Download, RelativePath = target, RemoteId = op.RemoteId }, ct);
            return;
        }

        await RenameTrackedPathsAsync(profile, op.RelativePath, target, ct);
    }

    private async Task MoveRemoteAsync(SyncProfile profile, PendingOperation op, CancellationToken ct)
    {
        var target = op.TargetRelativePath ?? throw new InvalidOperationException("MoveRemote requires a target.");
        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct);
        var remoteId = item?.RemoteId ?? op.RemoteId;
        if (remoteId is null) return;

        var newParentId = await EnsureRemoteFolderIdAsync(profile, PathNormalizer.ParentOf(target)!, ct);
        var oldParentId = item?.RemoteParentId;
        var newName = PathNormalizer.NameOf(target);
        var sameParent = oldParentId is null || oldParentId == newParentId;

        var result = await _cloud.MoveAsync(remoteId,
            newName == PathNormalizer.NameOf(op.RelativePath) ? null : newName,
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
        var content = $"[InternetShortcut]\r\nURL={info.WebViewLink}\r\n";
        var tmp = abs + ExclusionMatcher.TempSuffix;
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, abs, overwrite: true);

        var item = await _store.GetItemByPathAsync(profile.Id, op.RelativePath, ct)
                   ?? new SyncItem { ProfileId = profile.Id, RelativePath = op.RelativePath };
        item.IsGoogleNative = true;
        item.RemoteId = info.Id;
        item.RemoteParentId = info.ParentId;
        SetLocalBaseline(item, new FileInfo(abs), await FileHasher.ComputeMd5Async(abs, ct));
        item.LastSyncedRemoteModified = info.ModifiedTime;
        item.LastSyncedAt = DateTimeOffset.UtcNow;
        item.Status = SyncItemStatus.Synced;
        await _store.UpsertItemAsync(item, ct);
    }

    private async Task RenameTrackedPathsAsync(SyncProfile profile, string from, string to, CancellationToken ct)
    {
        var item = await _store.GetItemByPathAsync(profile.Id, from, ct);
        if (item is null) return;

        if (item.IsDirectory)
        {
            foreach (var child in await _store.GetItemsUnderPathAsync(profile.Id, from, ct))
            {
                await _store.DeleteItemAsync(child.Id, ct);
                child.Id = 0;
                child.RelativePath = to + child.RelativePath[from.Length..];
                await _store.UpsertItemAsync(child, ct);
            }
        }
        await _store.DeleteItemAsync(item.Id, ct);
        item.Id = 0;
        item.RelativePath = to;
        await _store.UpsertItemAsync(item, ct);
    }

    // ------------------------------------------------------------------ conflicts

    public async Task ResolveConflictAsync(long conflictId, ConflictResolution resolution, CancellationToken ct = default)
    {
        var profile = _profile ?? throw new InvalidOperationException("No active profile.");
        var conflict = await _store.GetConflictAsync(conflictId, ct) ?? throw new KeyNotFoundException($"Conflict {conflictId}");
        var item = await _store.GetItemByPathAsync(profile.Id, conflict.RelativePath, ct)
                   ?? throw new InvalidOperationException($"No sync item for '{conflict.RelativePath}'.");

        var ops = new List<PendingOperation>();
        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                ops.Add(Op(profile, SyncOperationType.Upload, item.RelativePath, item.RemoteId));
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

        item.Status = SyncItemStatus.Pending;
        await _store.UpsertItemAsync(item, ct);
        await _store.EnqueueOperationsAsync(ops, ct);
        await _store.ResolveConflictAsync(conflictId, resolution, ct);
        await RunLockedAsync(() => ExecutePendingAsync(profile, ct), ct);
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
        public List<SyncItem> Items { get; } = [];
        public List<PendingOperation> Ops { get; } = [];
        public List<PlannedConflict> Conflicts { get; } = [];
        public List<long> ItemDeletes { get; } = [];
        public List<string> SubtreeDeletes { get; } = [];
    }

    private sealed record PlannedConflict(SyncItem Item, string? LocalHash, string? RemoteHash,
        long? LocalSize, long? RemoteSize, DateTimeOffset? LocalModified, DateTimeOffset? RemoteModified);
}

public enum SyncEngineState { Stopped, InitialScan, Watching, Syncing, Error }

public sealed class SyncStatusEventArgs(SyncEngineState state, string message) : EventArgs
{
    public SyncEngineState State { get; } = state;
    public string Message { get; } = message;
}
