using TidyWin.Core.Paths;

namespace TidyWin.Platform;

/// <summary>Real file system answer for PathGuard. Missing paths are not reparse points.</summary>
public sealed class FileSystemReparseProbe : IReparseProbe
{
    public bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
