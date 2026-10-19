namespace TidyWin.Core.Memory;

public sealed record SystemMemory(
    long TotalBytes,
    long AvailableBytes,
    long CacheBytes,
    long CommitUsedBytes,
    long CommitLimitBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public double AvailablePercent => TotalBytes <= 0 ? 0 : (double)AvailableBytes / TotalBytes * 100.0;
    public double CommitPercent => CommitLimitBytes <= 0 ? 0 : (double)CommitUsedBytes / CommitLimitBytes * 100.0;
}

/// <param name="CpuSeconds">Total processor time so far, or -1 when it could not be read.</param>
public sealed record ProcInfo(
    int Pid,
    string Name,
    string? ImagePath,
    int SessionId,
    long WorkingSetBytes,
    long PrivateBytes,
    double CpuSeconds,
    bool HasWindow,
    string? WindowTitle);

public sealed record MemorySnapshot(
    DateTime At,
    SystemMemory System,
    IReadOnlyList<ProcInfo> Processes,
    int? ForegroundPid);

public enum MemoryPressure { Calm, Moderate, High, Critical }

public interface IMemorySource
{
    MemorySnapshot Read();
}

public enum TrimOutcome { Trimmed, AccessDenied, Gone, Failed }

public interface IProcessControl
{
    /// <summary>Asks Windows to move the process's idle pages out of its working set. Nothing is lost; pages come back when used.</summary>
    TrimOutcome Trim(int pid);

    /// <summary>Asks the main window to close, the same as the user clicking X. Unsaved work gets its normal prompt. Never kills.</summary>
    bool RequestClose(int pid);
}
