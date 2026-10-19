namespace TidyWin.Core.Paths;

/// <summary>
/// Remembers answers for one cleaning run so a hundred thousand files in the same folder don't
/// cause a hundred thousand attribute reads for the same ancestors. Make a new one per run.
/// </summary>
public sealed class CachingReparseProbe(IReparseProbe inner) : IReparseProbe
{
    private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReparsePoint(string path)
    {
        if (_cache.TryGetValue(path, out var known)) return known;
        var answer = inner.IsReparsePoint(path);
        _cache[path] = answer;
        return answer;
    }
}
