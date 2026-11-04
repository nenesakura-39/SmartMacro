using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SmartMacro.Controls;

/// <summary>Vector icon identifiers in <c>Themes/Icons.xaml</c>.</summary>
public enum StudioIconKind
{
    Library,
    Editor,
    Manual,
    Logs,
    Settings,
    Minimize,
    Maximize,
    Restore,
    Close,
    Pin,
    Play,
    Pause,
    Stop,
    Record,
    MissionControl,
    Bolt,
    Search,
    Save,
    Undo,
    Redo,
    Plus,
    Trash,
    Duplicate,
    Sliders,
    Sparkles,
    Flask,
    Blueprint,
    ExternalLink,
    Folder,
    Import,
    Export,
    ChevronDown,
    ChevronRight,
    Variable,
    Watcher,
    Checkpoint,
    SubMacro,
    SelfHeal,
    Target,
    Mouse,
    Keyboard,
    Clock,
    Eye,
    BranchIf,
    LoopRepeat,
    Gauge,
    Window,
    Camera,
    CheckCircle,
    WarningTriangle,
    ErrorCircle,
    InfoCircle,
}

/// <summary>
/// Crisp, zero-template vector icon control backed by frozen <see cref="Geometry"/> resources in <c>Themes/Icons.xaml</c>.
/// Automatically inherits <see cref="TextElement.ForegroundProperty"/> from any parent button, pill, or header.
/// </summary>
public sealed class StudioIcon : Viewbox
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(StudioIconKind),
        typeof(StudioIcon),
        new FrameworkPropertyMetadata(StudioIconKind.Bolt, OnKindChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(StudioIcon),
        new FrameworkPropertyMetadata(15.0, OnSizeChanged));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(StudioIcon),
        new FrameworkPropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(StudioIcon));

    private readonly System.Windows.Shapes.Path _path;

    public StudioIcon()
    {
        Stretch = Stretch.Uniform;
        Width = 15;
        Height = 15;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = false;
        IsHitTestVisible = false;

        _path = new System.Windows.Shapes.Path
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
        };
        _path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(TextElement.ForegroundProperty),
            RelativeSource = RelativeSource.Self,
        });

        Child = new Canvas
        {
            Width = 24,
            Height = 24,
            Children = { _path },
        };

        Loaded += (_, _) => UpdateGeometry();
        UpdateGeometry();
    }

    public StudioIconKind Kind
    {
        get => (StudioIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StudioIcon)d).UpdateGeometry();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (StudioIcon)d;
        var s = (double)e.NewValue;
        if (s > 0)
        {
            icon.Width = s;
            icon.Height = s;
        }
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (StudioIcon)d;
        if (e.NewValue is Brush brush)
        {
            icon._path.Fill = brush;
        }
        else
        {
            icon._path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding
            {
                Path = new PropertyPath(TextElement.ForegroundProperty),
                RelativeSource = RelativeSource.Self,
            });
        }
    }

    private void UpdateGeometry()
    {
        var key = $"Icon.{Kind}";
        if (TryFindResource(key) is Geometry g || Application.Current?.TryFindResource(key) is Geometry gApp && (g = gApp) is not null)
        {
            _path.Data = g;
        }
    }
}
