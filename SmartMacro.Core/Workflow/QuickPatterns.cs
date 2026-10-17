using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Workflow;

/// <summary>The ready-made multi-step starters offered under "Quick patterns" in the editor's Add step panel.</summary>
public enum QuickPattern
{
    /// <summary>Repeat while a button is on screen: find it and click its center (e.g. a game's "Shake" button).</summary>
    AutoClickButtonLoop,

    /// <summary>Hold to cast → the Shake loop → a feedback loop that follows the fish (reeling minigame).</summary>
    FishingCastShakeReel,

    /// <summary>Find a shop item and click it → scroll the list → click the Buy button.</summary>
    ShopClickAndBuy,
}

/// <summary>A Quick pattern's name and explanation for the editor.</summary>
public sealed record QuickPatternInfo(QuickPattern Pattern, string Glyph, string Title, string Description);

/// <summary>
/// Builds the steps of a Quick pattern: a normal, editable skeleton with sensible beginner settings (main monitor,
/// smooth glide, 120 ms wait after clicks, safety limits on every loop). Pictures / colours still have to be captured,
/// so the validator points the user at exactly what is left to set up. Plain data only, no game-specific code.
/// </summary>
public static class QuickPatterns
{
    /// <summary>How long the Shake loop keeps looking before it decides the button is really gone.</summary>
    public const int ShakeGraceMs = 800;

    /// <summary>The Shake loop's safety limit.</summary>
    public const int ShakeLoopMaxMs = 60_000;

    /// <summary>Default "hold to cast" time.</summary>
    public const int CastHoldMs = 800;

    public static IReadOnlyList<QuickPatternInfo> All { get; } = new[]
    {
        new QuickPatternInfo(QuickPattern.AutoClickButtonLoop, "⚡", "Auto-Click Dynamic Button Loop (e.g. Shake)",
            "Adds a Repeat that runs while a button is on screen (with an 800 ms grace period for buttons that re-appear), and inside it a Find image that glides to the button, clicks its center, waits 120 ms and moves the cursor away. Capture the button image in both."),
        new QuickPatternInfo(QuickPattern.FishingCastShakeReel, "\U0001F3A3", "Fishing Cast + Shake + Reel Full Template",
            "Adds a full fishing skeleton: hold the mouse 800 ms to cast, the Shake button loop, then a Feedback loop that follows the fish on a left / right reeling bar. Capture the Shake image, then pick the reeling bar area and colors."),
        new QuickPatternInfo(QuickPattern.ShopClickAndBuy, "\U0001F6D2", "Shop / Menu Click & Buy Loop",
            "Adds a Find image that clicks a shop item when it appears, a Scroll wheel step (3 notches down) and a Mouse click for the Buy button. Capture the item image and pick the Buy button position."),
    };

    public static QuickPatternInfo Info(QuickPattern pattern) => All.First(p => p.Pattern == pattern);

    /// <summary>The steps of a pattern, in order (fresh objects every call).</summary>
    public static List<MacroStep> Build(QuickPattern pattern) => pattern switch
    {
        QuickPattern.AutoClickButtonLoop => AutoClickLoop("Button"),
        QuickPattern.FishingCastShakeReel => FishingTemplate(),
        QuickPattern.ShopClickAndBuy => ShopTemplate(),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unknown quick pattern."),
    };

    /// <summary>Repeat while (image on screen, 800 ms grace) → Find image (click center, glide, move away) → End Repeat.</summary>
    private static List<MacroStep> AutoClickLoop(string name)
    {
        var condition = MacroFactory.NewCondition(ConditionKind.ImageVisible);
        var check = (FindImageStep)condition.Search!;
        check.TimeoutMs = 0;
        check.CheckIntervalMs = 100;

        var repeat = new RepeatStep
        {
            Mode = RepeatMode.While,
            Condition = condition,
            KeepCheckingForMs = ShakeGraceMs,
            MaxIterations = 0,
            MaxDurationMs = ShakeLoopMaxMs,
            WhenLimitReached = LoopLimitBehavior.ExitLoop,
            Comment = $"{name} loop: capture the {name.ToLowerInvariant()} image in this condition",
        };

        var find = MacroFactory.WithNewSearchDefaults(new FindImageStep());
        find.Action = FoundTextAction.Click;
        find.TimeoutMs = 1_000;
        find.CheckIntervalMs = 100;
        find.WhenNotFound = TextNotFoundBehavior.Continue;
        find.MoveCursorAwayAfterAction = true;
        find.PostActionDelayMs = MouseMotionDefaults.PostActionDelayMs;
        find.Comment = $"Click {name.ToLowerInvariant()}: capture the same image here";

        return new List<MacroStep> { repeat, find, new EndRepeatStep() };
    }

    /// <summary>Hold 800 ms (cast) → Delay → Shake loop → Feedback loop (horizontal bar follower).</summary>
    private static List<MacroStep> FishingTemplate()
    {
        var cast = (MouseClickStep)MacroFactory.CreateStep(StepKind.MouseClick);
        cast.Action = MouseClickAction.Hold;
        cast.HoldMs = CastHoldMs;
        cast.UseCurrentPosition = true;
        cast.Comment = "Cast (hold the mouse button)";

        var wait = new DelayStep { DurationMs = 1_000, Comment = "Wait for the bobber" };

        var reel = (FeedbackLoopStep)MacroFactory.CreateStep(StepKind.FeedbackLoop);
        reel.Orientation = BarOrientation.Horizontal;
        FeedbackPresets.Apply(reel, FeedbackPreset.FollowTargetBar);
        reel.Comment = "Reel: pick the bar area, the fish color and your bar color";

        var steps = new List<MacroStep> { cast, wait };
        steps.AddRange(AutoClickLoop("Shake"));
        steps.Add(reel);
        return steps;
    }

    /// <summary>Find image (click when found) → Scroll down 3 notches → Mouse click (Buy).</summary>
    private static List<MacroStep> ShopTemplate()
    {
        var item = MacroFactory.WithNewSearchDefaults(new FindImageStep());
        item.Action = FoundTextAction.Click;
        item.Comment = "Shop item: capture its image";

        var scroll = new MouseScrollStep { Delta = -3 * 120, UseCurrentPosition = true, Comment = "Scroll the list" };

        var buy = (MouseClickStep)MacroFactory.CreateStep(StepKind.MouseClick);
        buy.Comment = "Buy button: pick its position";

        return new List<MacroStep> { item, scroll, buy };
    }
}
