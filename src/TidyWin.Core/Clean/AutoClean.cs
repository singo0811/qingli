using TidyWin.Core.AiJunk;

namespace TidyWin.Core.Clean;

/// <summary>
/// What the resident clean-up is allowed to remove on its own: caches that rebuild themselves.
/// Downloads, models, chats and anything the person made are not in this list.
/// </summary>
public static class AutoClean
{
    public static IReadOnlyList<AiTarget> Regenerable(IEnumerable<AiTarget> targets) =>
        targets.Where(t => t.Safety == AiSafety.Regenerable).ToList();
}
