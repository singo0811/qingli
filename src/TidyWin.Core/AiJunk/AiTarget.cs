namespace TidyWin.Core.AiJunk;

/// <summary>What happens to the user if this is deleted.</summary>
public enum AiSafety
{
    /// <summary>Rebuilt automatically. Cost is only a slower first run. Selected by default.</summary>
    Regenerable,

    /// <summary>Comes back by downloading again. Can be large and needs network. Never selected by default.</summary>
    ReDownloadable,

    /// <summary>May hold things the user chose or made (installed models, chats). Only removed item by item, after an explicit pick.</summary>
    UserChoice,

    /// <summary>Never offered for deletion. Listed so the user can see we know about it and left it alone.</summary>
    Never,
}

public enum AiCategory
{
    PackageCache, ModelCache, ShaderAndKernelCache, EditorCache, RuntimeTemp, UserData,

    // Used by the system clean-up list.
    SystemTemp, BrowserCache, AppCache, DevCache, Diagnostics,
}

public enum KnownFolder { UserProfile, LocalAppData, AppData, Temp }

/// <summary>An environment variable that moves the location. The value is used as is, plus <see cref="Suffix"/> if any.</summary>
public readonly record struct AiOverride(string Variable, string? Suffix = null);

/// <summary>
/// One place on disk that AI tooling fills up. <see cref="RelativePath"/> may end with a single
/// wildcard segment such as "torchinductor_*"; wildcards are only allowed in the last segment.
/// </summary>
public sealed record AiTarget(
    string Id,
    string Tool,
    string Title,
    AiCategory Category,
    AiSafety Safety,
    KnownFolder Anchor,
    string RelativePath,
    IReadOnlyList<AiOverride> Overrides,
    string Consequence,
    int? MinAgeDays = null)
{
    public bool HasWildcard => RelativePath.Contains('*');
    public bool SelectedByDefault => Safety == AiSafety.Regenerable;
    public bool CanBeDeleted => Safety != AiSafety.Never;
}

public interface IAiEnvironment
{
    string? Variable(string name);
    string Folder(KnownFolder folder);
}

public sealed record ResolvedAiTarget(AiTarget Target, string Path);
