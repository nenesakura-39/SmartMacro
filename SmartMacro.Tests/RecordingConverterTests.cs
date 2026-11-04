using SmartMacro.Core.Models;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

public class RecordingConverterTests
{
    private static RawInputEvent Down(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseDown, x, y, b);
    private static RawInputEvent Up(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseUp, x, y, b);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private static string Describe(IEnumerable<MacroStep> steps) => string.Join(" | ", steps.Select(StepDescriber.Describe));

    [Fact]
    public void Quick_down_up_becomes_a_click_with_delays_between()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Down(1000, 10, 20), Up(1080, 10, 20),
            Down(1580, 30, 40), Up(1650, 31, 41),
        });

        // Each click keeps how long it was held; the gap between them is still 500 ms.
        Assert.Equal("Click left button at (10, 20) (hold 80 ms) | Wait 500 ms | Click left button at (30, 40) (hold 70 ms)", Describe(steps));
    }

    [Fact]
    public void No_leading_delay()
    {
        var steps = RecordingConverter.Convert(new[] { Down(5000, 1, 1), Up(5050, 1, 1) });
        Assert.IsType<MouseClickStep>(Assert.Single(steps));
    }

    [Fact]
    public void Long_hold_becomes_down_delay_up()
    {
        var steps = RecordingConverter.Convert(new[] { Down(0, 5, 5), Up(1200, 5, 5) });
        Assert.Equal("Press left button at (5, 5) | Wait 1.2 s | Release left button at (5, 5)", Describe(steps));
    }

    [Fact]
    public void Drag_keeps_movement()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Down(0, 0, 0), Move(50, 50, 0), Move(100, 100, 0), Up(150, 100, 0),
        });

        Assert.Equal(
            "Press left button at (0, 0) | Wait 50 ms | Move to (50, 0) | Wait 50 ms | Move to (100, 0) | Wait 50 ms | Release left button at (100, 0)",
            Describe(steps));
    }

    [Fact]
    public void Mouse_moves_are_thinned_and_can_be_turned_off()
    {
        var events = Enumerable.Range(0, 100).Select(i => Move(i * 5, i, i)).ToArray(); // every 5 ms

        var thinned = RecordingConverter.Convert(events, new RecordingOptions { MinMoveIntervalMs = 30 });
        Assert.InRange(thinned.OfType<MouseMoveStep>().Count(), 15, 20);

        var none = RecordingConverter.Convert(events, new RecordingOptions { RecordMouseMoves = false });
        Assert.Empty(none);
    }

    [Fact]
    public void Key_tap_becomes_press_and_shortcut_keeps_modifier_held()
    {
        // Ctrl down, C tap, Ctrl up = Ctrl+C
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, 0xA2), KeyDown(100, 0x43), KeyUp(150, 0x43), KeyUp(200, 0xA2),
        }, new RecordingOptions { MinDelayMs = 1000 });

        Assert.Equal("Hold down Left Ctrl | Press C (hold 50 ms) | Release Left Ctrl", Describe(steps));
    }

    [Fact]
    public void Orphan_releases_are_ignored_and_held_keys_released_at_end()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyUp(0, 0x11),                // released after recording started (start hotkey)
            Up(10, 0, 0),                  // same for a mouse button
            KeyDown(20, 0x41),             // pressed but never released
            KeyDown(900, 0x41),            // auto-repeat: not a new press (recording timing fix 2)
        }, new RecordingOptions { MinDelayMs = 100000 });

        Assert.Equal("Hold down A | Release A", Describe(steps));
    }

    [Fact]
    public void Stop_hotkey_is_trimmed_from_the_end()
    {
        int[] hotkey = { 0x11, 0xA2, 0x10, 0xA0, 0x78 };
        var steps = RecordingConverter.Convert(new[]
        {
            Down(0, 1, 1), Up(50, 1, 1),
            KeyDown(1000, 0xA2), KeyDown(1050, 0xA0), Move(1060, 3, 3), KeyDown(1100, 0x78),
        }, new RecordingOptions { TrimTrailingKeys = hotkey });

        Assert.Equal("Click left button at (1, 1) (hold 50 ms)", Describe(steps));
    }

    [Fact]
    public void Wheel_becomes_scroll_at_position()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            new RawInputEvent(0, RawInputKind.Wheel, 7, 8, Delta: -120),
            new RawInputEvent(10, RawInputKind.HorizontalWheel, 7, 8, Delta: 120),
            new RawInputEvent(20, RawInputKind.Wheel, 7, 8, Delta: 0),
        }, new RecordingOptions { MinDelayMs = 1000 });

        Assert.Equal("Scroll down 1 notch at (7, 8) | Scroll right 1 notch at (7, 8)", Describe(steps));
    }

    [Fact]
    public void Converted_steps_are_always_valid()
    {
        var random = new Random(42);
        var events = new List<RawInputEvent>();
        long t = 0;
        for (var i = 0; i < 2000; i++)
        {
            t += random.Next(0, 300);
            var kind = (RawInputKind)random.Next(0, 7);
            events.Add(new RawInputEvent(t, kind, random.Next(0, 1920), random.Next(0, 1080),
                (MacroMouseButton)random.Next(0, 3), random.Next(-3, 4) * 120, random.Next(0, 300)));
        }

        var macro = new Macro { Name = "Fuzz", Steps = RecordingConverter.Convert(events) };

        Assert.False(MacroValidator.Validate(macro).HasErrors());
    }

    [Fact]
    public void XButton1_and_XButton2_clicks_are_converted_and_described()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Down(0, 100, 200, MacroMouseButton.XButton1), Up(50, 100, 200, MacroMouseButton.XButton1),
            Down(500, 300, 400, MacroMouseButton.XButton2), Up(560, 300, 400, MacroMouseButton.XButton2),
        });

        Assert.Equal(
            "Click mouse 4 (back) button at (100, 200) (hold 50 ms) | Wait 450 ms | Click mouse 5 (forward) button at (300, 400) (hold 60 ms)",
            Describe(steps));
    }

    [Fact]
    public void KeyPressStep_preserves_hardware_ScanCode_and_Extended_flag()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            new RawInputEvent(0, RawInputKind.KeyDown, VirtualKey: 0x57, ScanCode: 0x11, Extended: false),
            new RawInputEvent(60, RawInputKind.KeyUp, VirtualKey: 0x57, ScanCode: 0x11, Extended: false),
            new RawInputEvent(200, RawInputKind.KeyDown, VirtualKey: 0x26, ScanCode: 0x48, Extended: true), // Up Arrow (unreleased)
        }, new RecordingOptions { MinDelayMs = 1000 });

        Assert.Equal(3, steps.Count);
        var wTap = Assert.IsType<KeyPressStep>(steps[0]);
        Assert.Equal(KeyAction.Press, wTap.Action);
        Assert.Equal(0x11, wTap.ScanCode);
        Assert.False(wTap.Extended);

        var upDown = Assert.IsType<KeyPressStep>(steps[1]);
        Assert.Equal(KeyAction.Down, upDown.Action);
        Assert.Equal(0x48, upDown.ScanCode);
        Assert.True(upDown.Extended);

        var upRelease = Assert.IsType<KeyPressStep>(steps[2]);
        Assert.Equal(KeyAction.Up, upRelease.Action);
        Assert.Equal(0x48, upRelease.ScanCode);
        Assert.True(upRelease.Extended);
    }

    [Fact]
    public void CoordinateScaling_scales_InputSequenceStep_positions()
    {
        var seq = new InputSequenceStep
        {
            StartX = 960,
            StartY = 540,
            Events = new List<InputEvent>
            {
                new() { TimeUs = 0, Kind = InputEventKind.MouseMove, X = 960, Y = 540, HasPosition = true },
                new() { TimeUs = 10_000, Kind = InputEventKind.MouseDown, Button = MacroMouseButton.Left, X = 1920, Y = 1080, HasPosition = true },
                new() { TimeUs = 20_000, Kind = InputEventKind.KeyDown, VirtualKey = 0x41 },
            },
        };

        var scaler = new SmartMacro.Core.Playback.CoordinateMapping(
            new SmartMacro.Core.Ocr.ScreenRect(0, 0, 1920, 1080),
            new SmartMacro.Core.Ocr.ScreenRect(0, 0, 3840, 2160));

        SmartMacro.Core.Playback.CoordinateScaling.ApplyInPlace(new MacroStep[] { seq }, scaler);

        Assert.Equal(1920, seq.StartX);
        Assert.Equal(1080, seq.StartY);
        Assert.Equal(1920, seq.Events[0].X);
        Assert.Equal(1080, seq.Events[0].Y);
        Assert.Equal(3840, seq.Events[1].X);
        Assert.Equal(2160, seq.Events[1].Y);
    }

    [Fact]
    public void ExpandInputSequence_preserves_ScanCode_and_Extended_flags()
    {
        var seq = new InputSequenceStep
        {
            Events = new List<InputEvent>
            {
                new() { TimeUs = 0, Kind = InputEventKind.KeyDown, VirtualKey = 0x26, ScanCode = 0x48, Extended = true },
                new() { TimeUs = 40_000, Kind = InputEventKind.KeyUp, VirtualKey = 0x26, ScanCode = 0x48, Extended = true },
            },
        };

        var expanded = SmartMacro.Core.Workflow.StepCleanup.ExpandInputSequence(seq);
        var keyStep = Assert.Single(expanded.OfType<KeyPressStep>());
        Assert.Equal(0x26, keyStep.VirtualKey);
        Assert.Equal(0x48, keyStep.ScanCode);
        Assert.True(keyStep.Extended);
    }

    [Fact]
    public void SmartClickConversion_rejects_Mouse4_and_Mouse5_buttons()
    {
        var click = new MouseClickStep
        {
            Button = MacroMouseButton.XButton1,
            Action = MouseClickAction.Click,
            TargetThumbnailBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        };

        Assert.False(SmartMacro.Core.Workflow.SmartClickConversion.HasConvertibleAction(click));
        Assert.Contains("Left or Right", SmartMacro.Core.Workflow.SmartClickConversion.WhyNotConvertible(click) ?? "");
    }
}
