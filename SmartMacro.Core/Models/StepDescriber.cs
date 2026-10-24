using SmartMacro.Core.Input;

namespace SmartMacro.Core.Models;

/// <summary>Short human-readable text for each step, used in the step list.</summary>
public static class StepDescriber
{
    public static string DisplayName(StepKind kind) => kind switch
    {
        StepKind.Delay => "Delay",
        StepKind.MouseMove => "Move mouse",
        StepKind.MouseClick => "Mouse click",
        StepKind.MouseScroll => "Scroll wheel",
        StepKind.KeyPress => "Key press",
        StepKind.TypeText => "Type text",
        StepKind.FindText => "Find text (OCR)",
        StepKind.InputSequence => "Recorded input",
        StepKind.FindImage => "Find image",
        StepKind.FindColor => "Find color",
        StepKind.If => "If",
        StepKind.Else => "Else",
        StepKind.EndIf => "End If",
        StepKind.Repeat => "Repeat",
        StepKind.EndRepeat => "End Repeat",
        StepKind.FeedbackLoop => "Feedback loop",
        StepKind.WaitForChange => "Wait for screen change",
        StepKind.FocusWindow => "Focus window",
        StepKind.Screenshot => "Take screenshot",
        _ => kind.ToString(),
    };

    public static string Describe(MacroStep step) => step switch
    {
        DelayStep d => d.DurationMs >= 1000
            ? $"Wait {d.DurationMs / 1000.0:0.###} s"
            : $"Wait {d.DurationMs} ms",
        MouseMoveStep m => $"{DescribeMoveTarget(m)}{MotionSuffix(m.MoveStyle, m.MoveDurationMs)}",
        MouseClickStep c => DescribeClick(c),
        MouseScrollStep s => $"Scroll {ScrollDirection(s)} {Notches(s.Delta)} {Where(s.UseCurrentPosition, s.X, s.Y)}",
        KeyPressStep k => $"{KeyVerb(k.Action)} {KeyCombo(k)}{Hold(k.Action == KeyAction.Press, k.HoldMs)}",
        TypeTextStep t => $"Type \"{Preview(t.Text)}\"",
        FindTextStep f => DescribeSearch(f, $"\"{Preview(f.Text)}\""),
        FindImageStep i => DescribeSearch(i, i.TemplateWidth > 0 ? $"image ({i.TemplateWidth}×{i.TemplateHeight})" : "image (not captured yet)"),
        FindColorStep c => DescribeSearch(c, $"color {c.Color} (±{c.Tolerance})"),
        InputSequenceStep q => DescribeSequence(q),
        IfStep i => $"If {DescribeCondition(i.Condition)}{ThenText(i)}",
        ElseStep => "Otherwise (when the If's condition is false)",
        EndIfStep => "End of the If block",
        RepeatStep r => DescribeRepeat(r),
        EndRepeatStep => "End of the Repeat block (go back for the next turn)",
        FeedbackLoopStep f => DescribeFeedback(f),
        WaitForChangeStep w => DescribeWaitForChange(w),
        FocusWindowStep w => DescribeFocusWindow(w),
        ScreenshotStep s => DescribeScreenshot(s),
        _ => step.GetType().Name,
    };

    /// <summary>"Wait up to 10 s for 1 % of (100, 200) 300×200 to change → click it".</summary>
    private static string DescribeWaitForChange(WaitForChangeStep w)
    {
        var limit = w.TimeoutMs == 0 ? "Check once if" : $"Wait up to {Seconds(w.TimeoutMs)} for";
        var percent = w.MinChangedPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var action = w.Action switch
        {
            FoundTextAction.Move => " → move there",
            FoundTextAction.Click => " → click it",
            FoundTextAction.DoubleClick => " → double-click it",
            FoundTextAction.RightClick => " → right-click it",
            FoundTextAction.Hold => $" → hold it {w.ActionHoldMs} ms",
            _ => "",
        };
        return $"{limit} {percent} % of ({w.RegionX}, {w.RegionY}) {w.RegionWidth}×{w.RegionHeight} to change{action}";
    }

