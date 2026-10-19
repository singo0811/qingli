using TidyWin.Core.Disk;
using TidyWin.Core.Software;
using TidyWin.Core.Startup;

// Everything the view models need from the outside world, as small interfaces. The real ones live in Platform;
// tests use fakes. Methods that change the computer return null on success and a message in plain words on failure.

namespace TidyWin.Core.Startup
{
    public interface IStartupSource
    {
        IReadOnlyList<StartupEntry> Read();
        string? SetEnabled(StartupEntry entry, bool enabled);

        /// <summary>Removes the entry itself (registry value or shortcut). Only offered for entries that point to nothing.</summary>
        string? Remove(StartupEntry entry);
    }
}

namespace TidyWin.Core.Software
{
    public interface ISoftwareSource
    {
        IReadOnlyList<RawUninstall> Read();
    }

    public interface IUninstaller
    {
        /// <summary>Starts the program's own uninstaller and waits for it. Null when it could not be started.</summary>
        Task<int?> RunAsync(string exe, string args, CancellationToken ct = default);
    }

    public interface IWinget
    {
        bool IsAvailable { get; }

        /// <summary>Null when winget could not be asked (not installed, no network, timed out).</summary>
        Task<IReadOnlyList<WingetUpgrade>?> ListUpgradesAsync(CancellationToken ct = default);

        Task<bool> UpgradeAsync(string id, CancellationToken ct = default);
    }
}

namespace TidyWin.Core.Disk
{
    public sealed record DriveEntry(string Name, string Label, long TotalBytes, long FreeBytes)
    {
        public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    }

    public interface IDrives
    {
        IReadOnlyList<DriveEntry> Read();
    }

    public interface IRecycler
    {
        /// <summary>Moves the file or folder to the Recycle Bin, so it can be restored. Null on success.</summary>
        string? Recycle(string path);
    }

    public interface IFileMerger
    {
        /// <summary>
        /// Checks that the two files are identical, byte for byte, then makes <paramref name="duplicate"/> a hard link to
        /// <paramref name="original"/>. Both paths keep working; the space of one copy comes back at once. Null on success.
        /// </summary>
        string? Merge(string duplicate, string original);
    }

    public sealed record KnownSpaceHit(KnownSpaceSpec Spec, string Path, long? Bytes);

    public interface IKnownSpaceReader
    {
        IReadOnlyList<KnownSpaceHit> Read();
    }
}
