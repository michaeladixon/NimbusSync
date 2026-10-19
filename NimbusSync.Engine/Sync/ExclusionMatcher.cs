using System.Text.RegularExpressions;

namespace NimbusSync.Engine.Sync;

/// <summary>
/// Simple glob matching (* and ?) applied to each path segment and to the whole
/// relative path. A pattern containing '/' matches the full path only.
/// </summary>
public sealed class ExclusionMatcher
{
    /// <summary>Patterns the engine always excludes: its own working files.</summary>
    public static readonly string[] EnginePatterns = [TrashDirName, "*" + TempSuffix];

    public const string TrashDirName = ".nimbus-trash";
    public const string TempSuffix = ".nimbus-tmp";

    private readonly List<(Regex regex, bool fullPathOnly)> _patterns = [];

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        foreach (var p in patterns.Concat(EnginePatterns))
        {
            var trimmed = p.Trim();
            if (trimmed.Length == 0) continue;
            var fullPathOnly = trimmed.Contains('/');
            _patterns.Add((GlobToRegex(trimmed), fullPathOnly));
        }
    }

    public bool IsExcluded(string relativePath)
    {
        if (relativePath.Length == 0) return false;
        var segments = relativePath.Split('/');

        foreach (var (regex, fullPathOnly) in _patterns)
        {
            if (regex.IsMatch(relativePath)) return true;
            if (fullPathOnly) continue;
            foreach (var seg in segments)
                if (regex.IsMatch(seg)) return true;
        }
        return false;
    }

    private static Regex GlobToRegex(string glob)
    {
        var escaped = Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
