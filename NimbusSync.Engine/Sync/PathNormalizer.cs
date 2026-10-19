namespace NimbusSync.Engine.Sync;

/// <summary>
/// Canonical relative paths: forward slashes, no leading or trailing slash.
/// </summary>
public static class PathNormalizer
{
    public static string ToRelative(string rootPath, string absolutePath)
    {
        var root = NormalizeSeparators(Path.GetFullPath(rootPath)).TrimEnd('/');
        var full = NormalizeSeparators(Path.GetFullPath(absolutePath));

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        // Compare against root + "/" so /sync does not match /sync2/file.
        if (!full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path '{absolutePath}' is not under root '{rootPath}'.");

        return full[(root.Length + 1)..];
    }

    public static string ToAbsolute(string rootPath, string relativePath)
    {
        var combined = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(combined);
    }

    public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    public static string Combine(string parentRelative, string name)
        => string.IsNullOrEmpty(parentRelative) ? name : parentRelative + "/" + name;

    public static string? ParentOf(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? string.Empty : relativePath[..idx];
    }

    public static string NameOf(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? relativePath : relativePath[(idx + 1)..];
    }

    public static bool IsUnder(string relativePath, string relativeDir)
        => relativePath.StartsWith(relativeDir + "/", StringComparison.OrdinalIgnoreCase);
}
