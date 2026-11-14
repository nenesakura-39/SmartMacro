namespace SmartMacro.Core.Input;

/// <summary>
/// Plans stepped mouse drag sub-movements and closed-loop relative deltas so Win32 title-bar moves
/// (<c>WM_NCLBUTTONDOWN</c> / <c>SC_MOVE</c>), WPF <c>DragMove()</c>, and modern WinUI / Chromium shells
/// receive continuous hardware-like pointer ticks while a mouse button is held.
/// </summary>
public static class DragStepPlanner
{
    /// <summary>Default maximum Euclidean distance (px) per sub-step during a button-held drag move.</summary>
    public const int DefaultMaxSubStepPx = 10;

    /// <summary>Default maximum relative count magnitude per single <c>MOUSEEVENTF_MOVE</c> packet during a drag.</summary>
    public const int DefaultMaxRelativeDeltaPx = 16;

    /// <summary>
    /// Subdivides the segment from <paramref name="from"/> to <paramref name="to"/> into intermediate
    /// waypoints spaced by at most <paramref name="maxStepPx"/> pixels, always ending at <paramref name="to"/>.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> ComputeSubSteps(
        (int X, int Y) from,
        (int X, int Y) to,
        int maxStepPx = DefaultMaxSubStepPx)
    {
        var stepSize = Math.Max(2, maxStepPx);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dist = Math.Sqrt((double)dx * dx + (double)dy * dy);

        if (dist <= stepSize)
        {
            return new[] { to };
        }

        var count = Math.Clamp((int)Math.Ceiling(dist / stepSize), 2, 128);
        var steps = new List<(int X, int Y)>(count);
        var last = from;

        for (var i = 1; i <= count; i++)
        {
            var t = (double)i / count;
            var pt = (
                X: i == count ? to.X : (int)Math.Round(from.X + dx * t),
                Y: i == count ? to.Y : (int)Math.Round(from.Y + dy * t));

            if (pt != last || i == count)
            {
                steps.Add(pt);
                last = pt;
            }
        }

        return steps;
    }

    /// <summary>
    /// Computes a single relative <c>MOUSEEVENTF_MOVE</c> delta toward <c>(targetX, targetY)</c> from
    /// the current cursor position <c>(currentX, currentY)</c>, compensated by <paramref name="gain"/>.
    /// </summary>
    public static (int Dx, int Dy) ComputeClosedLoopRelativeDelta(
        int targetX,
        int targetY,
        int currentX,
        int currentY,
        double gain = 1.0,
        int maxDeltaPx = DefaultMaxRelativeDeltaPx)
    {
        var errX = targetX - currentX;
        var errY = targetY - currentY;
        if (errX == 0 && errY == 0)
            return (0, 0);

        var safeGain = double.IsFinite(gain) ? Math.Clamp(gain, 0.25, 4.0) : 1.0;
        var maxStep = Math.Max(1, maxDeltaPx);

        var errDist = Math.Sqrt((double)errX * errX + (double)errY * errY);
        var clampedErrX = (double)errX;
        var clampedErrY = (double)errY;
        if (errDist > maxStep)
        {
            var scale = maxStep / errDist;
            clampedErrX *= scale;
            clampedErrY *= scale;
        }

        var dx = (int)Math.Round(clampedErrX * safeGain);
        var dy = (int)Math.Round(clampedErrY * safeGain);

        if (dx == 0 && errX != 0) dx = Math.Sign(errX);
        if (dy == 0 && errY != 0) dy = Math.Sign(errY);

        return (dx, dy);
    }

    /// <summary>
    /// Updates the closed-loop relative gain estimate based on how far the OS cursor actually moved
    /// in response to a relative move packet (accounting for Enhance Pointer Precision / mouse speed).
    /// </summary>
    public static double AdaptGain(double currentGain, double sentMagnitude, double actualMovedPx)
    {
        var baseGain = double.IsFinite(currentGain) ? Math.Clamp(currentGain, 0.25, 4.0) : 1.0;
        if (sentMagnitude < 1.0 || actualMovedPx < 0.5)
            return baseGain;

        var observedRatio = actualMovedPx / sentMagnitude;
        if (observedRatio <= 0.05 || !double.IsFinite(observedRatio))
            return baseGain;

        var idealGain = 1.0 / observedRatio;
        var blended = (baseGain * 0.6) + (idealGain * 0.4);
        return Math.Clamp(blended, 0.25, 4.0);
    }
}