    /// <summary>"Bring &quot;Roblox&quot; to the front (restore if minimized)".</summary>
    private static string DescribeFocusWindow(FocusWindowStep w)
    {
        var title = string.IsNullOrWhiteSpace(w.WindowTitle) ? "a window (not chosen yet)" : $"\"{Preview(w.WindowTitle)}\"";
        return $"Bring {title} to the front{(w.RestoreIfMinimized ? " (restore if minimized)" : "")}";
    }

    /// <summary>"Save a screenshot of the main monitor as SmartMacro_….png".</summary>
    private static string DescribeScreenshot(ScreenshotStep s)
    {
        var what = s.Area switch
        {
            ScreenshotArea.AllMonitors => "all monitors",
            ScreenshotArea.CustomArea => $"({s.RegionX}, {s.RegionY}) {s.RegionWidth}×{s.RegionHeight}",
            ScreenshotArea.Window => string.IsNullOrWhiteSpace(s.WindowTitle) ? "an app window (not chosen yet)" : $"the \"{Preview(s.WindowTitle)}\" window",
            _ => "the main monitor",
        };
        var folder = string.IsNullOrWhiteSpace(s.FolderPath) ? $"Pictures\\{Vision.ScreenshotFiles.DefaultFolderName}" : s.FolderPath.Trim();
        return $"Save a screenshot of {what} as {Vision.ScreenshotFiles.CleanPrefix(s.FileNamePrefix)}_….png in {folder}";
    }

    private static string DescribeClick(MouseClickStep c)
    {
        var button = $"{c.Button.ToString().ToLowerInvariant()} button";
        var lastFound = $"the last found spot{OffsetText(c.OffsetX, c.OffsetY)}";
        var where = c.UseLastFoundText ? "at " + lastFound : Where(c.UseCurrentPosition, c.X, c.Y);
        var motion = c.UseCurrentPosition ? "" : MotionSuffix(c.MoveStyle, c.MoveDurationMs);
        var aim = c.SmartAimAtTarget && c.HasTargetThumbnail && !c.UseCurrentPosition && !c.UseLastFoundText ? " · ✨ Smart-Aim" : "";
        return c.Action switch
        {
            MouseClickAction.Hold => $"Hold {button} for {c.HoldMs} ms {where}{motion}{aim}",
            MouseClickAction.Drag => $"Drag {button} from {(c.UseLastFoundText ? lastFound : c.UseCurrentPosition ? "the cursor" : $"({c.X}, {c.Y})")} to ({c.EndX}, {c.EndY}) over {c.DragDurationMs / 1000.0:0.##} s",
            _ => $"{ClickVerb(c.Action)} {button} {where}{Hold(c.Action == MouseClickAction.Click, c.HoldMs)}{motion}{aim}",
        };
    }

    /// <summary>"Move to (x, y)", "Move to the last found spot (+10, -5)" or "Move the cursor by (+20, 0)".</summary>
    private static string DescribeMoveTarget(MouseMoveStep m) => m.TargetMode switch
    {
        ClickTargetMode.LastFoundMatch => $"Move to the last found spot{OffsetText(m.OffsetX, m.OffsetY)}",
        ClickTargetMode.CurrentCursor => $"Move the cursor by ({m.OffsetX:+0;-0;0}, {m.OffsetY:+0;-0;0})",
        _ => $"Move to ({m.X}, {m.Y})",
    };

    /// <summary>" (+10, -5)" for a non-zero offset, empty otherwise.</summary>
    private static string OffsetText(int offsetX, int offsetY) =>
        offsetX != 0 || offsetY != 0 ? $" ({offsetX:+0;-0;0}, {offsetY:+0;-0;0})" : "";

    /// <summary>" (smooth glide 120 ms)" / " (human curve 120 ms)" for a styled movement; empty for instant jumps.</summary>
    private static string MotionSuffix(MouseMoveStyle? style, int durationMs) => style switch
    {
        MouseMoveStyle.SmoothGlide when durationMs > 0 => $" (smooth glide {durationMs} ms)",
        MouseMoveStyle.HumanCurve when durationMs > 0 => $" (human curve {durationMs} ms)",
        _ => "",
    };

