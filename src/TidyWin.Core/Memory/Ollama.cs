using System.Text.Json;

namespace TidyWin.Core.Memory;

/// <param name="InMemoryBytes">The part held in system RAM (total minus what sits in GPU memory).</param>
public sealed record LoadedModel(string Name, long SizeBytes, long GpuBytes, DateTime? ExpiresAt)
{
    public long InMemoryBytes => Math.Max(0, SizeBytes - GpuBytes);
}

public interface IOllama
{
    /// <summary>Null when Ollama is not running or not reachable; empty when running with nothing loaded.</summary>
    Task<IReadOnlyList<LoadedModel>?> ListLoadedAsync(CancellationToken ct = default);

    /// <summary>Tells Ollama to drop the model from memory right now. The model files stay on disk.</summary>
    Task<bool> UnloadAsync(string model, CancellationToken ct = default);
}

public static class OllamaPsParser
{
    /// <summary>Reads the body of GET /api/ps. Bad or unexpected JSON gives an empty list rather than an exception.</summary>
    public static IReadOnlyList<LoadedModel> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<LoadedModel>();
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString()
                         : m.TryGetProperty("model", out var n2) ? n2.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                var vram = m.TryGetProperty("size_vram", out var v) && v.TryGetInt64(out var vv) ? vv : 0;
                DateTime? expires = m.TryGetProperty("expires_at", out var e)
                    && e.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(e.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                        ? dt : null;

                list.Add(new LoadedModel(name!, size, vram, expires));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
