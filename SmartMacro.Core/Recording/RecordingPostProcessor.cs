using SmartMacro.Core.Models;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Summary metrics describing how a raw input recording was cleaned, deduplicated, and structured into workflow steps.
/// </summary>
public sealed record RecordingConversionMetrics(
    int RawEventCount,
    int StationaryDuplicatesRemoved,
    int OutputStepCount,
    int DragGestureCount,
    int CoalescedKeystrokeCount)
{
    public double NoiseReductionPercent =>
        RawEventCount <= 0 ? 0.0 : Math.Clamp((1.0 - (double)OutputStepCount / RawEventCount) * 100.0, 0.0, 99.9);

    public string FormatSummary()
    {
        var parts = new List<string>();
        if (DragGestureCount > 0)
            parts.Add($"{DragGestureCount} drag{(DragGestureCount == 1 ? "" : "s")}");
        if (CoalescedKeystrokeCount > 0)
            parts.Add($"{CoalescedKeystrokeCount} text block{(CoalescedKeystrokeCount == 1 ? "" : "s")}");
        if (StationaryDuplicatesRemoved > 0)
            parts.Add($"{StationaryDuplicatesRemoved} stationary duplicate{(StationaryDuplicatesRemoved == 1 ? "" : "s")} filtered");

        var details = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : string.Empty;
        return $"Recorded {OutputStepCount:N0} step{(OutputStepCount == 1 ? "" : "s")} from {RawEventCount:N0} raw event{(RawEventCount == 1 ? "" : "s")}{details}.";
    }
}

/// <summary>
/// Result of structuring recorded <c>Down → Moves → Up</c> sequences into either single <see cref="MouseClickAction.Drag"/>
/// steps (for linear drags) or collapsible <see cref="BeginGroupStep"/> / <see cref="EndGroupStep"/> sections (for multi-waypoint drags).
/// </summary>
public sealed record DragStructureResult(
    List<MacroStep> Steps,
    int CoalescedDrags,
    int GroupedDrags,
    int RemovedSteps);

