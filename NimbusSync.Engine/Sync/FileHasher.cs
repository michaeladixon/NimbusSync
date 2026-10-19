using System.Security.Cryptography;

namespace NimbusSync.Engine.Sync;

/// <summary>
/// MD5 is used because Google Drive reports md5Checksum natively. It is a
/// content fingerprint for change detection, not a security primitive.
/// </summary>
public static class FileHasher
{
    public static async Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return await ComputeMd5Async(stream, ct);
    }

    public static async Task<string> ComputeMd5Async(Stream stream, CancellationToken ct = default)
    {
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, ct);
        if (stream.CanSeek) stream.Position = 0;
        return Convert.ToHexStringLower(hash);
    }
}