    /// <summary>
    /// What a running step is doing right now, for the floating status pill, e.g. "Finding image \"SHAKE\"",
    /// "Waiting 500 ms", "Clicking at (640, 400)". A step's comment is used as its name when it has one.
    /// </summary>
    public static string Activity(MacroStep step)
    {
        var name = string.IsNullOrWhiteSpace(step.Comment) ? null : $"\"{Preview(step.Comment)}\"";
        return step switch
        {
            DelayStep d => d.DurationMs >= 1000 ? $"Waiting {d.DurationMs / 1000.0:0.#} s" : $"Waiting {d.DurationMs} ms",
            MouseMoveStep m => m.TargetMode switch
            {
                ClickTargetMode.LastFoundMatch => "Moving the mouse to the last found spot",
                ClickTargetMode.CurrentCursor => "Moving the mouse",
                _ => $"Moving the mouse to ({m.X}, {m.Y})",
            },
            MouseClickStep c => c.UseLastFoundText ? "Clicking the last found spot"
                : c.UseCurrentPosition ? "Clicking where the cursor is"
                : c.SmartAimAtTarget && c.HasTargetThumbnail ? $"Smart-Aim clicking near ({c.X}, {c.Y})"
                : $"Clicking at ({c.X}, {c.Y})",
            MouseScrollStep => "Scrolling",
            KeyPressStep k => $"Pressing {KeyCombo(k)}",
            TypeTextStep => "Typing text",
            FindTextStep t => $"Finding text \"{Preview(t.Text)}\" {Vision.SearchArea.Describe(t)}",
            FindImageStep i => $"Finding image {name ?? (i.TemplateWidth > 0 ? $"({i.TemplateWidth}×{i.TemplateHeight})" : "")}".TrimEnd() + $" {Vision.SearchArea.Describe(i)}",
            FindColorStep c => $"Finding color {c.Color} {Vision.SearchArea.Describe(c)}",
            IfStep => name is null ? "Checking an If condition" : $"Checking {name}",
            RepeatStep r => name is null ? $"Repeat loop ({DescribeRepeatMode(r)})" : $"Repeat {name}",
            FeedbackLoopStep => name is null ? "Running the feedback loop (minigame)" : $"Feedback loop {name}",
            InputSequenceStep => "Playing recorded input",
            WaitForChangeStep w => $"Waiting for the area at ({w.RegionX}, {w.RegionY}) to change",
            FocusWindowStep w => $"Focusing the \"{Preview(w.WindowTitle)}\" window",
            ScreenshotStep => "Taking a screenshot",
            _ => DisplayName(step.Kind),
        };
    }

    private static string DescribeRepeatMode(RepeatStep r) => r.Mode switch
    {
        RepeatMode.While => "while",
        RepeatMode.Until => "until",
        _ => $"{r.Count} times",
    };

