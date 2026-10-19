namespace NimbusSync.Engine.Abstractions;

/// <summary>
/// Provider-agnostic cloud storage contract. Google Drive is the first
/// implementation; nothing in the engine references Drive types.
/// </summary>
public interface ICloudProvider
{
    Task<string> GetStartChangeTokenAsync(string? sharedDriveId = null, CancellationToken ct = default);

    /// <summary>
    /// Changes since <paramref name="changeToken"/>. A file may appear more than
    /// once; callers must treat the last occurrence as authoritative.
    /// </summary>
    Task<(IReadOnlyList<RemoteChange> Changes, string NewToken)> GetChangesAsync(
        string changeToken, string? sharedDriveId = null, CancellationToken ct = default);

    /// <summary>Every non-trashed file and folder under <paramref name="folderId"/>, any depth.</summary>
    Task<IReadOnlyList<RemoteFileInfo>> ListFolderRecursiveAsync(
        string folderId, string? sharedDriveId = null, CancellationToken ct = default);

    Task DownloadFileAsync(string remoteFileId, Stream destination, CancellationToken ct = default);

    /// <summary>Upload a new file; returns the created file's metadata.</summary>
    Task<RemoteFileInfo> UploadFileAsync(
        string parentFolderId, string fileName, Stream content,
        string? mimeType = null, CancellationToken ct = default);

    /// <summary>Replace an existing file's content; returns updated metadata.</summary>
    Task<RemoteFileInfo> UpdateFileAsync(
        string remoteFileId, Stream content, string? mimeType = null, CancellationToken ct = default);

    Task<RemoteFileInfo> CreateFolderAsync(string parentFolderId, string folderName, CancellationToken ct = default);

    /// <summary>Rename and/or reparent. Pass null to leave a field unchanged.</summary>
    Task<RemoteFileInfo> MoveAsync(
        string remoteId, string? newName, string? oldParentId, string? newParentId, CancellationToken ct = default);

    /// <summary>Move to trash. Never permanently deletes.</summary>
    Task TrashAsync(string remoteId, CancellationToken ct = default);

    /// <summary>Returns null when the file does not exist or is not visible.</summary>
    Task<RemoteFileInfo?> GetFileInfoAsync(string remoteId, CancellationToken ct = default);
}

public sealed class RemoteFileInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? ParentId { get; set; }
    public bool IsFolder { get; set; }

    /// <summary>Google Docs/Sheets/Slides etc. No binary content, no MD5, no size.</summary>
    public bool IsGoogleNative { get; set; }

    public string? Md5Hash { get; set; }
    public long? Size { get; set; }
    public DateTimeOffset? ModifiedTime { get; set; }
    public string? MimeType { get; set; }
    public bool IsTrashed { get; set; }

    /// <summary>Browser URL, used for native-file link stubs.</summary>
    public string? WebViewLink { get; set; }

    /// <summary>Whether the current user may trash this item. Null when the provider does not report it.</summary>
    public bool? CanTrash { get; set; }
}

public sealed class RemoteChange
{
    public required string FileId { get; set; }
    public required RemoteChangeType Type { get; set; }

    /// <summary>Metadata after the change. Null for Removed.</summary>
    public RemoteFileInfo? File { get; set; }
}

public enum RemoteChangeType
{
    /// <summary>Created, modified, renamed, moved, or untrashed. Inspect <see cref="RemoteChange.File"/>.</summary>
    Modified,

    /// <summary>Moved to trash. Recoverable.</summary>
    Trashed,

    /// <summary>
    /// No longer visible to this user: permanently deleted, access revoked, or moved out of
    /// scope. The API does not distinguish these, so the engine must not destroy local data.
    /// </summary>
    Removed
}
