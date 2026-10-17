using SmartMacro.Core.Models;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Reduces recorded cursor movement to the points that matter for playback, so a smart / desktop recording doesn't
/// flood the editor with hundreds of "Wait 32 ms" + "Move mouse" steps.
///
/// Works on each stretch of consecutive mouse moves (a stretch ends at any click, key or wheel event). Always kept:
/// - the first and last move of the stretch (so the exact spot before a press / release is never lost),
/// - every direction reversal along X or Y (the extreme point of a slider dragged to 80 % and back to 50 %),
/// - every spot the cursor rested at for <c>restMs</c> or more,
/// - whatever Ramer–Douglas–Peucker needs between those anchors to stay within <c>tolerancePx</c> of the real path.
/// Everything else is dropped. Times of kept points don't change, so the Delay steps keep the original timing.
///
/// Drags (a mouse button is held during the stretch) are NOT simplified aggressively: sliders, scrollbars and
/// drag-and-drop follow the exact path, so there the tolerance is at most <see cref="DragTolerancePx"/> and a point
/// is kept at least every <see cref="DragKeepEveryMs"/> (playback glides between them).
/// </summary>
public static class MousePathSimplifier
{
    /// <summary>Largest path tolerance used while a button is held (pixels).</summary>
    public const int DragTolerancePx = 1;

    /// <summary>While a button is held, a point is kept at least this often (ms), so drags never turn into jumps.</summary>
    public const int DragKeepEveryMs = 50;

    public static List<RawInputEvent> Simplify(List<RawInputEvent> events, int tolerancePx, int restMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        var keep = new bool[events.Count];
        var held = new HashSet<MacroMouseButton>();
        var i = 0;
        while (i < events.Count)
        {
            var e = events[i];
            if (e.Kind != RawInputKind.MouseMove)
            {
                if (e.Kind == RawInputKind.MouseDown) held.Add(e.Button);
                else if (e.Kind == RawInputKind.MouseUp) held.Remove(e.Button);
                keep[i] = true;
                i++;
                continue;
            }

            var start = i;
            while (i < events.Count && events[i].Kind == RawInputKind.MouseMove) i++;
            var dragging = held.Count > 0;
            var tolerance = Math.Max(0, tolerancePx);
            MarkStretch(events, start, i - 1, keep, dragging ? Math.Min(tolerance, DragTolerancePx) : tolerance, restMs,
                dragging ? DragKeepEveryMs : 0);
        }

        var result = new List<RawInputEvent>(events.Count);
        for (var k = 0; k < events.Count; k++)
        {
            if (keep[k]) result.Add(events[k]);
        }

        return result;
    }

    private static void MarkStretch(List<RawInputEvent> events, int first, int last, bool[] keep, int tolerance, int restMs,
        int keepEveryMs)
    {
        var anchors = new SortedSet<int> { first, last };

        // Drags: never leave a gap longer than keepEveryMs between kept points.
        if (keepEveryMs > 0)
        {
            var lastKeptTime = events[first].TimeMs;
            for (var k = first + 1; k <= last; k++)
            {
                if (events[k].TimeMs - lastKeptTime < keepEveryMs) continue;
                anchors.Add(k);
                lastKeptTime = events[k].TimeMs;
            }
        }

        // Direction reversals per axis. Hand jitter of a pixel or two isn't a reversal: the cursor must come back
        // by more than the tolerance from the extreme point before that point counts.
        AddReversals(events, first, last, tolerance, e => e.X, anchors);
        AddReversals(events, first, last, tolerance, e => e.Y, anchors);

        // Spots the cursor rested at before moving on.
        for (var k = first + 1; k <= last; k++)
        {
            if (events[k].TimeMs - events[k - 1].TimeMs >= restMs) anchors.Add(k - 1);
        }

        var ordered = anchors.ToList();
        foreach (var index in ordered) keep[index] = true;
        for (var a = 0; a + 1 < ordered.Count; a++)
            DouglasPeucker(events, ordered[a], ordered[a + 1], keep, tolerance);
    }

    private static void AddReversals(List<RawInputEvent> events, int first, int last, int tolerance,
        Func<RawInputEvent, int> coordinate, SortedSet<int> anchors)
    {
        var direction = 0;
        var extreme = first;
        for (var k = first + 1; k <= last; k++)
        {
            var value = coordinate(events[k]);
            var extremeValue = coordinate(events[extreme]);
            if (direction == 0)
            {
                if (Math.Abs(value - extremeValue) > tolerance)
                {
                    direction = Math.Sign(value - extremeValue);
                    extreme = k;
                }
                continue; // still within the start's jitter
            }

            if ((direction > 0 && value >= extremeValue) || (direction < 0 && value <= extremeValue))
            {
                extreme = k; // still going the same way: new extreme
            }
            else if (Math.Abs(extremeValue - value) > tolerance)
            {
                anchors.Add(extreme); // turned around: the extreme point must be played exactly
                direction = -direction;
                extreme = k;
            }
        }
    }

    /// <summary>Keeps the points needed between two kept points so the path stays within the tolerance.</summary>
    private static void DouglasPeucker(List<RawInputEvent> events, int from, int to, bool[] keep, int tolerance)
    {
        var stack = new Stack<(int From, int To)>();
        stack.Push((from, to));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;

            double ax = events[a].X, ay = events[a].Y, bx = events[b].X, by = events[b].Y;
            var lengthSq = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
            var worst = -1.0;
            var worstIndex = -1;
            for (var k = a + 1; k < b; k++)
            {
                double px = events[k].X, py = events[k].Y;
                double distance;
                if (lengthSq < 1e-9)
                {
                    distance = Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
                }
                else
                {
                    var t = Math.Clamp(((px - ax) * (bx - ax) + (py - ay) * (by - ay)) / lengthSq, 0, 1);
                    var cx = ax + t * (bx - ax);
                    var cy = ay + t * (by - ay);
                    distance = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                }

                if (distance > worst)
                {
                    worst = distance;
                    worstIndex = k;
                }
            }

            if (worst <= tolerance) continue;
            keep[worstIndex] = true;
            stack.Push((a, worstIndex));
            stack.Push((worstIndex, b));
        }
    }
}
