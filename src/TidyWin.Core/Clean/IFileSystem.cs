namespace TidyWin.Core.Clean;

/// <param name="IsLink">The file itself is a reparse point (symlink etc.). Its length is meaningless and never counted.</param>
public sealed record FsFile(string Path, long Length, DateTime LastWriteUtc, bool IsReadOnly, bool IsLink = false);

public enum DeleteOutcome { Deleted, Missing, InUse, AccessDenied, Failed }

/// <summary>
/// What the cleaner needs from the disk. Enumeration must never follow directory reparse points
/// (junctions, symlinks, cloud placeholders): those directories are neither entered nor reported.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>One file's facts, or null when there is no such file (a directory does not count).</summary>
    FsFile? GetFile(string path);

    /// <summary>Immediate child directories (full paths), reparse points excluded.</summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>All files below the directory. Unreadable folders are skipped silently.</summary>
    IEnumerable<FsFile> EnumerateFilesRecursive(string directory);

    /// <summary>The same files, but allowed to be read by several workers at once: files of one folder come together, folders in no set order. For whole-disk counting, where order does not matter.</summary>
    IEnumerable<FsFile> EnumerateFilesRecursiveFast(string directory, CancellationToken ct = default) => EnumerateFilesRecursive(directory);

    /// <summary>Reads a small text file. Null when it is missing, unreadable, or bigger than <paramref name="maxBytes"/>.</summary>
    string? ReadText(string path, long maxBytes);

    /// <summary>Where a symbolic link points, exactly as stored (may be relative). Null when the path is not a link.</summary>
    string? LinkTarget(string path);

    /// <summary>Deletes one file. When the path is a link, only the link is removed.</summary>
    DeleteOutcome TryDeleteFile(string path);

    /// <summary>Removes the directory only if it is empty. Returns true when it is gone.</summary>
    bool TryDeleteEmptyDirectory(string path);
}
