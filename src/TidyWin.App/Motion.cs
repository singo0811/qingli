using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace TidyWin.App;

/// <summary>Small entrance animations. They run on the compositor thread, so they cost the UI thread nothing.</summary>
public static class Motion
{
    /// <summary>Fades an element in while it rises a few pixels into place.</summary>
    public static void RiseIn(UIElement element, double fromY = 14, int milliseconds = 280, int delayMilliseconds = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0f, new Vector3(0, (float)fromY, 0));
        rise.InsertKeyFrame(1f, Vector3.Zero, ease);
        rise.Duration = duration;
        rise.DelayTime = delay;
        rise.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", rise);
    }

    /// <summary>Rises several elements in one after another.</summary>
    /// <remarks>Do not pass elements that live inside a ScrollViewer: their animated visuals get clipped wrongly.</remarks>
    public static void Cascade(params UIElement[] elements)
    {
        for (var i = 0; i < elements.Length; i++)
            if (elements[i].Visibility == Visibility.Visible) RiseIn(elements[i], 16, 300, i * 70);
    }
}
