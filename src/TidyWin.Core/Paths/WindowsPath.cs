namespace TidyWin.Core.Paths;

/// <summary>Pure string handling for absolute Windows paths. No file system access.</summary>
public static class WindowsPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns a canonical absolute path, or null when the input is empty, relative,
    /// drive-relative, or climbs above its root with "..".
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var p = path.Trim().Replace('/', '\\');

        if (p.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[ExtendedUncPrefix.Length..];
        else if (p.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            p = p[ExtendedPrefix.Length..];

        string root;
        string rest;

        if (p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
        {
            root = char.ToUpperInvariant(p[0]) + @":\";
            rest = p[3..];
        }
        else if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            root = @"\\" + parts[0] + @"\" + parts[1] + @"\";
            rest = string.Join('\\', parts.Skip(2));
        }
        else
        {
            return null;
        }

        var stack = new List<string>();
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }

        return stack.Count == 0 ? root : root + string.Join('\\', stack);
    }

    public static bool IsRoot(string normalized) =>
        normalized.EndsWith('\\');

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> and not the root itself.</summary>
    public static bool IsStrictlyUnder(string? path, string? root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p is null || r is null) return false;
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return false;

        var prefix = r.EndsWith('\\') ? r : r + @"\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrUnder(string? path, string? root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p is null || r is null) return false;
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(p, r);
    }

    /// <summary>Every ancestor of <paramref name="path"/> below <paramref name="root"/>, shallowest first, plus the path itself.</summary>
    public static IReadOnlyList<string> ChainBelow(string path, string root)
    {
        var p = Normalize(path) ?? throw new ArgumentException("invalid path", nameof(path));
        var r = Normalize(root) ?? throw new ArgumentException("invalid root", nameof(root));
        if (!IsStrictlyUnder(p, r)) return Array.Empty<string>();

        var prefixLength = r.EndsWith('\\') ? r.Length : r.Length + 1;
        var segments = p[prefixLength..].Split('\\');
        var chain = new List<string>(segments.Length);
        var current = r.TrimEnd('\\');
        foreach (var s in segments)
        {
            current = current + @"\" + s;
            chain.Add(current);
        }
        return chain;
    }

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
