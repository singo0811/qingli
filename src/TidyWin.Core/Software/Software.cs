using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Software;

/// <summary>One "Uninstall" registry key exactly as read, before any filtering.</summary>
public sealed record RawUninstall(
    string KeyName, string Hive, string? DisplayName, string? DisplayVersion, string? Publisher, string? InstallDate,
    long? EstimatedSizeKb, string? InstallLocation, string? UninstallString, string? QuietUninstallString,
    bool SystemComponent, string? ParentKeyName, string? ReleaseType);

public sealed record InstalledApp(
    string Id, string Name, string Version, string Publisher, DateTime? InstalledOn, long? SizeBytes,
    string? InstallLocation, string UninstallString, string Hive)
{
    public bool IsMsi => UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
}

public static class SoftwareCatalog
{
    private static readonly Regex UpdateName = new(@"^(Security Update|Update for|Hotfix|Service Pack)|\bKB\d{6,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What a person would call "my installed programs": no system components, no Windows updates, nothing without a
    /// name or an uninstaller, and one row for a program that registers itself in both the 32 and 64 bit places.
    /// </summary>
    public static IReadOnlyList<InstalledApp> Build(IEnumerable<RawUninstall> raw)
    {
        var list = new List<InstalledApp>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r.DisplayName) || string.IsNullOrWhiteSpace(r.UninstallString)) continue;
            if (r.SystemComponent || !string.IsNullOrWhiteSpace(r.ParentKeyName)) continue;
            if (r.ReleaseType is { } rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || r.ReleaseType == "Hotfix") continue;
            if (UpdateName.IsMatch(r.DisplayName)) continue;

            list.Add(new InstalledApp(
                r.Hive + "|" + r.KeyName,
                r.DisplayName.Trim(),
                r.DisplayVersion?.Trim() ?? "",
                r.Publisher?.Trim() ?? "",
                ParseDate(r.InstallDate),
                r.EstimatedSizeKb is > 0 ? r.EstimatedSizeKb * 1024 : null,
                string.IsNullOrWhiteSpace(r.InstallLocation) ? null : r.InstallLocation.Trim().Trim('"'),
                r.UninstallString.Trim(),
                r.Hive));
        }

        return list
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.OrderByDescending(a => a.SizeBytes ?? 0).ThenByDescending(a => a.InstallLocation is not null).First())
            .OrderByDescending(a => a.SizeBytes ?? 0)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DateTime? ParseDate(string? yyyymmdd) =>
        yyyymmdd is { Length: 8 } && DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>An installer entry for an .msi is "MsiExec /I{GUID}" (repair); removing needs "/X{GUID}".</summary>
    public static (string Exe, string Args) UninstallCommand(InstalledApp app, Func<string, bool>? fileExists = null)
    {
        var (exe, args) = CommandLine.Split(app.UninstallString, fileExists);
        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            args = Regex.Replace(args, @"/I(?=\s*\{)", "/X", RegexOptions.IgnoreCase);
        return (exe, args);
    }
}

/// <summary>Folders an uninstaller usually leaves behind.</summary>
public sealed record Residual(string Path, long Bytes, string Why);

