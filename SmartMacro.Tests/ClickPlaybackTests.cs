using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;

namespace SmartMacro.Tests;

/// <summary>
/// Desktop-mode clicks: where the cursor goes, the order and timing of button down/up, and error reporting.
/// Fake input only. These prove what SmartMacro sends; whether Roblox (or any app) reacts to it has to be
/// checked by hand on Windows.
/// </summary>
public class ClickPlaybackTests
{
    private static RawInputEvent Down(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseDown, x, y, b);
    private static RawInputEvent Up(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseUp, x, y, b);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    private static async Task<(FakeInput Input, PlaybackResult Result)> PlayAsync(List<MacroStep> steps)
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var result = await new PlaybackEngine(input, clock).RunAsync(new Macro { Name = "Click", Steps = steps });
        return (input, result);
    }

    [Fact]
    public async Task Recorded_click_moves_to_the_recorded_pixel_then_presses_and_releases_after_the_hold()
    {
        // A UI click on a windowed game at (812, 455), held 70 ms, after the cursor arrived there.
        var steps = RecordingConverter.Convert(new[] { Move(0, 800, 450), Move(40, 812, 455), Down(300, 812, 455), Up(370, 812, 455) });
        var (input, result) = await PlayAsync(steps);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 800,450", "move 812,455", "move 812,455", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 40, 300, 300, 370 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Right_button_drag_keeps_down_moves_up_order()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Down(0, 500, 400, MacroMouseButton.Right), Move(50, 540, 400), Move(100, 580, 400), Up(150, 580, 400, MacroMouseButton.Right),
        });
        var (input, _) = await PlayAsync(steps);

        Assert.Equal(new[] { "move 500,400", "mouse Right down", "move 540,400", "move 580,400", "move 580,400", "mouse Right up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 0, 50, 100, 150, 150 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Desktop_and_game_mode_use_the_same_button_calls()
    {
        var (desktop, _) = await PlayAsync(new List<MacroStep>
        {
            new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.Down },
            new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.Up },
        });
        var (game, _) = await PlayAsync(new List<MacroStep>
        {
            new InputSequenceStep { Events = { InputEvent.ButtonDown(0, MacroMouseButton.Left), InputEvent.ButtonUp(10_000, MacroMouseButton.Left) } },
        });

        Assert.Equal(new[] { "mouse Left down", "mouse Left up" }, desktop.Calls);
        Assert.Equal(desktop.Calls, game.Calls);
    }

    [Fact]
    public async Task Failed_release_is_reported_and_retried_during_cleanup()
    {
        var input = new FakeInput { ThrowOn = new InputSimulationException("blocked"), ThrowWhen = "mouse Left up" };

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(new Macro
        {
            Name = "Fail",
            Steps = { new MouseClickStep { X = 10, Y = 20, HoldMs = 80 } },
        });

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Equal("blocked", result.Error);
        Assert.NotNull(result.Warning); // the cleanup release was also refused, and that is reported too
        Assert.Equal(new[] { "move 10,20", "mouse Left down", "mouse Left up", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task Failed_move_means_the_button_is_never_pressed()
    {
        var input = new FakeInput { ThrowOn = new InputSimulationException("refused"), ThrowWhen = "move 10,20" };

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(new Macro
        {
            Name = "Fail",
            Steps = { new MouseClickStep { X = 10, Y = 20 } },
        });

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("mouse Left down", input.Calls);
    }

    // ---------------- Absolute coordinate math (used for the SendInput move) ----------------

    [Theory]
    [InlineData(0, 1920)]
    [InlineData(-1920, 3840)]   // second monitor on the left
    [InlineData(0, 2560)]
    [InlineData(-1280, 4480)]
    public void Every_pixel_round_trips_through_normalized_coordinates(int origin, int size)
    {
        Assert.Equal(0, AbsoluteMouseMath.Normalize(origin, origin, size));
        Assert.Equal(AbsoluteMouseMath.MaxNormalized, AbsoluteMouseMath.Normalize(origin + size - 1, origin, size));

        for (var x = origin; x < origin + size; x++)
            Assert.Equal(x, AbsoluteMouseMath.ToPixel(AbsoluteMouseMath.Normalize(x, origin, size), origin, size));
    }

    [Fact]
    public void Positions_off_the_screen_are_clamped_to_the_edge()
    {
        Assert.Equal(0, AbsoluteMouseMath.Normalize(-50, 0, 1920));
        Assert.Equal(AbsoluteMouseMath.MaxNormalized, AbsoluteMouseMath.Normalize(5000, 0, 1920));
        Assert.Equal(0, AbsoluteMouseMath.Normalize(10, 0, 1)); // degenerate screen size
    }
}
