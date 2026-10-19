namespace TidyWin.Core.Clean;

public sealed record FileFacts(string Path, DateTimeOffset LastWrite, bool IsReadOnly, bool IsOpenElsewhere);

public enum FolderVerdict
{
    SafeToRemove,
    NoAgeRule,
    Empty,
    TooManyFiles,
    OverBudget,
    TooNew,
    ReadOnly,
    InUse,
}

/// <summary>
/// Decides whether a cache folder governed by an age rule can be removed as a whole.
/// It is all or nothing: one new, read-only or open file keeps the entire folder.
/// </summary>
public static class WholeFolderJudge
{
    public const int DefaultMaxFiles = 2000;

    public static FolderVerdict Judge(
        IEnumerable<FileFacts> files,
        DateTimeOffset now,
        int? minAgeDays,
        Func<bool> overBudget,
        int maxFiles = DefaultMaxFiles)
    {
        if (minAgeDays is not > 0) return FolderVerdict.NoAgeRule;

        var cutoff = now - TimeSpan.FromDays(minAgeDays.Value);
        var count = 0;

        foreach (var f in files)
        {
            if (overBudget()) return FolderVerdict.OverBudget;
            if (++count > maxFiles) return FolderVerdict.TooManyFiles;
            if (f.IsReadOnly) return FolderVerdict.ReadOnly;
            if (f.LastWrite > cutoff) return FolderVerdict.TooNew;
            if (f.IsOpenElsewhere) return FolderVerdict.InUse;
        }

        return count == 0 ? FolderVerdict.Empty : FolderVerdict.SafeToRemove;
    }
}
