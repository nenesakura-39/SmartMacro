using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>The 1-click "📚 Blueprints" (editor toolbar, Ctrl+K palette, Manual recipe cards).</summary>
public enum MacroBlueprint
{
    /// <summary>If the image is on screen → click where it was found; otherwise wait 500 ms.</summary>
    VisionClickWithFallback,

    /// <summary>Repeat 50× → press Space, wait 1.5 s (Humanize on).</summary>
    HumanizedAntiAfkLoop,

    /// <summary>Wait up to 5 s for a colour → click it.</summary>
    PixelColorTrigger,

    /// <summary>Repeat 10× → a 1 s feedback loop (minigame bar).</summary>
    MinigameTrackingLoop,
}

/// <summary>
/// A blueprint's texts. <see cref="EnablesHumanize"/>: inserting it also turns the macro's Humanize option on.
/// <see cref="Keywords"/> help the palette / Manual search.
/// </summary>
public sealed record MacroBlueprintInfo(MacroBlueprint Blueprint, string Glyph, string Title, string Shape, string Description,
    string Keywords, bool EnablesHumanize = false)
{
    /// <summary>"🎯 Vision Click + Fallback".</summary>
    public string DisplayName => $"{Glyph} {Title}";
}

/// <summary>
/// Builds the blueprints: normal, editable steps with beginner defaults (main monitor, smooth glide…) and balanced
/// If / Repeat blocks. Images / colours still have to be captured; the validator points at exactly what is left.
/// </summary>
public static class MacroBlueprints
{
    public const int FallbackWaitMs = 500;
    public const int AntiAfkTurns = 50;
    public const int AntiAfkWaitMs = 1500;
    public const int ColorWaitMs = 5000;
    public const int MinigameTurns = 10;
    public const int MinigameLoopMs = 1000;

    private const int VkSpace = 0x20;

    public static IReadOnlyList<MacroBlueprintInfo> All { get; } = new[]
    {
        new MacroBlueprintInfo(MacroBlueprint.VisionClickWithFallback, "🎯", "Vision Click + Fallback",
            "If Image → Click Found → Else → Wait",
            "If the image is on screen, click exactly where it was found; otherwise wait 500 ms and carry on. Capture the image in the If, then 🧪 Test it (Ctrl+T).",
            "image picture button vision click found fallback else if"),
        new MacroBlueprintInfo(MacroBlueprint.HumanizedAntiAfkLoop, "🔄", "Humanized Anti-AFK Loop",
            "Repeat 50× → Key Press + Delay",
            "Presses Space every 1.5 s, 50 times, with Humanize on (small random timing changes) so it doesn't look robotic. Change the key or the count in Properties.",
            "afk idle loop repeat key space humanize random", EnablesHumanize: true),
        new MacroBlueprintInfo(MacroBlueprint.PixelColorTrigger, "⚡", "Pixel Color Trigger",
            "Wait for Color → Click Found",
            "Waits up to 5 s for a colour to appear (e.g. a loot glow or a ready light) and clicks it. Pick the colour and the area to watch.",
            "color colour pixel trigger loot wait click"),
        new MacroBlueprintInfo(MacroBlueprint.MinigameTrackingLoop, "⚖️", "Minigame Tracking Loop",
            "Repeat → Feedback Loop",
            "Runs a 1 s feedback loop 10 times: it keeps a marker inside a zone by holding / releasing (fishing / balance bars). Pick the bar area and the marker / zone colours.",
            "minigame bar fishing feedback marker zone tracking loop"),
    };

    public static MacroBlueprintInfo Info(MacroBlueprint blueprint) => All.First(b => b.Blueprint == blueprint);

    /// <summary>The steps of a blueprint, in order (fresh objects and IDs every call).</summary>
    public static List<MacroStep> Build(MacroBlueprint blueprint) => blueprint switch
    {
        MacroBlueprint.VisionClickWithFallback => VisionClickWithFallback(),
        MacroBlueprint.HumanizedAntiAfkLoop => AntiAfkLoop(),
        MacroBlueprint.PixelColorTrigger => ColorTrigger(),
        MacroBlueprint.MinigameTrackingLoop => MinigameLoop(),
        _ => throw new ArgumentOutOfRangeException(nameof(blueprint), blueprint, "Unknown blueprint."),
    };

    private static List<MacroStep> VisionClickWithFallback()
    {
        var click = (MouseClickStep)MacroFactory.CreateStep(StepKind.MouseClick);
        click.TargetMode = ClickTargetMode.LastFoundMatch;
        click.Comment = "Clicks where the If found the image";
        var wait = (DelayStep)MacroFactory.CreateStep(StepKind.Delay, FallbackWaitMs);
        wait.Comment = "Not on screen: wait a moment";

        return new List<MacroStep>
        {
            new IfStep { Condition = MacroFactory.NewCondition(ConditionKind.ImageVisible), Comment = "Capture the image to look for" },
            click,
            new ElseStep(),
            wait,
            new EndIfStep(),
        };
    }

    private static List<MacroStep> AntiAfkLoop()
    {
        var press = (KeyPressStep)MacroFactory.CreateStep(StepKind.KeyPress);
        press.VirtualKey = VkSpace;
        press.Action = KeyAction.Press;
        press.Modifiers = KeyModifiers.None;

        return new List<MacroStep>
        {
            new RepeatStep { Mode = RepeatMode.Times, Count = AntiAfkTurns, Comment = "Anti-AFK" },
            press,
            MacroFactory.CreateStep(StepKind.Delay, AntiAfkWaitMs),
            new EndRepeatStep(),
        };
    }

    private static List<MacroStep> ColorTrigger()
    {
        var find = (FindColorStep)MacroFactory.CreateStep(StepKind.FindColor);
        find.TimeoutMs = ColorWaitMs;
        find.Action = FoundTextAction.Click;
        find.Comment = "Pick the colour to wait for";
        return new List<MacroStep> { find };
    }

    private static List<MacroStep> MinigameLoop()
    {
        var feedback = (FeedbackLoopStep)MacroFactory.CreateStep(StepKind.FeedbackLoop);
        feedback.MaxRuntimeMs = MinigameLoopMs;
        feedback.Comment = "Pick the bar area and the marker / zone colours";

        return new List<MacroStep>
        {
            new RepeatStep { Mode = RepeatMode.Times, Count = MinigameTurns, Comment = "Minigame rounds" },
            feedback,
            new EndRepeatStep(),
        };
    }
}
