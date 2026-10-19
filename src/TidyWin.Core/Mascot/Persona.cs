using TidyWin.Core.Localization;
namespace TidyWin.Core.Mascot;

/// <summary>The AI tools the mascot knows how to dress up for.</summary>
public enum AiTool { Ollama, LmStudio, ComfyUi, Cursor, HuggingFace, Claude, ChatGpt }

/// <summary>
/// One outfit per tool. These are tributes made of each tool's public colours and plain shapes (llama ears, antennae,
/// fox ears, a hard hat, hugging hands, a starburst, a blossom). None of them copies a logo or a mascot.
/// </summary>
public enum Outfit { Classic, Llama, Studio, Fox, Hardhat, Hugger, Claude, ChatGpt }

public enum Mood { Happy, Calm, Worried, Busy, Satisfied }

/// <summary>What the mascot looks like and says right now.</summary>
public sealed record MascotState(Mood Mood, string Line);

/// <summary>Evidence about one tool: is it running, and when was it last touched (null = not installed).</summary>
public sealed record ToolSignal(AiTool Tool, bool Running, DateTime? LastActiveUtc);

/// <summary>
/// Chooses which outfit the mascot wears: automatically from the AI tools the person really uses, or the one they picked.
/// </summary>
public static class Personas
{
    public const string Auto = "auto";

    /// <summary>Order the click-to-change button walks through. "auto" comes first and last.</summary>
    public static readonly IReadOnlyList<string> Choices =
        [Auto, "classic", "llama", "studio", "fox", "hardhat", "hugger", "claude", "chatgpt"];

    /// <summary>Who wins when two tools look equally busy.</summary>
    public static readonly IReadOnlyList<AiTool> Priority =
        [AiTool.Ollama, AiTool.LmStudio, AiTool.ComfyUi, AiTool.Cursor, AiTool.HuggingFace, AiTool.Claude, AiTool.ChatGpt];

    public static Outfit For(AiTool tool) => tool switch
    {
        AiTool.Ollama => Outfit.Llama,
        AiTool.LmStudio => Outfit.Studio,
        AiTool.ComfyUi => Outfit.Fox,
        AiTool.Cursor => Outfit.Hardhat,
        AiTool.HuggingFace => Outfit.Hugger,
        AiTool.Claude => Outfit.Claude,
        AiTool.ChatGpt => Outfit.ChatGpt,
        _ => Outfit.Classic,
    };

    public static string ToolName(AiTool tool) => tool switch
    {
        AiTool.Ollama => "Ollama",
        AiTool.LmStudio => "LM Studio",
        AiTool.ComfyUi => "ComfyUI",
        AiTool.Cursor => "Cursor",
        AiTool.HuggingFace => "Hugging Face",
        AiTool.Claude => "Claude",
        _ => "ChatGPT",
    };

    public static string OutfitName(Outfit outfit) => outfit switch
    {
        Outfit.Llama => Loc.T("羊驼款"),
        Outfit.Studio => Loc.T("触角款"),
        Outfit.Fox => Loc.T("狐狸款"),
        Outfit.Hardhat => Loc.T("安全帽款"),
        Outfit.Hugger => Loc.T("抱抱款"),
        Outfit.Claude => Loc.T("星芒款"),
        Outfit.ChatGpt => Loc.T("花结款"),
        _ => Loc.T("经典款"),
    };

    /// <summary>
    /// Accent colour of the outfit as RRGGBB, taken from the tool's published brand colours. The speech bubble and the
    /// bow use it. Ollama and ChatGPT are black and white, so theirs is a soft white that shows on the dark bubble.
    /// </summary>
    public static string Accent(Outfit outfit) => outfit switch
    {
        Outfit.Llama => "F2F2F2",     // Ollama: black and white only
        Outfit.Studio => "8F86E8",    // LM Studio: purple (#6760CF), lightened to read on dark
        Outfit.Fox => "F2FF59",       // Comfy Yellow
        Outfit.Hardhat => "F54E00",   // Cursor Orange
        Outfit.Hugger => "FFD21E",    // Hugging Yellow
        Outfit.Claude => "D97757",    // Claude terracotta
        Outfit.ChatGpt => "ECECEC",   // OpenAI: black and white only
        _ => "FF8FB1",
    };

    // ───────── choosing ─────────