    /// <summary>
    /// A plain-English explanation of a Find step for the summary banner at the top of its properties, e.g.
    /// "Searches Main Monitor every 250 ms for up to 10 s. When found: smoothly glides to its center and left-clicks
    /// (35 ms hold), then waits 120 ms. If it's not found in time: the macro stops."
    /// </summary>
    public static string Explain(ScreenSearchStep f)
    {
        var where = Vision.SearchArea.ShortName(f);
        var timing = f.TimeoutMs > 0
            ? $"every {f.CheckIntervalMs} ms for up to {Seconds(f.TimeoutMs)}"
            : "once";
        var text = $"Searches {where} {timing}.";

        if (f.Goal == WaitGoal.Disappear)
        {
            text += " Continues as soon as it's gone.";
        }
        else
        {
            var move = f.MoveStyle switch
            {
                MouseMoveStyle.SmoothGlide when f.MoveDurationMs > 0 => $"smoothly glides to its center ({f.MoveDurationMs} ms)",
                MouseMoveStyle.HumanCurve when f.MoveDurationMs > 0 => $"moves to its center along a natural curve ({f.MoveDurationMs} ms)",
                _ => "jumps to its center",
            };
            var offset = f.OffsetX != 0 || f.OffsetY != 0 ? $" (offset {f.OffsetX:+0;-0;0}, {f.OffsetY:+0;-0;0})" : "";
            var action = f.Action switch
            {
                FoundTextAction.Click => $"{move}{offset} and left-clicks ({f.ClickHoldMs} ms hold), then waits {f.PostActionDelayMs} ms",
                FoundTextAction.DoubleClick => $"{move}{offset} and double-clicks ({f.DoubleClickIntervalMs} ms apart), then waits {f.PostActionDelayMs} ms",
                FoundTextAction.RightClick => $"{move}{offset} and right-clicks, then waits {f.PostActionDelayMs} ms",
                FoundTextAction.Hold => $"{move}{offset} and holds the left button for {f.ActionHoldMs} ms, then waits {f.PostActionDelayMs} ms",
                FoundTextAction.Move => $"{move}{offset} (no click)",
                _ => "just continues with the next step",
            };
            var park = f.MoveCursorAwayAfterAction && f.Action is not (FoundTextAction.None or FoundTextAction.Move)
                ? ", and moves the cursor out of the way"
                : "";
            text += $" When found: {action}{park}.";
        }

        text += f.WhenNotFound switch
        {
            TextNotFoundBehavior.Continue => f.Goal == WaitGoal.Appear ? " If it's not found in time: continues anyway." : " If it's still there: continues anyway.",
            TextNotFoundBehavior.SkipNextStep => " If the time runs out: skips the next step.",
            _ => f.Goal == WaitGoal.Appear ? " If it's not found in time: the macro stops." : " If it's still there: the macro stops.",
        };
        return text;
    }

    private static string Seconds(int ms) => ms >= 1000 ? $"{ms / 1000.0:0.#} s" : $"{ms} ms";

    /// <summary>
    /// The Feedback loop's "rule preview": what it will do, in plain sentences, e.g. "While the target is to the RIGHT of
    /// your bar: holds the left mouse button down (your bar moves right)."
    /// </summary>
    public static IReadOnlyList<string> ExplainFeedbackRules(FeedbackLoopStep f)
    {
        var follow = f.Preset == FeedbackPreset.FollowTargetBar;
        var marker = follow ? "the target" : "the marker";
        var zone = follow ? "your bar" : "the target zone";
        var horizontal = f.Orientation == Vision.BarOrientation.Horizontal;
        var before = horizontal ? "to the LEFT of" : "ABOVE";
        var after = horizontal ? "to the RIGHT of" : "BELOW";
        var input = f.InputKind == FeedbackInputKind.MouseButton
            ? $"the {f.MouseButton.ToString().ToLowerInvariant()} mouse button"
            : $"the {VirtualKeys.GetName(f.VirtualKey)} key";

        string Rule(FeedbackAction action) => action switch
        {
            FeedbackAction.Hold => $"holds {input} down",
            FeedbackAction.Release => $"releases {input}",
            FeedbackAction.Tap => $"taps {input} once",
            FeedbackAction.Pulse => $"pulses {input} (short presses) so {zone} doesn't overshoot",
            _ => $"leaves {input} as it is",
        };

        var lines = new List<string>
        {
            $"Watches the minigame bar up to {f.MaxCapturesPerSecond} times a second.",
            $"While {marker} is {before} {zone}: {Rule(f.WhenBeforeZone)}.",
            $"While {marker} is inside {zone}: {Rule(f.WhenInsideZone)}.",
            $"While {marker} is {after} {zone}: {Rule(f.WhenAfterZone)}.",
        };
        if (follow && f.WhenBeforeZone != f.WhenAfterZone)
        {
            var pushes = horizontal ? "right" : "up";
            lines.Add($"Holding pushes your bar {pushes}; releasing lets it drift back.");
        }

        if (f.ControlMode == FeedbackControlMode.SmartPredictive)
        {
            var lead = f.LeadPredictionFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var hover = f.UsePwmMicroPulse
                ? $"taps {input} in quick {f.PulsePeriodMs} ms pulses so {zone} hovers"
                : $"leaves {input} as it is";
            lines.Add($"Smart Predictive: decides on where both bars will be {lead} s from now, so it lets go before {zone} overshoots, and brakes at once when {marker} turns around.");
            lines.Add($"Within {f.DeadzonePx} px of {marker}: {hover}.");
        }

        if (f.FinishWhenInZone) lines.Add($"Finishes as soon as {marker} reaches {zone}.");
        lines.Add(f.WhenTargetLost switch
        {
            FeedbackLostAction.ExitLoop => $"If the bar can't be seen for {Seconds(f.LostTimeoutMs)} (the minigame closed): lets go and continues with the next step.",
            FeedbackLostAction.StopMacro => $"If the bar can't be seen for {Seconds(f.LostTimeoutMs)}: lets go and stops the macro.",
            _ => $"If {marker} can't be seen for {Seconds(f.LostTimeoutMs)}: lets go and keeps looking.",
        });
        lines.Add($"Stops after {Seconds(f.MaxRuntimeMs)} at the latest. Esc stops it any time.");
        return lines;
    }

