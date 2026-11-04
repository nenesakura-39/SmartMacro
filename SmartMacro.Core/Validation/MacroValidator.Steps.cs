using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Validation;

public static partial class MacroValidator
{
    /// <summary>Checks a single step's own fields (no index information).</summary>
    public static IReadOnlyList<ValidationIssue> ValidateStep(MacroStep step, ScreenBounds? screen = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var issues = new List<ValidationIssue>();

        if (step.Comment is { Length: > MaxCommentLength })
            issues.Add(Error($"The comment can be at most {MaxCommentLength} characters.", nameof(MacroStep.Comment)));

        switch (step)
        {
            case DelayStep d:
                if (!Enum.IsDefined(d.Mode))
                    issues.Add(Error("Unknown delay mode. The macro may be damaged.", nameof(DelayStep.Mode)));
                if (d.DurationMs < 0)
                    issues.Add(Error("The delay can't be negative.", nameof(DelayStep.DurationMs)));
                else if (d.DurationMs > MaxDelayMs)
                    issues.Add(Error($"The delay can be at most {MaxDelayMs:N0} ms (1 hour).", nameof(DelayStep.DurationMs)));
                if (d.MinDurationMs < 0 || d.MinDurationMs > MaxDelayMs)
                    issues.Add(Error($"The minimum adaptive wait must be between 0 and {MaxDelayMs:N0} ms.", nameof(DelayStep.MinDurationMs)));
                if (d.MaxDurationMs < 0 || d.MaxDurationMs > MaxDelayMs)
                    issues.Add(Error($"The maximum adaptive wait must be between 0 and {MaxDelayMs:N0} ms.", nameof(DelayStep.MaxDurationMs)));
                else if (d.Mode != DelayMode.Fixed && d.MaxDurationMs > 0 && d.MaxDurationMs < d.MinDurationMs)
                    issues.Add(Error("The maximum adaptive wait must be at least the minimum wait.", nameof(DelayStep.MaxDurationMs)));
                if (d.QuietWindowMs < 0 || d.QuietWindowMs > 60_000)
                    issues.Add(Error("The screen calm window must be between 0 and 60,000 ms.", nameof(DelayStep.QuietWindowMs)));
                break;

            case MouseMoveStep { UseRelativeDelta: true } r:
                // Relative (3D camera) move: X / Y are only where it was recorded, the deltas are what plays.
                if (Math.Abs((long)r.DeltaX) > MaxRelativeMove || Math.Abs((long)r.DeltaY) > MaxRelativeMove)
                    issues.Add(Error($"A relative mouse move can be at most {MaxRelativeMove:N0} counts in each direction.", nameof(MouseMoveStep.DeltaX)));
                else if (r.DeltaX == 0 && r.DeltaY == 0)
                    issues.Add(Warning("This relative mouse move is 0, 0, so it does nothing.", nameof(MouseMoveStep.DeltaX)));
                break;

            case MouseMoveStep m:
                if (!Enum.IsDefined(m.TargetMode))
                    issues.Add(Error("Unknown \"where to move\" choice. The macro may be damaged.", nameof(MouseMoveStep.TargetMode)));
                else if (m.TargetMode == ClickTargetMode.Coordinates)
                    CheckPosition(issues, m.X, m.Y, screen);
                else
                    CheckOffset(issues, m.OffsetX, m.OffsetY);
                CheckMovement(issues, m.MoveStyle, m.MoveDurationMs);
                break;

            case MouseClickStep c:
                if (!Enum.IsDefined(c.Button))
                    issues.Add(Error("Unknown mouse button.", nameof(MouseClickStep.Button)));
                if (!Enum.IsDefined(c.Action))
                    issues.Add(Error("Unknown click action.", nameof(MouseClickStep.Action)));
                if (!c.UseCurrentPosition && !c.UseLastFoundText)
                    CheckPosition(issues, c.X, c.Y, screen);
                if (c.UseLastFoundText)
                    CheckOffset(issues, c.OffsetX, c.OffsetY);
                ValidateSmartClick(issues, c);
                CheckMovement(issues, c.MoveStyle, c.MoveDurationMs);
                if (c.DoubleClickIntervalMs < 0 || c.DoubleClickIntervalMs > MouseMotionDefaults.MaxDoubleClickIntervalMs)
                    issues.Add(Error($"The double-click gap must be 0 to {MouseMotionDefaults.MaxDoubleClickIntervalMs:N0} ms.", nameof(MouseClickStep.DoubleClickIntervalMs)));
                if (c.PostActionDelayMs < 0 || c.PostActionDelayMs > MouseMotionDefaults.MaxPostActionDelayMs)
                    issues.Add(Error($"The wait after the click must be 0 to {MouseMotionDefaults.MaxPostActionDelayMs:N0} ms.", nameof(MouseClickStep.PostActionDelayMs)));
                if (c.Action == MouseClickAction.Hold)
                {
                    if (c.HoldMs < 1 || c.HoldMs > MaxHoldMs)
                        issues.Add(Error($"Set how long to hold the button: 1 to {MaxHoldMs:N0} ms.", nameof(MouseClickStep.HoldMs)));
                }
                else
                {
                    // The click hold is used by Click and Double click. New steps start with 35 ms, which isn't worth a
                    // warning when the user later picks another action.
                    CheckHold(issues, c.HoldMs, c.Action is MouseClickAction.Click or MouseClickAction.DoubleClick
                                                || c.HoldMs == MouseMotionDefaults.ClickHoldMs, nameof(MouseClickStep.HoldMs), "Click / Double click");
                }

                if (c.Action == MouseClickAction.Drag)
                {
                    CheckPosition(issues, c.EndX, c.EndY, screen, "End position", "EndPosition");
                    if (c.DragDurationMs < 0 || c.DragDurationMs > MaxHoldMs)
                        issues.Add(Error($"The drag time must be 0 to {MaxHoldMs:N0} ms.", nameof(MouseClickStep.DragDurationMs)));
                    if (!c.UseCurrentPosition && !c.UseLastFoundText && c.X == c.EndX && c.Y == c.EndY)
                        issues.Add(Warning("The drag starts and ends at the same spot, so nothing is dragged. Pick an end position.", "EndPosition"));
                }
                break;

            case MouseScrollStep s:
                if (s.Delta == 0)
                    issues.Add(Error("The scroll amount can't be 0. Scroll at least 1 notch.", nameof(MouseScrollStep.Delta)));
                else if (Math.Abs((long)s.Delta) > MaxScrollDelta)
                    issues.Add(Error($"The scroll amount must be between -{MaxScrollDelta} and {MaxScrollDelta}.", nameof(MouseScrollStep.Delta)));
                if (!s.UseCurrentPosition)
                    CheckPosition(issues, s.X, s.Y, screen);
                break;

            case KeyPressStep k:
                if (!VirtualKeys.IsValid(k.VirtualKey))
                    issues.Add(Error("Choose a valid key.", nameof(KeyPressStep.VirtualKey)));
                if (k.ScanCode is < 0 or > 0xFF)
                    issues.Add(Error("Hardware scan code must be between 0 and 255.", nameof(KeyPressStep.ScanCode)));
                if (!Enum.IsDefined(k.Action))
                    issues.Add(Error("Unknown key action.", nameof(KeyPressStep.Action)));
                const KeyModifiers allModifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Windows;
                if ((k.Modifiers & ~allModifiers) != 0)
                    issues.Add(Error("Unknown modifier key.", nameof(KeyPressStep.Modifiers)));
                else if (k.Modifiers != KeyModifiers.None && k.Action != KeyAction.Press)
                    issues.Add(Warning("Modifier keys are only used with the Press action.", nameof(KeyPressStep.Modifiers)));
                CheckHold(issues, k.HoldMs, k.Action == KeyAction.Press, nameof(KeyPressStep.HoldMs), "Press");
                break;

            case InputSequenceStep q:
                ValidateSequence(issues, q);
                break;

            case TypeTextStep t:
                if (string.IsNullOrEmpty(t.Text))
                    issues.Add(Error("Enter the text to type.", nameof(TypeTextStep.Text)));
                else if (t.Text.Length > MaxTextLength)
                    issues.Add(Error($"The text can be at most {MaxTextLength:N0} characters.", nameof(TypeTextStep.Text)));
                if (t.CharacterDelayMs < 0 || t.CharacterDelayMs > MaxCharacterDelayMs)
                    issues.Add(Error($"The delay between characters must be 0 to {MaxCharacterDelayMs:N0} ms.", nameof(TypeTextStep.CharacterDelayMs)));
                break;

            case FindTextStep f:
                if (string.IsNullOrWhiteSpace(f.Text))
                    issues.Add(Error("Enter the text to look for.", nameof(FindTextStep.Text)));
                else if (f.Text.Length > MaxSearchTextLength)
                    issues.Add(Error($"The search text can be at most {MaxSearchTextLength} characters.", nameof(FindTextStep.Text)));
                if (f.MinSimilarityPercent < 50 || f.MinSimilarityPercent > 100)
                    issues.Add(Error("Match accuracy must be between 50% and 100%.", nameof(FindTextStep.MinSimilarityPercent)));
                if (!Enum.IsDefined(f.MatchMode) || !Enum.IsDefined(f.Preprocess))
                    issues.Add(Error("This step has an unknown option value."));
                ValidateSearch(issues, f, screen, MinCheckIntervalMs);
                break;

            case FindImageStep img:
                ValidateTemplate(issues, img);
                if (!Enum.IsDefined(img.Detector))
                    issues.Add(Error("Unknown image detector mode.", nameof(FindImageStep.Detector)));
                if (img.MinSimilarityPercent < 50 || img.MinSimilarityPercent > 100)
                    issues.Add(Error("Match accuracy must be between 50% and 100%.", nameof(FindImageStep.MinSimilarityPercent)));
                else if (img.MinSimilarityPercent < 70)
                    issues.Add(Warning("Match accuracy below 70% often matches the wrong thing. Test it with \"Test now\".", nameof(FindImageStep.MinSimilarityPercent)));
                if (img.MinVisiblePercent != 0 && (img.MinVisiblePercent < 40 || img.MinVisiblePercent > 100))
                    issues.Add(Error("\"Minimum visible part\" must be 0% (whole image) or 40% to 100%.", nameof(FindImageStep.MinVisiblePercent)));
                if (img.RotationRangeDeg is < 0 or > 45)
                    issues.Add(Error("\"Rotation tolerance\" must be 0° to 45°.", nameof(FindImageStep.RotationRangeDeg)));
                if (img.IgnoreEdgesPercent < 0 || img.IgnoreEdgesPercent > 40)
                    issues.Add(Error("\"Ignore the edges\" must be 0% to 40%.", nameof(FindImageStep.IgnoreEdgesPercent)));
                else if (img.IgnoreEdgesPercent > 0 && img.TemplateWidth > 0
                         && TemplateMatcher.InnerArea(img.TemplateWidth, img.TemplateHeight, img.IgnoreEdgesPercent / 100.0) is null)
                    issues.Add(Warning("The image is too small to ignore its edges, so the whole image is compared. Capture a bigger area.", nameof(FindImageStep.IgnoreEdgesPercent)));
                ValidateSearch(issues, img, screen, MinVisionCheckIntervalMs);
                if (img.EffectiveScope == SearchScope.CustomArea && img.TemplateWidth > 0
                    && (img.RegionWidth < img.TemplateWidth || img.RegionHeight < img.TemplateHeight))
                    issues.Add(Error("The search area is smaller than the image to find. Make the area bigger.", "Region"));
                break;

            case IfStep ifStep:
                ValidateCondition(issues, ifStep.Condition, screen);
                if (!Enum.IsDefined(ifStep.ThenAction))
                    issues.Add(Error("This If step has an unknown \"when true\" action. The macro may be damaged.", nameof(IfStep.ThenAction)));
                else if (ifStep.ThenAction != FoundTextAction.None)
                {
                    if (Math.Abs((long)ifStep.ThenOffsetX) > MaxOffset || Math.Abs((long)ifStep.ThenOffsetY) > MaxOffset)
                        issues.Add(Error($"Offsets must be between -{MaxOffset} and {MaxOffset} pixels.", "ThenOffset"));
                    if (ifStep.ThenAction == FoundTextAction.Hold && (ifStep.ThenHoldMs < 1 || ifStep.ThenHoldMs > MaxHoldMs))
                        issues.Add(Error($"The hold time must be 1 to {MaxHoldMs:N0} ms.", nameof(IfStep.ThenHoldMs)));
                    if (ifStep.Condition is { Negate: true })
                        issues.Add(Warning("With \"is NOT on screen\" the condition is true when nothing is found, so there's nothing to click: the \"when true\" action never runs.", nameof(IfStep.ThenAction)));
                    else if (ifStep.Condition is { Kind: ConditionKind.VariableCompare })
                        issues.Add(Warning("A variable comparison has no screen spot to click, so the \"when true\" mouse action is ignored.", nameof(IfStep.ThenAction)));
                }
                break;

            case ElseStep or EndIfStep or EndRepeatStep:
                break; // nothing to set; their place in the list is checked by ValidateBlocks

            case RepeatStep r:
                ValidateRepeat(issues, r, screen);
                break;

            case FeedbackLoopStep feedback:
                ValidateFeedbackLoop(issues, feedback, screen);
                break;

            case FindColorStep c:
                if (!Enum.IsDefined(c.ColorSpace) || !Enum.IsDefined(c.SelectRule))
                    issues.Add(Error("This Find color step has an unknown option value."));
                if (!RgbColor.TryParse(c.Color, out _))
                    issues.Add(Error("Enter the color as #RRGGBB (for example #FF0000 for red), or pick it from the screen.", nameof(FindColorStep.Color)));
                if (c.UseSecondaryColor && !RgbColor.TryParse(string.IsNullOrWhiteSpace(c.SecondaryColor) ? "#FFFFFF" : c.SecondaryColor, out _))
                    issues.Add(Error("Enter the second color as #RRGGBB (for example #FFFFFF for white), or pick it from the screen.", nameof(FindColorStep.SecondaryColor)));
                if (c.Tolerance < 0 || c.Tolerance > 255)
                    issues.Add(Error("Tolerance must be 0 to 255.", nameof(FindColorStep.Tolerance)));
                else if (c.Tolerance > 100)
                    issues.Add(Warning("A tolerance above 100 matches many different colors.", nameof(FindColorStep.Tolerance)));
                if (c.MinPixels < 1 || c.MinPixels > MaxColorPixels)
                    issues.Add(Error($"Minimum size must be 1 to {MaxColorPixels:N0} pixels.", nameof(FindColorStep.MinPixels)));
                if (c.MaxPixels < 0 || c.MaxPixels > MaxColorPixels)
                    issues.Add(Error($"Maximum size must be 0 (unlimited) to {MaxColorPixels:N0} pixels.", nameof(FindColorStep.MaxPixels)));
                else if (c.MaxPixels > 0 && c.MaxPixels < c.MinPixels)
                    issues.Add(Error("Maximum size must be at least the minimum size (or 0 for unlimited).", nameof(FindColorStep.MaxPixels)));
                if (c.MinCircularityPercent is < 0 or > 100)
                    issues.Add(Error("Minimum roundness must be 0% to 100%.", nameof(FindColorStep.MinCircularityPercent)));
                if (c.MinFillPercent is < 0 or > 100)
                    issues.Add(Error("Minimum fill density must be 0% to 100%.", nameof(FindColorStep.MinFillPercent)));
                ValidateSearch(issues, c, screen, MinVisionCheckIntervalMs);
                if (SearchArea.IsWholeMonitorOrMore(c))
                    issues.Add(Warning("Searching a whole monitor (or every monitor) for a color often finds something else. Pick a small area or an app window if you can.", "Region"));
                break;

            case WaitForChangeStep w:
                ValidateWaitForChange(issues, w, screen);
                break;

            case FocusWindowStep w:
                if (string.IsNullOrWhiteSpace(w.WindowTitle))
                    issues.Add(Error("Enter (part of) the window title, e.g. \"Roblox\", or pick it from the list.", nameof(FocusWindowStep.WindowTitle)));
                else if (w.WindowTitle.Trim().Length > SearchArea.MaxWindowTitleLength)
                    issues.Add(Error($"The window title can be at most {SearchArea.MaxWindowTitleLength} characters.", nameof(FocusWindowStep.WindowTitle)));
                if (w.WaitForWindowMs < 0 || w.WaitForWindowMs > MaxDelayMs)
                    issues.Add(Error($"\"Wait for the window\" must be 0 to {MaxDelayMs:N0} ms.", nameof(FocusWindowStep.WaitForWindowMs)));
                if (w.SettleMs < 0 || w.SettleMs > MaxHoldMs)
                    issues.Add(Error($"The pause after focusing must be 0 to {MaxHoldMs:N0} ms.", nameof(FocusWindowStep.SettleMs)));
                if (!Enum.IsDefined(w.WhenNotFound))
                    issues.Add(Error("This step has an unknown option value."));
                ValidateFailurePolicy(issues, w.FailurePolicy);
                break;

            case ScreenshotStep s:
                ValidateScreenshot(issues, s, screen);
                break;

            case SetVariableStep v:
                ValidateSetVariable(issues, v, screen);
                break;

            case CheckpointStep cp:
                if (!CheckpointStep.IsValidName(cp.Name))
                    issues.Add(Error($"Enter a checkpoint name (1 to {CheckpointStep.MaxNameLength} characters, no line breaks).", nameof(CheckpointStep.Name)));
                break;

            case CallMacroStep call:
                ValidateCallMacro(issues, call);
                break;

            default:
                issues.Add(Error($"Unsupported step type: {step.GetType().Name}."));
                break;
        }

        return issues;
    }

