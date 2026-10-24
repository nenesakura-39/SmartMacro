using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>
/// The 5 low-level recording / playback timing fixes (1:1 reproduction for precise macros and long AFK loops):
/// 1. deadline-synced playback timeline, 2. auto-repeat + chords, 3. exact click hold + drags,
/// 4. SmartMacro's own shortcuts / stop releases, 5. drift-free timestamps.
/// Fake input and a fake clock: they prove what SmartMacro sends and when, not how a game reacts.
/// </summary>
public class RecordingTimingFixesTests
{
    private const int W = 0x57;
    private const int A = 0x41;
    private const int B = 0x42;
    private const int Space = 0x20;

    private static RawInputEvent Down(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseDown, x, y, b);
    private static RawInputEvent Up(long t, int x, int y, MacroMouseButton b = MacroMouseButton.Left) => new(t, RawInputKind.MouseUp, x, y, b);
    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);
    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private static string Describe(IEnumerable<MacroStep> steps) => string.Join(" | ", steps.Select(StepDescriber.Describe));

    /// <summary>How long playback spends on these steps (delays + click / key holds), in ms.</summary>
    private static long Played(IEnumerable<MacroStep> steps) => steps.Sum(s => (long)(s switch
    {
        DelayStep d => d.DurationMs,
        MouseClickStep { Action: MouseClickAction.Click } c => c.HoldMs,
        KeyPressStep { Action: KeyAction.Press } k => k.HoldMs,
        _ => 0,
    }));

    /// <summary>The options the app uses for desktop recordings (taps keep any hold length).</summary>
    private static RecordingOptions AppOptions(params HotkeyBinding[] ignored) => new()
    {
        KeepHoldTime = true,
        MaxTapDurationMs = MacroValidator.MaxHoldMs,
        IgnoredHotkeys = ignored,
    };

    // ---------------- Fix 1: deadline-synced playback ----------------

    [Fact]
    public async Task Fifty_clicks_and_key_presses_do_not_add_their_sending_time_to_the_macro()
    {
        // Each press costs 40 ms to send (like Windows' hover nudge + settle before a click on a jumped-to spot) and every
        // other call 1 ms. Before the fix every click pushed the rest of the macro back: 50 × ~42 ms ≈ 2 s of drift.
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        input.OnCall = call => clock.Advance(TimeSpan.FromMilliseconds(call == "mouse Left down" ? 40 : 1));

        var steps = new List<MacroStep>();
        for (var i = 0; i < 50; i++)
        {
            steps.Add(new DelayStep { DurationMs = 100 });
            steps.Add(new MouseClickStep { X = 10, Y = 10, HoldMs = 50 });
            steps.Add(new DelayStep { DurationMs = 100 });
            steps.Add(new KeyPressStep { VirtualKey = A, HoldMs = 30 });
        }

        var result = await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // Schedule: 50 × (100 + 50 + 100 + 30) = 14,000 ms (+ the last release's 1 ms).
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 14_000, 14_010);

        // Every press keeps its full hold after it was sent (40 ms sending + 50 ms hold).
        var downs = input.Calls.Select((c, i) => (c, i)).Where(x => x.c == "mouse Left down").Select(x => x.i).ToList();
        Assert.Equal(50, downs.Count);
        foreach (var d in downs)
            Assert.Equal(90, (input.Times[d + 1] - input.Times[d]).TotalMilliseconds, 3);

        // The clicks stay on the recorded 280 ms rhythm (no creeping later and later).
        for (var k = 1; k < downs.Count; k++)
            Assert.Equal(280, (input.Times[downs[k]] - input.Times[downs[k - 1]]).TotalMilliseconds, 3);
    }

    [Fact]
    public async Task A_real_stall_is_skipped_instead_of_firing_a_burst_of_late_input()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        input.OnCall = call => clock.Advance(TimeSpan.FromMilliseconds(call == "key 65 down" ? 500 : 1)); // a 500 ms hiccup

        var steps = new List<MacroStep> { new KeyPressStep { VirtualKey = A } };
        for (var i = 1; i <= 5; i++)
        {
            steps.Add(new DelayStep { DurationMs = 16 });
            steps.Add(new MouseMoveStep { X = i * 10, Y = 0 });
        }

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));

        var moves = input.Calls.Select((c, i) => (c, i)).Where(x => x.c.StartsWith("move ", StringComparison.Ordinal)).Select(x => input.Times[x.i]).ToList();
        Assert.Equal(5, moves.Count);
        var keyUp = input.Times[input.Calls.IndexOf("key 65 up")];
        Assert.True((moves[0] - keyUp).TotalMilliseconds >= 15, "the first move still waits its Delay after the stall");
        for (var i = 1; i < moves.Count; i++)
            Assert.Equal(16, (moves[i] - moves[i - 1]).TotalMilliseconds, 1);
    }

    [Fact]
    public void High_precision_wait_uses_the_OS_delay_only_for_the_long_part()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(40), HighPrecisionWait.CoarsePart(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(TimeSpan.Zero, HighPrecisionWait.CoarsePart(TimeSpan.FromMilliseconds(16)));
        Assert.Equal(TimeSpan.Zero, HighPrecisionWait.CoarsePart(TimeSpan.FromMilliseconds(3)));
        // remainingMs = (deadline − now) × 1000 / frequency: 2.4 ms of step overhead comes off the wait.
        Assert.Equal(97.6, HighPrecisionWait.RemainingMs(deadlineTimestamp: 1_000_000, nowTimestamp: 24_000, frequency: 10_000_000), 6);
    }

    // ---------------- Fix 2: auto-repeat and chords ----------------

    [Fact]
    public void Auto_repeat_while_a_key_is_held_never_becomes_extra_steps()
    {
        var events = new List<RawInputEvent> { KeyDown(0, W), KeyDown(100, Space) };
        for (var t = 500; t < 2000; t += 33) events.Add(KeyDown(t, W)); // Windows' ~30 Hz repeat while W is held
        events.Add(KeyUp(150, Space));
        events.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        events.Add(KeyUp(2000, W));

        var steps = RecordingConverter.Convert(events, AppOptions());

        Assert.Equal("Hold down W | Wait 100 ms | Press Space (hold 50 ms) | Wait 1.85 s | Release W", Describe(steps));
    }

    [Fact]
    public void A_held_key_that_overlaps_nothing_is_one_press_with_its_exact_hold()
    {
        var events = new List<RawInputEvent> { KeyDown(0, W) };
        for (var t = 500; t < 1234; t += 33) events.Add(KeyDown(t, W));
        events.Add(KeyUp(1234, W));

        var press = Assert.IsType<KeyPressStep>(Assert.Single(RecordingConverter.Convert(events, AppOptions())));

        Assert.Equal((KeyAction.Press, 1234), (press.Action, press.HoldDurationMs));
    }

    [Fact]
    public void Holding_W_while_clicking_keeps_the_overlap_and_the_click_stays_one_step()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, W), Down(100, 50, 50), KeyDown(130, W) /* repeat */, Up(160, 50, 50), KeyUp(500, W),
        }, AppOptions());

        Assert.Equal("Hold down W | Wait 100 ms | Click left button at (50, 50) (hold 60 ms) | Wait 340 ms | Release W", Describe(steps));
    }

    [Fact]
    public async Task Overlapping_keys_replay_with_their_recorded_overlap()
    {
        // W 0–1000 ms, Space 300–400 ms while W is still down.
        var steps = RecordingConverter.Convert(new[] { KeyDown(0, W), KeyDown(300, Space), KeyUp(400, Space), KeyUp(1000, W) }, AppOptions());
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(steps.ToArray()));

        Assert.Equal(new[] { "key 87 down", "key 32 down", "key 32 up", "key 87 up" }, input.Calls);
        Assert.Equal(new[] { 0.0, 300, 400, 1000 }, input.Times.Select(t => Math.Round(t.TotalMilliseconds, 3)));
    }

    // ---------------- Fix 3: click hold + drags ----------------

    [Fact]
    public void A_550ms_rod_cast_click_keeps_its_hold()
    {
        var click = Assert.IsType<MouseClickStep>(Assert.Single(RecordingConverter.Convert(new[] { Down(0, 400, 300), Up(550, 401, 302) }, AppOptions())));

        Assert.Equal((MouseClickAction.Click, 550), (click.Action, click.HoldDurationMs));
        Assert.Equal(click.HoldMs, click.HoldDurationMs); // one stored value
    }

    [Fact]
    public void Very_short_clicks_get_the_12ms_floor_and_the_total_time_stays_exact()
    {
        var steps = RecordingConverter.Convert(new[] { Down(0, 5, 5), Up(3, 5, 5), Down(100, 5, 5), Up(160, 5, 5) }, AppOptions());

        Assert.Equal("Click left button at (5, 5) (hold 12 ms) | Wait 88 ms | Click left button at (5, 5) (hold 60 ms)", Describe(steps));
        Assert.Equal(160, Played(steps));
    }

    [Fact]
    public void Six_pixels_of_wobble_is_still_a_click_but_a_drag_that_comes_back_is_a_drag()
    {
        var wobble = RecordingConverter.Convert(new[] { Down(0, 100, 100), Move(20, 104, 103), Up(80, 103, 102) }, AppOptions());
        Assert.Equal(MouseClickAction.Click, Assert.IsType<MouseClickStep>(Assert.Single(wobble.OfType<MouseClickStep>())).Action);

        // Camera turn / slider: out 40 px and back to the same spot. The ends are 0 px apart, but it's a drag.
        var drag = RecordingConverter.Convert(new[]
        {
            Down(0, 100, 100), Move(50, 140, 100), Move(100, 100, 100), Up(150, 100, 100),
        }, AppOptions());

        Assert.Equal(
            "Press left button at (100, 100) | Wait 50 ms | Move to (140, 100) | Wait 50 ms | Move to (100, 100) | Wait 50 ms | Release left button at (100, 100)",
            Describe(drag));
    }

    [Theory]
    [InlineData(550, 1.0, 550)]
    [InlineData(550, 2.0, 275)]
    [InlineData(5, 1.0, 12)]
    public async Task Playback_holds_the_button_for_the_hold_duration(int holdMs, double speed, double expected)
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Flow.Macro(new MouseClickStep { X = 1, Y = 1, Button = MacroMouseButton.Right, HoldDurationMs = holdMs });
        macro.Speed = speed;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(new[] { "move 1,1", "mouse Right down", "mouse Right up" }, input.Calls);
        Assert.Equal(expected, (input.Times[2] - input.Times[1]).TotalMilliseconds, 3);
    }

    [Fact]
    public void Hold_duration_is_stored_once_as_holdMs()
    {
        var json = MacroJson.Serialize<MacroStep>(new MouseClickStep { HoldDurationMs = 550 });

        Assert.Contains("\"holdMs\": 550", json);
        Assert.DoesNotContain("holdDuration", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(550, Assert.IsType<MouseClickStep>(MacroJson.Deserialize<MacroStep>(json)).HoldDurationMs);
    }

    // ---------------- Fix 4: SmartMacro's own shortcuts + releases at stop ----------------

    [Fact]
    public void The_stop_shortcut_and_its_modifiers_never_reach_the_macro_even_with_releases_after_it()
    {
        // W held to the end; Ctrl+Shift+F9 stops the recording; the recorder releases everything still held at the stop.
        var events = new List<RawInputEvent> { KeyDown(0, W), KeyDown(1000, 0xA2), KeyDown(1050, 0xA0), KeyDown(1100, 0x78), KeyDown(1400, 0x78) };
        events.AddRange(RecordingCleanup.ReleasesAtStop(events, 1500));

        var steps = RecordingConverter.Convert(events, new RecordingOptions { IgnoredHotkeys = new[] { HotkeyBinding.DefaultRecord } });

        Assert.Equal("Hold down W | Wait 1.5 s | Release W", Describe(steps));
    }

    [Fact]
    public void Shortcut_chords_are_removed_in_the_middle_but_the_same_keys_on_their_own_are_kept()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, 0xA0), KeyDown(20, A), KeyUp(60, A), KeyUp(80, 0xA0),          // Shift+A: kept
            KeyDown(200, 0x78), KeyUp(240, 0x78),                                       // F9 alone: kept
            KeyDown(400, 0xA2), KeyDown(420, 0xA0), KeyDown(440, 0x79), KeyUp(470, 0x79), KeyUp(480, 0xA0), KeyUp(490, 0xA2), // Ctrl+Shift+F10: removed
            KeyDown(600, B), KeyUp(640, B),
        }, new RecordingOptions { IgnoredHotkeys = new[] { HotkeyBinding.DefaultRecord, HotkeyBinding.DefaultPlay }, MinDelayMs = 1 });

        var keys = steps.OfType<KeyPressStep>().Select(k => (k.VirtualKey, k.Action)).ToList();
        Assert.Equal(new[]
        {
            (0xA0, KeyAction.Down), (A, KeyAction.Press), (0xA0, KeyAction.Up), (0x78, KeyAction.Press), (B, KeyAction.Press),
        }, keys);
        // The removed chord's time isn't lost: B still comes 600 ms after the start.
        var played = 0L;
        foreach (var s in steps)
        {
            if (s is KeyPressStep { VirtualKey: B }) break;
            played += Played(new[] { s });
        }

        Assert.Equal(600, played);
    }

    [Fact]
    public void Smart_recordings_drop_the_shortcut_chord_with_generic_modifier_keys()
    {
        var events = new List<CapturedInputEvent>
        {
            new(InputEvent.KeyDown(0, A, 0x1E)), new(InputEvent.KeyUp(50_000, A, 0x1E)),
            new(InputEvent.KeyDown(1_000_000, 0x11, 0x1D)), new(InputEvent.KeyDown(1_050_000, 0x10, 0x2A)),
            new(InputEvent.KeyDown(1_100_000, 0x78, 0x43)), new(InputEvent.KeyUp(1_150_000, 0x78, 0x43)),
            new(InputEvent.KeyUp(1_200_000, 0x10, 0x2A)), new(InputEvent.KeyUp(1_250_000, 0x11, 0x1D)),
            new(InputEvent.KeyDown(2_000_000, B, 0x30)), new(InputEvent.KeyUp(2_040_000, B, 0x30)),
        };

        var steps = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { IgnoredHotkeys = new[] { HotkeyBinding.DefaultRecord } }).Steps;

        Assert.Equal(new[] { A, B }, steps.OfType<KeyPressStep>().Select(k => k.VirtualKey));
        Assert.All(steps.OfType<KeyPressStep>(), k => Assert.Equal(KeyAction.Press, k.Action));
    }

    [Fact]
    public void Releases_at_stop_close_every_key_and_button_still_held()
    {
        var events = new List<RawInputEvent> { KeyDown(0, W), KeyDown(30, W), Down(100, 5, 5), Move(200, 40, 40), KeyDown(300, A), KeyUp(350, A) };

        var releases = RecordingCleanup.ReleasesAtStop(events, 900);

        Assert.Equal(new[]
        {
            new RawInputEvent(900, RawInputKind.KeyUp, VirtualKey: W),
            new RawInputEvent(900, RawInputKind.MouseUp, 40, 40, MacroMouseButton.Left),
        }, releases);

        var smart = RecordingCleanup.ReleasesAtStop(new List<CapturedInputEvent>
        {
            new(InputEvent.KeyDown(0, 0xA0, 0x2A)), new(InputEvent.ButtonDown(10, MacroMouseButton.Right, 7, 8)),
        }, 5_000);
        Assert.Equal(new[] { InputEvent.KeyUp(5_000, 0xA0, 0x2A), InputEvent.ButtonUp(5_000, MacroMouseButton.Right, 7, 8) }, smart.Select(c => c.Input));
    }

    // ---------------- Fix 5: drift-free timestamps ----------------

    [Fact]
    public void A_thousand_events_have_zero_rounding_drift()
    {
        // 1,000 events 16.6667 ms apart (60 Hz) on a 10 MHz Stopwatch.
        const long frequency = 10_000_000;
        var clock = new RecordingClock(startTimestamp: 5_000, ticksPerSecond: frequency);
        var times = Enumerable.Range(0, 1001).Select(i => clock.ToMilliseconds(5_000 + (long)Math.Round(i * frequency / 60.0))).ToList();

        Assert.Equal(16_667, times[^1]); // 1000 / 60 s, not 16,000 (truncated) or 17,000 (rounded per interval)
        var delays = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.All(delays, d => Assert.InRange(d, 16, 17));
        Assert.Equal(times[^1] - times[0], delays.Sum());

        var quantizer = new DelayQuantizer();
        var total = Enumerable.Range(0, 1000).Sum(_ => quantizer.Next(16.4));
        Assert.Equal(16_400, total);
        Assert.InRange(Math.Abs(quantizer.Remainder), 0, 0.5);
    }

    [Fact]
    public void Recording_clock_never_goes_backwards()
    {
        var clock = new RecordingClock(1_000, 1_000_000);

        Assert.Equal(5, clock.ToMilliseconds(6_000));
        Assert.Equal(5, clock.ToMilliseconds(5_500)); // a late-arriving earlier stamp keeps the previous time
        Assert.Equal(0, new RecordingClock(1_000, 1_000_000).ToMilliseconds(0));
    }
}
