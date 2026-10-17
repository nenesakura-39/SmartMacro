using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;

namespace SmartMacro.Views;

/// <summary>
/// The SmartMacro brand mark (vector, scales to any size). Shapes and colours: Themes/Brand.xaml.
/// The glow blur radii are designed on the 256 × 256 grid; they are scaled with the control's size so a 30 px sidebar
/// logo gets a 30 px-sized glow instead of a haze (blur radii don't follow a Viewbox's scaling reliably).
/// </summary>
public partial class BrandLogo : UserControl
{
    private const double DesignSize = 256;
    private readonly (BlurEffect Effect, double Radius)[] _glows;

    public BrandLogo()
    {
        InitializeComponent();
        _glows = new[]
        {
            (BracketGlow, 12.0),
            (BoltHalo, 30.0),
            (BoltRim, 10.0),
            (CursorShadow, 12.0),
            (BoltCore, 4.0),
        };
        SizeChanged += (_, _) => ScaleGlows();
    }

    private void ScaleGlows()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var scale = size / DesignSize;
        foreach (var (effect, radius) in _glows)
            effect.Radius = Math.Max(0.5, radius * scale);
    }
}
