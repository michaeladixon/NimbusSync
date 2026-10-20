using System.IO;

namespace NimbusSync.App.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NimbusSync");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StateDb => Path.Combine(DataDir, "state.db");
    public static string TokenStore => Path.Combine(DataDir, "tokens");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(TokenStore);
        Directory.CreateDirectory(LogDir);
    }
}
