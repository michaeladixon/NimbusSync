namespace NimbusSync.Engine.Models;

/// <summary>
/// Per-item sync state. One row per file or folder the engine tracks.
/// The LastSynced* fields are the baseline every change is verified against:
/// a detected change whose current hash equals the baseline is an echo of our
/// own write and is dropped, which is what prevents local/remote ping-pong.
/// </summary>
public sealed class SyncItem
{
    public long Id { get; set; }
    public long ProfileId { get; set; }

    /// <summary>Relative path from the sync root, forward slashes, no leading slash.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Cloud file ID. Null only transiently, before first upload completes.</summary>
    public string? RemoteId { get; set; }
    public string? RemoteParentId { get; set; }

    public bool IsDirectory { get; set; }

    /// <summary>
    /// True for Google-native files (Docs, Sheets, Slides) represented locally by a
    /// link stub. Local edits to stubs are never uploaded.
    /// </summary>
    public bool IsGoogleNative { get; set; }

    public string? LastSyncedLocalHash { get; set; }
    public string? LastSyncedRemoteHash { get; set; }
    public long? LastSyncedLocalSize { get; set; }
    public long? LastSyncedRemoteSize { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? LastSyncedLocalModified { get; set; }
    public DateTimeOffset? LastSyncedRemoteModified { get; set; }

    public SyncItemStatus Status { get; set; } = SyncItemStatus.Synced;
}

public enum SyncItemStatus
{
    Synced,
    LocalModified,
    RemoteModified,
    Conflict,
    LocalNew,
    RemoteNew,
    LocalDeleted,
    RemoteDeleted,
    Error,
    /// <summary>Item is being written by the engine; local change events for it are expected.</summary>
    Pending
}

public sealed class SyncConflict
{
    public long Id { get; set; }
    public long ProfileId { get; set; }
    public required long SyncItemId { get; set; }
    public required string RelativePath { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LocalHash { get; set; }
    public string? RemoteHash { get; set; }
    public long? LocalSize { get; set; }
    public long? RemoteSize { get; set; }
    public DateTimeOffset? LocalModified { get; set; }
    public DateTimeOffset? RemoteModified { get; set; }

    public ConflictResolution? Resolution { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public enum ConflictResolution
{
    KeepLocal,
    KeepRemote,
    KeepBoth
}

public sealed class SyncProfile
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string LocalRootPath { get; set; }
    public required string RemoteFolderId { get; set; }
    public string? SharedDriveId { get; set; }
    public SyncDirection Direction { get; set; } = SyncDirection.Bidirectional;
    public int PollIntervalSeconds { get; set; } = 60;
    public string? ChangeToken { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How to represent Google-native files (Docs/Sheets/Slides) locally.</summary>
    public NativeFilePolicy NativeFilePolicy { get; set; } = NativeFilePolicy.LinkStub;

    /// <summary>
    /// Glob-style patterns matched against each path segment and the full relative path.
    /// Stored newline-separated. Engine always adds its own internal patterns.
    /// </summary>
    public string ExcludePatterns { get; set; } = string.Join('\n', DefaultExcludes);

    public static readonly string[] DefaultExcludes =
    [
        "~$*", "*.tmp", "*.crdownload", "*.partial", "Thumbs.db", "desktop.ini", ".DS_Store"
    ];
}

public enum SyncDirection
{
    Bidirectional,
    LocalToRemote,
    RemoteToLocal
}

public enum NativeFilePolicy
{
    /// <summary>Ignore Google-native files entirely.</summary>
    Skip,
    /// <summary>Write a Windows .url shortcut that opens the file in the browser.</summary>
    LinkStub
}

/// <summary>A raw local file-system event, before verification against state.</summary>
public sealed class DetectedChange
{
    public required string RelativePath { get; set; }
    public required ChangeType Type { get; set; }
}

public enum ChangeType
{
    Created,
    Modified,
    Deleted
}

/// <summary>
/// Durable work queue row. Operations are persisted before the change token
/// advances, so a crash or transient API failure never loses work.
/// </summary>
public sealed class PendingOperation
{
    public long Id { get; set; }
    public long ProfileId { get; set; }
    public required SyncOperationType Type { get; set; }
    public required string RelativePath { get; set; }

    /// <summary>Destination path for move operations.</summary>
    public string? TargetRelativePath { get; set; }
    public string? RemoteId { get; set; }
    public string? RemoteHash { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SyncOperationType
{
    Upload,
    Download,
    CreateRemoteFolder,
    CreateLocalFolder,
    DeleteLocal,
    DeleteRemote,
    MoveLocal,
    MoveRemote,
    WriteNativeStub
}
