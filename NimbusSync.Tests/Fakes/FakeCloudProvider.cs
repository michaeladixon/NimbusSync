using System.Security.Cryptography;
using NimbusSync.Engine.Abstractions;

namespace NimbusSync.Tests.Fakes;

/// <summary>
/// In-memory cloud with a Drive-like change feed. Every mutation appends a change
/// entry, exactly as the real API would, so tests can observe echo behavior.
/// </summary>
public sealed class FakeCloudProvider : ICloudProvider
{
    public const string RootId = "root";

    private readonly Dictionary<string, Entry> _files = new();
    private readonly List<(long seq, RemoteChange change)> _feed = [];
    private long _seq;
    private int _nextId;

    public Func<string, Exception?>? FailDownload { get; set; }
    public Func<string, Exception?>? FailUpload { get; set; }

    public int DownloadCount { get; private set; }
    public int UploadCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int MetadataFetchCount { get; private set; }

    public IReadOnlyDictionary<string, Entry> Files => _files;

    public sealed class Entry
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public bool IsFolder { get; init; }
        public bool IsNative { get; init; }
        public byte[] Content { get; set; } = [];
        public bool Trashed { get; set; }
        public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
        public string Md5 => Convert.ToHexStringLower(MD5.HashData(Content));
    }

    // ---- test helpers ----

    public Entry AddFile(string name, string content, string parentId = RootId)
        => Add(new Entry { Id = NewId(), Name = name, ParentId = parentId, Content = System.Text.Encoding.UTF8.GetBytes(content) });

    public Entry AddFolder(string name, string parentId = RootId)
        => Add(new Entry { Id = NewId(), Name = name, ParentId = parentId, IsFolder = true });

    public Entry AddNativeDoc(string name, string parentId = RootId)
        => Add(new Entry { Id = NewId(), Name = name, ParentId = parentId, IsNative = true });

    public void SetContent(string id, string content)
    {
        var e = _files[id];
        e.Content = System.Text.Encoding.UTF8.GetBytes(content);
        e.Modified = DateTimeOffset.UtcNow;
        Record(e);
    }

    public void Trash(string id) { _files[id].Trashed = true; Record(_files[id]); }

    public void Remove(string id)
    {
        _files.Remove(id);
        _feed.Add((++_seq, new RemoteChange { FileId = id, Type = RemoteChangeType.Removed }));
    }

    public void Rename(string id, string newName) { _files[id].Name = newName; Record(_files[id]); }
    public void Reparent(string id, string newParent) { _files[id].ParentId = newParent; Record(_files[id]); }

    public Entry? FindByPath(string relativePath)
    {
        var parent = RootId;
        Entry? current = null;
        foreach (var seg in relativePath.Split('/'))
        {
            current = _files.Values.FirstOrDefault(f => f.ParentId == parent && f.Name == seg && !f.Trashed);
            if (current is null) return null;
            parent = current.Id;
        }
        return current;
    }

    public string ContentOf(string relativePath)
        => System.Text.Encoding.UTF8.GetString(FindByPath(relativePath)!.Content);

    private Entry Add(Entry e) { _files[e.Id] = e; Record(e); return e; }
    private string NewId() => $"f{++_nextId:D4}";

    private void Record(Entry e)
    {
        var type = e.Trashed ? RemoteChangeType.Trashed : RemoteChangeType.Modified;
        _feed.Add((++_seq, new RemoteChange { FileId = e.Id, Type = type, File = Map(e) }));
    }

    private static RemoteFileInfo Map(Entry e) => new()
    {
        Id = e.Id, Name = e.Name, ParentId = e.ParentId, IsFolder = e.IsFolder, IsGoogleNative = e.IsNative,
        Md5Hash = e.IsFolder || e.IsNative ? null : e.Md5,
        Size = e.IsFolder || e.IsNative ? null : e.Content.Length,
        ModifiedTime = e.Modified, IsTrashed = e.Trashed,
        MimeType = e.IsFolder ? "application/vnd.google-apps.folder" : e.IsNative ? "application/vnd.google-apps.document" : "application/octet-stream",
        WebViewLink = e.IsNative ? $"https://docs.google.com/document/d/{e.Id}" : null,
        CanTrash = true
    };

    // ---- ICloudProvider ----

    public Task<string> GetStartChangeTokenAsync(string? sharedDriveId = null, CancellationToken ct = default)
        => Task.FromResult((_seq + 1).ToString());

    public Task<(IReadOnlyList<RemoteChange> Changes, string NewToken)> GetChangesAsync(string changeToken, string? sharedDriveId = null, CancellationToken ct = default)
    {
        var from = long.Parse(changeToken);
        var changes = _feed.Where(f => f.seq >= from).Select(f => f.change).ToList();
        return Task.FromResult<(IReadOnlyList<RemoteChange>, string)>((changes, (_seq + 1).ToString()));
    }

    public Task<IReadOnlyList<RemoteFileInfo>> ListFolderRecursiveAsync(string folderId, string? sharedDriveId = null, CancellationToken ct = default)
    {
        var result = new List<RemoteFileInfo>();
        var queue = new Queue<string>();
        queue.Enqueue(folderId);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var e in _files.Values.Where(f => f.ParentId == p && !f.Trashed))
            {
                result.Add(Map(e));
                if (e.IsFolder) queue.Enqueue(e.Id);
            }
        }
        return Task.FromResult<IReadOnlyList<RemoteFileInfo>>(result);
    }

    public async Task DownloadFileAsync(string remoteFileId, Stream destination, CancellationToken ct = default)
    {
        DownloadCount++;
        if (FailDownload?.Invoke(remoteFileId) is { } ex) throw ex;
        await destination.WriteAsync(_files[remoteFileId].Content, ct);
    }

    public async Task<RemoteFileInfo> UploadFileAsync(string parentFolderId, string fileName, Stream content, string? mimeType = null, CancellationToken ct = default)
    {
        UploadCount++;
        if (FailUpload?.Invoke(fileName) is { } ex) throw ex;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var e = Add(new Entry { Id = NewId(), Name = fileName, ParentId = parentFolderId, Content = ms.ToArray() });
        return Map(e);
    }

    public async Task<RemoteFileInfo> UpdateFileAsync(string remoteFileId, Stream content, string? mimeType = null, CancellationToken ct = default)
    {
        UpdateCount++;
        if (FailUpload?.Invoke(remoteFileId) is { } ex) throw ex;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var e = _files[remoteFileId];
        e.Content = ms.ToArray();
        e.Modified = DateTimeOffset.UtcNow;
        Record(e);
        return Map(e);
    }

    public Task<RemoteFileInfo> CreateFolderAsync(string parentFolderId, string folderName, CancellationToken ct = default)
        => Task.FromResult(Map(AddFolder(folderName, parentFolderId)));

    public Task<RemoteFileInfo> MoveAsync(string remoteId, string? newName, string? oldParentId, string? newParentId, CancellationToken ct = default)
    {
        var e = _files[remoteId];
        if (newName is not null) e.Name = newName;
        if (newParentId is not null) e.ParentId = newParentId;
        e.Modified = DateTimeOffset.UtcNow;
        Record(e);
        return Task.FromResult(Map(e));
    }

    public Task TrashAsync(string remoteId, CancellationToken ct = default) { Trash(remoteId); return Task.CompletedTask; }

    public Task<RemoteFileInfo?> GetFileInfoAsync(string remoteId, CancellationToken ct = default)
    {
        MetadataFetchCount++;
        return Task.FromResult(_files.TryGetValue(remoteId, out var e) ? Map(e) : null);
    }
}
