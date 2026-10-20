using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimbusSync.App.Services;

/// <summary>
/// App-level settings. Sync profiles live in the SQLite state store; this holds only
/// what the engine does not own. The OAuth client secret is DPAPI-protected at rest.
/// </summary>
public sealed class AppSettings
{
    public string? ClientId { get; set; }

    [JsonPropertyName("clientSecretProtected")]
    public string? ClientSecretProtected { get; set; }

    [JsonIgnore]
    public string? ClientSecret
    {
        get => ClientSecretProtected is null ? null : Dpapi.Unprotect(ClientSecretProtected);
        set => ClientSecretProtected = value is null ? null : Dpapi.Protect(value);
    }

    public bool StartMinimized { get; set; } = true;
    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

/// <summary>Windows DPAPI, current-user scope. Falls back to plaintext marker on non-Windows (tests only).</summary>
public static class Dpapi
{
    public static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) return "plain:" + plaintext;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string stored)
    {
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored[6..];
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI-protected value on a non-Windows host.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
