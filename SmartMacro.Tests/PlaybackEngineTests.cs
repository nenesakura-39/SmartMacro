using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>Records every simulated input call as text, e.g. "move 1,2" or "key 65 down".</summary>
internal sealed class FakeInput : IInputSimulator
{
    public List<string> Calls { get; } = new();

    /// <summary>When set, the clock time of every call is recorded in <see cref="Times"/> (same order as Calls).</summary>
    public IPlaybackClock? Clock { get; set; }

    public List<TimeSpan> Times { get; } = new();

    /// <summary>Lets a test react to a call (e.g. stop playback). Runs on the playback thread.</summary>
    public Action<string>? OnCall { get; set; }

    public Exception? ThrowOn { get; set; }
    public string? ThrowWhen { get; set; }

    public void MoveMouse(int x, int y) => Add($"move {x},{y}");
    public void MoveMouseRelative(int dx, int dy) => Add($"rel {dx},{dy}");
    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) =>
        Add($"key {virtualKey} {(down ? "down" : "up")} sc{scanCode}{(extended ? "e" : "")}");
    public void MouseButton(MacroMouseButton button, bool down) => Add($"mouse {button} {(down ? "down" : "up")}");
    public void Scroll(int delta, bool horizontal) => Add($"scroll {delta}{(horizontal ? " h" : "")}");
    public void Key(int virtualKey, bool down) => Add($"key {virtualKey} {(down ? "down" : "up")}");
    public void TypeCharacters(string text) => Add($"type {text.Replace("\n", "\\n")}");

    private void Add(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
            if (Clock is not null) Times.Add(Clock.Elapsed);
        }

        if (ThrowOn is not null && call == ThrowWhen) throw ThrowOn;
        OnCall?.Invoke(call);
    }
}

/// <summary>Instant clock: delays finish immediately but are added up.</summary>
internal sealed class FakeClock : IPlaybackClock
{
    private long _ticks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public TimeSpan TotalDelayed => Elapsed;

    /// <summary>Moves time forward without a delay (e.g. to simulate time passing while paused).</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _ticks, delay.Ticks);
        return Task.CompletedTask;
    }
}

public class PlaybackEngineTests
{
    private static Macro MacroWith(params MacroStep[] steps)
    {
        var m = new Macro { Name = "Test" };
        m.Steps.AddRange(steps);
        return m;
    }

