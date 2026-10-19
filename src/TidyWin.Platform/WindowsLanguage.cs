using System.Runtime.InteropServices;

namespace TidyWin.Platform;

/// <summary>
/// The language Windows is displayed in. .NET's own culture cannot be used for this: the program runs with invariant
/// globalization (to save about 30 MB), where every culture reads as "invariant".
/// </summary>
public static class WindowsLanguage
{
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>Two-letter code of the display language ("zh", "en", "de"…), or null when it cannot be read.</summary>
    public static string? TwoLetterCode()
    {
        try
        {
            return PrimaryToCode(GetUserDefaultUILanguage());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The low ten bits of a Windows language id are the primary language; only Chinese matters here.</summary>
    public static string? PrimaryToCode(ushort langId) => (langId & 0x3FF) switch
    {
        0x04 => "zh",
        0x09 => "en",
        _ => "other",
    };
}
