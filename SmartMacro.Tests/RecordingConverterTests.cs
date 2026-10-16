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

        Assert.Equal("Click left button at (10, 20) | Wait 500 ms | Click left button at (30, 40)", Describe(steps));
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

        Assert.Equal("Hold down Left Ctrl | Press C | Release Left Ctrl", Describe(steps));
    }

    [Fact]
    public void Orphan_releases_are_ignored_and_held_keys_released_at_end()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyUp(0, 0x11),                // released after recording started (start hotkey)
            Up(10, 0, 0),                  // same for a mouse button
            KeyDown(20, 0x41),             // pressed but never released
            KeyDown(900, 0x41),            // auto-repeat
        }, new RecordingOptions { MinDelayMs = 100000 });

        Assert.Equal("Hold down A | Hold down A | Release A", Describe(steps));
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

        Assert.Equal("Click left button at (1, 1)", Describe(steps));
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
}
