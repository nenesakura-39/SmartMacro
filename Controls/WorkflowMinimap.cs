using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SmartMacro.Converters;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Controls;

/// <summary>
/// Studio X Workflow Minimap v2: renders a zero-allocation vertical overview bar along the right edge of the step canvas,
/// drawing category-colored ticks, indentation depth, breakpoint dots, validation issue markers, active running step beacon,
/// a synced Viewport Lens rectangle, and dynamic hover preview tooltips.
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

    public static readonly DependencyProperty ViewportStartRatioProperty = DependencyProperty.Register(
        nameof(ViewportStartRatio),
        typeof(double),
        typeof(WorkflowMinimap),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewportHeightRatioProperty = DependencyProperty.Register(
        nameof(ViewportHeightRatio),
        typeof(double),
        typeof(WorkflowMinimap),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = Frozen(Color.FromArgb(0x28, 0x0F, 0x17, 0x2A));
    private static readonly Pen TrackBorderPen = FrozenPen(Color.FromArgb(0x38, 0x64, 0x74, 0x8B), 1.0);
    private static readonly Brush ViewportFillBrush = Frozen(Color.FromArgb(0x24, 0x38, 0xBD, 0xF8));
    private static readonly Pen ViewportBorderPen = FrozenPen(Color.FromArgb(0x88, 0x38, 0xBD, 0xF8), 1.0);
    private static readonly Brush InputBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush VisionBrush = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly Brush LogicBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush MemoryBrush = Frozen(Color.FromRgb(0x14, 0xB8, 0xA6));
    private static readonly Brush FeedbackBrush = Frozen(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush UtilityBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush TimingBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush DisabledBrush = Frozen(Color.FromArgb(0x45, 0x64, 0x74, 0x8B));
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush RunningGlowBrush = Frozen(Color.FromArgb(0x55, 0x10, 0xB9, 0x81));
    private static readonly Pen SelectedPen = FrozenPen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF), 1.3);

    private INotifyCollectionChanged? _subscribed;
    private readonly HashSet<StepViewModel> _subscribedSteps = new(ReferenceEqualityComparer.Instance);

    public WorkflowMinimap()
    {
        Width = 20;
        Cursor = Cursors.Hand;
        ToolTip = "Workflow Minimap — click or drag to scrub through steps";
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

    public double ViewportStartRatio
    {
        get => (double)GetValue(ViewportStartRatioProperty);
        set => SetValue(ViewportStartRatioProperty, value);
    }

    public double ViewportHeightRatio
    {
        get => (double)GetValue(ViewportHeightRatioProperty);
        set => SetValue(ViewportHeightRatioProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (WorkflowMinimap)d;
        if (map._subscribed is not null)
            map._subscribed.CollectionChanged -= map.OnCollectionChanged;
        map.UnsubscribeAllSteps();

        map._subscribed = e.NewValue as INotifyCollectionChanged;
        if (map._subscribed is not null)
            map._subscribed.CollectionChanged += map.OnCollectionChanged;
        map.SyncStepSubscriptions();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncStepSubscriptions();
        InvalidateVisual();
    }

    private void SyncStepSubscriptions()
    {
        UnsubscribeAllSteps();
        if (Items is not { } items) return;
        foreach (var step in items)
        {
            if (_subscribedSteps.Add(step))
                step.PropertyChanged += OnStepPropertyChanged;
        }
    }

    private void UnsubscribeAllSteps()
    {
        foreach (var step in _subscribedSteps)
            step.PropertyChanged -= OnStepPropertyChanged;
        _subscribedSteps.Clear();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepViewModel.HasBreakpoint)
            or nameof(StepViewModel.Enabled)
            or nameof(StepViewModel.HasError)
            or nameof(StepViewModel.HasWarning)
            or nameof(StepViewModel.IsRunning)
            or nameof(StepViewModel.IsVisibleInList)
            or nameof(StepViewModel.IndentLevel))
        {
            InvalidateVisual();
        }
    }

    private List<StepViewModel> GetVisibleItems()
    {
        if (Items is not { Count: > 0 } items) return [];
        var list = new List<StepViewModel>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsVisibleInList)
                list.Add(items[i]);
        }
        return list;
    }

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
        var y = e.GetPosition(this).Y;
        UpdateHoverTooltip(y);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            JumpAt(y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void UpdateHoverTooltip(double y)
    {
        var visible = GetVisibleItems();
        if (visible.Count == 0 || ActualHeight <= 8) return;
        var ratio = Math.Clamp((y - 4) / Math.Max(1, ActualHeight - 8), 0, 0.999999);
        var index = Math.Clamp((int)(ratio * visible.Count), 0, visible.Count - 1);
        var step = visible[index];
        var timePart = step.HasTimelineOffset ? $" [{step.TimelineOffsetText}]" : "";
        ToolTip = $"#{step.Number} {step.Title}{timePart}\n{step.Summary}";
    }

    private void JumpAt(double y)
    {
        var visible = GetVisibleItems();
        if (visible.Count == 0 || ActualHeight <= 8) return;
        var ratio = Math.Clamp((y - 4) / Math.Max(1, ActualHeight - 8), 0, 0.999999);
        var index = Math.Clamp((int)(ratio * visible.Count), 0, visible.Count - 1);
        StepClicked?.Invoke(this, visible[index]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var visible = GetVisibleItems();
        if (w <= 2 || h <= 8 || visible.Count == 0) return;

        dc.DrawRoundedRectangle(TrackBrush, TrackBorderPen, new Rect(1, 2, Math.Max(2, w - 2), h - 4), 5, 5);

        var usableH = Math.Max(4, h - 10);

        // Draw synced Viewport Lens when scrollbar is active
        var vpHeightRatio = Math.Clamp(ViewportHeightRatio, 0.04, 1.0);
        if (vpHeightRatio < 0.99)
        {
            var vpStart = Math.Clamp(ViewportStartRatio, 0.0, 1.0 - vpHeightRatio);
            var lensY = 5 + vpStart * usableH;
            var lensH = Math.Max(14.0, vpHeightRatio * usableH);
            dc.DrawRoundedRectangle(ViewportFillBrush, ViewportBorderPen, new Rect(1.5, lensY, Math.Max(2, w - 3), Math.Min(usableH, lensH)), 3.5, 3.5);
        }

        var count = visible.Count;
        var slotH = usableH / count;
        var tickH = Math.Clamp(slotH - 1.0, 1.8, 5.5);

        for (var i = 0; i < count; i++)
        {
            var step = visible[i];
            var y = 5 + i * slotH;
            var indentOffset = Math.Min(6.0, step.IndentLevel * 2.0);
            var x = 3.0 + indentOffset;
            var tickW = Math.Max(3.0, w - x - 3.0);

            if (step.IsRunning)
            {
                dc.DrawRoundedRectangle(RunningGlowBrush, null, new Rect(1.5, Math.Max(2, y - 2), Math.Max(3, w - 3), tickH + 4), 2.5, 2.5);
            }

            var brush = !step.Enabled ? DisabledBrush
                : step.HasError ? ErrorBrush
                : step.HasWarning ? WarningBrush
                : step.IsRunning ? FeedbackBrush
                : BrushFor(StepCategories.Of(step.Kind));

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, tickW, tickH), 1.2, 1.2);

            if (step.HasBreakpoint)
            {
                dc.DrawEllipse(ErrorBrush, null, new Point(2.5, y + tickH / 2), 1.8, 1.8);
            }

            if (ReferenceEquals(step, SelectedStep))
            {
                dc.DrawRoundedRectangle(null, SelectedPen, new Rect(1.5, Math.Max(2, y - 1.5), Math.Max(3, w - 3), tickH + 3), 2.5, 2.5);
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