    /// <summary>E.g. " → click it" for an If with a "when true" action (empty without one).</summary>
    private static string ThenText(IfStep i)
    {
        if (i.ThenAction == FoundTextAction.None) return "";
        return $" → {FoundActionText(i.ThenAction, i.ThenOffsetX, i.ThenOffsetY, i.ThenHoldMs)} when true";
    }

    /// <summary>E.g. "Click center", "Hold left button 500 ms on it", "Click center (+10, -5)".</summary>
    public static string FoundActionText(FoundTextAction action, int offsetX, int offsetY, int holdMs)
    {
        var offset = offsetX != 0 || offsetY != 0 ? $" ({offsetX:+0;-0;0}, {offsetY:+0;-0;0})" : "";
        return action switch
        {
            FoundTextAction.Move => $"Move to center{offset}",
            FoundTextAction.Click => $"Click center{offset}",
            FoundTextAction.DoubleClick => $"Double-click center{offset}",
            FoundTextAction.RightClick => $"Right-click center{offset}",
            FoundTextAction.Hold => $"Hold left button {holdMs} ms on center{offset}",
            _ => "Continue",
        };
    }

    /// <summary>E.g. "\"OK\" is on screen", "the image (40×30) is not in area (…)", plus the wait time if any.</summary>
    public static string DescribeCondition(Condition? condition)
    {
        if (condition?.Search is not { } search) return "(no condition yet)";

        var what = search switch
        {
            FindTextStep t => $"\"{Preview(t.Text)}\"",
            FindImageStep i => i.TemplateWidth > 0 ? $"the image ({i.TemplateWidth}×{i.TemplateHeight})" : "the image (not captured yet)",
            FindColorStep c => $"color {c.Color} (±{c.Tolerance})",
            _ => "it",
        };
        var where = Vision.SearchArea.Describe(search);
        var verb = condition.Negate ? "is not" : "is";
        var wait = search.TimeoutMs > 0 ? $" (wait up to {search.TimeoutMs / 1000.0:0.#} s)" : "";
        return $"{what} {verb} {where}{wait}";
    }

