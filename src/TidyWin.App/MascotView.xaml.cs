using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TidyWin.Core.Mascot;

namespace TidyWin.App;

/// <summary>
/// The whale girl. She is drawn with vectors only (no image files). Her outfit and mood are switched by showing and
/// hiding parts, which costs nothing while she is still. The only motion is an occasional blink and hop driven by
/// a slow timer, and that timer stops whenever the window cannot be seen.
/// </summary>
public sealed partial class MascotView : UserControl
{
    private readonly DispatcherQueueTimer _idle;
    private readonly DispatcherQueueTimer _blink;
    private int _beat;
    private Outfit _outfit = Outfit.Classic;

    public MascotView()
    {
        InitializeComponent();
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);

        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(4.5);
        _idle.Tick += (_, _) => Idle();

        _blink = DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(130);
        _blink.IsRepeating = false;
        _blink.Tick += (_, _) => BlinkScale.ScaleY = 1;

        Apply(Outfit.Classic, Mood.Calm);
    }

    // ───────── look ─────────

    public void Apply(Outfit outfit, Mood mood)
    {
        _outfit = outfit;

        OutfitClassic.Visibility = Palette.Vis(outfit == Outfit.Classic);
        OutfitLlama.Visibility = Palette.Vis(outfit == Outfit.Llama);
        OutfitStudio.Visibility = Palette.Vis(outfit == Outfit.Studio);
        OutfitFox.Visibility = Palette.Vis(outfit == Outfit.Fox);
        OutfitHardhat.Visibility = Palette.Vis(outfit == Outfit.Hardhat);
        OutfitHugger.Visibility = Palette.Vis(outfit == Outfit.Hugger);
        OutfitClaude.Visibility = Palette.Vis(outfit == Outfit.Claude);
        OutfitChatGpt.Visibility = Palette.Vis(outfit == Outfit.ChatGpt);
        HoodBack.Visibility = Palette.Vis(outfit == Outfit.Hugger);

        // The bow takes the outfit's accent colour.
        var accent = new SolidColorBrush(Palette.Hex(Personas.Accent(outfit)));
        BowWings.Fill = accent;
        BowKnot.Fill = accent;

        SetMood(mood);
    }

    /// <summary>TIDYWIN_MOOD=Happy|Calm|Worried|Busy|Satisfied pins the expression, for screenshots. Not set in normal use.</summary>
    private static readonly Mood? Pinned =
        Enum.TryParse<Mood>(Environment.GetEnvironmentVariable("TIDYWIN_MOOD"), ignoreCase: true, out var m) ? m : null;

    public void SetMood(Mood mood)
    {
        mood = Pinned ?? mood;
        var happyEyes = mood == Mood.Satisfied;
        EyesOpen.Visibility = Palette.Vis(!happyEyes);
        EyesHappy.Visibility = Palette.Vis(happyEyes);

        // Hats and ears cover the top of her head, so the little "thinking" dots and the water spout only show
        // when nothing is sitting there.
        var bareHead = _outfit is Outfit.Classic or Outfit.Studio or Outfit.Claude or Outfit.ChatGpt;
        Brows.Visibility = Palette.Vis(mood == Mood.Worried);
        Sweat.Visibility = Palette.Vis(mood == Mood.Worried);
        Dots.Visibility = Palette.Vis(mood == Mood.Busy && bareHead);
        Sparkles.Visibility = Palette.Vis(mood is Mood.Happy or Mood.Satisfied);
        Spout.Visibility = Palette.Vis(mood == Mood.Happy && bareHead);

        MouthHappy.Visibility = Palette.Vis(mood == Mood.Happy);
        MouthCalm.Visibility = Palette.Vis(mood == Mood.Calm);
        MouthWorried.Visibility = Palette.Vis(mood == Mood.Worried);
        MouthBusy.Visibility = Palette.Vis(mood == Mood.Busy);
        MouthSatisfied.Visibility = Palette.Vis(mood == Mood.Satisfied);
    }

    // ───────── life ─────────

    /// <summary>Starts the blink-and-hop timer. Call again when the window becomes visible.</summary>
    public void Wake() => _idle.Start();

    /// <summary>Stops all motion. Call when the window is minimized.</summary>
    public void Sleep()
    {
        _idle.Stop();
        _blink.Stop();
        BlinkScale.ScaleY = 1;
    }

    private void Idle()
    {
        Blink();
        _beat++;
        if (_beat % 3 == 0) Hop(4, 520);
    }

    private void Blink()
    {
        if (EyesOpen.Visibility != Visibility.Visible) return;
        BlinkScale.ScaleY = 0.08;
        _blink.Start();
    }

    /// <summary>A little jump. It runs on the compositor thread.</summary>
    public void Hop(double height = 9, int milliseconds = 560)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Art);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(Art, true);

        var up = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.4f, 1f));
        var down = c.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.8f, 0.4f));

        var hop = c.CreateVector3KeyFrameAnimation();
        hop.InsertKeyFrame(0f, Vector3.Zero);
        hop.InsertKeyFrame(0.42f, new Vector3(0, (float)-height, 0), up);
        hop.InsertKeyFrame(1f, Vector3.Zero, down);
        hop.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Translation", hop);

        // Squash and stretch around the feet.
        visual.CenterPoint = new Vector3((float)Art.ActualWidth / 2, (float)Art.ActualHeight, 0);
        var squash = c.CreateVector3KeyFrameAnimation();
        squash.InsertKeyFrame(0f, Vector3.One);
        squash.InsertKeyFrame(0.12f, new Vector3(1.06f, 0.93f, 1));
        squash.InsertKeyFrame(0.42f, new Vector3(0.96f, 1.06f, 1), up);
        squash.InsertKeyFrame(1f, Vector3.One, down);
        squash.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Scale", squash);
    }
}
