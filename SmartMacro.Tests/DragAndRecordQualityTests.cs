using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;
using Xunit;

namespace SmartMacro.Tests;

public sealed class DragAndRecordQualityTests
{
    [Fact]
    public void DragStepPlanner_subdivides_long_drag_segment_into_bounded_substeps()
    {
        var steps = DragStepPlanner.ComputeSubSteps((100, 200), (220, 200), maxStepPx: 10);

        Assert.True(steps.Count >= 12);
        Assert.Equal((220, 200), steps[^1]);

        var prev = (X: 100, Y: 200);
        foreach (var pt in steps)
        {
            var dist = Math.Sqrt(Math.Pow(pt.X - prev.X, 2) + Math.Pow(pt.Y - prev.Y, 2));
            Assert.True(dist <= 10.5, $"Expected sub-step <= 10.5px, got {dist:F2}px");
            prev = pt;
        }
    }

    [Fact]
    public void DragStepPlanner_computes_closed_loop_relative_delta_and_adapts_gain()
    {
        var (dx, dy) = DragStepPlanner.ComputeClosedLoopRelativeDelta(
            targetX: 250, targetY: 110, currentX: 200, currentY: 100, gain: 1.0, maxDeltaPx: 16);

        Assert.True(dx > 0);
        Assert.True(dy > 0);
        var mag = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(mag <= 17.0);

        // Simulate 2x OS pointer ballistics (sent 10 counts, cursor moved 20 px) -> gain adapts downward toward 0.5.
        var adapted = DragStepPlanner.AdaptGain(currentGain: 1.0, sentMagnitude: 10.0, actualMovedPx: 20.0);
        Assert.True(adapted < 1.0 && adapted >= 0.5);
    }

    [Fact]
    public void RecordingConverter_attaches_click_snapshot_and_smart_intent_to_MouseDown_drag_anchor()
    {
        var fingerprint = new TargetFingerprint
        {
            WindowTitle = "Untitled - Notepad",
            WindowProcessName = "notepad",
            WindowRelativeX = 200,
            WindowRelativeY = 18,
            WindowWidth = 800,
            WindowHeight = 600,
        };
        var snapshot = ClickSnapshots.FromBase64(Convert.ToBase64String(PngCodec.Encode(Aaa.Button)), 28, 28, fingerprint);

        var raw = new List<RawInputEvent>
        {
            new(0, RawInputKind.MouseDown, X: 300, Y: 120, Button: MacroMouseButton.Left, ClickedTarget: snapshot),
            new(40, RawInputKind.MouseMove, X: 450, Y: 120),
            new(80, RawInputKind.MouseMove, X: 200, Y: 120),
            new(120, RawInputKind.MouseUp, X: 200, Y: 120, Button: MacroMouseButton.Left),
        };

        var steps = RecordingConverter.Convert(raw, new RecordingOptions
        {
            RecordMouseMoves = true,
            SimplifyMousePath = true,
            AutoEnableSmartIntent = true,
        });

        var down = Assert.IsType<MouseClickStep>(steps[0]);
        Assert.Equal(MouseClickAction.Down, down.Action);
        Assert.True(down.HasTargetThumbnail);
        Assert.True(down.HasTargetIntent);
        Assert.True(down.UseSmartIntent);
        Assert.Equal("notepad", down.Target?.WindowProcessName);
    }

    [Fact]
    public void RecordingPostProcessor_deduplicates_stationary_moves_without_changing_timestamps()
    {
        var raw = new List<RawInputEvent>
        {
            new(10, RawInputKind.MouseMove, X: 100, Y: 100),
            new(25, RawInputKind.MouseMove, X: 100, Y: 100),
            new(40, RawInputKind.MouseMove, X: 100, Y: 100),
            new(60, RawInputKind.MouseMove, X: 150, Y: 100),
        };

        var cleaned = RecordingPostProcessor.DeduplicateStationaryMoves(raw, out var removed);
        Assert.Equal(2, removed);
        Assert.Equal(2, cleaned.Count);
        Assert.Equal(10, cleaned[0].TimeMs);
        Assert.Equal(60, cleaned[1].TimeMs);
    }

