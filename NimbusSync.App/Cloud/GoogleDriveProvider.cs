using System.IO;
using System.Net;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using NimbusSync.Engine.Abstractions;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace NimbusSync.App.Cloud;

/// <summary>
/// Google Drive API v3 implementation of <see cref="ICloudProvider"/>.
///
/// Shared Drive rules (verified against the v3 reference):
///  - every files.* call must set supportsAllDrives=true or Shared Drive items 404;
///  - files.list scoped to a Shared Drive needs corpora=drive, driveId, includeItemsFromAllDrives;
///  - changes.list takes driveId, supportsAllDrives, includeItemsFromAllDrives;
///  - changes.getStartPageToken takes only driveId and supportsAllDrives.
/// </summary>
public sealed class GoogleDriveProvider : ICloudProvider, IDisposable
{
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string ShortcutMime = "application/vnd.google-apps.shortcut";
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";
    private const string FileFields = "id,name,parents,mimeType,md5Checksum,size,modifiedTime,trashed,webViewLink";

    private readonly DriveService _service;

    public GoogleDriveProvider(DriveService service) => _service = service;

    /// <summary>Installed-app OAuth flow. The refresh token is persisted in <paramref name="tokenStorePath"/>.</summary>
    public static async Task<GoogleDriveProvider> CreateAsync(string clientId, string clientSecret, string tokenStorePath, CancellationToken ct = default)
    {
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            [DriveService.Scope.Drive],
            "user", ct, new FileDataStore(tokenStorePath, fullPath: true));

        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "NimbusSync"
        });
        return new GoogleDriveProvider(service);
    }

    public async Task<string> GetStartChangeTokenAsync(string? sharedDriveId = null, CancellationToken ct = default)
    {
        var request = _service.Changes.GetStartPageToken();
        request.SupportsAllDrives = true;
        if (sharedDriveId is not null) request.DriveId = sharedDriveId;
        var response = await request.ExecuteAsync(ct);
        return response.StartPageTokenValue;
    }

    public async Task<(IReadOnlyList<RemoteChange> Changes, string NewToken)> GetChangesAsync(
        string changeToken, string? sharedDriveId = null, CancellationToken ct = default)
    {
        var changes = new List<RemoteChange>();
        var pageToken = changeToken;

        while (true)
        {
            var request = _service.Changes.List(pageToken);
            request.Fields = $"nextPageToken,newStartPageToken,changes(fileId,removed,file({FileFields}))";
            request.Spaces = "drive";
            request.PageSize = 1000;
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
            request.IncludeRemoved = true;
            if (sharedDriveId is not null) request.DriveId = sharedDriveId;

            var response = await request.ExecuteAsync(ct);
            foreach (var c in response.Changes ?? [])
            {
                if (c.Removed == true)
                    changes.Add(new RemoteChange { FileId = c.FileId, Type = RemoteChangeType.Removed });
                else if (c.File is null || c.File.Trashed == true)
                    changes.Add(new RemoteChange { FileId = c.FileId, Type = RemoteChangeType.Trashed, File = c.File is null ? null : Map(c.File) });
                else
                    changes.Add(new RemoteChange { FileId = c.FileId, Type = RemoteChangeType.Modified, File = Map(c.File) });
            }

            if (response.NewStartPageToken is not null)
                return (changes, response.NewStartPageToken);
            if (response.NextPageToken is null)
                return (changes, pageToken); // defensive; the API always ends with newStartPageToken
            pageToken = response.NextPageToken;
        }
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListFolderRecursiveAsync(string folderId, string? sharedDriveId = null, CancellationToken ct = default)
    {
        var results = new List<RemoteFileInfo>();
        var queue = new Queue<string>();
        queue.Enqueue(folderId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            string? pageToken = null;
            do
            {
                var request = _service.Files.List();
                request.Q = $"'{parentId}' in parents and trashed = false";
                request.Fields = $"nextPageToken,files({FileFields})";
                request.PageSize = 1000;
                request.PageToken = pageToken;
                request.SupportsAllDrives = true;
                request.IncludeItemsFromAllDrives = true;
                if (sharedDriveId is not null)
                {
                    request.Corpora = "drive";
                    request.DriveId = sharedDriveId;
                }

                var response = await request.ExecuteAsync(ct);
                foreach (var f in response.Files ?? [])
                {
                    var info = Map(f);
                    results.Add(info);
                    if (info.IsFolder) queue.Enqueue(info.Id);
                }
                pageToken = response.NextPageToken;
            } while (pageToken is not null);
        }
        return results;
    }

    public async Task DownloadFileAsync(string remoteFileId, Stream destination, CancellationToken ct = default)
    {
        var request = _service.Files.Get(remoteFileId);
        request.SupportsAllDrives = true;
        var progress = await request.DownloadAsync(destination, ct);
        if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
            throw new IOException($"Download failed: {progress.Exception?.Message ?? progress.Status.ToString()}", progress.Exception);
    }

    public async Task<RemoteFileInfo> UploadFileAsync(string parentFolderId, string fileName, Stream content, string? mimeType = null, CancellationToken ct = default)
    {
        var metadata = new DriveFile { Name = fileName, Parents = [parentFolderId] };
        var request = _service.Files.Create(metadata, content, mimeType ?? "application/octet-stream");
        request.Fields = FileFields;
        request.SupportsAllDrives = true;
        var result = await request.UploadAsync(ct);
        if (result.Status != UploadStatus.Completed)
            throw new IOException($"Upload failed: {result.Exception?.Message ?? result.Status.ToString()}", result.Exception);
        return Map(request.ResponseBody);
    }

    public async Task<RemoteFileInfo> UpdateFileAsync(string remoteFileId, Stream content, string? mimeType = null, CancellationToken ct = default)
    {
        var request = _service.Files.Update(new DriveFile(), remoteFileId, content, mimeType ?? "application/octet-stream");
        request.Fields = FileFields;
        request.SupportsAllDrives = true;
        var result = await request.UploadAsync(ct);
        if (result.Status != UploadStatus.Completed)
            throw new IOException($"Update failed: {result.Exception?.Message ?? result.Status.ToString()}", result.Exception);
        return Map(request.ResponseBody);
    }

    public async Task<RemoteFileInfo> CreateFolderAsync(string parentFolderId, string folderName, CancellationToken ct = default)
    {
        var metadata = new DriveFile { Name = folderName, MimeType = FolderMime, Parents = [parentFolderId] };
        var request = _service.Files.Create(metadata);
        request.Fields = FileFields;
        request.SupportsAllDrives = true;
        return Map(await request.ExecuteAsync(ct));
    }

    public async Task<RemoteFileInfo> MoveAsync(string remoteId, string? newName, string? oldParentId, string? newParentId, CancellationToken ct = default)
    {
        var metadata = new DriveFile();
        if (newName is not null) metadata.Name = newName;
        var request = _service.Files.Update(metadata, remoteId);
        request.Fields = FileFields;
        request.SupportsAllDrives = true;
        if (newParentId is not null) request.AddParents = newParentId;
        if (oldParentId is not null) request.RemoveParents = oldParentId;
        return Map(await request.ExecuteAsync(ct));
    }

    public async Task TrashAsync(string remoteId, CancellationToken ct = default)
    {
        var request = _service.Files.Update(new DriveFile { Trashed = true }, remoteId);
        request.SupportsAllDrives = true;
        await request.ExecuteAsync(ct);
    }

    public async Task<RemoteFileInfo?> GetFileInfoAsync(string remoteId, CancellationToken ct = default)
    {
        try
        {
            var request = _service.Files.Get(remoteId);
            request.Fields = FileFields;
            request.SupportsAllDrives = true;
            return Map(await request.ExecuteAsync(ct));
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static RemoteFileInfo Map(DriveFile f)
    {
        var mime = f.MimeType ?? string.Empty;
        var isFolder = mime == FolderMime;
        return new RemoteFileInfo
        {
            Id = f.Id,
            Name = f.Name,
            ParentId = f.Parents?.FirstOrDefault(),
            IsFolder = isFolder,
            IsGoogleNative = !isFolder && mime.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal) && mime != ShortcutMime,
            Md5Hash = f.Md5Checksum,
            Size = f.Size,
            ModifiedTime = f.ModifiedTimeDateTimeOffset,
            MimeType = f.MimeType,
            IsTrashed = f.Trashed ?? false,
            WebViewLink = f.WebViewLink
        };
    }

    public void Dispose() => _service.Dispose();
}
