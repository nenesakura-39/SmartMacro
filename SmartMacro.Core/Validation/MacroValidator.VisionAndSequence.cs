using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Validation;

public static partial class MacroValidator
{
    /// <summary>"Offset X / Y" of a "last found spot" (or "move by") target.</summary>
    private static void CheckOffset(List<ValidationIssue> issues, int offsetX, int offsetY)
    {
        if (Math.Abs((long)offsetX) > MaxOffset || Math.Abs((long)offsetY) > MaxOffset)
            issues.Add(Error($"Offsets must be between -{MaxOffset} and {MaxOffset} pixels.", "Offset"));
    }

    /// <summary>
    /// Feedback loop (Stage 4): a bar area on screen, a valid marker colour, a zone, rules that press something,
    /// a key or button, rate limits, a required max runtime, a lost-target policy and optional stop conditions.
    /// Every number has a range, so a damaged or hand-edited file can't make the loop run wild.
    /// </summary>
    private static void ValidateFeedbackLoop(List<ValidationIssue> issues, FeedbackLoopStep f, ScreenBounds? screen)
    {
        if (!Enum.IsDefined(f.Orientation) || !Enum.IsDefined(f.ZoneMode) || !Enum.IsDefined(f.InputKind)
            || !Enum.IsDefined(f.WhenBeforeZone) || !Enum.IsDefined(f.WhenInsideZone) || !Enum.IsDefined(f.WhenAfterZone)
            || !Enum.IsDefined(f.WhenTargetLost) || !Enum.IsDefined(f.WhenTimeUp) || !Enum.IsDefined(f.WhenFailed)
            || !Enum.IsDefined(f.Preset))
        {
            issues.Add(Error("This Feedback loop step has an unknown option value. The macro may be damaged."));
            return;
        }

        // Observe: the bar area.
        const string region = "Region";
        if (f.RegionWidth < 5 || f.RegionHeight < 5)
            issues.Add(Error("The bar area must be at least 5×5 pixels. Use \"Pick bar area\".", region));
        else if (f.RegionWidth > MaxRegionSize || f.RegionHeight > MaxRegionSize)
            issues.Add(Error($"The bar area can be at most {MaxRegionSize:N0} pixels wide or tall.", region));
        else
        {
            var length = f.Orientation == BarOrientation.Horizontal ? f.RegionWidth : f.RegionHeight;
            if (length < MinBarLength)
                issues.Add(Error($"The bar area must be at least {MinBarLength} pixels long in the direction the marker moves. Check \"Direction\".", region));
            if ((long)f.RegionWidth * f.RegionHeight > LargeFeedbackAreaPixels)
                issues.Add(Warning("The bar area is large, so every frame takes longer. Pick only the bar itself.", region));
            if (screen is not null && !f.Region.IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The bar area is outside your current screen.", region));
        }

        // Observe: the marker.
        if (!RgbColor.TryParse(f.MarkerColor, out var marker))
            issues.Add(Error("Enter the marker color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.MarkerColor)));
        if (f.UseSecondaryMarkerColor && !RgbColor.TryParse(string.IsNullOrWhiteSpace(f.SecondaryMarkerColor) ? "#FFFFFF" : f.SecondaryMarkerColor, out _))
            issues.Add(Error("Enter the second marker color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.SecondaryMarkerColor)));
        CheckTolerance(issues, f.MarkerTolerance, nameof(FeedbackLoopStep.MarkerTolerance), "marker");
        if (f.MarkerMinPixels < 1 || f.MarkerMinPixels > MaxColorPixels)
            issues.Add(Error($"The marker's minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FeedbackLoopStep.MarkerMinPixels)));
        if (f.MaxJumpPercent < 1 || f.MaxJumpPercent > 100)
            issues.Add(Error("The glitch filter (largest jump in one frame) must be 1 to 100 %.", nameof(FeedbackLoopStep.MaxJumpPercent)));

        // Target zone.
        if (f.ZoneMode == FeedbackZoneMode.Fixed)
        {
            if (f.ZoneStartPercent < 0 || f.ZoneEndPercent > 100 || f.ZoneStartPercent >= f.ZoneEndPercent)
                issues.Add(Error("The zone must start before it ends, between 0 % and 100 % (for example 40 to 60).", nameof(FeedbackLoopStep.ZoneStartPercent)));
            else if (f.DeadBandPercent > 0 && f.DeadBandPercent * 2 >= f.ZoneEndPercent - f.ZoneStartPercent)
                issues.Add(Warning("The dead band is as wide as half the zone or more, so the marker may never count as inside it. Use a smaller dead band.", nameof(FeedbackLoopStep.DeadBandPercent)));
        }
        else
        {
            if (!RgbColor.TryParse(f.ZoneColor, out var zone))
                issues.Add(Error("Enter the zone color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.ZoneColor)));
            else if (RgbColor.TryParse(f.MarkerColor, out _) && Overlaps(marker, f.MarkerTolerance, zone, f.ZoneTolerance))
                issues.Add(Warning("The marker and zone colors are so close (with their tolerances) that one can be mistaken for the other. Lower a tolerance or pick more different colors.", nameof(FeedbackLoopStep.ZoneColor)));
            if (f.UseSecondaryZoneColor && !RgbColor.TryParse(string.IsNullOrWhiteSpace(f.SecondaryZoneColor) ? "#FFFFFF" : f.SecondaryZoneColor, out _))
                issues.Add(Error("Enter the second zone color as #RRGGBB, or pick it from the screen.", nameof(FeedbackLoopStep.SecondaryZoneColor)));
            CheckTolerance(issues, f.ZoneTolerance, nameof(FeedbackLoopStep.ZoneTolerance), "zone");
            if (f.ZoneMinPixels < 1 || f.ZoneMinPixels > MaxColorPixels)
                issues.Add(Error($"The zone's minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FeedbackLoopStep.ZoneMinPixels)));
        }

        // Decide.
        if (f.DeadBandPercent < 0 || f.DeadBandPercent > MaxDeadBandPercent)
            issues.Add(Error($"The dead band must be 0 to {MaxDeadBandPercent} %.", nameof(FeedbackLoopStep.DeadBandPercent)));
        var actions = new[] { f.WhenBeforeZone, f.WhenInsideZone, f.WhenAfterZone };
        if (f.WhenBeforeZone == FeedbackAction.Pulse || f.WhenAfterZone == FeedbackAction.Pulse)
            issues.Add(Error("\"Pulse\" only works while the marker is INSIDE the zone. Use Hold or Release before / after it.", "Rules"));
        if (f.ControlMode != FeedbackControlMode.SmartPredictive // smart mode has its own hover pulse check below
            && f.WhenInsideZone == FeedbackAction.Pulse && (f.PulsePeriodMs < 50 || f.PulsePeriodMs > MaxInputChangeMs))
            issues.Add(Error($"The pulse cycle must be 50 to {MaxInputChangeMs:N0} ms.", nameof(FeedbackLoopStep.PulsePeriodMs)));
        if (f.FinishWhenInZone && f.WhenInsideZone is not (FeedbackAction.Tap or FeedbackAction.Release))
            issues.Add(Error("\"Finish when the marker reaches the zone\" needs the INSIDE rule to be Tap (click) or Release.", nameof(FeedbackLoopStep.FinishWhenInZone)));
        if (!actions.Any(a => a is FeedbackAction.Hold or FeedbackAction.Tap or FeedbackAction.Pulse))
            issues.Add(Error("None of the rules presses anything. Set at least one place to \"Hold\" or \"Tap\".", "Rules"));
        else if (actions.Contains(FeedbackAction.Hold) && !actions.Contains(FeedbackAction.Release) && !actions.Contains(FeedbackAction.Tap))
            issues.Add(Warning("A rule holds the input but no rule releases it, so it stays held until the loop ends.", "Rules"));

        // Smart Predictive (Feedback Loop 2.0).
        var smart = f.ControlMode == FeedbackControlMode.SmartPredictive;
        if (!Enum.IsDefined(f.ControlMode))
            issues.Add(Error("Unknown control mode.", nameof(FeedbackLoopStep.ControlMode)));
        if (smart)
        {
            if (Playback.PredictiveFeedbackSettings.HoldDirectionOf(f) == 0)
                issues.Add(Error("Smart Predictive needs to know which way holding moves the bar: set one side (before / after the zone) to Hold and the other to Release.", "Rules"));
            if (f.FinishWhenInZone)
                issues.Add(Error("\"Finish when the marker reaches the zone\" is for Classic timing loops. Switch the control mode to Classic Simple Hold.", nameof(FeedbackLoopStep.FinishWhenInZone)));
            if (!double.IsFinite(f.LeadPredictionFactor) || f.LeadPredictionFactor < 0 || f.LeadPredictionFactor > MaxLeadPredictionSeconds)
                issues.Add(Error($"The lead prediction must be 0 to {MaxLeadPredictionSeconds} seconds (0.18 = predict 180 ms ahead).", nameof(FeedbackLoopStep.LeadPredictionFactor)));
            if (f.DeadzonePx < 0 || f.DeadzonePx > MaxDeadzonePx)
                issues.Add(Error($"The dead zone must be 0 to {MaxDeadzonePx} pixels.", nameof(FeedbackLoopStep.DeadzonePx)));
            if (f.UsePwmMicroPulse && (f.PulsePeriodMs < 20 || f.PulsePeriodMs > MaxInputChangeMs))
                issues.Add(Error($"The hover pulse cycle must be 20 to {MaxInputChangeMs:N0} ms.", nameof(FeedbackLoopStep.PulsePeriodMs)));
        }

        // Act.
        if (f.InputKind == FeedbackInputKind.MouseButton)
        {
            if (!Enum.IsDefined(f.MouseButton))
                issues.Add(Error("Unknown mouse button.", nameof(FeedbackLoopStep.MouseButton)));
        }
        else if (!VirtualKeys.IsValid(f.VirtualKey))
        {
            issues.Add(Error("Choose a valid key.", nameof(FeedbackLoopStep.VirtualKey)));
        }
        else if (f.VirtualKey == 0x1B)
        {
            issues.Add(Warning("Esc is also SmartMacro's emergency stop key (only when you press it yourself). Another key is clearer.", nameof(FeedbackLoopStep.VirtualKey)));
        }

        if (f.TapMs < 1 || f.TapMs > MaxTapMs)
            issues.Add(Error($"The tap length must be 1 to {MaxTapMs:N0} ms.", nameof(FeedbackLoopStep.TapMs)));

        // Limits.
        if (f.MaxCapturesPerSecond < 1 || f.MaxCapturesPerSecond > MaxCapturesPerSecond)
            issues.Add(Error($"Captures per second must be 1 to {MaxCapturesPerSecond}.", nameof(FeedbackLoopStep.MaxCapturesPerSecond)));
        else if (f.MaxCapturesPerSecond > 60 && !smart) // Smart Predictive is built for 120 Hz (it measures speeds)
            issues.Add(Warning("More than 60 captures per second is usually faster than the screen updates; it mostly uses more CPU. Check \"Measure loop speed\".", nameof(FeedbackLoopStep.MaxCapturesPerSecond)));

        if (f.MinInputChangeMs < 0 || f.MinInputChangeMs > MaxInputChangeMs)
            issues.Add(Error($"The minimum time between input changes must be 0 to {MaxInputChangeMs:N0} ms.", nameof(FeedbackLoopStep.MinInputChangeMs)));
        else if (f.MinInputChangeMs < 16 && !smart) // Smart Predictive pulses on purpose (games average the duty cycle)
            issues.Add(Warning("Changes less than 16 ms apart can be faster than a game's frame, so the game may miss some.", nameof(FeedbackLoopStep.MinInputChangeMs)));

        if (f.MaxRuntimeMs < MinFeedbackRuntimeMs || f.MaxRuntimeMs > MaxFeedbackRuntimeMs)
            issues.Add(Error($"A feedback loop needs a max runtime of {MinFeedbackRuntimeMs} to {MaxFeedbackRuntimeMs:N0} ms (1 hour), so it can't run forever.", nameof(FeedbackLoopStep.MaxRuntimeMs)));

        if (f.LostTimeoutMs < 0 || f.LostTimeoutMs > MaxLostTimeoutMs)
            issues.Add(Error($"\"Target missing for\" must be 0 to {MaxLostTimeoutMs:N0} ms.", nameof(FeedbackLoopStep.LostTimeoutMs)));

        // Stop when.
        if (f.CompleteWhen is not null)
            ValidateFeedbackCondition(issues, f.CompleteWhen, screen, "Done when", nameof(FeedbackLoopStep.CompleteWhen));
        if (f.FailWhen is not null)
            ValidateFeedbackCondition(issues, f.FailWhen, screen, "Failed when", nameof(FeedbackLoopStep.FailWhen));

        if (f.CompleteWhen is null && f.WhenTargetLost == FeedbackLostAction.ReleaseAndWait && f.WhenTimeUp == LoopLimitBehavior.StopMacro)
            issues.Add(Warning("This loop can only end with an error (when its max runtime is reached). Add a \"done when\" condition, or let it continue when the time is up or the target is gone.", nameof(FeedbackLoopStep.WhenTimeUp)));
    }

    /// <summary>A stop condition of a feedback loop: the normal condition rules, plus hints about what costs time here.</summary>
    private static void ValidateFeedbackCondition(List<ValidationIssue> issues, Condition condition, ScreenBounds? screen, string label, string property)
    {
        ValidateCondition(issues, condition, screen, label, property);
        if (condition.Search is not { } search) return;

        if (search is FindTextStep)
            issues.Add(Warning($"{label}: reading text (OCR) takes about 50-300 ms, and the loop can't react while it reads. An image or color condition is much faster.", property));
        if (search.TimeoutMs > 0)
            issues.Add(Warning($"{label}: \"keep checking for\" isn't used here; the loop looks once every \"check every\" while it runs.", property));
    }

    private static void CheckTolerance(List<ValidationIssue> issues, int tolerance, string property, string what)
    {
        if (tolerance < 0 || tolerance > 255)
            issues.Add(Error($"The {what} tolerance must be 0 to 255.", property));
        else if (tolerance > 100)
            issues.Add(Warning($"A {what} tolerance above 100 matches many different colors.", property));
    }

    /// <summary>True if some colour would match both colours (their tolerance boxes overlap on every channel).</summary>
    private static bool Overlaps(RgbColor a, int toleranceA, RgbColor b, int toleranceB)
    {
        var reach = Math.Max(0, toleranceA) + Math.Max(0, toleranceB);
        return Math.Abs(a.R - b.R) <= reach && Math.Abs(a.G - b.G) <= reach && Math.Abs(a.B - b.B) <= reach;
    }

    private static void CheckHold(List<ValidationIssue> issues, int holdMs, bool applies, string property, string actionName)
    {
        if (holdMs < 0 || holdMs > MaxHoldMs)
            issues.Add(Error($"The hold time must be 0 to {MaxHoldMs:N0} ms.", property));
        else if (holdMs > 0 && !applies)
            issues.Add(Warning($"The hold time is only used with the {actionName} action.", property));
    }

    /// <summary>
    /// Checks every recorded event. A damaged event (out-of-order time, unknown key, huge movement) is an error,
    /// so a broken or hand-edited file can't make playback do something wild.
    /// </summary>
    private static void ValidateSequence(List<ValidationIssue> issues, InputSequenceStep step)
    {
        const string property = nameof(InputSequenceStep.Events);
        var events = step.Events;
        if (events is null || events.Count == 0)
        {
            issues.Add(Error("This step has no recorded input.", property));
            return;
        }

        if (events.Count > MaxSequenceEvents)
        {
            issues.Add(Error($"A recorded input step can have at most {MaxSequenceEvents:N0} events.", property));
            return;
        }

        long previous = 0;
        var keysDown = new HashSet<int>();
        var buttonsDown = new HashSet<MacroMouseButton>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            string? problem = null;

            if (e.TimeUs < previous)
                problem = "is earlier than the event before it";
            else if (e.TimeUs > MaxSequenceDurationUs)
                problem = "is more than 1 hour after the start";
            else
            {
                if (e.HasPosition && (Math.Abs((long)e.X) > MaxScreenCoordinate || Math.Abs((long)e.Y) > MaxScreenCoordinate))
                    problem = "has a cursor position far outside any screen";
                else switch (e.Kind)
                {
                    case InputEventKind.MouseMove:
                        if (Math.Abs((long)e.Dx) > MaxRelativeMove || Math.Abs((long)e.Dy) > MaxRelativeMove)
                            problem = "moves the mouse too far in one go";
                        break;
                    case InputEventKind.MouseDown:
                    case InputEventKind.MouseUp:
                        if (!Enum.IsDefined(e.Button)) problem = "uses an unknown mouse button";
                        else if (e.Kind == InputEventKind.MouseDown) buttonsDown.Add(e.Button);
                        else buttonsDown.Remove(e.Button);
                        break;
                    case InputEventKind.Wheel:
                    case InputEventKind.HorizontalWheel:
                        if (e.Delta == 0 || Math.Abs((long)e.Delta) > MaxScrollDelta) problem = "has an invalid scroll amount";
                        break;
                    case InputEventKind.KeyDown:
                    case InputEventKind.KeyUp:
                        if (!VirtualKeys.IsValid(e.VirtualKey)) problem = "uses an invalid key";
                        else if (e.ScanCode is < 0 or > 0xFF) problem = "has an invalid scan code";
                        else if (e.Kind == InputEventKind.KeyDown) keysDown.Add(e.VirtualKey);
                        else keysDown.Remove(e.VirtualKey);
                        break;
                    default:
                        problem = "has an unknown type";
                        break;
                }
            }

            if (problem is not null)
            {
                issues.Add(Error($"Recorded event {i + 1:N0} {problem}. The recording may be damaged.", property));
                return; // one message is enough; a damaged list usually has many bad events
            }

            previous = e.TimeUs;
        }

        if (keysDown.Count > 0 || buttonsDown.Count > 0)
            issues.Add(Warning("Some keys or buttons are still held at the end of the recording; they'll be released when this step finishes.", property));

        ValidateMouseSegments(issues, step);
        ValidateSequenceClickTargets(issues, step);
    }

    /// <summary>
    /// Pictures of clicks inside recorded input: each must belong to a button-down event, once, and stay a sane size.
    /// Only "✂ Break into steps" reads them, so problems are warnings that say the pictures will be dropped.
    /// </summary>
    private static void ValidateSequenceClickTargets(List<ValidationIssue> issues, InputSequenceStep step)
    {
        if (step.ClickTargets is not { Count: > 0 } targets) return;
        const string property = nameof(InputSequenceStep.ClickTargets);
        var seen = new HashSet<int>();
        foreach (var target in targets)
        {
            var hasPicture = !string.IsNullOrEmpty(target?.TargetThumbnailBase64);
            var hasIntent = target?.Target is { HasAnySignal: true };
            var bad = target is null
                      || target.EventIndex < 0 || target.EventIndex >= step.Events.Count
                      || step.Events[target.EventIndex].Kind != InputEventKind.MouseDown
                      || !seen.Add(target.EventIndex)
                      || (!hasPicture && !hasIntent)
                      || (hasPicture && (target.TargetThumbnailBase64.Length > MaxThumbnailLength
                                         || target.AnchorX is < 0 or > MaxTemplateSize
                                         || target.AnchorY is < 0 or > MaxTemplateSize))
                      || (target.Target is { } fp && !IsValidTargetFingerprint(fp));
            if (!bad) continue;
            issues.Add(Warning("Some \"what you clicked\" pictures inside this recorded input don't match its clicks; they won't be used when you break it into steps.", property));
            return;
        }
    }

    /// <summary>
    /// Smart recordings: the segments must be sorted, start at a valid time and use known modes. A cursor-position
    /// segment needs recorded positions; movement without one is always played as relative movement.
    /// </summary>
    private static void ValidateMouseSegments(List<ValidationIssue> issues, InputSequenceStep step)
    {
        const string property = nameof(InputSequenceStep.MouseSegments);
        var segments = step.MouseSegments;
        if (segments is null) return;

        if (segments.Count == 0)
        {
            issues.Add(Error("The mouse playback parts of this recording are missing. The recording may be damaged.", property));
            return;
        }

        if (segments.Count > MaxMouseSegments)
        {
            issues.Add(Error($"A recorded input step can have at most {MaxMouseSegments:N0} mouse parts.", property));
            return;
        }

        long previous = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            string? problem = null;
            if (segment is null) problem = "is empty";
            else if (i == 0 && segment.StartUs != 0) problem = "doesn't start at the beginning of the recording";
            else if (segment.StartUs <= previous) problem = "starts before the part before it ends";
            else if (segment.StartUs > MaxSequenceDurationUs) problem = "starts more than 1 hour after the beginning";
            else if (!Enum.IsDefined(segment.Detected) || !Enum.IsDefined(segment.Choice) || !Enum.IsDefined(segment.Evidence))
                problem = "has an unknown option value";

            if (problem is not null)
            {
                issues.Add(Error($"Mouse part {i + 1:N0} {problem}. The recording may be damaged.", property));
                return;
            }

            previous = segment!.StartUs;
        }

        if (segments.Any(s => s.Effective == MouseMovementMode.CursorPosition)
            && step.Events is { } events && events.Any(e => e.Kind == InputEventKind.MouseMove && !e.HasPosition))
            issues.Add(Warning("Some mouse movement has no recorded cursor position; it's played as relative movement.", property));
    }

    /// <summary>Shared checks for Find text / image / color: area, timing, goal, action, offsets.</summary>
    private static void ValidateSearch(List<ValidationIssue> issues, ScreenSearchStep f, ScreenBounds? screen, int minInterval)
    {
        if (f.Scope is { } scope && !Enum.IsDefined(scope))
            issues.Add(Error("This step has an unknown \"where to look\" value. Choose where to look again.", nameof(ScreenSearchStep.Scope)));
        else if (f.EffectiveScope == SearchScope.Monitor && (f.MonitorNumber < 1 || f.MonitorNumber > SearchArea.MaxMonitorNumber))
            issues.Add(Error($"Choose a monitor from 1 to {SearchArea.MaxMonitorNumber}.", nameof(ScreenSearchStep.MonitorNumber)));
        else if (f.EffectiveScope == SearchScope.Window)
        {
            if (string.IsNullOrWhiteSpace(f.WindowTitle))
                issues.Add(Error("Choose the app window to search in (or type part of its title).", nameof(ScreenSearchStep.WindowTitle)));
            else if (f.WindowTitle.Length > SearchArea.MaxWindowTitleLength)
                issues.Add(Error($"The window title can be at most {SearchArea.MaxWindowTitleLength} characters.", nameof(ScreenSearchStep.WindowTitle)));
        }

        CheckMovement(issues, f.MoveStyle, f.MoveDurationMs);
        if (f.ClickHoldMs < 0 || f.ClickHoldMs > MaxHoldMs)
            issues.Add(Error($"The click hold must be 0 to {MaxHoldMs:N0} ms.", nameof(ScreenSearchStep.ClickHoldMs)));
        if (f.DoubleClickIntervalMs < 0 || f.DoubleClickIntervalMs > MouseMotionDefaults.MaxDoubleClickIntervalMs)
            issues.Add(Error($"The double-click gap must be 0 to {MouseMotionDefaults.MaxDoubleClickIntervalMs:N0} ms.", nameof(ScreenSearchStep.DoubleClickIntervalMs)));
        if (f.PostActionDelayMs < 0 || f.PostActionDelayMs > MouseMotionDefaults.MaxPostActionDelayMs)
            issues.Add(Error($"The wait after the click must be 0 to {MouseMotionDefaults.MaxPostActionDelayMs:N0} ms.", nameof(ScreenSearchStep.PostActionDelayMs)));

        if (f.EffectiveScope == SearchScope.CustomArea)
        {
            if (f.RegionWidth < 5 || f.RegionHeight < 5)
                issues.Add(Error("The search area must be at least 5×5 pixels.", "Region"));
            else if (f.RegionWidth > MaxRegionSize || f.RegionHeight > MaxRegionSize)
                issues.Add(Error($"The search area can be at most {MaxRegionSize:N0} pixels wide or tall.", "Region"));
            else if (screen is not null && !new Ocr.ScreenRect(f.RegionX, f.RegionY, f.RegionWidth, f.RegionHeight)
                         .IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The search area is outside your current screen.", "Region"));
        }

        if (f.TimeoutMs < 0 || f.TimeoutMs > MaxDelayMs)
            issues.Add(Error($"The wait time must be 0 to {MaxDelayMs:N0} ms.", nameof(ScreenSearchStep.TimeoutMs)));
        if (f.CheckIntervalMs < minInterval || f.CheckIntervalMs > MaxCheckIntervalMs)
            issues.Add(Error($"Check every must be {minInterval} to {MaxCheckIntervalMs:N0} ms.", nameof(ScreenSearchStep.CheckIntervalMs)));
        if (Math.Abs((long)f.OffsetX) > MaxOffset || Math.Abs((long)f.OffsetY) > MaxOffset)
            issues.Add(Error($"Offsets must be between -{MaxOffset} and {MaxOffset} pixels.", "Offset"));
        if (!Enum.IsDefined(f.Action) || !Enum.IsDefined(f.WhenNotFound) || !Enum.IsDefined(f.Goal)
            || !Enum.IsDefined(f.Scenario) || !Enum.IsDefined(f.Strategy)
            || !Enum.IsDefined(f.TargetSelection) || !Enum.IsDefined(f.MatchOrder))
            issues.Add(Error("This step has an unknown option value."));
        else if (f.Goal == WaitGoal.Disappear && f.Action != FoundTextAction.None)
            issues.Add(Warning("The \"when found\" action isn't used while waiting for something to disappear.", nameof(ScreenSearchStep.Action)));
        else if (f.Action == FoundTextAction.Hold && (f.ActionHoldMs < 1 || f.ActionHoldMs > MaxHoldMs))
            issues.Add(Error($"The hold time must be 1 to {MaxHoldMs:N0} ms.", nameof(ScreenSearchStep.ActionHoldMs)));
        if (f.TimeBudgetMs is < 0 or > 60_000)
            issues.Add(Error("The per-check time budget must be 0 to 60,000 ms.", nameof(ScreenSearchStep.TimeBudgetMs)));
        if (f.PredictiveLeadMs is < 0 or > 1_000)
            issues.Add(Error("The predictive click lead time must be 0 to 1,000 ms.", nameof(ScreenSearchStep.PredictiveLeadMs)));
        if (f.ConfirmFrames is < 0 or > 10)
            issues.Add(Error("Confirm frames must be 0 (single check) to 10.", nameof(ScreenSearchStep.ConfirmFrames)));
        if (f.TargetMatchIndex is < 0 or > 10_000)
            issues.Add(Error("Match number (#N) must be between 1 and 10,000.", nameof(ScreenSearchStep.TargetMatchIndex)));
        if (f.MinMatchCount is < 0 or > 10_000)
            issues.Add(Error("Minimum match count must be between 1 and 10,000.", nameof(ScreenSearchStep.MinMatchCount)));
        if (f.MaxMatchesToActOn is < 0 or > 10_000)
            issues.Add(Error("Maximum matches to act on must be 0 (all) to 10,000.", nameof(ScreenSearchStep.MaxMatchesToActOn)));
        if (f.BetweenMatchesDelayMs is < 0 or > 60_000)
            issues.Add(Error("Delay between matches must be 0 to 60,000 ms.", nameof(ScreenSearchStep.BetweenMatchesDelayMs)));
        if (!string.IsNullOrWhiteSpace(f.CountVariableName) && !MacroVariableDefinition.IsValidName(f.CountVariableName.Trim()))
            issues.Add(Error("The count variable name must start with a letter or underscore and use only letters, digits, or underscores.", nameof(ScreenSearchStep.CountVariableName)));
        ValidateFailurePolicy(issues, f.FailurePolicy);
    }

    /// <summary>Movement style must be known; the glide time 0 to 5 s.</summary>
    private static void CheckMovement(List<ValidationIssue> issues, MouseMoveStyle? style, int durationMs)
    {
        if (style is { } s && !Enum.IsDefined(s))
            issues.Add(Error("Unknown movement style.", "MoveStyle"));
        else if (style is MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve
                 && (durationMs < 0 || durationMs > MouseMotionDefaults.MaxMoveDurationMs))
            issues.Add(Error($"The glide time must be 0 to {MouseMotionDefaults.MaxMoveDurationMs:N0} ms.", "MoveDurationMs"));
    }

    /// <summary>The stored picture must exist, decode to its stated size, and not be one flat color.</summary>
    private static void ValidateTemplate(List<ValidationIssue> issues, FindImageStep step)
    {
        const string property = nameof(FindImageStep.TemplateData);
        if (string.IsNullOrEmpty(step.TemplateData))
        {
            issues.Add(Error("Capture the image to look for first (\"Capture image\").", property));
            return;
        }

        if (step.TemplateWidth < MinTemplateSize || step.TemplateHeight < MinTemplateSize
            || step.TemplateWidth > MaxTemplateSize || step.TemplateHeight > MaxTemplateSize)
        {
            issues.Add(Error($"The image must be {MinTemplateSize} to {MaxTemplateSize} pixels wide and tall. Capture a different area.", property));
            return;
        }

        if (step.TemplateData.Length > MaxTemplateDataLength)
        {
            issues.Add(Error("The stored image is too large. The macro may be damaged; capture the image again.", property));
            return;
        }

        if (!TemplateCache.TryGetOrDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var image, out var error))
        {
            issues.Add(Error(error ?? "The stored image is damaged.", property));
            return;
        }

        if (TemplateMatcher.IsFlat(image!))
            issues.Add(Error("The image is a single flat color, so its shape can't be matched. Capture an area with some detail, or use a \"Find color\" step.", property));
    }

    /// <summary>
    /// A fixed mouse position: far outside any possible screen is an error (a typo or damaged file); outside the current
    /// monitors is only a warning, because the macro may have been made with another monitor layout.
    /// </summary>
    private static void CheckPosition(List<ValidationIssue> issues, int x, int y, ScreenBounds? screen,
        string label = "Position", string property = "Position")
    {
        if (Math.Abs((long)x) > MaxScreenCoordinate || Math.Abs((long)y) > MaxScreenCoordinate)
            issues.Add(Error($"{label} ({x}, {y}) is far outside any screen (X and Y must be between -{MaxScreenCoordinate:N0} and {MaxScreenCoordinate:N0}). Use \"Pick position\" to choose a spot on your screen.", property));
        else if (screen is not null && !screen.Contains(x, y))
            issues.Add(Warning($"{label} ({x}, {y}) is outside your current screen area ({screen}). Use \"Pick position\" to choose a spot on your screen.", property));
    }

    private static ValidationIssue Error(string message, string? property = null) =>
        new(IssueSeverity.Error, message, Property: property);

    private static ValidationIssue Warning(string message, string? property = null) =>
        new(IssueSeverity.Warning, message, Property: property);
}