    /// <summary>E.g. "Keep marker #FF0000 in zone 40–60 %: hold before, release after (left button; max 30 s)".</summary>
    public static string DescribeFeedback(FeedbackLoopStep f)
    {
        var zone = f.ZoneMode == FeedbackZoneMode.Fixed
            ? $"zone {f.ZoneStartPercent}–{f.ZoneEndPercent} %"
            : $"zone {f.ZoneColor}";
        var input = f.InputKind == FeedbackInputKind.MouseButton
            ? $"{f.MouseButton.ToString().ToLowerInvariant()} button"
            : VirtualKeys.GetName(f.VirtualKey);

        var rules = new List<string>();
        AddRule(rules, f.WhenBeforeZone, "before");
        AddRule(rules, f.WhenInsideZone, "inside");
        AddRule(rules, f.WhenAfterZone, "after");
        var what = rules.Count == 0 ? "no rules" : string.Join(", ", rules);

        var stops = new List<string> { $"max {f.MaxRuntimeMs / 1000.0:0.#} s" };
        if (f.FinishWhenInZone) stops.Add("ends when it reaches the zone");
        if (f.CompleteWhen is not null) stops.Add("until done");
        if (f.FailWhen is not null) stops.Add("fail check");
        var details = $"{what} ({input}; {string.Join(", ", stops)})";
        return f.Preset switch
        {
            FeedbackPreset.FollowTargetBar => $"Follow target {f.MarkerColor} with bar {f.ZoneColor}: {details}",
            FeedbackPreset.ClickWhenInZone => $"{(f.WhenInsideZone == FeedbackAction.Release ? "Release" : "Click")} when marker {f.MarkerColor} enters {zone}: {details}",
            _ => $"Keep marker {f.MarkerColor} in {zone}: {details}",
        };
    }

    private static void AddRule(List<string> rules, FeedbackAction action, string where)
    {
        var verb = action switch
        {
            FeedbackAction.Hold => "hold",
            FeedbackAction.Release => "release",
            FeedbackAction.Tap => "tap",
            FeedbackAction.Pulse => "pulse",
            _ => null,
        };
        if (verb is not null) rules.Add($"{verb} {where}");
    }

    private static string DescribeRepeat(RepeatStep r)
    {
        var text = r.Mode switch
        {
            RepeatMode.Times => r.Count == 1 ? "Repeat 1 time" : $"Repeat {r.Count:N0} times",
            RepeatMode.While => $"Repeat while {DescribeCondition(r.Condition)}",
            RepeatMode.Until => $"Repeat until {DescribeCondition(r.Condition)}",
            _ => "Repeat",
        };

        var limits = new List<string>();
        if (r.Mode != RepeatMode.Times && r.MaxIterations > 0) limits.Add($"max {r.MaxIterations:N0} turns");
        if (r.MaxDurationMs > 0) limits.Add($"max {r.MaxDurationMs / 1000.0:0.#} s");
        return limits.Count == 0 ? text : $"{text} ({string.Join(", ", limits)})";
    }

