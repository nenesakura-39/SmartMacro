using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>
/// Game mode: relative mouse deltas and exact key timing in "Recorded input" steps.
/// Fake input / fake clock only — these prove what SmartMacro sends and when, not how a game reacts.
/// </summary>
public class GameModeTests
{
    private const int W = 0x57;
    private const int WScan = 0x11;
    private const int Shift = 0x10;

    private static InputSequenceStep Seq(params InputEvent[] events) => new() { Events = events.ToList() };

    private static Macro MacroWith(params MacroStep[] steps)
    {
        var m = new Macro { Name = "Game" };
        m.Steps.AddRange(steps);
        return m;
    }

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    // ---------------- Playback ----------------

    [Fact]
    public async Task Relative_deltas_keep_their_signs_values_times_and_order()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        var result = await new PlaybackEngine(input, clock).RunAsync(MacroWith(Seq(
            InputEvent.KeyDown(0, W, WScan),
            InputEvent.Move(1_000, 5, -3),
            InputEvent.Move(2_000, -7, 0),
            InputEvent.Move(2_000, 0, 12),               // same timestamp: order still kept
            InputEvent.ButtonDown(2_500, MacroMouseButton.Left),
            InputEvent.WheelEvent(3_000, -120),
            InputEvent.ButtonUp(4_000, MacroMouseButton.Left),
            InputEvent.KeyUp(120_000, W, WScan))));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[]
        {
            "key 87 down sc17", "rel 5,-3", "rel -7,0", "rel 0,12", "mouse Left down", "scroll -120", "mouse Left up", "key 87 up sc17",
        }, input.Calls);
        Assert.Equal(new[] { 0.0, 1, 2, 2, 2.5, 3, 4, 120 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Simultaneous_wasd_and_mouse_keep_their_recorded_order()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(MacroWith(Seq(
            InputEvent.KeyDown(0, W, WScan),
            InputEvent.KeyDown(0, 0x41, 0x1E),
            InputEvent.Move(500, 3, 0),
            InputEvent.KeyUp(80_000, 0x41, 0x1E),
            InputEvent.Move(80_000, 2, 1),
            InputEvent.KeyDown(90_000, 0x44, 0x20),
            InputEvent.KeyUp(150_000, W, WScan),
            InputEvent.KeyUp(150_000, 0x44, 0x20))));

        Assert.Equal(new[]
        {
            "key 87 down sc17", "key 65 down sc30", "rel 3,0", "key 65 up sc30", "rel 2,1", "key 68 down sc32", "key 87 up sc17", "key 68 up sc32",
        }, input.Calls);
        Assert.Equal(new[] { 0.0, 0, 0.5, 80, 80, 90, 150, 150 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Speed_scales_recorded_times()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = MacroWith(Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.KeyUp(120_000, W, WScan)));
        macro.Speed = 2;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(60, Ms(input.Times[1] - input.Times[0]));
    }

    [Fact]
    public async Task Sequence_starts_after_previous_delay_on_the_same_timeline()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock).RunAsync(MacroWith(
            new DelayStep { DurationMs = 100 },
            Seq(InputEvent.Move(0, 1, 0), InputEvent.Move(10_000, 1, 0))));

        Assert.Equal(new[] { 100.0, 110 }, input.Times.Select(Ms));
    }

    [Fact]
    public async Task Stop_releases_held_keys_with_the_recorded_scan_code()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "rel 1,1") engine.Stop(); };

        var result = await engine.RunAsync(MacroWith(Seq(
            InputEvent.KeyDown(0, Shift, 0x36),                  // right Shift
            InputEvent.ButtonDown(0, MacroMouseButton.Right),
            InputEvent.Move(10_000, 1, 1),
            InputEvent.Move(20_000, 9, 9),
            InputEvent.KeyUp(5_000_000, Shift, 0x36),
            InputEvent.ButtonUp(5_000_000, MacroMouseButton.Right))));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.DoesNotContain("rel 9,9", input.Calls);
        Assert.Contains("key 16 up sc54", input.Calls);   // right Shift, not left
        Assert.Contains("mouse Right up", input.Calls);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public async Task Input_failure_releases_held_input()
    {
        var input = new FakeInput { ThrowOn = new InputSimulationException("blocked"), ThrowWhen = "rel 1,1" };
        var engine = new PlaybackEngine(input, new FakeClock());

        var result = await engine.RunAsync(MacroWith(Seq(
            InputEvent.KeyDown(0, W, WScan),
            InputEvent.ButtonDown(0, MacroMouseButton.Left),
            InputEvent.Move(10_000, 1, 1),
            InputEvent.KeyUp(50_000, W, WScan),
            InputEvent.ButtonUp(50_000, MacroMouseButton.Left))));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Equal("blocked", result.Error);
        Assert.Equal(new[] { "key 87 up sc17", "mouse Left up" }, input.Calls.TakeLast(2));
    }

    [Fact]
    public async Task Keys_still_held_at_the_end_of_the_step_are_released()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(MacroWith(
            Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.ButtonDown(0, MacroMouseButton.Middle)),
            new MouseMoveStep { X = 1 }));

        Assert.Equal(new[] { "key 87 down sc17", "mouse Middle down", "key 87 up sc17", "mouse Middle up", "move 1,0" }, input.Calls);
    }

    [Fact]
    public async Task Pause_releases_held_keys_and_resume_presses_them_again_on_a_shifted_timeline()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        var paused = 0;
        input.OnCall = call =>
        {
            if (call != "key 87 down sc17" || Interlocked.Increment(ref paused) != 1) return;
            Assert.True(engine.Pause());
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                clock.Advance(TimeSpan.FromMilliseconds(500)); // half a second passes while paused
                engine.Resume();
            });
        };

        var result = await engine.RunAsync(MacroWith(Seq(
                InputEvent.KeyDown(0, W, WScan),
                InputEvent.Move(10_000, 1, 0),
                InputEvent.KeyUp(20_000, W, WScan))))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "key 87 down sc17", "key 87 up sc17", "key 87 down sc17", "rel 1,0", "key 87 up sc17" }, input.Calls);

        // The rest of the recording keeps its spacing, just 500 ms later.
        Assert.Equal(510, Ms(input.Times[3]));
        Assert.Equal(520, Ms(input.Times[4]));
    }

    [Fact]
    public async Task Releasing_while_paused_can_be_turned_off()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock()) { ReleaseHeldInputWhilePaused = false };
        input.OnCall = call =>
        {
            if (call != "key 87 down sc17") return;
            engine.Pause();
            _ = Task.Run(async () => { await Task.Delay(50); engine.Resume(); });
        };

        await engine.RunAsync(MacroWith(Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.KeyUp(20_000, W, WScan))))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "key 87 down sc17", "key 87 up sc17" }, input.Calls);
    }

    [Fact]
    public async Task Stop_while_paused_does_not_press_anything_again()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call =>
        {
            if (call != "key 87 down sc17") return;
            engine.Pause();
            _ = Task.Run(async () => { await Task.Delay(50); engine.Stop(); });
        };

        var result = await engine.RunAsync(MacroWith(Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.KeyUp(1_000_000, W, WScan))))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(new[] { "key 87 down sc17", "key 87 up sc17" }, input.Calls);
    }

    [Fact]
    public async Task Cancellation_token_stops_playback_and_releases_held_input()
    {
        using var cts = new CancellationTokenSource();
        var input = new FakeInput();
        input.OnCall = call => { if (call == "rel 1,1") cts.Cancel(); };

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(MacroWith(Seq(
            InputEvent.KeyDown(0, W, WScan),
            InputEvent.ButtonDown(0, MacroMouseButton.Left),
            InputEvent.Move(10_000, 1, 1),
            InputEvent.KeyUp(900_000, W, WScan),
            InputEvent.ButtonUp(900_000, MacroMouseButton.Left))), cts.Token);

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(new[] { "key 87 up sc17", "mouse Left up" }, input.Calls.TakeLast(2));
    }

    [Fact]
    public async Task Damaged_sequence_is_refused_before_anything_is_sent()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        await Assert.ThrowsAsync<MacroValidationException>(() => engine.RunAsync(MacroWith(Seq(
            InputEvent.Move(5_000, 1, 1), InputEvent.Move(1_000, 1, 1)))));
        Assert.Empty(input.Calls);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    // ---------------- Game recording converter ----------------

    [Fact]
    public void Every_consecutive_mouse_sample_is_kept()
    {
        var raw = Enumerable.Range(0, 1000)
            .Select(i => InputEvent.Move(5_000 + i * 1_000L, i % 2 == 0 ? 3 : -2, i % 5 - 2))
            .ToList();

        var step = GameRecordingConverter.Convert(raw)!;

        Assert.Equal(1000, step.Events.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            Assert.Equal(raw[i].TimeUs - 5_000, step.Events[i].TimeUs); // starts at 0, spacing unchanged
            Assert.Equal(raw[i].Dx, step.Events[i].Dx);
            Assert.Equal(raw[i].Dy, step.Events[i].Dy);
        }

        Assert.Equal(raw.Sum(e => e.Dx), step.Events.Sum(e => e.Dx));
    }

    [Fact]
    public void Game_conversion_keeps_keyboard_and_mouse_interleaving()
    {
        var raw = new[]
        {
            InputEvent.KeyDown(100, W, WScan), InputEvent.Move(150, 4, -1), InputEvent.ButtonDown(160, MacroMouseButton.Left),
            InputEvent.Move(170, -4, 1), InputEvent.KeyUp(200, W, WScan), InputEvent.ButtonUp(210, MacroMouseButton.Left),
        };

        var step = GameRecordingConverter.Convert(raw)!;

        Assert.Equal(raw.Select(e => e with { TimeUs = e.TimeUs - 100 }), step.Events);
    }

    [Fact]
    public void Stop_shortcut_is_trimmed_but_movement_before_it_is_kept()
    {
        int[] hotkey = { 0x11, 0xA2, 0x10, 0xA0, 0x78 }; // Ctrl+Shift+F9
        var raw = new[]
        {
            InputEvent.KeyDown(0, W, WScan), InputEvent.Move(10_000, 1, 1), InputEvent.KeyUp(20_000, W, WScan),
            InputEvent.Move(30_000, 4, 4),                                       // real camera turn: kept
            InputEvent.KeyDown(40_000, 0x11, 0x1D), InputEvent.Move(45_000, 1, 1),
            InputEvent.KeyDown(50_000, 0x10, 0x2A), InputEvent.KeyDown(60_000, 0x78, 0x43),
        };

        var step = GameRecordingConverter.Convert(raw, new GameRecordingOptions { TrimTrailingKeys = hotkey })!;

        Assert.Equal(raw.Take(4), step.Events);
    }

    [Fact]
    public void Early_releases_and_auto_repeat_are_dropped_and_held_input_is_released()
    {
        var raw = new[]
        {
            InputEvent.KeyUp(0, 0x11, 0x1D),                       // released after recording started
            InputEvent.ButtonUp(1, MacroMouseButton.Left),
            InputEvent.KeyDown(1_000, W, WScan),
            InputEvent.KeyDown(31_000, W, WScan),                  // auto-repeat
            InputEvent.ButtonDown(2_000_000, MacroMouseButton.Right),
        };

        var step = GameRecordingConverter.Convert(raw)!;

        Assert.Equal(new[]
        {
            InputEvent.KeyDown(0, W, WScan),
            InputEvent.ButtonDown(1_999_000, MacroMouseButton.Right),
            InputEvent.KeyUp(1_999_000, W, WScan),
            InputEvent.ButtonUp(1_999_000, MacroMouseButton.Right),
        }, step.Events);
        Assert.DoesNotContain(MacroValidator.ValidateStep(step), i => i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Left_and_right_shift_are_tracked_separately()
    {
        var raw = new[]
        {
            InputEvent.KeyDown(0, Shift, 0x2A), InputEvent.KeyDown(10, Shift, 0x36),
            InputEvent.KeyUp(20, Shift, 0x2A), InputEvent.KeyUp(30, Shift, 0x36),
        };

        Assert.Equal(raw, GameRecordingConverter.Convert(raw)!.Events);
    }

    [Fact]
    public void Optional_merging_sums_movement_without_losing_any()
    {
        var raw = new[] { 0L, 250, 500, 750, 1_000, 1_250 }.Select(t => InputEvent.Move(t, 1, -1)).ToList();

        var step = GameRecordingConverter.Convert(raw, new GameRecordingOptions { MoveCoalesceUs = 1_000 })!;

        Assert.Equal(new[] { InputEvent.Move(0, 5, -5), InputEvent.Move(1_250, 1, -1) }, step.Events);
    }

    [Fact]
    public void Nothing_useful_recorded_gives_no_step()
    {
        Assert.Null(GameRecordingConverter.Convert(Array.Empty<InputEvent>()));
        Assert.Null(GameRecordingConverter.Convert(new[] { InputEvent.Move(0, 0, 0), InputEvent.KeyUp(5, W, WScan) }));
    }

    [Fact]
    public void Random_game_recordings_convert_to_valid_steps()
    {
        var random = new Random(11);
        var raw = new List<InputEvent>();
        long t = 0;
        for (var i = 0; i < 5000; i++)
        {
            t += random.Next(0, 3000);
            raw.Add(random.Next(0, 6) switch
            {
                0 => InputEvent.KeyDown(t, random.Next(0, 300), random.Next(-5, 300), random.Next(2) == 1),
                1 => InputEvent.KeyUp(t, random.Next(0, 300), random.Next(-5, 300), random.Next(2) == 1),
                2 => InputEvent.ButtonDown(t, (MacroMouseButton)random.Next(0, 4)),
                3 => InputEvent.ButtonUp(t, (MacroMouseButton)random.Next(0, 4)),
                4 => InputEvent.WheelEvent(t, random.Next(-3, 4) * 120, random.Next(2) == 1),
                _ => InputEvent.Move(t, random.Next(-500, 500), random.Next(-500, 500)),
            });
        }

        var step = GameRecordingConverter.Convert(raw)!;
        var macro = MacroWith(step);

        Assert.False(MacroValidator.Validate(macro).HasErrors());
    }

    // ---------------- Saving, loading and damaged data ----------------

    [Fact]
    public void Codec_round_trips_every_value_exactly()
    {
        var events = new List<InputEvent>
        {
            InputEvent.Move(0, -100_000, 100_000),
            InputEvent.Move(1, int.MinValue, int.MaxValue),
            InputEvent.KeyDown(1, 0xA1, 0x36, extended: false),
            InputEvent.KeyUp(3_600_000_000, 0x25, 0x4B, extended: true),
            InputEvent.WheelEvent(3_600_000_000, -12_000, horizontal: true),
            InputEvent.ButtonDown(5, MacroMouseButton.Middle), // out of order: still round-trips (validator reports it)
            InputEvent.ButtonUp(6, (MacroMouseButton)9),
        };

        Assert.Equal(events, InputEventCodec.Decode(InputEventCodec.Encode(events)));
        Assert.Empty(InputEventCodec.Decode("v1"));
    }

    [Fact]
    public void Recorded_input_saves_and_loads_exactly_as_version_2()
    {
        var step = Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.Move(1_000, 5, -3), InputEvent.KeyUp(120_000, W, WScan));
        step.StartX = -1920;
        step.StartY = 40;
        step.MoveCursorToStart = true;
        var macro = MacroWith(step, new KeyPressStep { VirtualKey = W, HoldMs = 120 });

        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"schemaVersion\": 2", json);

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var loadedStep = Assert.IsType<InputSequenceStep>(loaded!.Steps[0]);
        Assert.Equal(step.Events, loadedStep.Events);
        Assert.Equal((-1920, 40, true), (loadedStep.StartX, loadedStep.StartY, loadedStep.MoveCursorToStart));
        Assert.Equal(120, Assert.IsType<KeyPressStep>(loaded.Steps[1]).HoldMs);
    }

    [Fact]
    public void Macros_without_new_features_are_still_saved_as_version_1()
    {
        var json = MacroFileFormat.Serialize(MacroWith(new KeyPressStep { VirtualKey = W }, new MouseClickStep()));
        Assert.Contains("\"schemaVersion\": 1", json);
    }

    [Fact]
    public async Task Version_1_files_still_load_and_play_instant_taps()
    {
        const string json = """
            { "schemaVersion": 1, "macro": { "id": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "name": "Old",
              "steps": [
                { "type": "keyPress", "id": "11111111-1111-1111-1111-111111111111", "virtualKey": 87, "action": "Press" },
                { "type": "mouseClick", "id": "22222222-2222-2222-2222-222222222222", "x": 10, "y": 20 },
                { "type": "mouseMove", "id": "33333333-3333-3333-3333-333333333333", "x": 1, "y": 2 },
                { "type": "mouseScroll", "id": "44444444-4444-4444-4444-444444444444", "delta": -120 },
                { "type": "typeText", "id": "55555555-5555-5555-5555-555555555555", "text": "hi" }
              ] } }
            """;

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        Assert.Equal(0, Assert.IsType<KeyPressStep>(macro!.Steps[0]).HoldMs);
        Assert.Equal(0, Assert.IsType<MouseClickStep>(macro.Steps[1]).HoldMs);
        Assert.False(MacroValidator.Validate(macro).HasErrors());

        var clock = new FakeClock();
        var input = new FakeInput();
        await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(new[]
        {
            "key 87 down", "key 87 up", "move 10,20", "mouse Left down", "mouse Left up", "move 1,2", "scroll -120", "type h", "type i",
        }, input.Calls);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v2 0,m,1,1")]
    [InlineData("v1 0,m,1")]
    [InlineData("v1 x,m,1,1")]
    [InlineData("v1 0,zz,1")]
    [InlineData("v1 0,kd,87,17,2")]
    [InlineData("v1 99999999999999999999,m,1,1")]
    [InlineData("v1 9223372036854775807,m,1,1 9223372036854775807,m,1,1")]
    public void Damaged_recorded_input_is_refused_with_a_reason(string events)
    {
        var json = $$"""{ "schemaVersion": 2, "macro": { "name": "x", "steps": [ { "type": "inputSequence", "events": "{{events}}" } ] } }""";

        Assert.False(MacroFileFormat.TryParse(json, out var macro, out var error));
        Assert.Null(macro);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Recorded_input_that_is_not_text_is_refused()
    {
        const string json = """{ "schemaVersion": 2, "macro": { "name": "x", "steps": [ { "type": "inputSequence", "events": [ 1, 2 ] } ] } }""";
        Assert.False(MacroFileFormat.TryParse(json, out _, out _));
    }

    [Fact]
    public void Missing_or_empty_recorded_input_loads_but_is_invalid()
    {
        const string json = """{ "schemaVersion": 2, "macro": { "name": "x", "steps": [ { "type": "inputSequence", "id": "11111111-1111-1111-1111-111111111111", "events": null } ] } }""";

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        Assert.Contains(MacroValidator.Validate(macro!), i => i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Invalid_events_are_validation_errors(int which)
    {
        var bad = which switch
        {
            0 => InputEvent.Move(5, 1, 1),                                        // earlier than the one before
            1 => InputEvent.Move(20, MacroValidator.MaxRelativeMove + 1, 0),      // huge jump
            2 => InputEvent.KeyDown(20, 0, 0),                                    // invalid key
            3 => InputEvent.KeyDown(20, W, 0x1FF),                                // invalid scan code
            4 => InputEvent.ButtonDown(20, (MacroMouseButton)7),                  // unknown button
            5 => InputEvent.WheelEvent(20, 0),                                    // zero scroll
            6 => new InputEvent(20, (InputEventKind)42),                          // unknown kind
            _ => InputEvent.Move(MacroValidator.MaxSequenceDurationUs + 1, 1, 1), // more than an hour in
        };

        var step = Seq(InputEvent.Move(10, 1, 1), bad);
        Assert.Contains(MacroValidator.ValidateStep(step), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Empty_recorded_input_is_an_error_and_unreleased_keys_only_a_warning()
    {
        Assert.Contains(MacroValidator.ValidateStep(Seq()), i => i.Severity == IssueSeverity.Error);

        var held = MacroValidator.ValidateStep(Seq(InputEvent.KeyDown(0, W, WScan)));
        Assert.DoesNotContain(held, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(held, i => i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Recorded_input_steps_are_not_created_from_the_toolbox()
    {
        Assert.Throws<ArgumentException>(() => MacroFactory.CreateStep(StepKind.InputSequence));
    }

    [Fact]
    public void Recorded_input_is_described_in_the_step_list()
    {
        var text = StepDescriber.Describe(Seq(InputEvent.KeyDown(0, W, WScan), InputEvent.Move(500, 1, 1), InputEvent.KeyUp(1_500_000, W, WScan)));
        Assert.Equal("Play recorded input: 1.5 s, 3 events (1 mouse moves, 1 key presses)", text);
    }

    [Fact]
    public void Duplicating_a_recorded_step_copies_all_events()
    {
        var step = Seq(InputEvent.Move(0, 1, -1), InputEvent.Move(10, -1, 1));
        var copy = Assert.IsType<InputSequenceStep>(MacroFactory.DuplicateStep(step));

        Assert.NotEqual(step.Id, copy.Id);
        Assert.Equal(step.Events, copy.Events);
        Assert.NotSame(step.Events, copy.Events);
    }
}
