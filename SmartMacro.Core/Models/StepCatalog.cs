namespace SmartMacro.Core.Models;

/// <summary>Toolbox section / logical grouping for a <see cref="StepKind"/>.</summary>
public enum StepCategory
{
    Input,
    Vision,
    Utility,
    Logic,
    Brain,
    Recorded,
}

/// <summary>
/// Declarative metadata for a <see cref="StepKind"/> in the centralized <see cref="StepCatalog"/>.
/// Adding a new step type registers its display name, glyph, description, category, and permission flags in one place.
/// </summary>
public sealed record StepDescriptor(
    StepKind Kind,
    StepCategory Category,
    string DisplayName,
    string Glyph,
    string Description,
    bool IsAddableFromToolbox = true,
    bool RequiresExternalPermission = false);

/// <summary>
/// Single source of truth for all step types in SmartMacro.
/// </summary>
public static class StepCatalog
{
    public static IReadOnlyList<StepDescriptor> All { get; } =
    [
        new(
            StepKind.Delay,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.Delay),
            "\uE916",
            "Wait for a set amount of time."),
        new(
            StepKind.MouseMove,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.MouseMove),
            "\uE7C2",
            "Moves the cursor straight to a screen position (it jumps there; it isn't animated). Use \"Pick position\" to choose the spot on your screen."),
        new(
            StepKind.MouseClick,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.MouseClick),
            "\uE962",
            "Clicks, double-clicks, holds for a set time or drags with a mouse button: at a chosen screen position, where the cursor already is, or on what a Find step found."),
        new(
            StepKind.MouseScroll,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.MouseScroll),
            "\uE8CB",
            "Turns the mouse wheel up, down, left or right by a number of notches, where the cursor already is or at a chosen screen position."),
        new(
            StepKind.KeyPress,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.KeyPress),
            "\uE765",
            "Press a key or shortcut, or hold/release a key."),
        new(
            StepKind.TypeText,
            StepCategory.Input,
            StepDescriber.DisplayName(StepKind.TypeText),
            "\uE8D2",
            "Type a piece of text."),
        new(
            StepKind.FindText,
            StepCategory.Vision,
            StepDescriber.DisplayName(StepKind.FindText),
            "\uE721",
            "Wait for text to appear on screen (OCR), and optionally click it."),
        new(
            StepKind.FindImage,
            StepCategory.Vision,
            StepDescriber.DisplayName(StepKind.FindImage),
            "\uE8B9",
            "Wait for a picture (button, icon…) to appear on screen or disappear, and optionally click it."),
        new(
            StepKind.FindColor,
            StepCategory.Vision,
            StepDescriber.DisplayName(StepKind.FindColor),
            "\uE790",
            "Wait for a color to appear in an area (or disappear), and optionally click it. Simple color matching, not object recognition."),
        new(
            StepKind.FeedbackLoop,
            StepCategory.Vision,
            StepDescriber.DisplayName(StepKind.FeedbackLoop),
            "\uE895",
            "Keeps a moving marker in a target zone: watches a bar on screen many times per second and holds, releases or taps one key or mouse button depending on where the marker is. Always has a max runtime; Stop and Esc work at any time."),
        new(
            StepKind.WaitForChange,
            StepCategory.Vision,
            StepDescriber.DisplayName(StepKind.WaitForChange),
            "\uE890",
            "Waits until something changes inside an area (a bite, a pop-up, a loot window, a bar that moves). The middle of the change becomes the last found spot, and the step can click it right away."),
        new(
            StepKind.FocusWindow,
            StepCategory.Utility,
            StepDescriber.DisplayName(StepKind.FocusWindow),
            "\uE8A7",
            "Brings an app window (e.g. your game) to the front, restoring it if it's minimized, so the keys and clicks after it go to that app."),
        new(
            StepKind.Screenshot,
            StepCategory.Utility,
            StepDescriber.DisplayName(StepKind.Screenshot),
            "\uE722",
            "Saves a PNG of the main monitor, all monitors, an area or an app window, named with the date and time. Handy for logging rare catches and drops while a macro runs."),
        new(
            StepKind.InputSequence,
            StepCategory.Recorded,
            StepDescriber.DisplayName(StepKind.InputSequence),
            "\uE7FC",
            "Mouse and keyboard input recorded on one timeline and played back with its original timing: exact key and button down/up, plus cursor positions or relative (camera) mouse movement.",
            IsAddableFromToolbox: false),
        new(
            StepKind.If,
            StepCategory.Logic,
            StepDescriber.DisplayName(StepKind.If),
            "\uE8AB",
            "Runs the steps inside only when a condition is true: some text, an image or a color is (or is not) on screen. Adding it from the toolbox adds the If and its End If."),
        new(
            StepKind.Else,
            StepCategory.Logic,
            StepDescriber.DisplayName(StepKind.Else),
            "\uE76C",
            "Optional, inside an If block: the steps after Else run when the If's condition is false."),
        new(
            StepKind.EndIf,
            StepCategory.Logic,
            StepDescriber.DisplayName(StepKind.EndIf),
            "\uE70E",
            "Closes an If block."),
        new(
            StepKind.Repeat,
            StepCategory.Logic,
            StepDescriber.DisplayName(StepKind.Repeat),
            "\uE8EE",
            "Runs the steps inside several times, or while / until a condition is true. Loops always have a safety limit. Adding it from the toolbox adds the Repeat and its End Repeat."),
        new(
            StepKind.EndRepeat,
            StepCategory.Logic,
            StepDescriber.DisplayName(StepKind.EndRepeat),
            "\uE70E",
            "Closes a Repeat block: playback goes back to the Repeat for the next turn."),
        new(
            StepKind.SetVariable,
            StepCategory.Brain,
            StepDescriber.DisplayName(StepKind.SetVariable),
            "\uE945",
            "Stores or updates a variable in memory: a constant, a number or text read from the screen (OCR), the clipboard, the last found X/Y, or the loop counter."),
        new(
            StepKind.Checkpoint,
            StepCategory.Brain,
            StepDescriber.DisplayName(StepKind.Checkpoint),
            "\uE7C1",
            "Marks a named top-level restart point that failure policies and Watchers can jump back to."),
        new(
            StepKind.CallMacro,
            StepCategory.Brain,
            StepDescriber.DisplayName(StepKind.CallMacro),
            "\uE71B",
            "Runs another macro from your library as a reusable sub-routine, with optional Input and Output variable bindings."),
    ];

    private static readonly Dictionary<StepKind, StepDescriptor> ByKind =
        All.ToDictionary(d => d.Kind);

    public static StepDescriptor Get(StepKind kind) =>
        ByKind.TryGetValue(kind, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown step kind.");

    public static IReadOnlyList<StepDescriptor> ByCategory(StepCategory category) =>
        All.Where(d => d.Category == category).ToList();
}
