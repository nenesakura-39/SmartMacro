using SmartMacro.Core.Validation;
using SmartMacro.Services.Input;

namespace SmartMacro.Services;

public interface IScreenInfoProvider
{
    /// <summary>The rectangle covering all monitors, in physical pixels.</summary>
    ScreenBounds GetVirtualScreen();
}

/// <summary>
/// Reads the virtual screen size from Windows. The app is per-monitor DPI aware (see app.manifest),
/// so these are real pixels - the same coordinates mouse automation uses.
/// </summary>
public sealed class ScreenInfoProvider : IScreenInfoProvider
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    public ScreenBounds GetVirtualScreen() => new(
        NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN));
}
