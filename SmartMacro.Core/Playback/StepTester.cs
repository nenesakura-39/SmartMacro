using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Result of "🧪 Test Step" (Ctrl+T) on a vision step (Find image / colour / text) or an If / Repeat with a screen
/// condition: one live look at the screen, no playback. <see cref="Text"/> is the status line, e.g.
/// "🧪 Test Passed: Found at (512, 340) · 96% confidence · 0.8ms" or
/// "🧪 Test Failed: Not found (Closest match on screen: 79% at 512, 340 — step requires 85%)".
/// <see cref="Match"/> = what was found (empty on a miss); <see cref="NearMiss"/> / <see cref="NearMissConfidence"/> = the
/// closest thing on screen when it wasn't found (empty when unknown). <see cref="ConditionIsTrue"/> is set for If / Repeat.
/// </summary>
public sealed record StepTestResult(
    bool Passed,
    string Text,
    ScreenRect Match = default,
    double Confidence = 0,
    double ElapsedMs = 0,
    ScreenRect NearMiss = default,
    double NearMissConfidence = 0,
    double RequiredConfidence = 0,
    bool? ConditionIsTrue = null)
{
    /// <summary>A near miss this close (or closer) is worth flashing on screen in amber.</summary>
    public const double NearMissFlashThreshold = 0.50;

    /// <summary>True when the amber "almost matched here" reticle should be shown.</summary>
    public bool ShowNearMiss => !Passed && !NearMiss.IsEmpty && NearMissConfidence >= NearMissFlashThreshold;
}

/// <summary>
/// The logic behind "🧪 Test Step": which steps can be tested by looking at the screen, one look with the same
/// detection code playback uses (<see cref="ScreenProbes"/>), and the plain-English result. Pure Core, unit tested with
/// fake screens; the app adds hiding SmartMacro and the on-screen reticle.
/// </summary>
public static class StepTester
{
    /// <summary>
    /// The screen search a step tests: a Find image / colour / text step itself, or the condition of an If / Repeat
    /// (While / Until). Null for steps that don't look at the screen (those are executed once instead).
    /// </summary>
    public static ScreenSearchStep? SearchOf(MacroStep? step) => step switch
    {
        ScreenSearchStep search => search,
        IfStep { Condition.Search: { } check } => check,
        RepeatStep { Mode: not RepeatMode.Times, Condition.Search: { } check } => check,
        _ => null,
    };

    /// <summary>True when <see cref="TestAsync"/> can test the step by looking at the screen.</summary>
    public static bool IsVisionTest(MacroStep? step) => SearchOf(step) is not null;

    /// <summary>
    /// Looks at the screen once for the step's target (a condition's "keep checking for" time is ignored: one look) and
    /// describes the result. Never throws for "can't look" problems (no OCR, no capture, damaged picture): those come back
    /// as a failed test with the reason.
    /// </summary>
    public static async Task<StepTestResult> TestAsync(ScreenProbes probes, MacroStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(step);
        if (SearchOf(step) is not { } search)
            return new StepTestResult(false, "🧪 This step doesn't look at the screen, so there is nothing to detect.");

        ScreenProbe probe;
        try
        {
            probe = await probes.Prepare(search).LookAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepTestResult(false, $"🧪 Test Failed: {ex.Message}");
        }

        var negate = step is IfStep { Condition.Negate: true } or RepeatStep { Condition.Negate: true };
        bool? conditionIsTrue = step is IfStep or RepeatStep ? probe.Found != negate : null;
        return Describe(probe, search, conditionIsTrue);
    }

    /// <summary>The result line for one look (also used by the tests).</summary>
    public static StepTestResult Describe(ScreenProbe probe, ScreenSearchStep search, bool? conditionIsTrue = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        var condition = conditionIsTrue is { } isTrue ? $" → condition is {(isTrue ? "TRUE" : "FALSE")}" : string.Empty;

        if (probe.Found)
        {
            var text = $"🧪 Test Passed: Found at ({probe.X}, {probe.Y}) · {Percent(probe.Confidence)} confidence · {TargetDetection.FormatMs(probe.ElapsedMs)}{condition}";
            return new StepTestResult(true, text, probe.Match, probe.Confidence, probe.ElapsedMs, probe.Match, probe.Confidence,
                probe.RequiredConfidence, conditionIsTrue);
        }

        string failed;
        if (!probe.BestMatch.IsEmpty && probe.BestConfidence > 0)
        {
            failed = search switch
            {
                FindColorStep color => $"🧪 Test Failed: Not found (Closest patch on screen: {Math.Round(probe.BestConfidence * color.MinPixels):0} px at {probe.BestX}, {probe.BestY} — step requires {color.MinPixels} px)",
                _ => $"🧪 Test Failed: Not found (Closest match on screen: {Percent(probe.BestConfidence)} at {probe.BestX}, {probe.BestY} — step requires {Percent(probe.RequiredConfidence)})",
            };
        }
        else
        {
            var why = probe.MissText.Trim();
            failed = why.Length == 0 ? "🧪 Test Failed: Not found." : $"🧪 Test Failed: Not found. {why}";
        }

        return new StepTestResult(false, failed + condition, default, 0, probe.ElapsedMs, probe.BestMatch, probe.BestConfidence,
            probe.RequiredConfidence, conditionIsTrue);
    }

    /// <summary>"🧪 Executed step #4: Press Space" for an action step that was run once.</summary>
    public static string ExecutedText(int stepNumber, MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return $"🧪 Executed step #{stepNumber}: {StepDescriber.Describe(step)}";
    }

    private static string Percent(double value) =>
        ((int)Math.Round(Math.Clamp(value, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}
