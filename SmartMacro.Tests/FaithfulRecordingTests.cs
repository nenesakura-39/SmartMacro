using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>
/// Desktop recordings: key / button hold times, event order and total timing must survive
/// conversion and playback. These use fake input and a fake clock; they say nothing about whether a
/// real game accepts the input.
/// </summary>
public class FaithfulRecordingTests
{
    private const int W = 0x57;
    private const int A = 0x41;

    private static RawInputEvent Down(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseDown, x, y, b);
    private static RawInputEvent Up(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseUp, x, y, b);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private static string Describe(IEnumerable<MacroStep> steps) => string.Join(" | ", steps.Select(StepDescriber.Describe));

    private static async Task<(FakeInput Input, FakeClock Clock, PlaybackResult Result)> PlayAsync(List<MacroStep> steps, double speed = 1)
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var result = await new PlaybackEngine(input, clock).RunAsync(new Macro { Name = "Test", Steps = steps, Speed = speed });
        return (input, clock, result);
    }

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    // ---------------- Converter ----------------

    [Fact]
    public void Movement_key_held_less_than_500ms_keeps_its_duration()
    {
        var steps = RecordingConverter.Convert(new[] { KeyDown(1000, W), KeyUp(1120, W) });

        var press = Assert.IsType<KeyPressStep>(Assert.Single(steps));
        Assert.Equal(KeyAction.Press, press.Action);
        Assert.Equal(120, press.HoldMs);
    }

    [Fact]
    public void Movement_key_held_more_than_500ms_keeps_its_duration()
    {
        var steps = RecordingConverter.Convert(new[] { KeyDown(0, W), KeyUp(1500, W) });

        Assert.Equal("Hold down W | Wait 1.5 s | Release W", Describe(steps));
    }

    [Fact]
    public void Old_instant_taps_are_still_available_as_an_option()
    {
        var steps = RecordingConverter.Convert(new[] { KeyDown(0, W), KeyUp(120, W) }, new RecordingOptions { KeepHoldTime = false });

        Assert.Equal(0, Assert.IsType<KeyPressStep>(Assert.Single(steps)).HoldMs);
    }

    [Fact]
    public void Wasd_held_while_the_mouse_moves_keeps_the_original_order()
    {
        // Before the fix, a W tap with mouse moves in between became "Press W" followed by the moves
        // with no delays, i.e. the moves happened after W was released.
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, W), Move(40, 10, 0), KeyDown(60, A), Move(80, 20, 0), KeyUp(100, A), KeyUp(120, W),
        });

        Assert.Equal(
            "Hold down W | Wait 40 ms | Move to (10, 0) | Wait 20 ms | Hold down A | Wait 20 ms | Move to (20, 0) | " +
            "Wait 20 ms | Release A | Wait 20 ms | Release W",
            Describe(steps));
    }

    [Fact]
    public void Short_gaps_are_carried_into_the_next_delay_instead_of_lost()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, A), KeyUp(5, A), KeyDown(10, 0x42), KeyUp(15, 0x42), KeyDown(40, 0x43), KeyUp(45, 0x43),
        });

        Assert.Equal("Press A (hold 5 ms) | Press B (hold 5 ms) | Wait 30 ms | Press C (hold 5 ms)", Describe(steps));

        // Playback time (delays + holds) equals the recorded time from the first press to the last release.
        var total = steps.OfType<DelayStep>().Sum(d => d.DurationMs) + steps.OfType<KeyPressStep>().Sum(k => k.HoldMs);
        Assert.Equal(45, total);
    }

    [Fact]
    public void Total_recorded_time_is_kept_within_one_minimum_delay()
    {
        var random = new Random(7);
        var events = new List<RawInputEvent>();
        long t = 0;
        for (var i = 0; i < 300; i++)
        {
            var vk = 0x41 + random.Next(0, 4);
            events.Add(KeyDown(t, vk));
            t += random.Next(1, 40);
            events.Add(KeyUp(t, vk));
            t += random.Next(0, 25);
        }

        var options = new RecordingOptions { MinDelayMs = 15 };
        var steps = RecordingConverter.Convert(events, options);
        var total = steps.OfType<DelayStep>().Sum(d => d.DurationMs) + steps.OfType<KeyPressStep>().Sum(k => k.HoldMs);
        var recorded = events[^1].TimeMs - events[0].TimeMs;

        Assert.InRange(recorded - total, 0, options.MinDelayMs);
    }

    [Fact]
    public void Short_mouse_button_hold_keeps_its_duration()
    {
        var steps = RecordingConverter.Convert(new[] { Down(0, 5, 5, MacroMouseButton.Right), Up(90, 5, 5, MacroMouseButton.Right) });

        var click = Assert.IsType<MouseClickStep>(Assert.Single(steps));
        Assert.Equal(MouseClickAction.Click, click.Action);
        Assert.Equal(90, click.HoldMs);
    }

    [Fact]
    public void Where_the_cursor_came_to_rest_is_kept_after_thinning()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Move(0, 0, 0), Move(10, 5, 5), Move(20, 9, 9), KeyDown(500, A), KeyUp(510, A),
        });

        Assert.Equal("Move to (0, 0) | Wait 20 ms | Move to (9, 9) | Wait 480 ms | Press A (hold 10 ms)", Describe(steps));
    }

    // ---------------- Converter + playback ----------------

    [Fact]
    public async Task Short_key_hold_lasts_as_long_when_played()
    {
        var (input, _, result) = await PlayAsync(RecordingConverter.Convert(new[] { KeyDown(0, W), KeyUp(120, W) }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "key 87 down", "key 87 up" }, input.Calls);
        Assert.Equal(120, Ms(input.Times[1] - input.Times[0]));
    }

    [Fact]
    public async Task Long_key_hold_lasts_as_long_when_played()
    {
        var (input, _, _) = await PlayAsync(RecordingConverter.Convert(new[] { KeyDown(0, W), KeyUp(1500, W) }));

        Assert.Equal(new[] { "key 87 down", "key 87 up" }, input.Calls);
        Assert.Equal(1500, Ms(input.Times[1] - input.Times[0]));
    }

    [Fact]
    public async Task Keys_and_mouse_play_back_at_their_recorded_times_and_order()
    {
        var (input, _, _) = await PlayAsync(RecordingConverter.Convert(new[]
        {
            KeyDown(0, W), Move(40, 10, 0), KeyDown(60, A), Move(80, 20, 0), KeyUp(100, A), KeyUp(120, W),
        }));

        Assert.Equal(new[] { "key 87 down", "move 10,0", "key 65 down", "move 20,0", "key 65 up", "key 87 up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 40, 60, 80, 100, 120 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Short_click_hold_lasts_as_long_when_played()
    {
        var (input, _, _) = await PlayAsync(new List<MacroStep>
        {
            new MouseClickStep { X = 5, Y = 5, Button = MacroMouseButton.Right, HoldMs = 90 },
        });

        Assert.Equal(new[] { "move 5,5", "mouse Right down", "mouse Right up" }, input.Calls);
        Assert.Equal(90, Ms(input.Times[2] - input.Times[1]));
    }

    [Fact]
    public async Task Hold_time_is_scaled_by_speed()
    {
        var (input, _, _) = await PlayAsync(new List<MacroStep>
        {
            new KeyPressStep { VirtualKey = W, HoldMs = 200, Modifiers = KeyModifiers.Shift },
        }, speed: 2);

        Assert.Equal(new[] { "key 16 down", "key 87 down", "key 87 up", "key 16 up" }, input.Calls);
        Assert.Equal(100, Ms(input.Times[2] - input.Times[1]));
    }

    [Fact]
    public async Task Stop_during_a_hold_releases_the_key()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "key 87 down") engine.Stop(); };

        var result = await engine.RunAsync(new Macro
        {
            Name = "Hold",
            Steps = { new KeyPressStep { VirtualKey = W, HoldMs = 5000 }, new MouseMoveStep { X = 9 } },
        });

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(new[] { "key 87 down", "key 87 up" }, input.Calls);
    }

    [Fact]
    public async Task Timer_overshoot_does_not_add_up_over_many_delays()
    {
        // Every wait takes 1 ms longer than asked, like a real timer. Before the fix each Delay step
        // measured its own time, so 100 x 10 ms took 1100 ms; now the timeline absorbs the overshoot.
        var clock = new OvershootClock(TimeSpan.FromMilliseconds(1));
        var macro = new Macro { Name = "Drift" };
        for (var i = 0; i < 100; i++)
        {
            macro.Steps.Add(new DelayStep { DurationMs = 10 });
            macro.Steps.Add(new MouseMoveStep { X = i });
        }

        await new PlaybackEngine(new FakeInput(), clock).RunAsync(macro);

        Assert.InRange(clock.Elapsed.TotalMilliseconds, 1000, 1005);
    }

    [Fact]
    public void Hold_time_is_validated()
    {
        Assert.Contains(MacroValidator.ValidateStep(new KeyPressStep { HoldMs = -1 }), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MacroValidator.ValidateStep(new MouseClickStep { HoldMs = MacroValidator.MaxHoldMs + 1 }), i => i.Severity == IssueSeverity.Error);

        var unused = MacroValidator.ValidateStep(new KeyPressStep { Action = KeyAction.Down, HoldMs = 100 });
        Assert.DoesNotContain(unused, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(unused, i => i.Severity == IssueSeverity.Warning);
    }
}

/// <summary>A clock whose waits always overshoot by a fixed amount (like a real OS timer).</summary>
internal sealed class OvershootClock : IPlaybackClock
{
    private readonly TimeSpan _overshoot;
    private long _ticks;

    public OvershootClock(TimeSpan overshoot) => _overshoot = overshoot;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _ticks, (delay + _overshoot).Ticks);
        return Task.CompletedTask;
    }
}
