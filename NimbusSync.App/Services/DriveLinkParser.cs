namespace NimbusSync.App.Services;

/// <summary>Parses a Google Drive folder or drive ID from a pasted URL or raw string.</summary>
public static class DriveLinkParser
{
    /// <summary>Extracts a Drive ID from a URL or raw string.</summary>
    public static bool TryGetId(string? input, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase))
        {
            string? candidate = null;

            // Check path segments for folders/ or /d/
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (seg == "folders" || seg == "d")
                {
                    candidate = segments[i + 1];
                    break;
                }
            }

            // Check query string for id parameter
            if (candidate is null && !string.IsNullOrEmpty(uri.Query))
            {
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = pair[..eq];
                        var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
                        if (key == "id")
                        {
                            candidate = val;
                            break;
                        }
                    }
                }
            }

            if (candidate is not null && IsValidId(candidate))
            {
                id = candidate;
                return true;
            }
            return false;
        }

        // Raw ID
        if (IsValidId(trimmed))
        {
            id = trimmed;
            return true;
        }
        return false;
    }

    private static bool IsValidId(string s)
    {
        if (s == "root") return true;
        if (s.Length < 10 || s.Length > 120) return false;
        foreach (var c in s)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }
}