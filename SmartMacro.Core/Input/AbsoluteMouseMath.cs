namespace SmartMacro.Core.Input;

/// <summary>
/// Converts screen pixels to the 0-65535 "normalized absolute" coordinates SendInput uses with
/// MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK. Kept free of Windows calls so it can be unit tested.
/// Windows maps a normalized value back to a pixel with about one pixel of rounding, which is why the
/// simulator still finishes with SetCursorPos to land on the exact pixel.
/// </summary>
public static class AbsoluteMouseMath
{
    public const int MaxNormalized = 65535;

    /// <summary>
    /// Normalizes one axis. <paramref name="origin"/> and <paramref name="size"/> describe the virtual screen
    /// (all monitors) on that axis in physical pixels. Positions outside the screen are clamped to its edges.
    /// </summary>
    public static int Normalize(int position, int origin, int size)
    {
        if (size <= 1) return 0;
        var offset = Math.Clamp((long)position - origin, 0, size - 1);
        return (int)Math.Round(offset * (double)MaxNormalized / (size - 1));
    }

    /// <summary>The pixel a normalized value points at (same scale as <see cref="Normalize"/>), for checks and tests.</summary>
    public static int ToPixel(int normalized, int origin, int size)
    {
        if (size <= 1) return origin;
        return origin + (int)Math.Round(Math.Clamp(normalized, 0, MaxNormalized) * (double)(size - 1) / MaxNormalized);
    }
}
