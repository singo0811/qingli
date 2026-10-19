using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWin.Core.AiJunk;
using Windows.Foundation;
using Windows.UI;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>One place for colours so the whole app speaks the same visual language.</summary>
public static class Palette
{
    public static Color Hex(string rgb, byte alpha = 255)
    {
        var v = Convert.ToUInt32(rgb.TrimStart('#'), 16);
        return Color.FromArgb(alpha, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static readonly Color Mint = Hex("35F2B3");
    public static readonly Color Cyan = Hex("19B9E6");
    public static readonly Color Amber = Hex("FFB84D");
    public static readonly Color Coral = Hex("FF7A45");
    public static readonly Color Violet = Hex("B38CFF");
    public static readonly Color Indigo = Hex("6E7BFF");
    public static readonly Color Slate = Hex("667085");

    public static readonly Brush MintBrush = new SolidColorBrush(Mint);
    public static readonly Brush CyanBrush = new SolidColorBrush(Cyan);

    /// <summary>How full a disk is: calm below 70%, amber to 90%, coral above.</summary>
    public static Brush UsageBrush(double percent) => new SolidColorBrush(percent >= 90 ? Coral : percent >= 70 ? Amber : Mint);
    public static readonly Brush Text = new SolidColorBrush(Hex("EAF0FA"));
    public static readonly Brush Dim = new SolidColorBrush(Hex("8794AB"));
    public static readonly Brush Faint = new SolidColorBrush(Hex("4A566B"));

    public static Color Accent(AiSafety s) => s switch
    {
        AiSafety.Regenerable => Mint,
        AiSafety.ReDownloadable => Amber,
        AiSafety.UserChoice => Violet,
        _ => Slate,
    };

    public static Color Second(AiSafety s) => s switch
    {
        AiSafety.Regenerable => Cyan,
        AiSafety.ReDownloadable => Coral,
        AiSafety.UserChoice => Indigo,
        _ => Hex("475467"),
    };

    public static Color Found(FoundSafety s) => s switch
    {
        FoundSafety.Safe => Mint,
        FoundSafety.Careful => Amber,
        _ => Slate,
    };

    public static Color Level(TidyWin.Core.Insights.InsightLevel l) => l switch
    {
        TidyWin.Core.Insights.InsightLevel.Urgent => Coral,
        TidyWin.Core.Insights.InsightLevel.Warn => Amber,
        TidyWin.Core.Insights.InsightLevel.Tip => Cyan,
        _ => Mint,
    };

    public static Brush LevelBrush(TidyWin.Core.Insights.InsightLevel l) => new SolidColorBrush(Level(l));

    public static string LevelLabel(TidyWin.Core.Insights.InsightLevel l) => l switch
    {
        TidyWin.Core.Insights.InsightLevel.Urgent => Loc.T("尽快处理"),
        TidyWin.Core.Insights.InsightLevel.Warn => Loc.T("建议处理"),
        TidyWin.Core.Insights.InsightLevel.Tip => Loc.T("有空看看"),
        _ => Loc.T("一切正常"),
    };

    /// <summary>Stripe colour of a start-up entry: hostile coral, dead amber, things you may turn off violet, the rest mint.</summary>
    public static Brush StartupBrush(TidyWin.Core.Startup.StartupAdvice a) => new SolidColorBrush(
        a.Kind == TidyWin.Core.Startup.StartupKind.Suspicious ? Coral :
        a.Kind == TidyWin.Core.Startup.StartupKind.Dead ? Amber :
        a.Verdict == TidyWin.Core.Startup.StartupVerdict.Optional ? Violet : Mint);

    public static Brush FoundBrush(FoundSafety s) => new SolidColorBrush(Found(s));

    public static string FoundLabel(FoundSafety s) => s switch
    {
        FoundSafety.Safe => Loc.T("有明确理由"),
        FoundSafety.Careful => Loc.T("需要你判断"),
        _ => Loc.T("正在使用"),
    };

    /// <summary>Solid accent for text and stripes. Used from x:Bind.</summary>
    public static Brush Tier(AiSafety s) => new SolidColorBrush(Accent(s));

    public static Brush Gradient(AiSafety s) => Horizontal(Accent(s), Second(s));

    public static Brush Horizontal(Color a, Color b) => new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 0),
        GradientStops =
        {
            new GradientStop { Color = a, Offset = 0 },
            new GradientStop { Color = b, Offset = 1 },
        },
    };

    public static Brush State(TidyWin.ViewModels.MemoryGroupState s) => new SolidColorBrush(s switch
    {
        TidyWin.ViewModels.MemoryGroupState.InUse => Mint,
        TidyWin.ViewModels.MemoryGroupState.RecentlyUsed => Cyan,
        TidyWin.ViewModels.MemoryGroupState.IdleBackground => Amber,
        TidyWin.ViewModels.MemoryGroupState.System => Slate,
        _ => Hex("8794AB"),
    });

    public static Color Pressure(TidyWin.Core.Memory.MemoryPressure p) => p switch
    {
        TidyWin.Core.Memory.MemoryPressure.Calm => Mint,
        TidyWin.Core.Memory.MemoryPressure.Moderate => Cyan,
        TidyWin.Core.Memory.MemoryPressure.High => Amber,
        _ => Coral,
    };

    public static Color Sev(TidyWin.Core.Security.Severity s) => s switch
    {
        TidyWin.Core.Security.Severity.Risk => Coral,
        TidyWin.Core.Security.Severity.Warn => Amber,
        TidyWin.Core.Security.Severity.Info => Cyan,
        _ => Mint,
    };

    /// <summary>A card background that glows faintly from the left in the severity colour and fades into the normal card colour.</summary>
    public static Brush CardWash(TidyWin.Core.Security.Severity s)
    {
        var c = Sev(s);
        var strength = s switch
        {
            TidyWin.Core.Security.Severity.Risk => (byte)0x30,
            TidyWin.Core.Security.Severity.Warn => (byte)0x26,
            TidyWin.Core.Security.Severity.Info => (byte)0x16,
            _ => (byte)0x10,
        };
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(0.7, 0.5),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(strength, c.R, c.G, c.B), Offset = 0 },
                new GradientStop { Color = Hex("0F1520"), Offset = 1 },
            },
        };
    }

    /// <summary>Solid severity colour for x:Bind.</summary>
    public static Brush SevBrush(TidyWin.Core.Security.Severity s) => new SolidColorBrush(Sev(s));

    /// <summary>Colour of one connection result: reached, reached but refused the region, failed, or not tried.</summary>
    public static Brush OutcomeBrush(int level) => level switch
    {
        0 => new SolidColorBrush(Mint),
        1 => new SolidColorBrush(Amber),
        2 => new SolidColorBrush(Coral),
        _ => Faint,
    };

    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisNot(bool visible) => visible ? Visibility.Collapsed : Visibility.Visible;
}

