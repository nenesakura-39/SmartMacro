using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Regression tests for the 8 bugs found in the 2026-10 test round (screenshots + logs).
/// Fake input / clock / screen only: they prove what SmartMacro decides and sends, not that a real app reacts.
/// Bug 1 (radio buttons unreadable) is XAML-only and is checked by hand.
/// </summary>
public class BugFixTests
{
    // ---------------- Bug 2: OCR look-alike characters ----------------

    private static OcrReadResult OcrScreen(params string[] words)
    {
        var x = 0;
        var boxes = words.Select(w =>
        {
            var box = new OcrWordBox(w, new ScreenRect(x, 0, w.Length * 10, 20));
            x += w.Length * 10 + 10;
            return box;
        }).ToList();
        return new OcrReadResult(new ScreenRect(0, 0, 1000, 100), new[] { new OcrLineBox(boxes) });
    }

    [Theory]
    [InlineData("0K", "OK")]       // zero read for capital O (the logged case)
    [InlineData("C0NTINUE", "Continue")]
    [InlineData("lnfo", "Info")]   // lower-case L read for capital I
    [InlineData("1nfo", "Info")]   // one read for capital I
    [InlineData("|nfo", "Info")]   // pipe read for capital I
    public void Look_alike_characters_match_when_normalizing(string onScreen, string query)
    {
        var screen = OcrScreen("Press", onScreen, "now");

        Assert.Empty(OcrTextMatcher.Find(screen, query, TextMatchMode.WholeWords, false, 1.0, normalizeLookAlikes: false));
        var match = Assert.Single(OcrTextMatcher.Find(screen, query, TextMatchMode.WholeWords, false, 1.0, normalizeLookAlikes: true));
        Assert.Equal(onScreen, match.Text);
        Assert.Equal(1.0, match.Similarity);
    }

    [Fact]
    public void Normalizing_look_alikes_still_rejects_different_words()
    {
        var screen = OcrScreen("Cancel", "0K");
        Assert.Empty(OcrTextMatcher.Find(screen, "NO", TextMatchMode.WholeWords, false, 1.0, normalizeLookAlikes: true));
        Assert.Empty(OcrTextMatcher.Find(screen, "OX", TextMatchMode.WholeWords, false, 1.0, normalizeLookAlikes: true));
    }

    [Fact]
    public void Normalizing_works_with_case_sensitive_search_and_contains()
    {
        Assert.NotEmpty(OcrTextMatcher.Find(OcrScreen("0K"), "OK", TextMatchMode.WholeWords, true, 1.0, normalizeLookAlikes: true));
        Assert.Empty(OcrTextMatcher.Find(OcrScreen("0k"), "OK", TextMatchMode.WholeWords, true, 1.0, normalizeLookAlikes: true));
        Assert.NotEmpty(OcrTextMatcher.Find(OcrScreen("Press0K"), "OK", TextMatchMode.Contains, false, 1.0, normalizeLookAlikes: true));
    }

    [Fact]
    public void Fold_maps_the_documented_characters()
    {
        Assert.Equal("OKIII", OcrTextMatcher.FoldLookAlikes("0K1l|"));
        Assert.Equal(string.Empty, OcrTextMatcher.FoldLookAlikes(""));
    }

    [Fact]
    public void Find_text_normalizes_by_default_also_in_old_files()
    {
        Assert.True(new FindTextStep().NormalizeLookAlikes);
        var json = """{ "schemaVersion": 1, "macro": { "name": "x", "steps": [ { "type": "findText", "text": "OK" } ] } }""";
        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        Assert.True(Assert.IsType<FindTextStep>(macro!.Steps[0]).NormalizeLookAlikes);
    }

    // ---------------- Bug 2: Find step gates the next step ----------------

    private static FindColorStep MissingGreen(TextNotFoundBehavior behavior, bool enabled = true) => new()
    {
        Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50,
        SearchFullScreen = false, RegionX = 0, RegionY = 0, RegionWidth = 300, RegionHeight = 200,
        WhenNotFound = behavior, Enabled = enabled,
    };