    /// <summary>Smart Click Snapshots: the recorded picture and the Smart-Aim settings.</summary>
    private static void ValidateSmartClick(List<ValidationIssue> issues, MouseClickStep c)
    {
        if (c.TargetThumbnailBase64 is { } thumbnail)
        {
            if (thumbnail.Length > MaxThumbnailLength)
            {
                issues.Add(Error("The recorded \"what you clicked\" picture is too big. The macro may be damaged.", nameof(MouseClickStep.TargetThumbnailBase64)));
                return;
            }

            if (c.SmartAimAtTarget && SmartClickConversion.TryDecodeThumbnail(c, out var error) is null)
                issues.Add(Warning($"Smart-Aim can't use the recorded picture ({error}), so this click always uses its X / Y.", nameof(MouseClickStep.SmartAimAtTarget)));
        }

        if (c.TargetAnchorX is < 0 or > MaxTemplateSize || c.TargetAnchorY is < 0 or > MaxTemplateSize)
            issues.Add(Error("The recorded click spot inside the picture is invalid. The macro may be damaged.", nameof(MouseClickStep.TargetAnchorX)));

        if (double.IsNaN(c.SmartAimAccuracy) || c.SmartAimAccuracy < MinSmartAimAccuracy || c.SmartAimAccuracy > MaxSmartAimAccuracy)
            issues.Add(Error($"Smart-Aim accuracy must be {MinSmartAimAccuracy:P0} to {MaxSmartAimAccuracy:P0}.", nameof(MouseClickStep.SmartAimAccuracy)));
        if (c.SmartAimRadiusPx < MinSmartAimRadiusPx || c.SmartAimRadiusPx > MaxSmartAimRadiusPx)
            issues.Add(Error($"The Smart-Aim distance must be {MinSmartAimRadiusPx} to {MaxSmartAimRadiusPx:N0} px.", nameof(MouseClickStep.SmartAimRadiusPx)));

        if (c.SmartAimAtTarget)
        {
            if (!c.HasTargetThumbnail)
                issues.Add(Warning("Smart-Aim needs a recorded picture of what was clicked, and this click has none, so it always uses its X / Y.", nameof(MouseClickStep.SmartAimAtTarget)));
            else if (c.UseCurrentPosition || c.UseLastFoundText)
                issues.Add(Warning("Smart-Aim only works with \"At a chosen position\": it looks for the picture around X / Y.", nameof(MouseClickStep.SmartAimAtTarget)));
        }

        if (c.Target is { } target && !IsValidTargetFingerprint(target))
        {
            issues.Add(Error("The recorded click Intent metadata is invalid or too long. The macro may be damaged.", nameof(MouseClickStep.Target)));
        }

        if (c.UseSmartIntent)
        {
            if (!c.HasTargetIntent && !c.HasTargetThumbnail)
                issues.Add(Warning("Smart Intent has no recorded UI element, text, window, or picture for this click, so it will fall back to X / Y.", nameof(MouseClickStep.UseSmartIntent)));
            else if (c.UseCurrentPosition || c.UseLastFoundText)
                issues.Add(Warning("Smart Intent only applies when clicking \"At a chosen position\".", nameof(MouseClickStep.UseSmartIntent)));
        }
    }

