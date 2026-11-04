using System.Runtime.CompilerServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;


public sealed class InputSequenceStepViewModel : StepViewModel
{
    private readonly InputSequenceStep _step;
    private InputSequenceSummary _summary;

    public InputSequenceStepViewModel(InputSequenceStep step) : base(step)
    {
        _step = step;
        _summary = StepDescriber.Summarize(step);

        // A damaged file could contain empty entries; the validator reports those, the editor just skips them.
        var segments = (step.MouseSegments ?? new List<MouseSegment>()).Where(segment => segment is not null).ToList();
        var lastUs = step.Events is { Count: > 0 } events ? events[^1].TimeUs : 0;
        MouseParts = segments
            .Select((segment, index) => new MouseSegmentViewModel(
                segment, index + 1,
                index + 1 < segments.Count ? segments[index + 1].StartUs : Math.Max(lastUs, segment.StartUs),
                OnMousePartChanged))
            .ToList();

        UseCursorEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.CursorPosition), () => HasMouseParts);
        UseRelativeEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.Relative), () => HasMouseParts);
        UseAutomaticEverywhereCommand = new RelayCommand(() => SetAll(MouseModeChoice.Automatic), () => HasMouseParts);
    }

    /// <summary>Smart recordings: one entry per mouse part. Empty for game-mode recordings.</summary>
    public IReadOnlyList<MouseSegmentViewModel> MouseParts { get; }

    public bool HasMouseParts => MouseParts.Count > 0;

    /// <summary>Game-mode recordings (always relative) show the older explanation.</summary>
    public bool IsGameModeRecording => !HasMouseParts;

    public RelayCommand UseCursorEverywhereCommand { get; }
    public RelayCommand UseRelativeEverywhereCommand { get; }
    public RelayCommand UseAutomaticEverywhereCommand { get; }

    public string PlaybackExplanation => HasMouseParts
        ? "Played back with the original timing. Each part below plays the mouse one way only: \"Cursor position\" puts the cursor on the recorded screen spot (menus, buttons); \"Relative\" sends the recorded mouse movement (camera turning while a game locks the cursor). SmartMacro guessed each part while recording; change a part if it guessed wrong. The macro's Speed setting also applies here."
        : "Recorded in game mode: played back with the original timing, as relative mouse movement and exact key down/up. Start from the same spot and camera direction you recorded from. The macro's Speed setting also applies here.";

    public string DurationText => $"{_summary.DurationMs / 1000.0:0.00} s";

    public string EventCountText => $"{_summary.EventCount:N0}";

    public string MouseText => HasMouseParts
        ? $"{_summary.MouseMoves:N0} moves, each with a cursor position and relative counts ({_summary.CursorParts} cursor-position part(s), {_summary.RelativeParts} relative part(s))"
        : $"{_summary.MouseMoves:N0} moves (total {_summary.TotalAbsDx:N0} horizontal, {_summary.TotalAbsDy:N0} vertical counts)";

    private void SetAll(MouseModeChoice choice)
    {
        var changed = false;
        foreach (var part in MouseParts) changed |= part.SetChoiceSilently(choice);
        if (changed) OnMousePartChanged();
    }

    private void OnMousePartChanged()
    {
        _summary = StepDescriber.Summarize(_step);
        OnPropertyChanged(nameof(MouseText));
        OnPropertyChanged(nameof(CanBreakIntoSteps));
        OnPropertyChanged(nameof(BreakIntoStepsHint));
        NotifyChanged(nameof(MouseParts));
    }

    /// <summary>"✂ Break into steps" is possible (cursor-position input only).</summary>
    public bool CanBreakIntoSteps => SmartMacro.Core.Workflow.StepCleanup.WhyNotExpandable(_step) is null;

    /// <summary>What "✂ Break into steps" does, or why it can't here.</summary>
    public string BreakIntoStepsHint => SmartMacro.Core.Workflow.StepCleanup.WhyNotExpandable(_step) is { } why
        ? $"Can't break this one into steps: {why}"
        : $"Turns this recording into normal steps you can edit one by one (Delay, Move mouse, Mouse click, Key press…). " +
          (_step.ClickTargets is { Count: > 0 } targets
              ? $"{targets.Count} click(s) keep their \"what you clicked\" picture, so Smart-Aim still works. "
              : "") + "Ctrl+Z undoes it.";

    public string KeysText => _summary.KeyPresses == 0
        ? "None"
        : $"{_summary.KeyPresses:N0} presses: {string.Join(", ", _summary.KeysUsed)}{(_summary.KeysUsed.Count >= 12 ? ", …" : "")}";

    public string ButtonsText => $"{_summary.ButtonPresses:N0} button presses, {_summary.WheelEvents:N0} wheel events";

    public bool MoveCursorToStart
    {
        get => _step.MoveCursorToStart;
        set { if (_step.MoveCursorToStart == value) return; _step.MoveCursorToStart = value; NotifyChanged(); }
    }

    public int StartX
    {
        get => _step.StartX;
        set { if (_step.StartX == value) return; _step.StartX = value; NotifyChanged(); }
    }

    public int StartY
    {
        get => _step.StartY;
        set { if (_step.StartY == value) return; _step.StartY = value; NotifyChanged(); }
    }
}

/// <summary>One mouse part of a smart recording in the editor: what was detected, and the user's choice.</summary>
public sealed class MouseSegmentViewModel : ObservableObject
{
    private readonly MouseSegment _segment;
    private readonly Action _changed;

    public MouseSegmentViewModel(MouseSegment segment, int number, long endUs, Action changed)
    {
        _segment = segment;
        _changed = changed;
        Number = number;
        RangeText = $"{segment.StartUs / 1_000_000.0:0.00} s – {endUs / 1_000_000.0:0.00} s";
    }

    public static IReadOnlyList<Choice<MouseModeChoice>> Choices { get; } = new[]
    {
        new Choice<MouseModeChoice>(MouseModeChoice.Automatic, "Automatic (use what was detected)"),
        new Choice<MouseModeChoice>(MouseModeChoice.CursorPosition, "Cursor position (menus, buttons)"),
        new Choice<MouseModeChoice>(MouseModeChoice.Relative, "Relative movement (camera)"),
    };

    public int Number { get; }

    public string Title => $"Part {Number}";

    public string RangeText { get; }

    public string DetectedText => $"Detected: {ModeName(_segment.Detected)} — {EvidenceText(_segment.Evidence)}";

    public string EffectiveText => $"Plays as: {ModeName(_segment.Effective)}";

    public MouseModeChoice SelectedMode
    {
        get => _segment.Choice;
        set
        {
            if (!SetChoiceSilently(value)) return;
            _changed();
        }
    }

    /// <summary>Changes the choice without telling the step (used by the "all parts" buttons, which tell it once).</summary>
    internal bool SetChoiceSilently(MouseModeChoice value)
    {
        if (_segment.Choice == value || !Enum.IsDefined(value)) return false;
        _segment.Choice = value;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(EffectiveText));
        return true;
    }

    private static string ModeName(MouseMovementMode mode) => mode == MouseMovementMode.Relative ? "relative movement" : "cursor position";

    private static string EvidenceText(MouseContextEvidence evidence) => evidence switch
    {
        MouseContextEvidence.FreeCursor => "the cursor followed the mouse",
        MouseContextEvidence.HiddenCursor => "the cursor was hidden",
        MouseContextEvidence.ConfinedCursor => "the cursor was confined to one spot",
        MouseContextEvidence.LockedCursor => "the mouse moved but the cursor stayed in place",
        _ => "not enough movement to tell, so it follows the part before",
    };
}
