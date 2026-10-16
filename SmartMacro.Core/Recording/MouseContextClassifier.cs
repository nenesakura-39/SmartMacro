using SmartMacro.Core.Models;

namespace SmartMacro.Core.Recording;

/// <summary>What the recorder saw about the cursor when a mouse event arrived.</summary>
[Flags]
public enum CursorFlags : byte
{
    None = 0,

    /// <summary>The cursor was hidden (no cursor shown, or the app set an empty cursor).</summary>
    Hidden = 1,

    /// <summary>The cursor was confined (ClipCursor) to a tiny area, which is how some games lock it.</summary>
    ConfinedToSpot = 2,

    /// <summary>
    /// The cursor didn't move and sits on the edge of its monitor / allowed area, so it simply couldn't follow
    /// the mouse. Movement there says nothing about whether a game locked the cursor.
    /// </summary>
    AtEdge = 4,
}

/// <summary>One recorded event plus the cursor state at that moment (smart recording).</summary>
public readonly record struct CapturedInputEvent(InputEvent Input, CursorFlags Cursor = CursorFlags.None);

/// <summary>Tuning for <see cref="MouseContextClassifier"/>. The defaults are what the app uses.</summary>
public sealed class MouseContextOptions
{
    /// <summary>How much recent movement is looked at to decide (microseconds).</summary>
    public int WindowUs { get; init; } = 150_000;

    /// <summary>Upper bound on mouse samples in one window (keeps the work per sample small at high polling rates).</summary>
    public int MaxWindowSamples { get; init; } = 200;

    /// <summary>Minimum net relative movement (mouse counts) in the window before deciding anything.</summary>
    public int MinRawMotion { get; init; } = 20;

    /// <summary>
    /// "Locked": the cursor stayed within LockedSlackPixels + LockedRatio × the relative movement.
    /// Games that keep snapping the cursor back to one spot still pass this, because the cursor never gets far.
    /// </summary>
    public int LockedSlackPixels { get; init; } = 3;

    public double LockedRatio { get; init; } = 0.12;

    /// <summary>"Free": the cursor covered at least FreeRatio × the relative movement (pointer speed can be below 1).</summary>
    public double FreeRatio { get; init; } = 0.25;

    /// <summary>Parts shorter than this are merged into a neighbour (e.g. the cursor reappearing after typing).</summary>
    public int MinSegmentUs { get; init; } = 60_000;
}

