using TidyWin.Core.Clean;

namespace TidyWin.Platform;

public sealed class WindowsFileSystem : IFileSystem
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FsFile? GetFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            return new FsFile(info.FullName, isLink ? 0 : info.Length, info.LastWriteTimeUtc, info.Attributes.HasFlag(FileAttributes.ReadOnly), isLink);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        var folders = new List<string>();
        FastDirectory.ReadFolder(directory, null, folders); // links are left out, unreadable folders give nothing
        return folders;
    }

    public IEnumerable<FsFile> EnumerateFilesRecursive(string directory) => FastDirectory.Walk(directory);

    public IEnumerable<FsFile> EnumerateFilesRecursiveFast(string directory, CancellationToken ct = default) =>
        FastDirectory.WalkParallel(directory, Math.Min(Environment.ProcessorCount, 8), ct);

    public string? ReadText(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? LinkTarget(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public DeleteOutcome TryDeleteFile(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.Directory)) return DeleteOutcome.Failed;
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return DeleteOutcome.Deleted;
        }
        catch (FileNotFoundException) { return DeleteOutcome.Missing; }
        catch (DirectoryNotFoundException) { return DeleteOutcome.Missing; }
        catch (UnauthorizedAccessException) { return DeleteOutcome.AccessDenied; }
        catch (IOException e) when ((e.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            return DeleteOutcome.InUse;
        }
        catch (IOException) { return DeleteOutcome.Failed; }
    }

    public bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false); // fails on non-empty
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
