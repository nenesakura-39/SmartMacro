using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;

namespace SmartMacro.Tests;

/// <summary>
/// Audit for multi-hour AFK sessions: nothing stays held after Stop / Pause (fix 1), no 0 ms catch-up bursts (fix 2),
/// absolute X / Y stays authoritative with deltas recorded alongside (fix 4), and clean first-event / out-of-order timing
/// in recordings (fix 6). Fake input and a fake clock.
/// </summary>
public class AfkSafetyTests
{
    private const int W = 0x57;
    private const int LShift = 0xA0;

    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private static List<TimeSpan> TimesOf(FakeInput input, Func<string, bool> which) =>
        input.Calls.Select((c, i) => (c, i)).Where(x => which(x.c)).Select(x => input.Times[x.i]).ToList();

    // ---------------- Fix 1: nothing stays held ----------------

    [Fact]
    public async Task Stopping_inside_a_recorded_chord_releases_every_held_key()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "key 87 down") engine.Stop(); }; // F10 while Shift + W are held

        var result = await engine.RunAsync(Flow.Macro(
            new KeyPressStep { VirtualKey = LShift, Action = KeyAction.Down },
            new KeyPressStep { VirtualKey = W, Action = KeyAction.Down },
            new DelayStep { DurationMs = 60_000 },
            new KeyPressStep { VirtualKey = W, Action = KeyAction.Up },
            new KeyPressStep { VirtualKey = LShift, Action = KeyAction.Up }));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Contains("key 87 up", input.Calls);
        Assert.Contains("key 160 up", input.Calls);
        Assert.True(input.ReleaseAllCount >= 1); // plus the simulator's own failsafe
    }

    [Fact]
    public async Task Stopping_in_the_middle_of_a_click_hold_releases_the_button_at_once()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "mouse Left down") engine.Stop(); };

        var result = await engine.RunAsync(Flow.Macro(new MouseClickStep { X = 5, Y = 5, HoldDurationMs = 550 }));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal("mouse Left up", input.Calls[^1]);
    }

    [Fact]
    public async Task Pausing_in_the_middle_of_a_click_hold_lets_go_and_resume_finishes_the_hold()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        var first = true;
        input.OnCall = call =>
        {
            if (call != "mouse Left down" || !first) return;
            first = false;
            Assert.True(engine.Pause()); // F8 while the rod is being charged
        };

        var run = engine.RunAsync(Flow.Macro(new MouseClickStep { X = 640, Y = 360, HoldDurationMs = 550 }));
        await Waits.Until(() => Waits.Has(input, "mouse Left up"));
        Assert.True(engine.IsPaused);
        Assert.True(input.ReleaseAllCount >= 1); // let go the moment F8 was pressed

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(engine.Resume());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 640,360", "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(5_000, input.Times[3].TotalMilliseconds, 3);                         // pressed again on resume
        Assert.Equal(550, (input.Times[4] - input.Times[3]).TotalMilliseconds, 3);         // the whole hold, paused time not counted
    }

    // ---------------- Fix 2: no 0 ms catch-up bursts ----------------

    [Fact]
    public async Task Catching_up_after_a_slow_click_never_fires_moves_in_a_burst()
    {
        // The press costs 45 ms to send; then 10 recorded moves 16 ms apart.
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        input.OnCall = call => { if (call == "mouse Left down") clock.Advance(TimeSpan.FromMilliseconds(45)); };
        var steps = new List<MacroStep> { new MouseClickStep { X = 300, Y = 300 } };
        for (var i = 1; i <= 10; i++)
        {
            steps.Add(new DelayStep { DurationMs = 16 });
            steps.Add(new MouseMoveStep { X = 300 + i * 10, Y = 300 });
        }

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));

        var up = TimesOf(input, c => c == "mouse Left up").Single();
        var moves = TimesOf(input, c => c.StartsWith("move ", StringComparison.Ordinal) && c != "move 300,300");
        Assert.Equal(10, moves.Count);
        // Every gap keeps at least half its recorded length (8 of 16 ms): never two moves in the same instant…
        Assert.True((moves[0] - up).TotalMilliseconds >= 8);
        for (var i = 1; i < moves.Count; i++)
            Assert.True((moves[i] - moves[i - 1]).TotalMilliseconds >= 8, $"move {i + 1} came {(moves[i] - moves[i - 1]).TotalMilliseconds} ms after the one before");
        // …and the 45 ms are still caught up: the last move is exactly on the recorded schedule (10 × 16 ms).
        Assert.Equal(160, moves[^1].TotalMilliseconds, 3);
    }

    [Fact]
    public async Task Falling_more_than_50ms_behind_restarts_the_timeline_from_now()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        input.OnCall = call => { if (call == "mouse Left down") clock.Advance(TimeSpan.FromMilliseconds(80)); };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseClickStep { X = 1, Y = 1 }, new DelayStep { DurationMs = 16 }, new MouseMoveStep { X = 2, Y = 2 },
            new DelayStep { DurationMs = 16 }, new MouseMoveStep { X = 3, Y = 3 }));

        var up = TimesOf(input, c => c == "mouse Left up").Single();
        var moves = TimesOf(input, c => c is "move 2,2" or "move 3,3");
        Assert.Equal(16, (moves[0] - up).TotalMilliseconds, 3); // full recorded gap, the 80 ms aren't chased
        Assert.Equal(16, (moves[1] - moves[0]).TotalMilliseconds, 3);
    }

    [Fact]
    public async Task Catching_up_never_shortens_a_click_hold()
    {
        // 45 ms of lateness is still owed when a recorded 60 ms hold starts: the hold keeps its 60 ms, the next Delay pays.
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var first = true;
        input.OnCall = call =>
        {
            if (call != "mouse Left down" || !first) return;
            first = false;
            clock.Advance(TimeSpan.FromMilliseconds(45));
        };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseClickStep { X = 1, Y = 1 }, new DelayStep { DurationMs = 16 },
            new MouseClickStep { X = 2, Y = 2, HoldDurationMs = 60 }, new DelayStep { DurationMs = 100 },
            new MouseMoveStep { X = 3, Y = 3 }));

        var downs = TimesOf(input, c => c == "mouse Left down");
        var ups = TimesOf(input, c => c == "mouse Left up");
        Assert.Equal(60, (ups[1] - downs[1]).TotalMilliseconds, 3);
        Assert.Equal(16 + 60 + 100, TimesOf(input, c => c == "move 3,3").Single().TotalMilliseconds, 3); // still on schedule
    }

    [Fact]
    public async Task Smooth_glides_that_start_behind_schedule_never_burst()
    {
        // The app's default "smooth mouse movement": a recorded Delay glides the cursor to the next Move's spot.
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        input.OnCall = call => { if (call == "mouse Left down") clock.Advance(TimeSpan.FromMilliseconds(45)); };
        var steps = new List<MacroStep> { new MouseClickStep { X = 1, Y = 1 } };
        for (var i = 1; i <= 5; i++)
        {
            steps.Add(new DelayStep { DurationMs = 16 });
            steps.Add(new MouseMoveStep { X = 1 + i * 50, Y = 1 });
        }

        await new PlaybackEngine(input, clock) { SmoothMouseMoves = true }.RunAsync(Flow.Macro(steps.ToArray()));

        // Every new cursor position comes at least 4 ms after the one before (half of an 8 ms glide step).
        var positions = new List<(string Call, TimeSpan At)>();
        for (var i = 0; i < input.Calls.Count; i++)
        {
            if (!input.Calls[i].StartsWith("move ", StringComparison.Ordinal)) continue;
            if (positions.Count > 0 && positions[^1].Call == input.Calls[i]) continue; // the Move step landing on the glide's end
            positions.Add((input.Calls[i], input.Times[i]));
        }

        Assert.True(positions.Count >= 10, $"{positions.Count} positions");
        for (var i = 1; i < positions.Count; i++)
            Assert.True((positions[i].At - positions[i - 1].At).TotalMilliseconds >= 4,
                $"{positions[i].Call} came {(positions[i].At - positions[i - 1].At).TotalMilliseconds} ms after {positions[i - 1].Call}");
        // …and the lateness is still being caught up (4 ms per glide point: 40 of the 45 ms by the last of 10 points).
        // Before the fix the first points all fired at 45 ms in a burst.
        Assert.InRange(positions[^1].At.TotalMilliseconds, 5 * 16, 5 * 16 + 5);
    }

    // ---------------- Fix 4: absolute X / Y authoritative, deltas recorded too ----------------

    [Fact]
    public async Task Drags_play_at_absolute_positions_and_still_remember_their_deltas()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            Move(0, 100, 100), new RawInputEvent(50, RawInputKind.MouseDown, 100, 100, MacroMouseButton.Right),
            Move(100, 160, 120), Move(150, 220, 90), new RawInputEvent(200, RawInputKind.MouseUp, 220, 90, MacroMouseButton.Right),
        }, new RecordingOptions { Fidelity = RecordingFidelity.Exact1To1 }); // the app's default: relative mode off

        var moves = steps.OfType<MouseMoveStep>().ToList();
        Assert.All(moves, m => Assert.False(m.UseRelativeDelta));
        Assert.Equal(new[] { (60, 20), (60, -30) }, moves.Skip(1).Select(m => (m.DeltaX, m.DeltaY)));
        Assert.Equal(new[] { (160, 120), (220, 90) }, moves.Skip(1).Select(m => (m.X, m.Y)));

        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));
        Assert.Contains("move 160,120", input.Calls);
        Assert.Contains("move 220,90", input.Calls);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("rel ", StringComparison.Ordinal));
    }

    // ---------------- Fix 6: first event / out-of-order times ----------------

    [Fact]
    public void A_first_input_two_seconds_after_start_has_no_leading_wait_and_no_stale_remainder()
    {
        var clock = new RecordingClock(startTimestamp: 1_000_000, ticksPerSecond: 10_000_000);
        Assert.Equal(2_000, clock.ToMilliseconds(1_000_000 + 20_000_000)); // 2 s after Start
        Assert.Equal(0, new DelayQuantizer().Remainder);

        var steps = RecordingConverter.Convert(new[] { KeyDown(2_000, W), KeyUp(2_080, W) });
        var press = Assert.IsType<KeyPressStep>(Assert.Single(steps)); // the 2 s before the first input isn't a Delay
        Assert.Equal(80, press.HoldMs);
    }

    [Fact]
    public void Out_of_order_times_never_make_a_negative_or_zero_delay()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(500, 0x41), KeyUp(520, 0x41), KeyDown(100, 0x42), KeyUp(130, 0x42), KeyDown(130, 0x43), KeyUp(140, 0x43),
        }, new RecordingOptions { MinDelayMs = 0 });

        Assert.All(steps.OfType<DelayStep>(), d => Assert.InRange(d.DurationMs, 1, int.MaxValue));
        Assert.Equal(3, steps.OfType<KeyPressStep>().Count());
    }
}