    private static bool IsValidTargetFingerprint(TargetFingerprint fp)
    {
        static bool TooLong(string? s) => s is { Length: > TargetFingerprint.MaxFieldLength };

        if (TooLong(fp.ElementName) || TooLong(fp.AutomationId) || TooLong(fp.ControlType)
            || TooLong(fp.ClassName) || TooLong(fp.NearbyText) || TooLong(fp.WindowTitle)
            || TooLong(fp.WindowProcessName))
            return false;

        if (fp.ElementNormalizedX is { } nx && (double.IsNaN(nx) || double.IsInfinity(nx) || nx is < 0 or > 1))
            return false;
        if (fp.ElementNormalizedY is { } ny && (double.IsNaN(ny) || double.IsInfinity(ny) || ny is < 0 or > 1))
            return false;

        if (Math.Abs((long)fp.NearbyTextOffsetX) > MaxOffset || Math.Abs((long)fp.NearbyTextOffsetY) > MaxOffset)
            return false;

        if (fp.WindowWidth < 0 || fp.WindowHeight < 0
            || fp.WindowWidth > MaxScreenCoordinate || fp.WindowHeight > MaxScreenCoordinate)
            return false;

        if (fp.WindowRelativeX is { } rx && Math.Abs((long)rx) > MaxScreenCoordinate)
            return false;
        if (fp.WindowRelativeY is { } ry && Math.Abs((long)ry) > MaxScreenCoordinate)
            return false;

        return true;
    }