    private static async Task<(PlaybackResult Result, List<string> Calls)> PlayOnBlackScreen(params MacroStep[] steps)
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(TestImages.Solid(300, 200, RgbColor.Black)));
        var result = await engine.RunAsync(Flow.Macro(steps));
        return (result, input.Calls);
    }

    [Fact]
    public async Task Skip_next_step_skips_only_the_step_below()
    {
        var (result, calls) = await PlayOnBlackScreen(MissingGreen(TextNotFoundBehavior.SkipNextStep), Flow.Move(1), Flow.Move(2));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 2,0" }, calls);
    }

    [Fact]
    public async Task Continue_still_runs_the_step_below()
    {
        var (_, calls) = await PlayOnBlackScreen(MissingGreen(TextNotFoundBehavior.Continue), Flow.Move(1), Flow.Move(2));

        Assert.Equal(new[] { "move 1,0", "move 2,0" }, calls);
    }

    [Fact]
    public async Task Stop_macro_runs_nothing_below()
    {
        var (result, calls) = await PlayOnBlackScreen(MissingGreen(TextNotFoundBehavior.StopMacro), Flow.Move(1));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Skip_next_step_skips_a_whole_block_below()
    {
        var (result, calls) = await PlayOnBlackScreen(
            MissingGreen(TextNotFoundBehavior.SkipNextStep),
            Flow.Times(3), Flow.Move(1), new EndRepeatStep(),
            Flow.Move(2));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 2,0" }, calls);
    }

    [Fact]
    public async Task Skip_next_step_at_the_end_of_a_block_does_not_break_the_block()
    {
        var (result, calls) = await PlayOnBlackScreen(
            Flow.Times(2), Flow.Move(1), MissingGreen(TextNotFoundBehavior.SkipNextStep), new EndRepeatStep(),
            Flow.Move(2));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 1,0", "move 2,0" }, calls);
    }

    [Fact]
    public void Skip_next_step_needs_format_7_and_round_trips()
    {
        var macro = Flow.Macro(MissingGreen(TextNotFoundBehavior.SkipNextStep), Flow.Move(1));
        Assert.Equal(7, MacroDocument.RequiredSchemaVersion(macro));

        Assert.True(MacroFileFormat.TryParse(MacroFileFormat.Serialize(macro), out var loaded, out var error), error);
        Assert.Equal(TextNotFoundBehavior.SkipNextStep, Assert.IsType<FindColorStep>(loaded!.Steps[0]).WhenNotFound);

        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(MissingGreen(TextNotFoundBehavior.Continue))));
    }

    [Fact]
    public void Disabled_find_step_with_steps_after_it_gets_a_warning()
    {
        var issues = MacroValidator.Validate(Flow.Macro(MissingGreen(TextNotFoundBehavior.StopMacro, enabled: false), Flow.Move(1)));

        Assert.False(issues.HasErrors());
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 0 && i.Message.Contains("turned off"));
    }

    [Fact]
    public void Skip_next_step_with_nothing_to_skip_gets_a_warning()
    {
        var issues = MacroValidator.Validate(Flow.Macro(Flow.Move(1), MissingGreen(TextNotFoundBehavior.SkipNextStep)));

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 1 && i.Message.Contains("nothing to skip"));
    }

    // ---------------- Bug 3: Type text default delay ----------------

    [Fact]
    public void Type_text_defaults_to_20_ms_between_characters()
    {
        Assert.Equal(20, new TypeTextStep().CharacterDelayMs);
        Assert.Equal(20, Assert.IsType<TypeTextStep>(MacroFactory.CreateStep(StepKind.TypeText)).CharacterDelayMs);
    }

    [Fact]
    public async Task Type_text_waits_between_characters_including_around_spaces()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(new TypeTextStep { Text = "Found Wow" }));

        Assert.Equal(new[] { "type F", "type o", "type u", "type n", "type d", "type  ", "type W", "type o", "type w" }, input.Calls);
        for (var i = 1; i < input.Times.Count; i++)
            Assert.Equal(TimeSpan.FromMilliseconds(20), input.Times[i] - input.Times[i - 1]);
    }

    // ---------------- Bug 4: Find image on round / game UI ----------------

    /// <summary>A 40×40 "round button": a patterned disc; the corners outside the disc show <paramref name="corners"/>.</summary>
    private static BgraImage RoundButton(BgraImage pattern, Func<int, int, RgbColor> corners)
    {
        var image = pattern.Clone();
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var dx = x - 19.5;
                var dy = y - 19.5;
                if (dx * dx + dy * dy > 20.0 * 20.0) image.SetPixel(x, y, corners(x, y));
            }
        }

        return image;
    }

    [Fact]
    public void Ignoring_the_edges_finds_a_round_button_on_a_changing_background()
    {
        var pattern = TestImages.Icon(40, 40, seed: 3);
        var captured = RoundButton(pattern, (_, _) => new RgbColor(90, 140, 60)); // what was behind it when captured

        // Now the 3D scene behind the button is completely different (noise).
        var random = new Random(5);
        var onScreen = RoundButton(pattern, (_, _) => new RgbColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        var screen = TestImages.Blocky(320, 240, seed: 9);
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++)
                screen.SetPixel(150 + x, 90 + y, onScreen.GetPixel(x, y));

        var whole = TemplateMatcher.Find(screen, captured, new TemplateMatchOptions { MinScore = 0.97 });
        var inner = TemplateMatcher.Find(screen, captured, new TemplateMatchOptions { MinScore = 0.97, IgnoreEdgeFraction = 0.15 });

        Assert.Null(whole.Best);
        var best = Assert.IsType<VisualMatch>(inner.Best);
        Assert.Equal(new ScreenRect(150, 90, 40, 40), best.Bounds); // full button rectangle, not the inner part
        Assert.True(best.Score > 0.99);
        Assert.True(inner.BestScore > whole.BestScore);
    }

    [Theory]
    [InlineData(40, 40, 0.15, 6, 6, 28, 28)]
    [InlineData(121, 134, 0.15, 18, 20, 85, 94)] // the Roblox "SHAKE" button size from the test
    [InlineData(10, 10, 0.4, 2, 2, 6, 6)]        // shrinks the border so 6 px stay
    public void Inner_area_keeps_enough_pixels(int w, int h, double fraction, int x, int y, int iw, int ih)
    {
        Assert.Equal(new ScreenRect(x, y, iw, ih), TemplateMatcher.InnerArea(w, h, fraction));
    }

    [Fact]
    public void Inner_area_is_null_when_nothing_is_cut()
    {
        Assert.Null(TemplateMatcher.InnerArea(40, 40, 0));
        Assert.Null(TemplateMatcher.InnerArea(6, 6, 0.3));
    }

    [Fact]
    public void Ignore_edges_is_validated_and_needs_format_7()
    {
        var step = new FindImageStep { IgnoreEdgesPercent = 41 };
        Assert.Contains(MacroValidator.ValidateStep(step), i => i.Property == nameof(FindImageStep.IgnoreEdgesPercent) && i.Severity == IssueSeverity.Error);

        Assert.Equal(7, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindImageStep { IgnoreEdgesPercent = 15 })));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindImageStep())));
    }

    // ---------------- Bug 5: recording flood, drag reversals, final spot ----------------

    private static RawInputEvent Move(long t, int x, int y) => new(t, RawInputKind.MouseMove, x, y);

    [Fact]
    public void Hovering_for_10_seconds_gives_a_few_moves_not_hundreds()
    {
        // ~300 samples, one every 33 ms, along a gentle curve (like the 9.9 s Roblox clip).
        var events = Enumerable.Range(0, 300)
            .Select(i => Move(i * 33, 100 + i * 3, 300 + (int)Math.Round(40 * Math.Sin(i / 300.0 * Math.PI))))
            .ToList();

        var old = RecordingConverter.Convert(events);
        var steps = RecordingConverter.Convert(events, new RecordingOptions { SimplifyMousePath = true });

        Assert.True(old.Count > 280, $"old behaviour gave {old.Count} steps");
        Assert.InRange(steps.Count, 2, 40);
        var last = Assert.IsType<MouseMoveStep>(steps[^1]);
        Assert.Equal((100 + 299 * 3, 300), (last.X, last.Y)); // ends exactly where the cursor ended
        Assert.Equal(299 * 33, steps.OfType<DelayStep>().Sum(d => d.DurationMs)); // timing kept
    }

    [Fact]
    public void Slider_drag_keeps_the_reversal_point_and_the_final_spot_before_release()
    {
        // Press at 50 %, drag to 80 % (x 580), back to 50 % (x 500), release.
        var events = new List<RawInputEvent> { new(0, RawInputKind.MouseDown, 500, 400) };
        var t = 0L;
        for (var x = 502; x <= 580; x += 2) events.Add(Move(t += 16, x, 400));
        for (var x = 578; x >= 500; x -= 2) events.Add(Move(t += 16, x, 400));
        events.Add(new RawInputEvent(t + 16, RawInputKind.MouseUp, 500, 400));

        var steps = RecordingConverter.Convert(events, new RecordingOptions { SimplifyMousePath = true });

        var moves = steps.OfType<MouseMoveStep>().Select(m => m.X).ToList();
        Assert.Contains(580, moves);                // the extreme of the drag
        Assert.Equal(500, moves[^1]);               // back at 50 % before the release
        Assert.True(moves.Count <= 4, string.Join(",", moves));
        var up = Assert.IsType<MouseClickStep>(steps[^1]);
        Assert.Equal((MouseClickAction.Up, 500, 400), (up.Action, up.X, up.Y));
        Assert.Equal((int)(t + 16), steps.OfType<DelayStep>().Sum(d => d.DurationMs));
    }

    [Fact]
    public void Hand_jitter_is_not_a_reversal()
    {
        var events = new List<RawInputEvent>();
        for (var i = 0; i < 100; i++) events.Add(Move(i * 10, 200 + i * 2 + (i % 2), 200 + (i % 3 == 0 ? 1 : 0)));

        var kept = MousePathSimplifier.Simplify(events, tolerancePx: 3, restMs: 120);

        Assert.InRange(kept.Count, 2, 4);
        Assert.Equal(events[0], kept[0]);
        Assert.Equal(events[^1], kept[^1]);
    }

    [Fact]
    public void A_rest_is_kept()
    {
        var events = new List<RawInputEvent> { Move(0, 0, 0), Move(10, 10, 0), Move(20, 20, 0), Move(500, 30, 0), Move(510, 40, 0) };

        var kept = MousePathSimplifier.Simplify(events, tolerancePx: 3, restMs: 120);

        Assert.Contains(events[2], kept); // the cursor waited at x 20 for 480 ms
    }

    [Fact]
    public void Smart_recording_of_free_cursor_movement_is_compact()
    {
        var events = new List<CapturedInputEvent>();
        for (var i = 0; i < 300; i++) events.Add(new(InputEvent.Move(i * 33_000L, 3, 0, 100 + i * 3, 300)));
        events.Add(new(InputEvent.ButtonDown(9_950_000, MacroMouseButton.Left, 997, 300)));
        events.Add(new(InputEvent.ButtonUp(10_000_000, MacroMouseButton.Left, 997, 300)));

        var result = SmartRecordingConverter.Convert(events);

        Assert.DoesNotContain(result.Steps, s => s is InputSequenceStep);
        Assert.InRange(result.Steps.Count, 2, 12);
        var click = Assert.Single(result.Steps.OfType<MouseClickStep>());
        Assert.Equal((997, 300), (click.X, click.Y));
    }

    // ---------------- Bug 6: scroll notches ----------------

    [Fact]
    public async Task Scroll_notches_are_15_ms_apart_and_do_not_shorten_the_next_delay()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(
            new MouseScrollStep { Delta = -360 }, new DelayStep { DurationMs = 100 }, Flow.Move(1)));

        Assert.Equal(new[] { "scroll -120", "scroll -120", "scroll -120", "move 1,0" }, input.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(15), input.Times[1] - input.Times[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(15), input.Times[2] - input.Times[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), input.Times[3] - input.Times[2]);
    }

    [Fact]
    public async Task Recorded_wheel_events_are_sent_as_recorded()
    {
        var input = new FakeInput();
        var step = new InputSequenceStep { Events = { InputEvent.WheelEvent(0, -240) } };
        await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(step));

        Assert.Equal(new[] { "scroll -240" }, input.Calls);
    }

    // ---------------- Bug 7: fine relative slices, no drift ----------------

    [Fact]
    public void Move_merging_never_uses_slices_wider_than_8_ms()
    {
        var raw = Enumerable.Range(0, 40).Select(i => InputEvent.Move(i * 1_000L, 1, 0)).ToList(); // 40 ms, 1 ms apart

        var step = GameRecordingConverter.Convert(raw, new GameRecordingOptions { MoveCoalesceUs = 50_000 })!;

        Assert.Equal(40, step.Events.Sum(e => e.Dx)); // nothing lost
        Assert.True(step.Events.Count >= 5, $"{step.Events.Count} slices");
        for (var i = 1; i < step.Events.Count; i++)
            Assert.True(step.Events[i].TimeUs - step.Events[i - 1].TimeUs <= GameRecordingConverter.MaxMoveCoalesceUs + 1_000);
    }

    /// <summary>A clock whose waits always overshoot by 1 ms, like a real timer.</summary>
    private sealed class LateClock : IPlaybackClock
    {
        private long _ticks;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _ticks, (delay + TimeSpan.FromMilliseconds(1)).Ticks);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Relative_movement_follows_the_recorded_timeline_without_drift()
    {
        var clock = new LateClock();
        var input = new FakeInput { Clock = clock };
        var step = new InputSequenceStep();
        for (var i = 0; i < 500; i++) step.Events.Add(InputEvent.Move(i * 8_000L, 2, 0)); // 4 s of camera turning

        await new PlaybackEngine(input, clock).RunAsync(Flow.Macro(step));

        Assert.Equal(500, input.Calls.Count);
        var first = input.Times[0];
        var last = input.Times[^1] - first;
        // 499 waits that each overshoot by 1 ms would add 0.5 s if the waits were relative.
        Assert.InRange(last.TotalMilliseconds, 499 * 8, 499 * 8 + 2);
    }

    [Fact]
    public async Task Delay_steps_do_not_add_up_timer_overshoot()
    {
        var clock = new LateClock();
        var input = new FakeInput { Clock = clock };
        var macro = Flow.Macro();
        for (var i = 0; i < 200; i++)
        {
            macro.Steps.Add(new MouseMoveStep { X = i });
            macro.Steps.Add(new DelayStep { DurationMs = 10 });
        }

        await new PlaybackEngine(input, clock).RunAsync(macro);

        var span = input.Times[^1] - input.Times[0];
        Assert.InRange(span.TotalMilliseconds, 199 * 10, 199 * 10 + 2);
    }

    // ---------------- Bug 8: modifier combos ----------------

    private static RawInputEvent KeyDown(long t, int vk) => new(t, RawInputKind.KeyDown, VirtualKey: vk);
    private static RawInputEvent KeyUp(long t, int vk) => new(t, RawInputKind.KeyUp, VirtualKey: vk);

    private const int LAlt = 0xA4;
    private const int LCtrl = 0xA2;
    private const int LShift = 0xA0;
    private const int Tab = 0x09;

    [Theory]
    [InlineData(LAlt, Tab)]
    [InlineData(LCtrl, 0x43)]  // Ctrl+C
    [InlineData(LShift, 0x41)] // Shift+A
    public void Modifier_held_across_a_key_is_kept_around_it(int modifier, int key)
    {
        var steps = RecordingConverter.Convert(new[] { KeyDown(0, modifier), KeyDown(80, key), KeyUp(130, key), KeyUp(200, modifier) });

        var keys = steps.OfType<KeyPressStep>().Select(k => (k.VirtualKey, k.Action)).ToList();
        Assert.Equal(new[] { (modifier, KeyAction.Down), (key, KeyAction.Press), (modifier, KeyAction.Up) }, keys);
    }

    [Fact]
    public void Alt_tab_tab_keeps_every_tab()
    {
        var steps = RecordingConverter.Convert(new[]
        {
            KeyDown(0, LAlt), KeyDown(100, Tab), KeyUp(150, Tab), KeyDown(300, Tab), KeyUp(350, Tab), KeyUp(500, LAlt),
        });

        Assert.Equal(2, steps.OfType<KeyPressStep>().Count(k => k.VirtualKey == Tab && k.Action == KeyAction.Press));
        Assert.DoesNotContain(steps.OfType<KeyPressStep>(), k => k.VirtualKey == LAlt && k.Action == KeyAction.Press);
    }

    [Fact]
    public void Smart_recording_keeps_alt_tab_as_a_combo()
    {
        var events = new List<CapturedInputEvent>
        {
            new(InputEvent.KeyDown(0, 0x12, 0x38)),        // Alt (generic VK from Raw Input, left Alt scan code)
            new(InputEvent.KeyDown(90_000, Tab, 0x0F)),
            new(InputEvent.KeyUp(140_000, Tab, 0x0F)),
            new(InputEvent.KeyUp(220_000, 0x12, 0x38)),
        };

        var result = SmartRecordingConverter.Convert(events);

        var keys = result.Steps.OfType<KeyPressStep>().Select(k => (k.VirtualKey, k.Action)).ToList();
        Assert.Equal(new[] { (LAlt, KeyAction.Down), (Tab, KeyAction.Press), (LAlt, KeyAction.Up) }, keys);
    }

    [Fact]
    public async Task Alt_tab_combo_plays_in_order()
    {
        var input = new FakeInput();
        var steps = RecordingConverter.Convert(new[] { KeyDown(0, LAlt), KeyDown(80, Tab), KeyUp(130, Tab), KeyUp(200, LAlt) });

        await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(steps.ToArray()));

        Assert.Equal(new[] { $"key {LAlt} down", $"key {Tab} down", $"key {Tab} up", $"key {LAlt} up" }, input.Calls);
    }
}