    [Fact]
    public async Task Plays_steps_in_order()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        var result = await engine.RunAsync(MacroWith(
            new MouseMoveStep { X = 1, Y = 2 },
            new MouseClickStep { X = 5, Y = 6 },
            new MouseClickStep { UseCurrentPosition = true, Button = MacroMouseButton.Right, Action = MouseClickAction.DoubleClick },
            new MouseScrollStep { Delta = -240 },
            new KeyPressStep { VirtualKey = 0x41, Modifiers = KeyModifiers.Control | KeyModifiers.Shift },
            new TypeTextStep { Text = "a\r\nb" }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[]
        {
            "move 1,2",
            "move 5,6", "mouse Left down", "mouse Left up",
            "mouse Right down", "mouse Right up", "mouse Right down", "mouse Right up",
            "scroll -240",
            "key 17 down", "key 16 down", "key 65 down", "key 65 up", "key 16 up", "key 17 up",
            "type a", "type \\n", "type b",
        }, input.Calls);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public async Task Disabled_steps_are_skipped()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        await engine.RunAsync(MacroWith(new MouseMoveStep { X = 1, Enabled = false }, new MouseMoveStep { X = 2 }));

        Assert.Equal(new[] { "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Delays_are_scaled_by_speed()
    {
        var clock = new FakeClock();
        var macro = MacroWith(new DelayStep { DurationMs = 1000 }, new MouseMoveStep());
        macro.Speed = 2;

        await new PlaybackEngine(new FakeInput(), clock).RunAsync(macro);

        Assert.Equal(500, clock.TotalDelayed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Repeats_the_requested_number_of_times()
    {
        var input = new FakeInput();
        var macro = MacroWith(new MouseMoveStep { X = 9 });
        macro.RepeatCount = 3;

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(macro);

        Assert.Equal(3, result.IterationsCompleted);
        Assert.Equal(3, input.Calls.Count);
    }

    [Fact]
    public async Task Waits_between_repeats_but_not_after_the_last()
    {
        var clock = new FakeClock();
        var macro = MacroWith(new MouseMoveStep());
        macro.RepeatCount = 3;
        macro.RepeatDelayMs = 400;

        await new PlaybackEngine(new FakeInput(), clock).RunAsync(macro);

        Assert.Equal(800, clock.TotalDelayed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Repeat_until_stopped_runs_until_Stop()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var count = 0;
        input.OnCall = _ => { if (Interlocked.Increment(ref count) == 5) engine.Stop(); };
        var macro = MacroWith(new MouseMoveStep(), new DelayStep { DurationMs = 10 });
        macro.RepeatCount = 0;

        var result = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.Equal(5, input.Calls.Count);
    }

    [Fact]
    public async Task Stop_releases_held_keys_and_buttons()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call => { if (call == "mouse Left down") engine.Stop(); };

        var result = await engine.RunAsync(MacroWith(
            new KeyPressStep { VirtualKey = 0x10, Action = KeyAction.Down },
            new MouseClickStep { UseCurrentPosition = true, Action = MouseClickAction.Down },
            new DelayStep { DurationMs = 5000 },
            new MouseMoveStep { X = 99 }));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.DoesNotContain("move 99,0", input.Calls);
        Assert.Contains("key 16 up", input.Calls);
        Assert.Contains("mouse Left up", input.Calls);
    }

    [Fact]
    public async Task Input_failure_is_reported_and_held_input_released()
    {
        var input = new FakeInput { ThrowOn = new InputSimulationException("blocked"), ThrowWhen = "move 1,0" };
        var engine = new PlaybackEngine(input, new FakeClock());

        var result = await engine.RunAsync(MacroWith(
            new KeyPressStep { VirtualKey = 0x11, Action = KeyAction.Down },
            new MouseMoveStep { X = 1 }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Equal("blocked", result.Error);
        Assert.Equal("key 17 up", input.Calls[^1]);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public async Task Cannot_start_twice()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input.OnCall = _ => { started.TrySetResult(); SpinWait.SpinUntil(() => release.Task.IsCompleted, 5000); };

        var first = engine.RunAsync(MacroWith(new MouseMoveStep()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(MacroWith(new MouseMoveStep())));

        release.SetResult();
        Assert.Equal(PlaybackOutcome.Completed, (await first).Outcome);
    }

    [Fact]
    public async Task Invalid_or_empty_macro_is_refused()
    {
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());

        await Assert.ThrowsAsync<MacroValidationException>(() => engine.RunAsync(MacroWith()));
        await Assert.ThrowsAsync<MacroValidationException>(() => engine.RunAsync(MacroWith(new DelayStep { DurationMs = -1 })));
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var states = new List<PlaybackState>();
        engine.StateChanged += (_, s) => { lock (states) states.Add(s); };
        input.OnCall = call =>
        {
            if (call != "move 1,0") return;
            Assert.True(engine.Pause());
            _ = Task.Run(async () => { await Task.Delay(50); engine.Resume(); });
        };

        var result = await engine.RunAsync(MacroWith(new MouseMoveStep { X = 1 }, new MouseMoveStep { X = 2 }))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 2,0" }, input.Calls);
        Assert.Contains(PlaybackState.Paused, states);
    }

    [Fact]
    public async Task Stop_while_paused_ends_playback()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        input.OnCall = call =>
        {
            if (call != "move 1,0") return;
            engine.Pause();
            _ = Task.Run(async () => { await Task.Delay(50); engine.Stop(); });
        };

        var result = await engine.RunAsync(MacroWith(new MouseMoveStep { X = 1 }, new MouseMoveStep { X = 2 }))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
        Assert.DoesNotContain("move 2,0", input.Calls);
    }

    [Fact]
    public async Task Progress_reports_editor_step_numbers()
    {
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());
        var numbers = new List<int>();
        engine.ProgressChanged += (_, p) => numbers.Add(p.StepNumber);

        await engine.RunAsync(MacroWith(new MouseMoveStep(), new MouseMoveStep { Enabled = false }, new MouseMoveStep()));

        Assert.Equal(new[] { 1, 3 }, numbers);
    }

    [Fact]
    public async Task Typed_text_handles_surrogate_pairs_and_character_delay()
    {
        var input = new FakeInput();
        var clock = new FakeClock();

        await new PlaybackEngine(input, clock).RunAsync(MacroWith(new TypeTextStep { Text = "a😀b", CharacterDelayMs = 10 }));

        Assert.Equal(new[] { "type a", "type 😀", "type b" }, input.Calls);
        Assert.Equal(20, clock.TotalDelayed.TotalMilliseconds, precision: 3);
    }
}
