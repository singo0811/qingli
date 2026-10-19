using TidyWin.Core.Clean;

namespace TidyWin.Core.Models;

public enum ModelSource { Ollama, LmStudio, HuggingFace }

/// <summary>
/// One file that belongs to a model. <paramref name="ShareKey"/> is set for files other models may also use
/// (an Ollama layer): such a file is only deleted when no model that stays still needs it.
/// </summary>
public sealed record ModelPart(FsFile File, string? ShareKey = null);

public sealed record ModelEntry(
    string Id,
    ModelSource Source,
    string Name,
    string Kind,
    DateTime UpdatedUtc,
    IReadOnlyList<ModelPart> Parts,
    string? Note = null)
{
    public long OwnBytes => Parts.Sum(p => p.File.IsLink ? 0 : p.File.Length);
}

public sealed record ModelPlan(IReadOnlyList<FsFile> Files, long Bytes);

public static class ModelPlanner
{
    /// <summary>
    /// What deleting <paramref name="selected"/> would remove. A shared file stays if any model outside the
    /// selection still uses it; a file two selected models share is listed, and counted, once.
    /// </summary>
    public static ModelPlan Plan(IEnumerable<ModelEntry> selected, IEnumerable<ModelEntry> all)
    {
        var chosen = selected.ToList();
        var chosenIds = chosen.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        var stillNeeded = all
            .Where(e => !chosenIds.Contains(e.Id))
            .SelectMany(e => e.Parts)
            .Select(p => p.ShareKey)
            .Where(k => k is not null)
            .ToHashSet(StringComparer.Ordinal);

        var files = chosen
            .SelectMany(e => e.Parts)
            .Where(p => p.ShareKey is null || !stillNeeded.Contains(p.ShareKey))
            .Select(p => p.File)
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModelPlan(files, files.Sum(f => f.IsLink ? 0 : f.Length));
    }

    /// <summary>The space this one model would free if it were the only one removed.</summary>
    public static long ExclusiveBytes(ModelEntry entry, IEnumerable<ModelEntry> all) =>
        Plan([entry], all).Bytes;
}
