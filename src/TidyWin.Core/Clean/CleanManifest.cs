using System.Text;
using System.Text.Json;

namespace TidyWin.Core.Clean;

/// <summary>What a clean-up really did, kept so the user can look back or export it.</summary>
public sealed record ManifestEntry(DateTime AtUtc, string Source, int Deleted, long BytesFreed, IReadOnlyList<string> Paths, int MorePaths);

public interface IManifestSink
{
    void Record(ManifestEntry entry);
}

public static class CleanManifest
{
    /// <summary>A long list is cut; the count of what was left out is kept.</summary>
    public const int MaxPaths = 2000;

    public static ManifestEntry Create(string source, CleanReport report, IReadOnlyCollection<string> deleted, DateTime nowUtc) =>
        new(nowUtc, source, report.Deleted, report.BytesFreed,
            deleted.Take(MaxPaths).ToList(), Math.Max(0, deleted.Count - MaxPaths));

    public static string ToJson(ManifestEntry e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("at", e.AtUtc.ToString("O"));
            w.WriteString("source", e.Source);
            w.WriteNumber("deleted", e.Deleted);
            w.WriteNumber("bytesFreed", e.BytesFreed);
            w.WriteNumber("morePaths", e.MorePaths);
            w.WriteStartArray("paths");
            foreach (var p in e.Paths) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
