using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TidyWin.Core.Mascot;
using TidyWin.Platform;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>
/// The whale girl and her speech bubble. It dresses her by the AI tools this person really uses (or by the outfit
/// they picked, remembered between runs) and lets the current page tell her what to say.
/// </summary>
public sealed partial class MascotDock : UserControl
{
    private static readonly TimeSpan BubbleTime = TimeSpan.FromSeconds(7);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AiToolDetector _detector = new();
    private readonly DispatcherQueueTimer _hide;

    private MascotState _page = new(Mood.Calm, "");
    private AiTool? _detected;
    private IReadOnlyList<AiTool> _inUse = [];
    private Outfit _outfit;
    private bool _awake = true;
    private bool _greeting;
    private int _detections;

    /// <summary>Shared with the window so both write the same file and neither overwrites the other's changes.</summary>
    public AppSettings Settings => _settings;

    /// <summary>AI tools seen running or recently used (empty until the first look is done).</summary>
    public IReadOnlyList<AiTool> InUse => _inUse;

    /// <summary>Raised on the UI thread each time a look at the AI tools has finished.</summary>
    public event Action? Detected;

    public MascotDock()
    {
        InitializeComponent();

        _outfit = Personas.Resolve(_settings.Outfit, null);
        _hide = DispatcherQueue.CreateTimer();
        _hide.Interval = BubbleTime;
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => FadeBubble();

        Whale.Apply(_outfit, _page.Mood);
        RefreshTip();
    }

    // ───────── what the window tells us ─────────

    /// <summary>A different page was opened: show what it says.</summary>
    public void PageOpened(MascotState state)
    {
        _page = state;
        Whale.SetMood(_greeting ? Mood.Happy : state.Mood);
        Say(state.Line, tag: null);
    }

    /// <summary>The page being shown changed. She only speaks up again when her mood changes; otherwise the text just updates.</summary>
    public void PageChanged(MascotState state)
    {
        var moodChanged = state.Mood != _page.Mood;
        _page = state;
        if (_greeting) return;

        Whale.SetMood(state.Mood);
        if (moodChanged) Say(state.Line, tag: null);
        else if (Bubble.Opacity > 0) BubbleText.Text = state.Line;
    }

    public void Sleep()
    {
        _awake = false;
        _hide.Stop();
        Whale.Sleep();
    }

    public void Wake()
    {
        _awake = true;
        Whale.Wake();
    }

    // ───────── knowing which AI tools are used ─────────

    /// <summary>Looks at running programs and a few folders. It runs on a worker thread, so start-up is not held up.</summary>
    public async void Detect(bool greet)
    {
        var generation = ++_detections;
        IReadOnlyList<ToolSignal> signals;
        try { signals = await Task.Run(_detector.Detect); }
        catch { return; } // not knowing is fine: she just keeps her current outfit
        if (generation != _detections) return; // a newer look is already on its way

        var now = DateTime.UtcNow;
        _detected = Personas.Pick(signals, now);
        _inUse = Personas.InUse(signals, now);
        Detected?.Invoke();

        var next = Personas.Resolve(_settings.Outfit, _detected);
        var automatic = Personas.Normalize(_settings.Outfit) == Personas.Auto;
        var changed = next != _outfit;
        _outfit = next;
        Whale.Apply(_outfit, _greeting ? Mood.Happy : _page.Mood);
        RefreshTip();

        // Changing clothes by herself is worth a word; staying in the same clothes is not.
        if (_awake && (greet || (changed && automatic)))
            Greet(Personas.Greeting(_outfit, automatic));
    }

    // ───────── the person clicks her ─────────

    private void OnEnter(object sender, PointerRoutedEventArgs e)
    {
        if (!_awake || _greeting) return;
        Say(_page.Line, tag: null);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        var choice = Personas.NextChoice(_settings.Outfit);
        _settings.Outfit = choice;
        _ = Task.Run(() => _settings.Save()); // a failed save only means she forgets next time

        if (choice == Personas.Auto)
        {
            Detect(greet: true); // going back to automatic is the moment to look again
            return;
        }

        _outfit = Personas.Resolve(choice, _detected);
        Whale.Apply(_outfit, Mood.Happy);
        RefreshTip();
        Greet(Personas.Greeting(_outfit, automatic: false));
    }

    private void Greet(string text)
    {
        _greeting = true;
        Whale.SetMood(Mood.Happy);
        Whale.Hop();
        Say(text, tag: Personas.OutfitName(_outfit));
    }

    // ───────── the bubble ─────────

    private void Say(string text, string? tag)
    {
        if (!_awake || string.IsNullOrWhiteSpace(text)) return;

        var accent = Palette.Hex(Personas.Accent(_outfit));
        BubbleText.Text = text;
        BubbleTag.Text = tag ?? "";
        BubbleTag.Visibility = Palette.Vis(tag is not null);
        BubbleTag.Foreground = new SolidColorBrush(accent);

        var edge = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, accent.R, accent.G, accent.B));
        BubbleCard.BorderBrush = edge;
        BubbleTail.Stroke = edge;

        var visual = ElementCompositionPreview.GetElementVisual(Bubble);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(Bubble, true);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Opacity", fade);

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(-10, 0, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(240);
        visual.StartAnimation("Translation", slide);

        _hide.Stop();
        _hide.Start();
    }

    private void FadeBubble()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Bubble);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation("Opacity", fade);

        // After a greeting she goes back to how the page makes her feel.
        if (_greeting)
        {
            _greeting = false;
            Whale.SetMood(_page.Mood);
        }
    }

    private void RefreshTip()
    {
        var automatic = Personas.Normalize(_settings.Outfit) == Personas.Auto;
        var why = !automatic
            ? Loc.T("你选的")
            : _inUse.Count > 0
                ? Loc.T("按你在用的 AI 自动选择:") + string.Join(Loc.T("、"), _inUse.Take(3).Select(Personas.ToolName))
                : Loc.T("自动,没认出常用的 AI 工具");
        ToolTipService.SetToolTip(Whale, Loc.F("点我换装 · 现在是{0}({1})",Personas.OutfitName(_outfit),why));
    }
}
