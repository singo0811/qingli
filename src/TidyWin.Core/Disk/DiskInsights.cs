using TidyWin.Core.Paths;

namespace TidyWin.Core.Disk;

/// <summary>Reads a finished scan and picks out what is worth telling the person about.</summary>
public static class DiskInsights
{
    // Folders that hold other things rather than being something themselves. Looked into, never named.
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "users", "appdata", "local", "roaming", "locallow", "program files", "program files (x86)", "programdata",
    };

    /// <summary>
    /// The biggest folder the person can act on: not Windows itself, not Program Files, and not the folder of their
    /// own profile or the places that only hold other folders. Null when nothing stands out.
    /// </summary>
    public static DiskNode? BiggestActionable(DiskScan scan, IEnumerable<string> systemRoots, string userProfile, long minBytes = 1L << 30)
    {
        var system = systemRoots.Select(WindowsPath.Normalize).Where(p => p is not null).Select(p => p!).ToList();
        var profile = WindowsPath.Normalize(userProfile);

        DiskNode? best = null;

        void Visit(DiskNode n, int depth)
        {
            foreach (var c in n.Folders)
            {
                if (system.Any(s => WindowsPath.IsSameOrUnder(c.Path, s))) continue;
                if (c.Name.StartsWith('$') || c.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)) continue;

                var isProfile = profile is not null && WindowsPath.IsSameOrUnder(profile, c.Path);
                if (isProfile || Containers.Contains(c.Name))
                {
                    if (depth < 4) Visit(c, depth + 1);
                    continue;
                }

                if (best is null || c.Bytes > best.Bytes) best = c;
            }
        }

        Visit(scan.Root, 0);
        return best is not null && best.Bytes >= minBytes ? best : null;
    }
}
