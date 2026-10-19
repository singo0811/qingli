using System.Globalization;

// Lives in Core so the command line can use it too; the namespace stays so every page and view-model keeps compiling unchanged.
namespace TidyWin.ViewModels;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>1024-based, one decimal from MB up, e.g. "12.4 GB". Negative numbers are treated as zero.</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        var text = u <= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
        return text + " " + Units[u];
    }

    /// <summary>Number and unit apart, so the UI can draw the number big and the unit small.</summary>
    public static (string Number, string Unit) Split(long bytes)
    {
        var s = Format(bytes);
        var i = s.IndexOf(' ');
        return (s[..i], s[(i + 1)..]);
    }
}
