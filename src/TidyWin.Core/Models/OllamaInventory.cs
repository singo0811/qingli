using System.Text.Json;
using System.Text.RegularExpressions;
using TidyWin.Core.Clean;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Models;

/// <summary>
/// Reads Ollama's model folder: manifests\&lt;host&gt;\&lt;namespace&gt;\&lt;model&gt;\&lt;tag&gt; (JSON) and blobs\sha256-&lt;hex&gt;.
/// A blob name is only ever built from a digest that is exactly 64 lowercase hex digits, so a hostile manifest
/// cannot make us point at another path.
/// </summary>
public static partial class OllamaInventory
{
    private const long MaxManifestBytes = 256 * 1024;

    [GeneratedRegex("^sha256:([0-9a-f]{64})$")]
    private static partial Regex DigestPattern();

    public static IReadOnlyList<ModelEntry> Scan(string root, IFileSystem fs)
    {
        var manifestDir = root + @"\manifests";
        var blobDir = root + @"\blobs";
        if (!fs.DirectoryExists(manifestDir)) return [];

        var blobs = new Dictionary<string, FsFile>(StringComparer.OrdinalIgnoreCase);
        if (fs.DirectoryExists(blobDir))
            foreach (var f in fs.EnumerateFilesRecursive(blobDir)) blobs[LastSegment(f.Path)] = f;

        var entries = new List<ModelEntry>();
        foreach (var file in fs.EnumerateFilesRecursive(manifestDir))
        {
            if (file.IsLink) continue;
            var rel = file.Path[(manifestDir.Length + 1)..].Split('\\');
            if (rel.Length < 3) continue; // host\model\tag at the very least

            var text = fs.ReadText(file.Path, MaxManifestBytes);
            if (text is null) continue;

            var parts = new List<ModelPart> { new(file, null) };
            foreach (var hex in Digests(text))
            {
                if (blobs.TryGetValue("sha256-" + hex, out var blob))
                    parts.Add(new ModelPart(blob, "ollama:" + hex));
            }

            var name = DisplayName(rel);
            entries.Add(new ModelEntry("ollama:" + string.Join('/', rel), ModelSource.Ollama, name, Loc.T("Ollama 模型"),
                file.LastWriteUtc, parts));
        }
        return entries.OrderByDescending(e => e.OwnBytes).ToList();
    }

    /// <summary>"registry.ollama.ai\library\llama3\8b" → "llama3:8b"; other namespaces and hosts are kept visible.</summary>
    public static string DisplayName(string[] relative)
    {
        var host = relative[0];
        var tag = relative[^1];
        var model = relative[^2];
        var ns = string.Join('/', relative[1..^2]);

        var name = $"{model}:{tag}";
        if (ns.Length > 0 && !ns.Equals("library", StringComparison.OrdinalIgnoreCase)) name = $"{ns}/{name}";
        if (!host.Equals("registry.ollama.ai", StringComparison.OrdinalIgnoreCase)) name = $"{host}/{name}";
        return name;
    }

    private static IEnumerable<string> Digests(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<string>();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("config", out var cfg)) Collect(cfg, found);
                if (doc.RootElement.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                    foreach (var l in layers.EnumerateArray()) Collect(l, found);
            }

            foreach (var hex in found)
                if (seen.Add(hex)) yield return hex;
        }
    }

    private static void Collect(JsonElement e, List<string> into)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("digest", out var d) || d.ValueKind != JsonValueKind.String) return;
        var m = DigestPattern().Match(d.GetString() ?? "");
        if (m.Success) into.Add(m.Groups[1].Value);
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];
}