    /// <summary>Counts for the recorded-input editor and the step list.</summary>
    public static InputSequenceSummary Summarize(InputSequenceStep step)
    {
        var events = step.Events ?? new List<InputEvent>();
        int moves = 0, keys = 0, buttons = 0, wheels = 0, positioned = 0;
        long dx = 0, dy = 0;
        var keyNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    moves++;
                    if (e.HasPosition) positioned++;
                    dx += Math.Abs((long)e.Dx);
                    dy += Math.Abs((long)e.Dy);
                    break;
                case InputEventKind.KeyDown:
                    keys++;
                    if (keyNames.Count < 12) keyNames.Add(VirtualKeys.GetName(e.VirtualKey));
                    break;
                case InputEventKind.MouseDown:
                    buttons++;
                    break;
                case InputEventKind.Wheel:
                case InputEventKind.HorizontalWheel:
                    wheels++;
                    break;
            }
        }

        var duration = events.Count == 0 ? 0 : Math.Max(0, events[^1].TimeUs) / 1000.0;
        var cursorParts = 0;
        var relativeParts = 0;
        if (step.MouseSegments is { } segments)
        {
            foreach (var segment in segments)
            {
                if (segment is null) continue;
                if (segment.Effective == MouseMovementMode.Relative) relativeParts++;
                else cursorParts++;
            }
        }

        return new InputSequenceSummary(events.Count, duration, moves, keys, buttons, wheels, dx, dy, keyNames.ToList(),
            positioned, cursorParts, relativeParts);
    }

    private static string DescribeSequence(InputSequenceStep step)
    {
        var s = Summarize(step);
        var seconds = s.DurationMs / 1000.0;
        if (!step.HasMouseSegments)
            return $"Play recorded input: {seconds:0.0} s, {s.EventCount:N0} events ({s.MouseMoves:N0} mouse moves, {s.KeyPresses:N0} key presses)";

        var mouse = (s.CursorParts, s.RelativeParts) switch
        {
            (_, 0) => "cursor positions",
            (0, _) => "camera / relative movement",
            _ => $"{s.CursorParts} cursor part{(s.CursorParts == 1 ? "" : "s")} + {s.RelativeParts} camera part{(s.RelativeParts == 1 ? "" : "s")}",
        };
        return $"Play recorded input: {seconds:0.0} s, {mouse}, {s.KeyPresses:N0} key presses, {s.ButtonPresses:N0} clicks";
    }

    private static string Hold(bool applies, int holdMs) => applies && holdMs > 0 ? $" (hold {holdMs} ms)" : "";

    public static string KeyCombo(KeyPressStep step)
    {
        var parts = new List<string>();
        if (step.Action == KeyAction.Press)
        {
            if (step.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (step.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (step.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (step.Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        }

        parts.Add(VirtualKeys.GetName(step.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string DescribeSearch(ScreenSearchStep f, string what)
    {
        var seconds = $"{f.TimeoutMs / 1000.0:0.#} s";
        var where = Vision.SearchArea.Describe(f);
        if (f.Goal == WaitGoal.Disappear)
            return f.TimeoutMs > 0 ? $"Wait up to {seconds} until {what} is gone {where}" : $"Check that {what} is not {where}";

        // E.g. "Find image (121×134) on screen → Click center when found (wait up to 10 s)".
        var wait = f.TimeoutMs > 0 ? $"wait up to {seconds}" : "look once";
        var notFound = f.WhenNotFound switch
        {
            TextNotFoundBehavior.SkipNextStep => "; if not found: skip next step",
            TextNotFoundBehavior.Continue => "; if not found: continue",
            _ => "",
        };
        return $"Find {what} {where} → {FoundActionText(f.Action, f.OffsetX, f.OffsetY, f.ActionHoldMs)} when found ({wait}{notFound})";
    }

    private static string ClickVerb(MouseClickAction action) => action switch
    {
        MouseClickAction.Click => "Click",
        MouseClickAction.DoubleClick => "Double-click",
        MouseClickAction.Down => "Press",
        MouseClickAction.Up => "Release",
        _ => action.ToString(),
    };

    private static string KeyVerb(KeyAction action) => action switch
    {
        KeyAction.Press => "Press",
        KeyAction.Down => "Hold down",
        KeyAction.Up => "Release",
        _ => action.ToString(),
    };

    private static string Where(bool current, int x, int y) => current ? "at cursor" : $"at ({x}, {y})";

    private static string ScrollDirection(MouseScrollStep s) =>
        s.Horizontal ? (s.Delta >= 0 ? "right" : "left") : (s.Delta >= 0 ? "up" : "down");

    private static string Notches(int delta)
    {
        var notches = Math.Abs((long)delta) / 120.0;
        return notches == 1 ? "1 notch" : $"{notches:0.##} notches";
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace("\r\n", "⏎").Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }
}

/// <summary>
/// Counts shown for a recorded-input step. DurationMs is the time of the last event.
/// MovesWithPosition: mouse moves that also have a cursor position (smart recordings).
/// CursorParts / RelativeParts: how many mouse segments play cursor positions / relative movement (0 / 0 for game mode).
/// </summary>
public sealed record InputSequenceSummary(
    int EventCount,
    double DurationMs,
    int MouseMoves,
    int KeyPresses,
    int ButtonPresses,
    int WheelEvents,
    long TotalAbsDx,
    long TotalAbsDy,
    IReadOnlyList<string> KeysUsed,
    int MovesWithPosition = 0,
    int CursorParts = 0,
    int RelativeParts = 0);
