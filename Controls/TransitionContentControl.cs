using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartMacro.Services;

namespace SmartMacro.Controls;

/// <summary>
/// Lightweight, zero-NuGet page host that plays a crisp 160ms CubicEase fade + 8px vertical slide
/// whenever <see cref="ContentControl.Content"/> switches between Studio pages.
/// Automatically skips animation when Windows "Animation effects" or SmartMacro's "Reduce motion" is active.
/// </summary>
public sealed class TransitionContentControl : ContentControl
{
    private readonly TranslateTransform _translate = new();

    public TransitionContentControl()
    {
        Focusable = false;
        RenderTransform = _translate;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (ReferenceEquals(oldContent, newContent) || newContent is null || !IsLoaded)
            return;

        if (!ThemeManager.AreAnimationsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            _translate.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1.0;
            _translate.Y = 0.0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(160));

        var fade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
        var slide = new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = ease };

        BeginAnimation(OpacityProperty, fade);
        _translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }
}
