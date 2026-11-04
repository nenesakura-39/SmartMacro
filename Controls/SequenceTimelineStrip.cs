using System.Windows;
using System.Windows.Media;
using SmartMacro.Core.Models;

namespace SmartMacro.Controls;

/// <summary>
/// Multi-track mini timeline density strip for the Inspector's InputSequenceStep editor.
/// Visualizes Mouse Movement (top track, cyan), Mouse Clicks/Wheel (middle track, amber),
/// and Keyboard events (bottom track, violet) across the sequence's duration.
/// </summary>
public sealed class SequenceTimelineStrip : FrameworkElement
{
    public static readonly DependencyProperty EventsProperty = DependencyProperty.Register(
        nameof(Events),
        typeof(IReadOnlyList<InputEvent>),
        typeof(SequenceTimelineStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush SurfaceBrush = Frozen(Color.FromArgb(0x66, 0x09, 0x0D, 0x16));
    private static readonly Pen BorderPen = FrozenPen(Color.FromArgb(0x40, 0x64, 0x74, 0x8B), 1.0);
    private static readonly Pen TrackLinePen = FrozenPen(Color.FromArgb(0x22, 0x94, 0xA3, 0xB8), 1.0);
    private static readonly Brush MoveBrush = Frozen(Color.FromArgb(0xBB, 0x38, 0xBD, 0xF8));
    private static readonly Brush ClickBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush KeyBrush = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7));

    public SequenceTimelineStrip()
    {
        Height = 46;
        Margin = new Thickness(0, 8, 0, 4);
        ToolTip = "Recorded Event Density — Top: Mouse Move (Cyan) · Middle: Clicks/Wheel (Amber) · Bottom: Keys (Violet)";
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<InputEvent>? Events
    {
        get => (IReadOnlyList<InputEvent>?)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 16 || h <= 16) return;

        dc.DrawRoundedRectangle(SurfaceBrush, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);

        var padX = 10.0;
        var usableW = Math.Max(8.0, w - padX * 2);
        var yMove = 11.0;
        var yClick = h / 2.0;
        var yKey = h - 11.0;

        dc.DrawLine(TrackLinePen, new Point(padX, yMove), new Point(padX + usableW, yMove));
        dc.DrawLine(TrackLinePen, new Point(padX, yClick), new Point(padX + usableW, yClick));
        dc.DrawLine(TrackLinePen, new Point(padX, yKey), new Point(padX + usableW, yKey));

        if (Events is not { Count: > 0 } events) return;

        var maxUs = Math.Max(1L, events[^1].TimeUs);
        const int buckets = 96;
        var moveBuckets = new bool[buckets];
        var clickBuckets = new bool[buckets];
        var keyBuckets = new bool[buckets];

        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var idx = Math.Clamp((int)(ev.TimeUs * (buckets - 1) / maxUs), 0, buckets - 1);
            switch (ev.Kind)
            {
                case InputEventKind.MouseMove:
                    moveBuckets[idx] = true;
                    break;
                case InputEventKind.MouseDown:
                case InputEventKind.MouseUp:
                case InputEventKind.Wheel:
                case InputEventKind.HorizontalWheel:
                    clickBuckets[idx] = true;
                    break;
                case InputEventKind.KeyDown:
                case InputEventKind.KeyUp:
                    keyBuckets[idx] = true;
                    break;
            }
        }

        var slotW = usableW / buckets;
        var barW = Math.Max(2.0, slotW - 0.8);

        for (var b = 0; b < buckets; b++)
        {
            var x = padX + b * slotW;
            if (moveBuckets[b])
                dc.DrawRoundedRectangle(MoveBrush, null, new Rect(x, yMove - 3.5, barW, 7.0), 1.5, 1.5);
            if (clickBuckets[b])
                dc.DrawRoundedRectangle(ClickBrush, null, new Rect(x, yClick - 4.0, Math.Max(2.5, barW), 8.0), 2.0, 2.0);
            if (keyBuckets[b])
                dc.DrawRoundedRectangle(KeyBrush, null, new Rect(x, yKey - 4.0, Math.Max(2.5, barW), 8.0), 2.0, 2.0);
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }
}