/// <summary>
/// Post-processing and zero-loss hygiene helpers for recorded input streams and workflow steps.
/// </summary>
public static class RecordingPostProcessor
{
    /// <summary>
    /// Removes consecutive duplicate <see cref="RawInputKind.MouseMove"/> events that report the exact same screen
    /// position <c>(X, Y)</c> and zero relative delta <c>(DeltaX, DeltaY) == (0, 0)</c> as the immediately preceding
    /// <see cref="RawInputKind.MouseMove"/> event, preserving all timestamps of remaining events so inter-event delays
    /// stay 100% identical.
    /// </summary>
    public static List<RawInputEvent> DeduplicateStationaryMoves(IReadOnlyList<RawInputEvent> events, out int removedCount)
    {
        removedCount = 0;
        if (events is null || events.Count == 0)
            return new List<RawInputEvent>();

        var result = new List<RawInputEvent>(events.Count);
        (int X, int Y)? lastMovePos = null;

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Kind == RawInputKind.MouseMove)
            {
                if (lastMovePos == (e.X, e.Y))
                {
                    removedCount++;
                    continue;
                }

                lastMovePos = (e.X, e.Y);
                result.Add(e);
            }
            else
            {
                // Keep exact pre-click/post-click anchor behavior around non-move events.
                if (e.Kind is RawInputKind.MouseDown or RawInputKind.MouseUp)
                    lastMovePos = (e.X, e.Y);
                else
                    lastMovePos = null;

                result.Add(e);
            }
        }

        return result;
    }

    /// <summary>
    /// Structures recorded <c>MouseClick(Down) → Delay/MouseMove… → MouseClick(Up)</c> drag sequences:
    /// <list type="bullet">
    /// <item>When <paramref name="coalesceLinearDrags"/> is true and all intermediate points stay within
    /// <paramref name="linearTolerancePx"/> of the straight line from start to end (moving monotonically without reversing),
    /// collapses the sequence into a single <see cref="MouseClickAction.Drag"/> step with <see cref="MouseMoveStyle.SmoothGlide"/>
    /// and transfers the <c>Down</c> step's Smart Intent / Smart-Aim snapshot.</item>
    /// <item>When <paramref name="groupMultiPointDrags"/> is true and the drag follows a multi-waypoint or oscillating path
    /// (such as dragging a window left and right), wraps the <c>Down … Up</c> sequence in a collapsible
    /// <see cref="BeginGroupStep"/> / <see cref="EndGroupStep"/> block so the canvas stays clean while every waypoint remains editable.</item>
    /// </list>
    /// </summary>
    public static DragStructureResult StructureRecordedDrags(
        IReadOnlyList<MacroStep> steps,
        bool coalesceLinearDrags = true,
        bool groupMultiPointDrags = true,
        double linearTolerancePx = 6.0)
    {
        return StructureRecordedDrags(steps, 0, steps.Count - 1, coalesceLinearDrags, groupMultiPointDrags, linearTolerancePx);
    }

    /// <inheritdoc cref="StructureRecordedDrags(IReadOnlyList{MacroStep}, bool, bool, double)"/>
    public static DragStructureResult StructureRecordedDrags(
        IReadOnlyList<MacroStep> steps,
        int firstIndex,
        int lastIndex,
        bool coalesceLinearDrags = true,
        bool groupMultiPointDrags = true,
        double linearTolerancePx = 6.0)
    {
        if (steps is null || steps.Count == 0 || (!coalesceLinearDrags && !groupMultiPointDrags))
            return new DragStructureResult(steps?.ToList() ?? new List<MacroStep>(), 0, 0, 0);

        firstIndex = Math.Clamp(firstIndex, 0, steps.Count - 1);
        lastIndex = Math.Clamp(lastIndex, firstIndex, steps.Count - 1);

        var output = new List<MacroStep>(steps.Count);
        var coalesced = 0;
        var grouped = 0;
        var removed = 0;

        for (var i = 0; i < firstIndex; i++)
            output.Add(steps[i]);

        var idx = firstIndex;
        while (idx <= lastIndex)
        {
            if (steps[idx] is MouseClickStep
                {
                    Action: MouseClickAction.Down,
                    Enabled: true,
                    UseCurrentPosition: false,
                    UseLastFoundText: false
                } down
                && TryFindMatchingDragSpan(steps, idx, lastIndex, down.Button, out var upIdx, out var moveSteps, out var totalDelayMs))
            {
                var up = (MouseClickStep)steps[upIdx];
                var isLinear = IsMonotonicLinearDrag(down.X, down.Y, up.X, up.Y, moveSteps, linearTolerancePx);

                if (isLinear && coalesceLinearDrags && string.IsNullOrWhiteSpace(down.Comment) && string.IsNullOrWhiteSpace(up.Comment))
                {
                    var dragStep = new MouseClickStep
                    {
                        Button = down.Button,
                        Action = MouseClickAction.Drag,
                        X = down.X,
                        Y = down.Y,
                        EndX = up.X,
                        EndY = up.Y,
                        DragDurationMs = Math.Clamp(totalDelayMs > 0 ? totalDelayMs : 250, 50, 60_000),
                        MoveStyle = MouseMoveStyle.SmoothGlide,
                        OffsetX = down.OffsetX,
                        OffsetY = down.OffsetY,
                        TargetThumbnailBase64 = down.TargetThumbnailBase64,
                        SnapshotAssetId = down.SnapshotAssetId,
                        TargetAnchorX = down.TargetAnchorX,
                        TargetAnchorY = down.TargetAnchorY,
                        SmartAimAtTarget = down.SmartAimAtTarget,
                        SmartAimAccuracy = down.SmartAimAccuracy,
                        SmartAimRadiusPx = down.SmartAimRadiusPx,
                        Target = down.Target?.Clone(),
                        UseSmartIntent = down.UseSmartIntent,
                    };
                    output.Add(dragStep);
                    coalesced++;
                    removed += (upIdx - idx);
                    idx = upIdx + 1;
                    continue;
                }

                // Multi-waypoint or curved drag: wrap in a collapsible BeginGroupStep / EndGroupStep unless already wrapped.
                var alreadyGrouped = idx > 0
                    && steps[idx - 1] is BeginGroupStep
                    && upIdx + 1 < steps.Count
                    && steps[upIdx + 1] is EndGroupStep;

                if (groupMultiPointDrags && !alreadyGrouped)
                {
                    output.Add(new BeginGroupStep
                    {
                        Title = $"Drag {down.Button} ({down.X}, {down.Y}) → ({up.X}, {up.Y})",
                        ColorHex = "#38BDF8",
                    });
                    for (var k = idx; k <= upIdx; k++)
                        output.Add(steps[k]);
                    output.Add(new EndGroupStep());
                    grouped++;
                    idx = upIdx + 1;
                    continue;
                }
            }

            output.Add(steps[idx]);
            idx++;
        }

        for (var i = lastIndex + 1; i < steps.Count; i++)
            output.Add(steps[i]);

        return new DragStructureResult(output, coalesced, grouped, removed);
    }

    /// <summary>
    /// Computes conversion telemetry comparing raw input events with the resulting macro workflow steps.
    /// </summary>
    public static RecordingConversionMetrics AnalyzeConversion(
        IReadOnlyList<RawInputEvent> rawEvents,
        IReadOnlyList<MacroStep> steps)
    {
        var rawCount = rawEvents?.Count ?? 0;
        var stationaryRemoved = 0;
        if (rawEvents is { Count: > 1 })
        {
            _ = DeduplicateStationaryMoves(rawEvents, out stationaryRemoved);
        }

        var stepCount = steps?.Count ?? 0;
        var dragCount = 0;
        var typeCount = 0;

        if (steps is not null)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                switch (steps[i])
                {
                    case MouseClickStep { Action: MouseClickAction.Drag or MouseClickAction.Down }:
                        dragCount++;
                        break;
                    case TypeTextStep:
                        typeCount++;
                        break;
                }
            }
        }

        return new RecordingConversionMetrics(
            RawEventCount: rawCount,
            StationaryDuplicatesRemoved: stationaryRemoved,
            OutputStepCount: stepCount,
            DragGestureCount: dragCount,
            CoalescedKeystrokeCount: typeCount);
    }

    private static bool TryFindMatchingDragSpan(
        IReadOnlyList<MacroStep> steps,
        int downIdx,
        int maxIdx,
        MacroMouseButton button,
        out int upIdx,
        out List<MouseMoveStep> moveSteps,
        out int totalDelayMs)
    {
        upIdx = -1;
        moveSteps = new List<MouseMoveStep>();
        totalDelayMs = 0;

        for (var i = downIdx + 1; i <= maxIdx; i++)
        {
            var step = steps[i];
            if (!step.Enabled || !string.IsNullOrWhiteSpace(step.Comment))
                return false;

            switch (step)
            {
                case DelayStep { Mode: DelayMode.Fixed } d:
                    totalDelayMs += Math.Max(0, d.DurationMs);
                    break;

                case MouseMoveStep { UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates } m:
                    moveSteps.Add(m);
                    break;

                case MouseClickStep
                {
                    Action: MouseClickAction.Up,
                    UseCurrentPosition: false,
                    UseLastFoundText: false
                } up when up.Button == button:
                    if (moveSteps.Count == 0)
                        return false;
                    upIdx = i;
                    return true;

                default:
                    return false;
            }
        }

        return false;
    }

    private static bool IsMonotonicLinearDrag(
        int startX,
        int startY,
        int endX,
        int endY,
        IReadOnlyList<MouseMoveStep> moves,
        double tolerancePx)
    {
        var dx = (double)(endX - startX);
        var dy = (double)(endY - startY);
        var segLenSq = dx * dx + dy * dy;
        var segLen = Math.Sqrt(segLenSq);

        // A drag whose start and end are nearly identical (e.g. moving Notepad left and then back right to the start)
        // is an oscillating path, never a straight linear drag.
        if (segLen < 8.0)
            return false;

        var prevProj = 0.0;
        for (var i = 0; i < moves.Count; i++)
        {
            var px = (double)(moves[i].X - startX);
            var py = (double)(moves[i].Y - startY);

            // Perpendicular distance from point to segment line.
            var cross = Math.Abs(px * dy - py * dx) / segLen;
            if (cross > tolerancePx)
                return false;

            // Projection along the segment vector [0 .. segLen]; must not reverse direction by more than tolerancePx.
            var proj = (px * dx + py * dy) / segLen;
            if (proj < -tolerancePx || proj > segLen + tolerancePx || proj + tolerancePx < prevProj)
                return false;

            if (proj > prevProj)
                prevProj = proj;
        }

        return true;
    }
}