    /// <summary>Wait for screen change: an area on screen, how much must change, timing, and the "when it changes" action.</summary>
    private static void ValidateWaitForChange(List<ValidationIssue> issues, WaitForChangeStep w, ScreenBounds? screen)
    {
        if (w.RegionWidth < 5 || w.RegionHeight < 5)
            issues.Add(Error("The watched area must be at least 5×5 pixels. Use \"Pick area\".", "Region"));
        else if (w.RegionWidth > MaxRegionSize || w.RegionHeight > MaxRegionSize)
            issues.Add(Error($"The watched area can be at most {MaxRegionSize:N0} pixels wide or tall.", "Region"));
        else
        {
            if (screen is not null && !w.Region.IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The watched area is outside your current screen.", "Region"));
            if ((long)w.RegionWidth * w.RegionHeight > LargeWatchAreaPixels)
                issues.Add(Warning("The watched area is very large, so every look takes longer and unrelated changes (chat, animations) can trigger it. Pick only the part that matters.", "Region"));
        }

        if (!double.IsFinite(w.MinChangedPercent) || w.MinChangedPercent < MinChangedPercent || w.MinChangedPercent > 100)
            issues.Add(Error($"\"How much must change\" must be {MinChangedPercent} to 100 %.", nameof(WaitForChangeStep.MinChangedPercent)));
        if (w.PixelTolerance < 0 || w.PixelTolerance > 255)
            issues.Add(Error("\"Ignore small colour shifts\" must be 0 to 255.", nameof(WaitForChangeStep.PixelTolerance)));
        else if (w.PixelTolerance < 5)
            issues.Add(Warning("A colour tolerance below 5 reacts to tiny flicker that you can't see. 20–40 is typical.", nameof(WaitForChangeStep.PixelTolerance)));
        if (w.TimeoutMs < 0 || w.TimeoutMs > MaxDelayMs)
            issues.Add(Error($"The wait time must be 0 to {MaxDelayMs:N0} ms.", nameof(WaitForChangeStep.TimeoutMs)));
        if (w.CheckIntervalMs < MinVisionCheckIntervalMs || w.CheckIntervalMs > MaxCheckIntervalMs)
            issues.Add(Error($"Check every must be {MinVisionCheckIntervalMs} to {MaxCheckIntervalMs:N0} ms.", nameof(WaitForChangeStep.CheckIntervalMs)));
        if (!Enum.IsDefined(w.Action) || !Enum.IsDefined(w.WhenNotFound))
            issues.Add(Error("This step has an unknown option value."));
        else if (w.Action == FoundTextAction.Hold && (w.ActionHoldMs < 1 || w.ActionHoldMs > MaxHoldMs))
            issues.Add(Error($"The hold time must be 1 to {MaxHoldMs:N0} ms.", nameof(WaitForChangeStep.ActionHoldMs)));
        ValidateFailurePolicy(issues, w.FailurePolicy);
    }

    private static void ValidateSetVariable(List<ValidationIssue> issues, SetVariableStep v, ScreenBounds? screen)
    {
        if (!MacroVariableDefinition.IsValidName(v.VariableName))
            issues.Add(Error($"Enter a valid variable name (start with a letter or _, then letters, digits, or _, up to {MacroVariableDefinition.MaxNameLength} chars).", nameof(SetVariableStep.VariableName)));
        if (!Enum.IsDefined(v.VariableType) || !Enum.IsDefined(v.Operation) || !Enum.IsDefined(v.Source))
            issues.Add(Error("This Set Variable step has an unknown option value."));
        if ((v.Value?.Length ?? 0) > MacroVariableDefinition.MaxValueLength)
            issues.Add(Error($"The value can be at most {MacroVariableDefinition.MaxValueLength:N0} characters.", nameof(SetVariableStep.Value)));

        if (v.UsesScreenRegion)
        {
            if (v.RegionWidth < 5 || v.RegionHeight < 5)
                issues.Add(Error("The screen region to read must be at least 5×5 pixels. Use \"Pick area\".", "Region"));
            else if (v.RegionWidth > MaxRegionSize || v.RegionHeight > MaxRegionSize)
                issues.Add(Error($"The screen region can be at most {MaxRegionSize:N0} pixels wide or tall.", "Region"));
            else if (screen is not null && !v.Region.IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The screen region is outside your current screen.", "Region"));
        }

        if (v.Source == VariableSourceKind.ScreenGaugePercent)
        {
            if (!GaugeFillReader.TryParseHexColor(v.EffectiveGaugeColor, out _, out _, out _))
                issues.Add(Error("Enter the bar fill color as #RRGGBB (for example #22C55E), or pick it from the screen.", nameof(SetVariableStep.GaugeColor)));
            if (v.GaugeTolerance is < 0 or > 255)
                issues.Add(Error("Bar color tolerance must be 0 to 255.", nameof(SetVariableStep.GaugeTolerance)));
            if (!Enum.IsDefined(v.GaugeDirection))
                issues.Add(Error("Unknown bar fill direction.", nameof(SetVariableStep.GaugeDirection)));
        }
    }

    private static void ValidateCallMacro(List<ValidationIssue> issues, CallMacroStep call)
    {
        if (call.TargetMacroId == Guid.Empty && string.IsNullOrWhiteSpace(call.TargetMacroName))
            issues.Add(Error("Choose a library sub-macro to call.", nameof(CallMacroStep.TargetMacroId)));
        if ((call.TargetMacroName?.Length ?? 0) > MaxNameLength)
            issues.Add(Error($"Sub-macro name can be at most {MaxNameLength} characters.", nameof(CallMacroStep.TargetMacroName)));

        ValidateBindings(issues, call.Inputs, isOutput: false, nameof(CallMacroStep.Inputs));
        ValidateBindings(issues, call.Outputs, isOutput: true, nameof(CallMacroStep.Outputs));
    }

    private static void ValidateBindings(List<ValidationIssue> issues, List<VariableBinding>? bindings, bool isOutput, string property)
    {
        if (bindings is null) return;
        if (bindings.Count > MaxVariableBindings)
        {
            issues.Add(Error($"At most {MaxVariableBindings} variable bindings are allowed.", property));
            return;
        }

        foreach (var b in bindings)
        {
            if (b is null)
            {
                issues.Add(Error("A variable binding is empty.", property));
                continue;
            }

            if (!MacroVariableDefinition.IsValidName(b.TargetVariable))
                issues.Add(Error($"Enter a valid target variable name in {(isOutput ? "Outputs" : "Inputs")}.", property));

            if (isOutput)
            {
                if (!MacroVariableDefinition.IsValidName(b.SourceExpression))
                    issues.Add(Error("Enter a valid sub-macro variable name to read in Outputs.", property));
            }
            else if ((b.SourceExpression?.Length ?? 0) > MacroVariableDefinition.MaxValueLength)
            {
                issues.Add(Error($"Binding value can be at most {MacroVariableDefinition.MaxValueLength:N0} characters.", property));
            }
        }
    }

    private static void ValidateFailurePolicy(List<ValidationIssue> issues, StepFailurePolicy? policy)
    {
        if (policy is null) return;
        if (!Enum.IsDefined(policy.Action))
        {
            issues.Add(Error("Unknown failure policy action.", nameof(StepFailurePolicy.Action)));
            return;
        }

        switch (policy.Action)
        {
            case StepFailureAction.Retry:
                if (policy.RetryCount < 1 || policy.RetryCount > MaxRetryCount)
                    issues.Add(Error($"Retry count must be 1 to {MaxRetryCount}.", nameof(StepFailurePolicy.RetryCount)));
                if (policy.RetryDelayMs < 0 || policy.RetryDelayMs > MaxCheckIntervalMs)
                    issues.Add(Error($"Retry delay must be 0 to {MaxCheckIntervalMs:N0} ms.", nameof(StepFailurePolicy.RetryDelayMs)));
                break;

            case StepFailureAction.RunRecoveryMacro:
                if ((policy.RecoveryMacroId is null || policy.RecoveryMacroId == Guid.Empty) && string.IsNullOrWhiteSpace(policy.RecoveryMacroName))
                    issues.Add(Error("Choose a recovery macro to run when this step fails.", nameof(StepFailurePolicy.RecoveryMacroId)));
                else if ((policy.RecoveryMacroName?.Length ?? 0) > MaxNameLength)
                    issues.Add(Error($"Recovery macro name can be at most {MaxNameLength} characters.", nameof(StepFailurePolicy.RecoveryMacroName)));
                break;

            case StepFailureAction.RestartFromCheckpoint:
                if (!CheckpointStep.IsValidName(policy.CheckpointName))
                    issues.Add(Error($"Enter the checkpoint name to restart from (1 to {CheckpointStep.MaxNameLength} chars).", nameof(StepFailurePolicy.CheckpointName)));
                break;
        }
    }

    /// <summary>Screenshot: what to capture (an area / window when chosen), a usable folder and file name prefix.</summary>
    private static void ValidateScreenshot(List<ValidationIssue> issues, ScreenshotStep s, ScreenBounds? screen)
    {
        if (!Enum.IsDefined(s.Area))
        {
            issues.Add(Error("This step has an unknown option value."));
            return;
        }

        if (s.Area == ScreenshotArea.CustomArea)
        {
            if (s.RegionWidth < 5 || s.RegionHeight < 5)
                issues.Add(Error("The screenshot area must be at least 5×5 pixels. Use \"Pick area\".", "Region"));
            else if (s.RegionWidth > MaxRegionSize || s.RegionHeight > MaxRegionSize)
                issues.Add(Error($"The screenshot area can be at most {MaxRegionSize:N0} pixels wide or tall.", "Region"));
            else if (screen is not null && !s.Region.IntersectsWith(new Ocr.ScreenRect(screen.Left, screen.Top, screen.Width, screen.Height)))
                issues.Add(Warning("The screenshot area is outside your current screen.", "Region"));
        }
        else if (s.Area == ScreenshotArea.Window)
        {
            if (string.IsNullOrWhiteSpace(s.WindowTitle))
                issues.Add(Error("Enter (part of) the window title to take a screenshot of.", nameof(ScreenshotStep.WindowTitle)));
            else if (s.WindowTitle.Trim().Length > SearchArea.MaxWindowTitleLength)
                issues.Add(Error($"The window title can be at most {SearchArea.MaxWindowTitleLength} characters.", nameof(ScreenshotStep.WindowTitle)));
        }

        if (!string.IsNullOrWhiteSpace(s.FolderPath))
        {
            var folder = s.FolderPath.Trim();
            if (folder.Length > MaxFolderPathLength || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(folder))
                issues.Add(Error("Choose a full folder path, e.g. C:\\Users\\you\\Pictures\\Catches (or leave it empty for Pictures\\SmartMacro Screenshots).", nameof(ScreenshotStep.FolderPath)));
        }

        if ((s.FileNamePrefix?.Trim().Length ?? 0) > ScreenshotFiles.MaxPrefixLength)
            issues.Add(Error($"The file name prefix can be at most {ScreenshotFiles.MaxPrefixLength} characters.", nameof(ScreenshotStep.FileNamePrefix)));
        else if (ScreenshotFiles.HasInvalidFileNameChars(s.FileNamePrefix))
            issues.Add(Warning("The file name prefix has characters a file name can't contain (< > : \" / \\ | ? *); they're replaced by _.", nameof(ScreenshotStep.FileNamePrefix)));
    }

    /// <summary>
    /// Repeat: known mode; a count for Times; a condition plus a required safety limit (max turns and/or max time)
    /// for While / Until; limits in range.
    /// </summary>
    private static void ValidateRepeat(List<ValidationIssue> issues, RepeatStep r, ScreenBounds? screen)
    {
        if (!Enum.IsDefined(r.Mode) || !Enum.IsDefined(r.WhenLimitReached))
        {
            issues.Add(Error("This Repeat step has an unknown option value. The macro may be damaged."));
            return;
        }

        if (r.MaxDurationMs < 0 || r.MaxDurationMs > MaxLoopDurationMs)
            issues.Add(Error($"Max time must be 0 (no time limit) to {MaxLoopDurationMs:N0} ms (24 hours).", nameof(RepeatStep.MaxDurationMs)));
        if (r.KeepCheckingForMs is < 0 or > RepeatStep.MaxKeepCheckingForMs)
            issues.Add(Error($"\"Keep checking before leaving\" must be 0 to {RepeatStep.MaxKeepCheckingForMs:N0} ms.", nameof(RepeatStep.KeepCheckingForMs)));

        if (r.Mode == RepeatMode.Times)
        {
            if (r.Count < 1 || r.Count > MaxLoopCount)
                issues.Add(Error($"Repeat count must be 1 to {MaxLoopCount:N0}.", nameof(RepeatStep.Count)));
            return;
        }

        if (r.MaxIterations < 0 || r.MaxIterations > MaxLoopCount)
            issues.Add(Error($"Max turns must be 0 (no turn limit) to {MaxLoopCount:N0}.", nameof(RepeatStep.MaxIterations)));
        else if (r.MaxIterations == 0 && r.MaxDurationMs == 0)
            issues.Add(Error("A Repeat while / until loop needs a safety limit: set Max turns and/or Max time, so it can't run forever.", nameof(RepeatStep.MaxIterations)));

        if (r.Condition is null)
            issues.Add(Error("Choose the condition for this loop.", nameof(RepeatStep.Condition)));
        else
            ValidateCondition(issues, r.Condition, screen);
    }

    /// <summary>
    /// A condition needs search settings of a supported kind; those are checked with the same rules as the
    /// Find text / image / color step (messages start with "Condition:").
    /// </summary>
    private static void ValidateCondition(List<ValidationIssue> issues, Condition? condition, ScreenBounds? screen,
        string label = "Condition", string property = "Condition")
    {
        if (condition is null)
        {
            issues.Add(Error($"{label}: choose what the condition checks.", property));
            return;
        }

        if (condition.Comparison is { } comp && condition.Check is null)
        {
            if (!MacroVariableDefinition.IsValidName(comp.VariableName))
                issues.Add(Error($"{label}: enter a valid variable name to compare.", property + ".VariableName"));
            if (!Enum.IsDefined(comp.Operator))
                issues.Add(Error($"{label}: unknown variable comparison operator.", property + ".Operator"));
            if ((comp.RightValue?.Length ?? 0) > MacroVariableDefinition.MaxValueLength)
                issues.Add(Error($"{label}: comparison value can be at most {MacroVariableDefinition.MaxValueLength:N0} characters.", property + ".RightValue"));
            return;
        }

        if (condition.Check is null)
        {
            issues.Add(Error($"{label}: choose what the condition looks for (text, image, color, or variable).", property));
            return;
        }

        if (condition.Search is not { } search)
        {
            issues.Add(Error($"{label}: a condition can't use a \"{condition.Check.GetType().Name}\" step. Use text, image or color. The macro may be damaged.", property));
            return;
        }

        foreach (var issue in ValidateStep(search, screen))
        {
            // The "when found" action, wait goal and "when not found" choice aren't used by conditions.
            if (issue.Property is nameof(ScreenSearchStep.Action) or nameof(ScreenSearchStep.WhenNotFound) or nameof(ScreenSearchStep.Goal)
                or nameof(StepFailurePolicy.Action) or nameof(StepFailurePolicy.RetryCount) or nameof(StepFailurePolicy.RetryDelayMs)
                or nameof(StepFailurePolicy.RecoveryMacroId) or nameof(StepFailurePolicy.CheckpointName)) continue;
            issues.Add(issue with { Message = label + ": " + issue.Message, Property = property + "." + (issue.Property ?? "") });
        }
    }
}
