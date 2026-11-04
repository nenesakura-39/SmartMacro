using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SmartMacro.Converters;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Controls;

/// <summary>
/// Studio X Workflow Minimap: renders a zero-allocation vertical overview bar along the right edge of the step canvas,
/// drawing category-colored ticks, validation issue markers, active running step glow, and click-to-jump navigation.
/// </summary>
public sealed class WorkflowMinimap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IList<StepViewModel>),
        typeof(WorkflowMinimap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty SelectedStepProperty = DependencyProperty.Register(
        nameof(SelectedStep),
        typeof(StepViewModel),
        typeof(WorkflowMinimap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x22, 0x94, 0xA3, 0xB8));
    private static readonly Brush InputBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush VisionBrush = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly Brush LogicBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush MemoryBrush = Frozen(Color.FromRgb(0x14, 0xB8, 0xA6));
    private static readonly Brush FeedbackBrush = Frozen(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush UtilityBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush TimingBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Pen SelectedPen = FrozenPen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF), 1.2);

    private INotifyCollectionChanged? _subscribed;

    public WorkflowMinimap()
    {
        Width = 14;
        Cursor = Cursors.Hand;
        ToolTip = "Workflow Minimap — click or drag to jump to any step";
        SnapsToDevicePixels = true;
    }

    public event EventHandler<StepViewModel>? StepClicked;

    public IList<StepViewModel>? Items
    {
        get => (IList<StepViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public StepViewModel? SelectedStep
    {
        get => (StepViewModel?)GetValue(SelectedStepProperty);
        set => SetValue(SelectedStepProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (WorkflowMinimap)d;
        if (map._subscribed is not null)
            map._subscribed.CollectionChanged -= map.OnCollectionChanged;
        map._subscribed = e.NewValue as INotifyCollectionChanged;
        if (map._subscribed is not null)
            map._subscribed.CollectionChanged += map.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        JumpAt(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            JumpAt(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void JumpAt(double y)
    {
        if (Items is not { Count: > 0 } items || ActualHeight <= 8) return;
        var ratio = Math.Clamp((y - 4) / Math.Max(1, ActualHeight - 8), 0, 0.999999);
        var index = Math.Clamp((int)(ratio * items.Count), 0, items.Count - 1);
        StepClicked?.Invoke(this, items[index]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 8 || Items is not { Count: > 0 } items) return;

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(1, 2, Math.Max(2, w - 2), h - 4), 4, 4);

        var usableH = Math.Max(4, h - 10);
        var count = items.Count;
        var slotH = usableH / count;
        var tickH = Math.Clamp(slotH - 1.0, 1.8, 5.0);

        for (var i = 0; i < count; i++)
        {
            var step = items[i];
            var y = 5 + i * slotH;
            var indentOffset = Math.Min(4.0, step.IndentLevel * 1.5);
            var x = 2.5 + indentOffset;
            var tickW = Math.Max(3.0, w - x - 2.5);

            var brush = step.HasError ? ErrorBrush
                : step.HasWarning ? WarningBrush
                : step.IsRunning ? FeedbackBrush
                : BrushFor(StepCategories.Of(step.Kind));

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, tickW, tickH), 1, 1);

            if (ReferenceEquals(step, SelectedStep))
            {
                dc.DrawRoundedRectangle(null, SelectedPen, new Rect(1.5, Math.Max(2, y - 1.5), Math.Max(3, w - 3), tickH + 3), 2, 2);
            }
        }
    }

    private static Brush BrushFor(string category) => category switch
    {
        StepCategories.Input => InputBrush,
        StepCategories.Vision => VisionBrush,
        StepCategories.Logic => LogicBrush,
        StepCategories.Memory => MemoryBrush,
        StepCategories.Feedback => FeedbackBrush,
        StepCategories.Utility => UtilityBrush,
        _ => TimingBrush,
    };

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