public static class ResidualFinder
{
    // Folder names that are shared by many programs. Never offered, whatever the app was called.
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "commonfiles", "packages", "temp", "programs", "google", "adobe", "apple", "intel", "nvidia", "amd",
        "realtek", "oracle", "java", "dotnet", "windowsapps", "modifiablewindowsapps", "common", "cache", "data", "logs", "installer",
    };

    /// <summary>Lower-case letters and digits only, without versions, architectures and bracketed text.</summary>
    public static string Normalize(string name)
    {
        var s = Regex.Replace(name, "[\\(\\[（].*?[\\)\\]）]", " ");
        s = Regex.Replace(s, @"\b(x64|x86|64-?bit|32-?bit|v?\d+(\.\d+)+)\b", " ", RegexOptions.IgnoreCase);
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <param name="roots">Folders that hold per-program folders: Program Files, AppData, ProgramData.</param>
    public static IReadOnlyList<Residual> Find(
        InstalledApp gone, IReadOnlyList<InstalledApp> stillInstalled, IReadOnlyList<string> roots, IFileSystem fs)
    {
        var result = new List<Residual>();
        var name = Normalize(gone.Name);
        var normalizedRoots = roots.Select(WindowsPath.Normalize).Where(r => r is not null).Select(r => r!).ToList();

        bool InsideOtherApp(string dir) => stillInstalled.Any(o =>
            Normalize(o.Name) == Normalize(Path.GetFileName(dir)) ||
            (o.InstallLocation is { } l && WindowsPath.Normalize(l) is { } nl && WindowsPath.IsSameOrUnder(nl, dir)));

        // Where it was installed, if that still exists and is a proper folder inside one of the roots.
        if (gone.InstallLocation is { } loc && WindowsPath.Normalize(loc) is { } nloc
            && normalizedRoots.Any(r => WindowsPath.IsStrictlyUnder(nloc, r))
            && fs.DirectoryExists(nloc) && !InsideOtherApp(nloc))
            result.Add(new Residual(nloc, SizeOf(nloc, fs), Loc.T("它的安装目录还在")));

        if (name.Length < 4) return result;

        foreach (var root in normalizedRoots)
        {
            foreach (var dir in fs.EnumerateDirectories(root))
            {
                var folder = Normalize(Path.GetFileName(dir));
                if (folder.Length < 4 || Shared.Contains(folder)) continue;
                if (folder != name && !folder.StartsWith(name, StringComparison.Ordinal)) continue;
                if (result.Any(r => r.Path.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
                if (InsideOtherApp(dir)) continue;
                result.Add(new Residual(dir, SizeOf(dir, fs), Loc.T("文件夹名和它一致")));
            }
        }
        return result;
    }

    private static long SizeOf(string dir, IFileSystem fs) =>
        fs.EnumerateFilesRecursive(dir).Where(f => !f.IsLink).Sum(f => f.Length);
}

public sealed record WingetUpgrade(string Name, string Id, string Current, string Available, string Source);

/// <summary>
/// Reads the table that "winget upgrade" prints. The column headers are translated on a Chinese Windows, so columns are
/// found from the position of the header words, counting a Chinese character as two columns wide (as winget does).
/// </summary>
public static class WingetTableParser
{
    private static readonly Regex IdLike = new(@"^[A-Za-z0-9][A-Za-z0-9._\-+]*$", RegexOptions.Compiled);

    public static IReadOnlyList<WingetUpgrade> Parse(string? output)
    {
        var result = new List<WingetUpgrade>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Replace("\r", "").Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            if (!IsSeparator(lines[i])) continue;

            var starts = ColumnStarts(lines[i - 1]);
            if (starts.Count < 4) continue; // not a package table

            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line)) break;

                string Cell(int c) => Slice(line, starts[c], c + 1 < starts.Count ? starts[c + 1] : int.MaxValue).Trim();

                var id = Cell(1);
                var current = Cell(2);
                var available = Cell(3);
                if (!IdLike.IsMatch(id) || available.Length == 0 || !available.Any(char.IsDigit)) break;

                result.Add(new WingetUpgrade(Cell(0), id, current, available, starts.Count > 4 ? Cell(4) : ""));
            }
        }
        return result;
    }

    private static bool IsSeparator(string line)
    {
        var t = line.Trim();
        return t.Length >= 8 && t.All(c => c == '-');
    }

    private static int Width(char c) => c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3)
                                                          || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE6F)
                                                          || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6)) ? 2 : 1;

    /// <summary>Display column where each header word starts: after two or more spaces, or at the very start.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var col = 0;
        var spaces = 2;
        foreach (var c in header)
        {
            if (c == ' ') spaces++;
            else
            {
                if (spaces >= 2) starts.Add(col);
                spaces = 0;
            }
            col += Width(c);
        }
        return starts;
    }

    private static string Slice(string line, int from, int to)
    {
        var sb = new StringBuilder();
        var col = 0;
        foreach (var c in line)
        {
            if (col >= from && col < to) sb.Append(c);
            col += Width(c);
            if (col >= to) break;
        }
        return sb.ToString();
    }
}