/// <summary>
/// Decides, for each part of a smart recording, whether the mouse was moving a visible cursor (play cursor
/// positions) or turning a camera while a game hid / locked the cursor (play relative movement).
///
/// Signals, strongest first:
/// 1. The cursor was hidden → relative.
/// 2. The cursor was confined to a tiny area → relative.
/// 3. In the ~150 ms before and the ~150 ms after a sample, the mouse reported real movement but the cursor
///    stayed in one small spot (it was locked, or kept being snapped back) → relative. If the cursor travelled a
///    fair share of the movement → cursor positions. When the two windows disagree (right at a change), the
///    sample's own movement decides: the mouse moved but the cursor didn't → relative, otherwise cursor.
///    Movement while the cursor is pressed against a screen edge is ignored.
/// Anything unclear keeps the mode of the movement before it. Very short parts are merged into a neighbour.
/// Clicks with a visible cursor right after camera movement (or before any movement) are put in a cursor part,
/// so playback moves the cursor to the recorded spot before clicking.
///
/// This is a heuristic. It can be wrong (for example with a very low pointer speed, or a game that locks the cursor
/// in an unusual way), which is why the editor lets the user override each part.
/// Holding a mouse button is NOT treated as a signal on its own: right-dragging can just as well be a selection.
/// </summary>
public static class MouseContextClassifier
{
    /// <summary>
    /// Returns segments sorted by start time (absolute times, same clock as the events). The first segment starts at
    /// the first event. Empty only when there are no events.
    /// </summary>
    public static List<MouseSegment> Classify(IReadOnlyList<CapturedInputEvent> events, MouseContextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        options ??= new MouseContextOptions();
        var segments = new List<MouseSegment>();
        if (events.Count == 0) return segments;

        var origin = events[0].Input.TimeUs;
        var moves = new List<int>();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Input.Kind == InputEventKind.MouseMove) moves.Add(i);
        }

        if (moves.Count == 0)
        {
            segments.Add(new MouseSegment { StartUs = origin, Detected = MouseMovementMode.CursorPosition, Evidence = MouseContextEvidence.Unclear });
            return segments;
        }

        var labels = LabelMoves(events, moves, options, out var evidence);
        return BuildSegments(events, moves, labels, evidence, origin, options);
    }

    private static MouseMovementMode[] LabelMoves(IReadOnlyList<CapturedInputEvent> events, List<int> moves, MouseContextOptions options,
        out MouseContextEvidence[] evidence)
    {
        var n = moves.Count;
        var labels = new MouseMovementMode?[n];
        evidence = new MouseContextEvidence[n];

        // Running totals of the relative movement, so a window's net movement is one subtraction.
        var sumDx = new long[n + 1];
        var sumDy = new long[n + 1];
        for (var k = 0; k < n; k++)
        {
            var e = events[moves[k]].Input;
            sumDx[k + 1] = sumDx[k] + e.Dx;
            sumDy[k + 1] = sumDy[k] + e.Dy;
        }

        var back = 0;    // first sample of the window ending at i
        var forward = 0; // last sample of the window starting at i
        for (var i = 0; i < n; i++)
        {
            var current = events[moves[i]];
            var time = current.Input.TimeUs;
            while (back < i && (time - events[moves[back]].Input.TimeUs > options.WindowUs || i - back > options.MaxWindowSamples))
                back++;
            if (forward < i) forward = i;
            while (forward + 1 < n && events[moves[forward + 1]].Input.TimeUs - time <= options.WindowUs && forward + 1 - i <= options.MaxWindowSamples)
                forward++;

            // 1. Strong signals for this sample alone.
            if ((current.Cursor & CursorFlags.Hidden) != 0)
            {
                labels[i] = MouseMovementMode.Relative;
                evidence[i] = MouseContextEvidence.HiddenCursor;
                continue;
            }

            if ((current.Cursor & CursorFlags.ConfinedToSpot) != 0)
            {
                labels[i] = MouseMovementMode.Relative;
                evidence[i] = MouseContextEvidence.ConfinedCursor;
                continue;
            }

            // 2. The movement just before and just after this sample. Both agreeing is the normal case. Near a change
            //    (cursor starts / stops being locked) they disagree, and this sample's own movement decides, so a
            //    camera turn never loses its first or last moments to the free-cursor part next to it.
            var before = Judge(events, moves, sumDx, sumDy, back, i, options);
            var after = Judge(events, moves, sumDx, sumDy, i, forward, options);
            var own = JudgeSample(events, moves, i);

            Verdict? verdict;
            if (before is { } b && after is { } a && b.Mode == a.Mode) verdict = b;
            else if (own is { } o) verdict = o;
            else verdict = before ?? after;

            if (verdict is { } v)
            {
                labels[i] = v.Mode;
                evidence[i] = v.Evidence;
            }
        }

        // Unclear samples keep the mode before them (or the first known mode at the very start).
        MouseMovementMode? firstKnown = null;
        foreach (var label in labels)
        {
            if (label is { } known)
            {
                firstKnown = known;
                break;
            }
        }

        var fill = firstKnown ?? MouseMovementMode.CursorPosition;
        var result = new MouseMovementMode[n];
        for (var k = 0; k < n; k++)
        {
            if (labels[k] is { } known) fill = known;
            result[k] = fill;
        }

        return result;
    }

    private readonly record struct Verdict(MouseMovementMode Mode, MouseContextEvidence Evidence);

    /// <summary>Judges the samples first..last (needs at least two, real movement, and usable positions).</summary>
    private static Verdict? Judge(IReadOnlyList<CapturedInputEvent> events, List<int> moves, long[] sumDx, long[] sumDy,
        int first, int last, MouseContextOptions options)
    {
        if (last <= first) return null;

        // Net relative movement after the first sample (whose position is the starting point).
        double rawX = sumDx[last + 1] - sumDx[first + 1];
        double rawY = sumDy[last + 1] - sumDy[first + 1];
        var raw = Math.Sqrt(rawX * rawX + rawY * rawY);
        if (raw < options.MinRawMotion) return null;

        if (!TryGetExtent(events, moves, first, last, out var extent)) return null;

        if (extent <= options.LockedSlackPixels + options.LockedRatio * raw)
            return new Verdict(MouseMovementMode.Relative, MouseContextEvidence.LockedCursor);
        if (extent >= options.FreeRatio * raw)
            return new Verdict(MouseMovementMode.CursorPosition, MouseContextEvidence.FreeCursor);
        return null;
    }

    /// <summary>
    /// One sample on its own: the mouse moved but the cursor didn't (locked), or the cursor moved (free).
    /// Unknown at the start, without positions, or when the cursor is stuck on an edge.
    /// </summary>
    private static Verdict? JudgeSample(IReadOnlyList<CapturedInputEvent> events, List<int> moves, int i)
    {
        if (i == 0) return null;
        var previous = events[moves[i - 1]];
        var current = events[moves[i]];
        if (!previous.Input.HasPosition || !current.Input.HasPosition) return null;
        if (previous.Cursor != CursorFlags.None || current.Cursor != CursorFlags.None) return null;
        if (current.Input.Dx == 0 && current.Input.Dy == 0) return null;

        var cursorMoved = previous.Input.X != current.Input.X || previous.Input.Y != current.Input.Y;
        return cursorMoved
            ? new Verdict(MouseMovementMode.CursorPosition, MouseContextEvidence.FreeCursor)
            : new Verdict(MouseMovementMode.Relative, MouseContextEvidence.LockedCursor);
    }

    /// <summary>
    /// Size (diagonal of the bounding box, in pixels) of the area the cursor covered in the window. False when the
    /// window can't be judged: a sample has no position, was hidden / confined, or the cursor was stuck on an edge.
    /// </summary>
    private static bool TryGetExtent(IReadOnlyList<CapturedInputEvent> events, List<int> moves, int start, int end, out double extent)
    {
        extent = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var k = start; k <= end; k++)
        {
            var c = events[moves[k]];
            if (!c.Input.HasPosition || c.Cursor != CursorFlags.None) return false;
            minX = Math.Min(minX, c.Input.X);
            maxX = Math.Max(maxX, c.Input.X);
            minY = Math.Min(minY, c.Input.Y);
            maxY = Math.Max(maxY, c.Input.Y);
        }

        double w = (long)maxX - minX;
        double h = (long)maxY - minY;
        extent = Math.Sqrt(w * w + h * h);
        return true;
    }

    private static List<MouseSegment> BuildSegments(IReadOnlyList<CapturedInputEvent> events, List<int> moves, MouseMovementMode[] labels,
        MouseContextEvidence[] evidence, long origin, MouseContextOptions options)
    {
        long MoveTime(int k) => events[moves[k]].Input.TimeUs;

        // 1. Runs of equal labels.
        var runs = new List<Run>();
        for (var k = 0; k < labels.Length; k++)
        {
            if (runs.Count > 0 && runs[^1].Mode == labels[k])
            {
                runs[^1] = runs[^1] with { LastMove = k };
                continue;
            }

            runs.Add(new Run(MoveTime(k), labels[k], k, k));
        }

        // 2. Parts that are too short to be real (measured by their movement) join the part before them.
        var lastTime = events[^1].Input.TimeUs;
        var merged = new List<Run>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var end = r + 1 < runs.Count ? runs[r + 1].StartUs : lastTime;
            if (merged.Count > 0 && (merged[^1].Mode == run.Mode || end - run.StartUs < options.MinSegmentUs || run.StartUs <= merged[^1].StartUs))
            {
                merged[^1] = merged[^1] with { LastMove = run.LastMove };
                continue;
            }

            merged.Add(run);
        }

        // 3. A too-short first part (e.g. the cursor still hidden from typing when the mouse first moves) joins the next one.
        if (merged.Count > 1 && merged[1].StartUs - merged[0].StartUs < options.MinSegmentUs)
        {
            merged[1] = merged[1] with { StartUs = merged[0].StartUs, FirstMove = merged[0].FirstMove };
            merged.RemoveAt(0);
        }

        // 4. Presses / wheel with a normal visible cursor right after camera movement (before the cursor moves again)
        //    belong to the cursor: playback then puts the cursor on the recorded spot first. Same for clicks before the
        //    very first movement. Keys don't depend on the mouse mode, so they don't move any boundary.
        var segments = new List<MouseSegment>(merged.Count + 2);
        var leadingCursorStart = merged[0].Mode == MouseMovementMode.Relative
            ? FirstVisibleCursorEvent(events, 0, moves[merged[0].FirstMove])
            : null;
        if (leadingCursorStart is not null && merged[0].StartUs > origin)
            segments.Add(new MouseSegment { StartUs = origin, Detected = MouseMovementMode.CursorPosition, Evidence = MouseContextEvidence.Unclear });

        for (var r = 0; r < merged.Count; r++)
        {
            var run = merged[r];
            var start = segments.Count == 0 ? origin : run.StartUs;
            if (run.Mode == MouseMovementMode.CursorPosition && r > 0)
            {
                // Pull the start back to the first visible-cursor click after the previous (camera) part's movement.
                var previousRun = merged[r - 1];
                var earliest = FirstVisibleCursorEvent(events, moves[previousRun.LastMove] + 1, moves[run.FirstMove]);
                if (earliest is long t && t > segments[^1].StartUs && t < start) start = t;
            }

            if (segments.Count > 0 && start <= segments[^1].StartUs) start = segments[^1].StartUs + 1;
            segments.Add(new MouseSegment
            {
                StartUs = start,
                Detected = run.Mode,
                Evidence = MainEvidence(evidence, run.FirstMove, run.LastMove, run.Mode),
            });
        }

        // Clicks after the last camera movement, with a visible cursor again, get a cursor part of their own.
        if (merged[^1].Mode == MouseMovementMode.Relative)
        {
            var trailing = FirstVisibleCursorEvent(events, moves[merged[^1].LastMove] + 1, events.Count);
            if (trailing is long t && t > segments[^1].StartUs)
                segments.Add(new MouseSegment { StartUs = t, Detected = MouseMovementMode.CursorPosition, Evidence = MouseContextEvidence.FreeCursor });
        }

        return segments;
    }

    /// <summary>
    /// Time of the first button press or wheel event in [from, to) whose cursor was visible and not confined, or null.
    /// Releases don't count: the release that ends a camera drag must stay in the camera part, because moving the
    /// cursor while the game still has the button down could turn the camera.
    /// </summary>
    private static long? FirstVisibleCursorEvent(IReadOnlyList<CapturedInputEvent> events, int from, int to)
    {
        for (var i = from; i < to && i < events.Count; i++)
        {
            var c = events[i];
            if (c.Input.Kind is not (InputEventKind.MouseDown or InputEventKind.Wheel or InputEventKind.HorizontalWheel)) continue;
            if (!c.Input.HasPosition) continue;
            if ((c.Cursor & (CursorFlags.Hidden | CursorFlags.ConfinedToSpot)) != 0) continue;
            return c.Input.TimeUs;
        }

        return null;
    }

    /// <summary>The most common reason inside a part that agrees with its mode (Unclear if none).</summary>
    private static MouseContextEvidence MainEvidence(MouseContextEvidence[] evidence, int first, int last, MouseMovementMode mode)
    {
        var counts = new int[Enum.GetValues<MouseContextEvidence>().Length];
        for (var k = first; k <= last; k++) counts[(int)evidence[k]]++;

        var best = MouseContextEvidence.Unclear;
        var bestCount = 0;
        foreach (var candidate in Enum.GetValues<MouseContextEvidence>())
        {
            if (candidate == MouseContextEvidence.Unclear || ModeOf(candidate) != mode) continue;
            if (counts[(int)candidate] > bestCount)
            {
                best = candidate;
                bestCount = counts[(int)candidate];
            }
        }

        return best;
    }

    private static MouseMovementMode ModeOf(MouseContextEvidence evidence) =>
        evidence == MouseContextEvidence.FreeCursor ? MouseMovementMode.CursorPosition : MouseMovementMode.Relative;

    private readonly record struct Run(long StartUs, MouseMovementMode Mode, int FirstMove, int LastMove);
}
