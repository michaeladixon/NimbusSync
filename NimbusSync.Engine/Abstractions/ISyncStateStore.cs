using NimbusSync.Engine.Models;

namespace NimbusSync.Engine.Abstractions;

/// <summary>
/// Persistence for sync baselines, conflicts, and the durable operation queue.
/// Implementations must be safe to call concurrently from multiple threads.
/// </summary>
public interface ISyncStateStore
{
    Task InitializeAsync(CancellationToken ct = default);

    // Profiles
    Task<SyncProfile?> GetProfileAsync(long profileId, CancellationToken ct = default);
    Task<IReadOnlyList<SyncProfile>> GetAllProfilesAsync(CancellationToken ct = default);
    Task<long> UpsertProfileAsync(SyncProfile profile, CancellationToken ct = default);
    Task DeleteProfileAsync(long profileId, CancellationToken ct = default);
    Task UpdateChangeTokenAsync(long profileId, string changeToken, CancellationToken ct = default);

    // Items
    Task<SyncItem?> GetItemByIdAsync(long itemId, CancellationToken ct = default);
    Task<SyncItem?> GetItemByPathAsync(long profileId, string relativePath, CancellationToken ct = default);
    Task<SyncItem?> GetItemByRemoteIdAsync(long profileId, string remoteId, CancellationToken ct = default);
    Task<IReadOnlyList<SyncItem>> GetItemsByStatusAsync(long profileId, SyncItemStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<SyncItem>> GetAllItemsAsync(long profileId, CancellationToken ct = default);
    Task<IReadOnlyList<SyncItem>> GetItemsUnderPathAsync(long profileId, string relativeDirPath, CancellationToken ct = default);
    Task<long> UpsertItemAsync(SyncItem item, CancellationToken ct = default);
    Task<int> BulkUpsertItemsAsync(IEnumerable<SyncItem> items, CancellationToken ct = default);
    Task DeleteItemAsync(long itemId, CancellationToken ct = default);
    Task<int> DeleteItemsUnderPathAsync(long profileId, string relativeDirPath, CancellationToken ct = default);

    /// <summary>Rename a path and its subtree in place, keeping row ids (and therefore conflict references).</summary>
    Task<int> RenameItemPathsAsync(long profileId, string fromPath, string toPath, CancellationToken ct = default);

    // Conflicts
    Task<IReadOnlyList<SyncConflict>> GetUnresolvedConflictsAsync(long profileId, CancellationToken ct = default);
    Task<SyncConflict?> GetConflictAsync(long conflictId, CancellationToken ct = default);
    Task<long> AddConflictAsync(SyncConflict conflict, CancellationToken ct = default);
    Task ResolveConflictAsync(long conflictId, ConflictResolution resolution, CancellationToken ct = default);

    // Pending operations (outbox)
    Task<int> EnqueueOperationsAsync(IEnumerable<PendingOperation> operations, CancellationToken ct = default);
    Task<IReadOnlyList<PendingOperation>> GetDueOperationsAsync(long profileId, DateTimeOffset now, int limit, CancellationToken ct = default);
    Task<int> CountPendingOperationsAsync(long profileId, CancellationToken ct = default);
    Task CompleteOperationAsync(long operationId, CancellationToken ct = default);
    Task FailOperationAsync(long operationId, string error, DateTimeOffset nextAttemptAt, CancellationToken ct = default);

    /// <summary>
    /// Atomically enqueue operations and advance the change token. Guarantees the
    /// token never moves past work that has not been durably recorded.
    /// </summary>
    Task CommitCycleAsync(long profileId, IEnumerable<SyncItem> itemUpserts, IEnumerable<PendingOperation> operations,
        string? newChangeToken, CancellationToken ct = default);
}
