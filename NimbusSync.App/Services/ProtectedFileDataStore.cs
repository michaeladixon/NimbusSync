using System.IO;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace NimbusSync.App.Services;

/// <summary>
/// Drop-in replacement for Google's <see cref="FileDataStore"/> that DPAPI-protects
/// the stored OAuth token so the refresh token is never plaintext on disk.
/// </summary>
public sealed class ProtectedFileDataStore(string folderPath) : IDataStore
{
    private string PathFor(string key) => Path.Combine(folderPath, SanitizeKey(key) + ".bin");

    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(folderPath);
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        File.WriteAllText(PathFor(key), Dpapi.Protect(json));
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return Task.FromResult<T>(default!);
        try
        {
            var json = Dpapi.Unprotect(File.ReadAllText(path));
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }
        catch
        {
            return Task.FromResult<T>(default!); // corrupt or foreign-user token: force re-auth
        }
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(folderPath))
            foreach (var f in Directory.GetFiles(folderPath, "*.bin")) File.Delete(f);
        return Task.CompletedTask;
    }

    private static string SanitizeKey(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
        return key;
    }
}