    [Fact]
    public void RecordingPostProcessor_coalesces_linear_drag_and_groups_oscillating_window_drag()
    {
        var fp = new TargetFingerprint { WindowProcessName = "notepad", WindowTitle = "Notepad", WindowRelativeX = 250, WindowRelativeY = 20, WindowWidth = 500, WindowHeight = 400 };
        var linearSteps = new List<MacroStep>
        {
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Down, X = 100, Y = 100, Target = fp, UseSmartIntent = true },
            new DelayStep { DurationMs = 50 },
            new MouseMoveStep { X = 200, Y = 100 },
            new DelayStep { DurationMs = 50 },
            new MouseMoveStep { X = 300, Y = 100 },
            new DelayStep { DurationMs = 50 },
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Up, X = 300, Y = 100 },
        };

        var linearRes = RecordingPostProcessor.StructureRecordedDrags(linearSteps, coalesceLinearDrags: true, groupMultiPointDrags: true);
        Assert.Equal(1, linearRes.CoalescedDrags);
        var dragStep = Assert.Single(linearRes.Steps.OfType<MouseClickStep>());
        Assert.Equal(MouseClickAction.Drag, dragStep.Action);
        Assert.Equal(100, dragStep.X);
        Assert.Equal(300, dragStep.EndX);
        Assert.Equal(150, dragStep.DragDurationMs);
        Assert.Equal(MouseMoveStyle.SmoothGlide, dragStep.MoveStyle);
        Assert.True(dragStep.UseSmartIntent);
        Assert.Equal("notepad", dragStep.Target?.WindowProcessName);

        // Oscillating Notepad title-bar drag (left and right): must be grouped, NOT collapsed into a straight line.
        var oscillatingSteps = new List<MacroStep>
        {
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Down, X = 300, Y = 100 },
            new DelayStep { DurationMs = 40 },
            new MouseMoveStep { X = 500, Y = 100 },
            new DelayStep { DurationMs = 40 },
            new MouseMoveStep { X = 180, Y = 100 },
            new DelayStep { DurationMs = 40 },
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Up, X = 180, Y = 100 },
        };

        var oscRes = RecordingPostProcessor.StructureRecordedDrags(oscillatingSteps, coalesceLinearDrags: true, groupMultiPointDrags: true);
        Assert.Equal(0, oscRes.CoalescedDrags);
        Assert.Equal(1, oscRes.GroupedDrags);
        Assert.IsType<BeginGroupStep>(oscRes.Steps[0]);
        Assert.IsType<EndGroupStep>(oscRes.Steps[^1]);
    }

    [Fact]
    public async Task PlaybackEngine_translates_intermediate_drag_moves_and_up_when_SmartIntent_relocates_Down()
    {
        var fakeInput = new RecordingSequenceInput();
        var fakeUi = new RelocatingUiLocator(resolvedPoint: (420, 160)); // shifted by (+120, +60) from recorded (300, 100)
        var engine = new PlaybackEngine(fakeInput, uiLocator: fakeUi);

        var macro = new Macro
        {
            Name = "Notepad Window Drag",
            Steps =
            {
                new MouseClickStep
                {
                    Button = MacroMouseButton.Left,
                    Action = MouseClickAction.Down,
                    X = 300,
                    Y = 100,
                    UseSmartIntent = true,
                    Target = new TargetFingerprint
                    {
                        WindowProcessName = "notepad",
                        AutomationId = "TitleBar",
                    },
                },
                new MouseMoveStep { X = 400, Y = 100 },
                new MouseMoveStep { X = 250, Y = 100 },
                new MouseClickStep
                {
                    Button = MacroMouseButton.Left,
                    Action = MouseClickAction.Up,
                    X = 250,
                    Y = 100,
                },
            },
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);

        // Down happened at relocated (420, 160), Move 1 shifted to (520, 160), Move 2 shifted to (370, 160), Up shifted to (370, 160).
        Assert.Contains(("Move", 420, 160), fakeInput.Events);
        Assert.Contains(("Down", 0, 0), fakeInput.Events);
        Assert.Contains(("Move", 520, 160), fakeInput.Events);
        Assert.Contains(("Move", 370, 160), fakeInput.Events);
        Assert.Equal(("Up", 0, 0), fakeInput.Events[^1]);
    }

    [Fact]
    public void StepCleanup_PolishRecordedWorkflow_and_ExpandDragStepToMoves_roundtrip_cleanly()
    {
        var steps = new List<MacroStep>
        {
            new DelayStep { DurationMs = 30 },
            new DelayStep { DurationMs = 45 },
            new KeyPressStep { VirtualKey = 0x48, Action = KeyAction.Press }, // 'H'
            new DelayStep { DurationMs = 20 },
            new KeyPressStep { VirtualKey = 0x49, Action = KeyAction.Press }, // 'I'
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Down, X = 100, Y = 100 },
            new DelayStep { DurationMs = 50 },
            new MouseMoveStep { X = 200, Y = 100 },
            new DelayStep { DurationMs = 50 },
            new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Up, X = 300, Y = 100 },
        };

        var polished = StepCleanup.PolishRecordedWorkflow(steps);
        Assert.True(polished.TotalChanges >= 3);
        Assert.Equal(1, polished.CreatedTypeSteps);
        Assert.Equal(1, polished.CoalescedDrags);
        Assert.True(polished.MergedDelays >= 1);

        var dragStep = Assert.Single(polished.Steps.OfType<MouseClickStep>());
        Assert.Equal(MouseClickAction.Drag, dragStep.Action);

        var expanded = StepCleanup.ExpandDragStepToMoves(dragStep, stepIntervalMs: 25);
        Assert.IsType<MouseClickStep>(expanded[0]);
        Assert.Equal(MouseClickAction.Down, ((MouseClickStep)expanded[0]).Action);
        Assert.True(expanded.OfType<MouseMoveStep>().Any());
        Assert.IsType<MouseClickStep>(expanded[^1]);
        Assert.Equal(MouseClickAction.Up, ((MouseClickStep)expanded[^1]).Action);
    }

    [Fact]
    public void MacroValidator_ValidateInputHygiene_and_LibraryAuditService_report_unbalanced_holds_and_large_drag_jumps()
    {
        var macro = new Macro
        {
            Name = "Unbalanced Drag",
            Steps =
            {
                new MouseClickStep { Button = MacroMouseButton.Left, Action = MouseClickAction.Down, X = 100, Y = 100 },
                new MouseMoveStep { X = 500, Y = 100, MoveStyle = MouseMoveStyle.Instant },
            },
        };

        var issues = MacroValidator.ValidateInputHygiene(macro);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("jumps 400 px", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Message.Contains("no matching 'Release Left button'", StringComparison.OrdinalIgnoreCase));

        var audit = LibraryAuditService.Audit(new[] { macro });
        Assert.Contains(audit.Entries, e => e.Category == "Input Hygiene");
    }

    private sealed class RecordingSequenceInput : IInputSimulator
    {
        public List<(string Kind, int X, int Y)> Events { get; } = new();
        private (int X, int Y) _cursor;

        public void MoveMouse(int x, int y)
        {
            _cursor = (x, y);
            Events.Add(("Move", x, y));
        }

        public void MoveMouseRelative(int dx, int dy)
        {
            _cursor = (_cursor.X + dx, _cursor.Y + dy);
            Events.Add(("RelMove", dx, dy));
        }

        public bool TryGetCursorPosition(out int x, out int y)
        {
            (x, y) = _cursor;
            return true;
        }

        public void MouseButton(MacroMouseButton button, bool down) =>
            Events.Add((down ? "Down" : "Up", 0, 0));

        public void Scroll(int delta, bool horizontal = false) { }
        public void Key(int virtualKey, bool down) { }
        public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) { }
        public void TypeCharacters(string text) { }
    }

    private sealed class RelocatingUiLocator : IUiElementLocator
    {
        private readonly (int X, int Y) _point;
        public RelocatingUiLocator((int X, int Y) resolvedPoint) => _point = resolvedPoint;
        public bool IsAvailable => true;
        public UiElementSnapshot? InspectPoint(int x, int y, CancellationToken cancellationToken = default) => null;

        public Task<UiElementMatch?> FindAsync(
            TargetFingerprint target,
            SmartMacro.Core.Ocr.ScreenRect? windowBounds,
            (int X, int Y)? expectedPoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UiElementMatch?>(new UiElementMatch(
                new SmartMacro.Core.Ocr.ScreenRect(_point.X - 40, _point.Y - 12, 80, 24),
                0.98,
                "UI Automation: TitleBar"));
    }
}