    /// <summary>
    /// Running right now counts most; after that how recently the tool was used. A tool that is not installed scores 0.
    /// </summary>
    public static int Score(ToolSignal s, DateTime nowUtc)
    {
        if (s.LastActiveUtc is null && !s.Running) return 0;

        var score = s.Running ? 100 : 0;
        if (s.LastActiveUtc is { } last)
        {
            var days = (nowUtc - last).TotalDays;
            score += days switch
            {
                <= 1 => 60,
                <= 7 => 40,
                <= 30 => 20,
                _ => 5,
            };
        }
        return score;
    }

    /// <summary>The tool the person seems to use most, or null when none of them is installed or running.</summary>
    public static AiTool? Pick(IEnumerable<ToolSignal> signals, DateTime nowUtc)
    {
        AiTool? best = null;
        var bestScore = 0;
        foreach (var s in signals)
        {
            var score = Score(s, nowUtc);
            if (score <= 0) continue;
            if (score > bestScore || (score == bestScore && best is { } b && Rank(s.Tool) < Rank(b)))
            {
                best = s.Tool;
                bestScore = score;
            }
        }
        return best;
    }

    private static int Rank(AiTool t)
    {
        for (var i = 0; i < Priority.Count; i++) if (Priority[i] == t) return i;
        return int.MaxValue;
    }

    /// <summary>All tools that look in use, most relevant first. Used to explain the choice to the person.</summary>
    public static IReadOnlyList<AiTool> InUse(IEnumerable<ToolSignal> signals, DateTime nowUtc) =>
        signals.Select(s => (s.Tool, Score: Score(s, nowUtc)))
               .Where(x => x.Score > 0)
               .OrderByDescending(x => x.Score).ThenBy(x => Rank(x.Tool))
               .Select(x => x.Tool).ToList();

    // ───────── the saved choice ─────────

    /// <summary>"auto" or the name of an outfit. Anything else, including numbers, counts as "auto".</summary>
    public static string Normalize(string? choice)
    {
        var c = (choice ?? "").Trim().ToLowerInvariant();
        return Choices.Contains(c) ? c : Auto;
    }

    public static Outfit Resolve(string? choice, AiTool? detected)
    {
        var c = Normalize(choice);
        if (c == Auto) return detected is { } t ? For(t) : Outfit.Classic;
        return Enum.Parse<Outfit>(c, ignoreCase: true);
    }

    public static string ChoiceOf(Outfit outfit) => outfit.ToString().ToLowerInvariant();

    /// <summary>The next stop when the person clicks the mascot: auto → each outfit → auto.</summary>
    public static string NextChoice(string? current)
    {
        var i = IndexOf(Normalize(current));
        return Choices[(i + 1) % Choices.Count];
    }

    private static int IndexOf(string c)
    {
        for (var i = 0; i < Choices.Count; i++) if (Choices[i] == c) return i;
        return 0;
    }

    /// <summary>What she says when she changes clothes.</summary>
    public static string Greeting(Outfit outfit, bool automatic) => (outfit, automatic) switch
    {
        (Outfit.Classic, true) => Loc.T("没认出你常用的 AI 工具,先穿经典款啦。"),
        (Outfit.Classic, false) => Loc.T("换回经典款啦。"),
        (Outfit.Llama, true) => Loc.T("看你在用 Ollama,我戴上羊驼耳朵啦。"),
        (Outfit.Llama, false) => Loc.T("羊驼款!黑白配色,Ollama 的味道。"),
        (Outfit.Studio, true) => Loc.T("看你在用 LM Studio,我竖起紫色小触角啦。"),
        (Outfit.Studio, false) => Loc.T("触角款,紫色是 LM Studio 的颜色。"),
        (Outfit.Fox, true) => Loc.T("看你在用 ComfyUI,我戴上狐狸耳朵啦。"),
        (Outfit.Fox, false) => Loc.T("狐狸款,荧光黄配深紫,来连节点吧。"),
        (Outfit.Hardhat, true) => Loc.T("看你在用 Cursor,我戴上橙色安全帽啦。"),
        (Outfit.Hardhat, false) => Loc.T("安全帽款,橙色和写代码最配。"),
        (Outfit.Hugger, true) => Loc.T("看你在用 Hugging Face,我来个抱抱。"),
        (Outfit.Hugger, false) => Loc.T("抱抱款,给你一个模型库的拥抱。"),
        (Outfit.Claude, true) => Loc.T("看你在用 Claude,我别上赤陶色的星芒啦。"),
        (Outfit.Claude, false) => Loc.T("星芒款,赤陶橙很温暖。"),
        (Outfit.ChatGpt, true) => Loc.T("看你在用 ChatGPT,我戴上黑白花结啦。"),
        _ => Loc.T("花结款,黑白很利落。"),
    };
}
