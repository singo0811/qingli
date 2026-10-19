namespace TidyWin.Core.Paths;

public enum GuardVerdict
{
    Allowed,
    InvalidPath,
    OutsideAllowedRoots,
    ProtectedLocation,
    ReparsePoint,
}

public sealed record GuardResult(GuardVerdict Verdict, string? NormalizedPath)
{
    public bool IsAllowed => Verdict == GuardVerdict.Allowed;
}

/// <summary>The only thing the guard needs to ask the file system.</summary>
public interface IReparseProbe
{
    bool IsReparsePoint(string path);
}

/// <summary>
/// Every delete goes through this. A path is allowed only when it is strictly inside an
/// allowed root, not inside or equal to a protected location, and no directory between the
/// root and it is a reparse point (junction, symlink, cloud stub).
/// By default the path itself must not be one either. A caller deleting a single file may pass
/// <c>allowLeafLink</c>: removing a link never follows it, so the link itself is fine to delete.
/// </summary>
public sealed class PathGuard
{
    private readonly string[] _allowed;
    private readonly string[] _protected;
    private readonly HashSet<string> _allowedFiles;
    private readonly IReparseProbe _probe;

    /// <param name="allowedFiles">
    /// Single files that may be deleted by their exact path (for example a config file that sits directly in the user's
    /// profile). Nothing else in that file's folder becomes deletable because of it.
    /// </param>
    public PathGuard(IEnumerable<string> allowedRoots, IEnumerable<string> protectedRoots, IReparseProbe probe,
        IEnumerable<string>? allowedFiles = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

        _allowed = allowedRoots.Select(NormalizeRoot).ToArray();
        _protected = protectedRoots.Select(NormalizeRoot).ToArray();
        _allowedFiles = new HashSet<string>((allowedFiles ?? []).Select(NormalizeRoot), StringComparer.OrdinalIgnoreCase);

        if (_allowed.Any(WindowsPath.IsRoot))
            throw new ArgumentException("A drive or share root cannot be an allowed root.", nameof(allowedRoots));
        if (_allowedFiles.Any(WindowsPath.IsRoot))
            throw new ArgumentException("A drive or share root cannot be an allowed file.", nameof(allowedFiles));
    }

    public GuardResult Check(string? path, bool allowLeafLink = false)
    {
        var normalized = WindowsPath.Normalize(path);
        if (normalized is null) return new(GuardVerdict.InvalidPath, null);

        if (_protected.Any(p => WindowsPath.IsSameOrUnder(normalized, p)))
            return new(GuardVerdict.ProtectedLocation, normalized);

        if (_allowedFiles.Contains(normalized))
            return !allowLeafLink && _probe.IsReparsePoint(normalized)
                ? new(GuardVerdict.ReparsePoint, normalized)
                : new(GuardVerdict.Allowed, normalized);

        var root = _allowed.FirstOrDefault(r => WindowsPath.IsStrictlyUnder(normalized, r));
        if (root is null) return new(GuardVerdict.OutsideAllowedRoots, normalized);

        var chain = WindowsPath.ChainBelow(normalized, root);
        for (var i = 0; i < chain.Count; i++)
        {
            var isLeaf = i == chain.Count - 1;
            if (isLeaf && allowLeafLink) continue;
            if (_probe.IsReparsePoint(chain[i]))
                return new(GuardVerdict.ReparsePoint, normalized);
        }

        return new(GuardVerdict.Allowed, normalized);
    }

    private static string NormalizeRoot(string root) =>
        WindowsPath.Normalize(root) ?? throw new ArgumentException($"Invalid root: {root}");
}
