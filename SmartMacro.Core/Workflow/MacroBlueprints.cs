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

    /// <summary>v1.2 Brain: Repeat Until (gold >= 10000) → Smart Click + Adaptive Delay + Read Screen Number.</summary>
    ScreenCounterGoalLoop,

    /// <summary>v1.2 Brain: Top-level Checkpoint "start" → Find Text (Retry 2×) → Find Image (Jump back to "start").</summary>
    CheckpointRecoveryFlow,

    /// <summary>v1.2 Brain: Set Batch Variable → Call Sub-Macro (Inputs &amp; Outputs) → If Result &gt; 0.</summary>
    ModularSubMacroPipeline,
}

/// <summary>
/// A blueprint's texts. <see cref="EnablesHumanize"/>: inserting it also turns the macro's Humanize option on.
/// <see cref="RequiresTopLevel"/>: inserting it advances past any open If / Repeat block so top-level steps (e.g. Checkpoints) stay valid.
/// <see cref="Keywords"/> help the palette / Manual search.
/// </summary>
public sealed record MacroBlueprintInfo(
    MacroBlueprint Blueprint,
    string Glyph,
    string Title,
    string Shape,
    string Description,
    string Keywords,
    bool EnablesHumanize = false,
    bool RequiresTopLevel = false)
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
    public const int CounterGoalTurns = 100;
    public const int CounterGoalTarget = 10000;

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
        new MacroBlueprintInfo(MacroBlueprint.ScreenCounterGoalLoop, "🧠", "Farm Until Goal Counter",
            "Repeat Until (gold >= 10000) → Action + Read Screen Number",
            "Reads a live number off the screen each round into {gold}, counts completed {runs}, waits until the screen settles, and stops automatically once gold reaches 10,000.",
            "variable memory counter gold currency goal number read screen until farm"),
        new MacroBlueprintInfo(MacroBlueprint.CheckpointRecoveryFlow, "📍", "Checkpoint + Auto-Retry Recovery",
            "Checkpoint \"start\" → Text Retry (2×) → Image Check (Jump to \"start\")",
            "Marks a safe top-level Checkpoint, retries transient misses twice, and jumps cleanly back to \"start\" if a later screen times out.",
            "checkpoint recovery resilience retry restart jump anchor self-recovering",
            RequiresTopLevel: true),
        new MacroBlueprintInfo(MacroBlueprint.ModularSubMacroPipeline, "📦", "Modular Sub-Macro Pipeline",
            "Set Batch → Call Sub-Macro (Inputs & Outputs) → If Result",
            "Passes {batchSize} into a reusable library sub-macro, receives {earned} back into this macro's memory, and branches on the returned result.",
            "submacro composition call subroutine helper parameter input output bundle"),
    };

    public static MacroBlueprintInfo Info(MacroBlueprint blueprint) => All.First(b => b.Blueprint == blueprint);

    /// <summary>The steps of a blueprint, in order (fresh objects and IDs every call).</summary>
    public static List<MacroStep> Build(MacroBlueprint blueprint) => blueprint switch
    {
        MacroBlueprint.VisionClickWithFallback => VisionClickWithFallback(),
        MacroBlueprint.HumanizedAntiAfkLoop => AntiAfkLoop(),
        MacroBlueprint.PixelColorTrigger => ColorTrigger(),
        MacroBlueprint.MinigameTrackingLoop => MinigameLoop(),
        MacroBlueprint.ScreenCounterGoalLoop => ScreenCounterGoalLoop(),
        MacroBlueprint.CheckpointRecoveryFlow => CheckpointRecoveryFlow(),
        MacroBlueprint.ModularSubMacroPipeline => ModularSubMacroPipeline(),
        _ => throw new ArgumentOutOfRangeException(nameof(blueprint), blueprint, "Unknown blueprint."),
    };

    /// <summary>Default macro variable declarations seeded when a blueprint uses v1.2 Brain variables.</summary>
    public static IReadOnlyList<MacroVariableDefinition> BuildVariables(MacroBlueprint blueprint) => blueprint switch
    {
        MacroBlueprint.ScreenCounterGoalLoop => new[]
        {
            new MacroVariableDefinition { Name = "gold", Type = MacroVariableType.Number, DefaultValue = "0" },
            new MacroVariableDefinition { Name = "runs", Type = MacroVariableType.Number, DefaultValue = "0" },
        },
        MacroBlueprint.ModularSubMacroPipeline => new[]
        {
            new MacroVariableDefinition { Name = "batchSize", Type = MacroVariableType.Number, DefaultValue = "10" },
            new MacroVariableDefinition { Name = "earned", Type = MacroVariableType.Number, DefaultValue = "0" },
        },
        _ => Array.Empty<MacroVariableDefinition>(),
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

    private static List<MacroStep> ScreenCounterGoalLoop()
    {
        var click = (MouseClickStep)MacroFactory.CreateStep(StepKind.MouseClick);
        click.X = 500;
        click.Y = 500;
        click.Comment = "Pick or record your farming / collect click";

        var settle = (DelayStep)MacroFactory.CreateStep(StepKind.Delay, 800);
        settle.Mode = DelayMode.UntilScreenSettles;
        settle.MinDurationMs = 150;
        settle.MaxDurationMs = 2400;
        settle.Comment = "Continues as soon as the UI settles";

        return new List<MacroStep>
        {
            new RepeatStep
            {
                Mode = RepeatMode.Until,
                MaxIterations = CounterGoalTurns,
                Comment = "Stop once {gold} reaches target",
                Condition = new Condition
                {
                    Comparison = new VariableComparison
                    {
                        VariableName = "gold",
                        Operator = VariableCompareOperator.GreaterOrEqual,
                        RightValue = CounterGoalTarget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                },
            },
            new SetVariableStep
            {
                VariableName = "runs",
                VariableType = MacroVariableType.Number,
                Operation = VariableOperation.Add,
                Source = VariableSourceKind.Constant,
                Value = "1",
                Comment = "Count completed rounds",
            },
            click,
            settle,
            new SetVariableStep
            {
                VariableName = "gold",
                VariableType = MacroVariableType.Number,
                Operation = VariableOperation.Set,
                Source = VariableSourceKind.ScreenOcrNumber,
                RegionX = 100,
                RegionY = 50,
                RegionWidth = 220,
                RegionHeight = 60,
                Comment = "Pick the screen area showing your number / currency",
            },
            new EndRepeatStep(),
        };
    }

    private static List<MacroStep> CheckpointRecoveryFlow()
    {
        var findPlay = (FindTextStep)MacroFactory.CreateStep(StepKind.FindText);
        findPlay.Text = "Play";
        findPlay.MatchCase = false;
        findPlay.TimeoutMs = 3000;
        findPlay.Action = FoundTextAction.Click;
        findPlay.FailurePolicy = new StepFailurePolicy
        {
            Action = StepFailureAction.Retry,
            RetryCount = 2,
            RetryDelayMs = 500,
        };
        findPlay.Comment = "Retries twice if the button takes a moment to appear";

        var settle = (DelayStep)MacroFactory.CreateStep(StepKind.Delay, 600);
        settle.Mode = DelayMode.UntilScreenSettles;
        settle.MinDurationMs = 150;
        settle.MaxDurationMs = 2000;

        var findTarget = (FindImageStep)MacroFactory.CreateStep(StepKind.FindImage);
        findTarget.AllowMultiScale = true;
        findTarget.TimeoutMs = 5000;
        findTarget.Action = FoundTextAction.Click;
        findTarget.FailurePolicy = new StepFailurePolicy
        {
            Action = StepFailureAction.RestartFromCheckpoint,
            CheckpointName = "start",
        };
        findTarget.Comment = "Capture target image — jumps back to 'start' on timeout";

        return new List<MacroStep>
        {
            new CheckpointStep { Name = "start", Comment = "Safe top-level restart anchor" },
            findPlay,
            settle,
            findTarget,
        };
    }

    private static List<MacroStep> ModularSubMacroPipeline()
    {
        var followUpWait = (DelayStep)MacroFactory.CreateStep(StepKind.Delay, 300);
        followUpWait.Comment = "Add follow-up steps when {earned} > 0";

        return new List<MacroStep>
        {
            new SetVariableStep
            {
                VariableName = "batchSize",
                VariableType = MacroVariableType.Number,
                Operation = VariableOperation.Set,
                Source = VariableSourceKind.Constant,
                Value = "10",
                Comment = "Input parameter passed to the helper sub-macro",
            },
            new CallMacroStep
            {
                TargetMacroName = "Sell Inventory",
                Inputs = new List<VariableBinding>
                {
                    new() { TargetVariable = "count", SourceExpression = "{batchSize}" },
                },
                Outputs = new List<VariableBinding>
                {
                    new() { SourceExpression = "profit", TargetVariable = "earned" },
                },
                Comment = "Choose a helper macro from your Library",
            },
            new IfStep
            {
                Condition = new Condition
                {
                    Comparison = new VariableComparison
                    {
                        VariableName = "earned",
                        Operator = VariableCompareOperator.GreaterThan,
                        RightValue = "0",
                    },
                },
                Comment = "Branch on output returned from the sub-macro",
            },
            followUpWait,
            new EndIfStep(),
        };
    }
}
