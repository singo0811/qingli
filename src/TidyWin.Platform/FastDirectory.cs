using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;
using TidyWin.Core.Clean;

namespace TidyWin.Platform;

/// <summary>
/// Lists folders with one call per folder that returns names, sizes, dates and attributes together (a large directory buffer),
/// so no file is opened or asked about separately. Folder links are neither entered nor reported, exactly as before.
/// The parallel form splits the folder tree across a few workers; it needs no administrator rights.
/// </summary>
internal static class FastDirectory
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,       // a folder that cannot be read gives nothing, quietly
        AttributesToSkip = 0,            // hidden and system files count too; links are decided below
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,          // one call fetches many entries: fewer trips into the kernel
    };

    /// <summary>One folder's direct contents. Files go to <paramref name="files"/>, real subfolders (not links) to <paramref name="folders"/>. False when it could not be read.</summary>
    public static bool ReadFolder(string dir, List<FsFile>? files, List<string>? folders)
    {
        try
        {
            var entries = new FileSystemEnumerable<(string Path, bool IsDir, bool IsLink, long Length, DateTime Written, bool ReadOnly)>(
                dir.Length > 3 ? dir.TrimEnd('\\') : dir,
                static (ref FileSystemEntry e) =>
                {
                    var attrs = e.Attributes;
                    return (e.ToFullPath(), e.IsDirectory, (attrs & FileAttributes.ReparsePoint) != 0, e.IsDirectory ? 0 : e.Length,
                        e.LastWriteTimeUtc.UtcDateTime, (attrs & FileAttributes.ReadOnly) != 0);
                },
                Options);

            foreach (var e in entries)
            {
                if (e.IsDir)
                {
                    if (!e.IsLink) folders?.Add(e.Path); // a directory link is neither entered nor reported
                    continue;
                }
                files?.Add(new FsFile(e.Path, e.Length, e.Written, e.ReadOnly, e.IsLink));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>All files below the folder, one folder at a time.</summary>
    public static IEnumerable<FsFile> Walk(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var files = new List<FsFile>();
        var subs = new List<string>();

        while (pending.Count > 0)
        {
            files.Clear();
            subs.Clear();
            ReadFolder(pending.Pop(), files, subs);
            foreach (var s in subs) pending.Push(s);
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>
    /// All files below the folder, read by several workers at once. Files of one folder arrive together, folders in no
    /// particular order. Stops promptly when the caller stops reading or the token is cancelled.
    /// </summary>
    public static IEnumerable<FsFile> WalkParallel(string directory, int workers, CancellationToken ct)
    {
        workers = Math.Clamp(workers, 1, 16);
        var results = Channel.CreateBounded<List<FsFile>>(new BoundedChannelOptions(workers * 8)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var queue = new ConcurrentQueue<string>();
        queue.Enqueue(directory);
        var outstanding = 1;                                  // folders queued or being read; zero means the walk is over
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);   // not disposed: workers may still look at it as the caller leaves
        var wake = new SemaphoreSlim(0);

        void Worker()
        {
            var subs = new List<string>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!queue.TryDequeue(out var dir))
                    {
                        if (Volatile.Read(ref outstanding) == 0) break;
                        wake.Wait(2, stop.Token);              // short nap: another worker may be about to queue folders
                        continue;
                    }

                    var files = new List<FsFile>();
                    subs.Clear();
                    ReadFolder(dir, files, subs);

                    foreach (var s in subs) { Interlocked.Increment(ref outstanding); queue.Enqueue(s); }
                    if (subs.Count > 0) wake.Release(Math.Min(subs.Count, workers));

                    if (files.Count > 0) results.Writer.WriteAsync(files, stop.Token).AsTask().GetAwaiter().GetResult();
                    Interlocked.Decrement(ref outstanding);
                }
            }
            catch (OperationCanceledException) { }
        }

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++) tasks[i] = Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning);
        _ = Task.WhenAll(tasks).ContinueWith(_ => results.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            while (true)
            {
                List<FsFile>? batch;
                try
                {
                    if (!results.Reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) yield break;
                }
                catch (OperationCanceledException) { yield break; }

                while (results.Reader.TryRead(out batch))
                    foreach (var f in batch) yield return f;
            }
        }
        finally
        {
            stop.Cancel(); // the caller stopped reading (or finished): let the workers go
        }
    }
}
