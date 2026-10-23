using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

/// <summary>One cursor position along a planned movement, <see cref="At"/> after the movement started.</summary>
public readonly record struct MotionPoint(TimeSpan At, int X, int Y);

/// <summary>
/// Plans how the cursor travels from one spot to another (pure maths, no Windows calls, unit tested):
/// - Instant: one point, the target.
/// - Smooth glide: a straight line with a gentle start and stop (smoothstep), one point per tick.
/// - Human curve: a slight cubic Bézier arc (bending to one side by about 12 % of the distance, at most 60 px) with
///   ease-in / ease-out speed (smootherstep), like a hand moving a mouse.
/// The last point is always exactly the target. Ticks are ~8 ms (about 120 cursor updates per second), so sliders,
/// scroll bars and game buttons get a stream of real mouse-move events instead of one teleport.
/// </summary>
public static class MouseMotion
{
    /// <summary>Time between cursor updates (~120 Hz).</summary>
    public static readonly TimeSpan DefaultTick = TimeSpan.FromMilliseconds(8);

    /// <summary>Moves shorter than this (in pixels, Manhattan distance) just jump.</summary>
    public const int MinGlideDistance = 3;

    /// <summary>How far a Human curve bends sideways, as a share of the distance.</summary>
    public const double CurveBend = 0.12;

    /// <summary>The most a Human curve bends sideways, in pixels.</summary>
    public const double MaxCurveBendPixels = 60;

    /// <summary>
    /// The points to visit, oldest first. <paramref name="duration"/> ≤ 0, <see cref="MouseMoveStyle.Instant"/> or a
    /// tiny distance give a single point (the target) at time 0.
    /// <paramref name="glideArcPixels"/> (Humanize): a Smooth glide bows sideways by up to this many pixels in the middle
    /// (+ = to the left of the direction of travel, − = to the right), like a hand that doesn't move in a perfect line.
    /// 0 = a straight line (the default). Start and end stay exact.
    /// </summary>
    public static IReadOnlyList<MotionPoint> Plan((int X, int Y) from, (int X, int Y) to, MouseMoveStyle style, TimeSpan duration,
        TimeSpan? tick = null, double glideArcPixels = 0)
    {
        var step = tick is { } t && t > TimeSpan.Zero ? t : DefaultTick;
        var distance = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);
        if (style == MouseMoveStyle.Instant || duration <= TimeSpan.Zero || distance < MinGlideDistance)
            return new[] { new MotionPoint(TimeSpan.Zero, to.X, to.Y) };

        var count = Math.Max(1, (int)Math.Round(duration / step));
        var points = new List<MotionPoint>(count);

        // Human curve: control points a third and two thirds along the line, pushed sideways (the same side, the
        // second one less, so the arc leans a little towards the start like a real wrist movement).
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var bend = Math.Min(length * CurveBend, MaxCurveBendPixels) * CurveSide(from, to);
        var nx = length > 0 ? -dy / length : 0;
        var ny = length > 0 ? dx / length : 0;
        var c1 = (X: from.X + dx * 0.3 + nx * bend, Y: from.Y + dy * 0.3 + ny * bend);
        var c2 = (X: from.X + dx * 0.7 + nx * bend * 0.6, Y: from.Y + dy * 0.7 + ny * bend * 0.6);

        for (var i = 1; i <= count; i++)
        {
            var at = TimeSpan.FromTicks(duration.Ticks * i / count);
            if (i == count)
            {
                points.Add(new MotionPoint(duration, to.X, to.Y)); // always end exactly on the target
                break;
            }

            var p = i / (double)count;
            double x, y;
            if (style == MouseMoveStyle.HumanCurve)
            {
                var e = SmootherStep(p);
                var u = 1 - e;
                x = u * u * u * from.X + 3 * u * u * e * c1.X + 3 * u * e * e * c2.X + e * e * e * to.X;
                y = u * u * u * from.Y + 3 * u * u * e * c1.Y + 3 * u * e * e * c2.Y + e * e * e * to.Y;
            }
            else
            {
                var e = SmoothStep(p);
                var bow = glideArcPixels * 4 * e * (1 - e); // 0 at both ends, the full arc halfway
                x = from.X + dx * e + nx * bow;
                y = from.Y + dy * e + ny * bow;
            }

            points.Add(new MotionPoint(at, (int)Math.Round(x), (int)Math.Round(y)));
        }

        return points;
    }

    /// <summary>Gentle start and stop: 3t² − 2t³.</summary>
    public static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Softer start and stop than <see cref="SmoothStep"/>: 6t⁵ − 15t⁴ + 10t³.</summary>
    public static double SmootherStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>Which way the curve bends (+1 / −1). Fixed for a given start and end, so playback is repeatable.</summary>
    private static int CurveSide((int X, int Y) from, (int X, int Y) to) =>
        ((from.X + from.Y * 3 + to.X * 7 + to.Y * 11) & 1) == 0 ? 1 : -1;
}
