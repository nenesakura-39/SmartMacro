using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

// Fixes from the owner's live test of b408b36 (7 bugs). The UI / layout bugs (1-3) and "hide before test" (7) are WPF
// only and are covered by the manual checklist (docs/LIVE_TEST_FIXES_MANUAL_TESTS.md); this file covers bugs 4-6.

// ---------------- Bug 4: small templates and see-through game buttons ----------------

public class SmallTemplateAndForegroundMatchingTests
{
    /// <summary>
    /// A see-through round game button like Roblox Fisch's "SHAKE": a white ring and white block letters drawn at 90 %
    /// over whatever is behind it.
    /// </summary>
    internal static BgraImage SeeThroughButton(BgraImage background)
    {
        var image = background.Clone();
        int w = image.Width, h = image.Height;
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, r = Math.Min(w, h) / 2.0 - 1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                var ring = d <= r && d >= r - 3;
                // Five "letters": 3 px wide strokes in the middle band.
                var inBand = y >= h * 0.4 && y <= h * 0.6;
                var letter = inBand && x >= w * 0.2 && x <= w * 0.8 && ((x - (int)(w * 0.2)) % 9 < 3 || y <= h * 0.42 || y >= h * 0.58);
                if (!ring && !letter) continue;
                var p = background.GetPixel(x, y);
                image.SetPixel(x, y, new RgbColor(Blend(p.R), Blend(p.G), Blend(p.B)));
            }
        }

        return image;

        static byte Blend(byte v) => (byte)Math.Round(v * 0.1 + 255 * 0.9);
    }

    /// <summary>A textured surface (wood, water…): a base colour with deterministic noise.</summary>
    internal static BgraImage Textured(int width, int height, RgbColor color, int seed, int amount = 18)
    {
        var random = new Random(seed);
        var image = BgraImage.Create(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var n = random.Next(-amount, amount + 1);
                image.SetPixel(x, y, new RgbColor(Clamp(color.R + n), Clamp(color.G + n), Clamp(color.B + n)));
            }
        }

        return image;

        static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    private static readonly RgbColor Wood = new(120, 80, 40);
    private static readonly RgbColor Water = new(30, 90, 170);

    [Fact]
    public void Small_templates_are_never_shrunk_to_a_quarter()
    {
        // The Notepad "Hi" case: a ~30×36 template in a 1424×777 area used a 1/4 coarse pass and missed.
        Assert.True(TemplateMatcher.ChooseScale(1424, 777, 30, 36) <= 2);
        Assert.True(TemplateMatcher.ChooseScale(800, 600, 103, 85) <= 2);
        // Big templates on a big screen still use the fast coarse pass.
        Assert.True(TemplateMatcher.ChooseScale(1920, 1080, 200, 150) > 2);
    }

    [Fact]
    public void Thin_text_on_a_big_plain_area_is_found_exactly()
    {
        var screen = TestImages.Solid(1424, 777, new RgbColor(40, 40, 40));
        var glyphs = TestImages.Solid(30, 36, new RgbColor(40, 40, 40));
        // "Hi" with 1-pixel strokes.
        glyphs.FillRectangle(new ScreenRect(6, 8, 1, 20), RgbColor.White);
        glyphs.FillRectangle(new ScreenRect(14, 8, 1, 20), RgbColor.White);
        glyphs.FillRectangle(new ScreenRect(6, 17, 9, 1), RgbColor.White);
        glyphs.FillRectangle(new ScreenRect(21, 14, 1, 14), RgbColor.White);
        glyphs.FillRectangle(new ScreenRect(21, 10, 1, 2), RgbColor.White);
        TestImages.Paste(screen, glyphs, 25, 167);
        // Other text-like strokes elsewhere.
        screen.FillRectangle(new ScreenRect(600, 400, 1, 20), RgbColor.White);
        screen.FillRectangle(new ScreenRect(900, 100, 12, 1), RgbColor.White);

        var result = TemplateMatcher.Find(screen, glyphs, new TemplateMatchOptions { MinScore = 0.85 });

        Assert.True(result.Scale <= 2);
        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(25, 167, 30, 36), best.Bounds);
        Assert.True(best.Score > 0.95, $"score {best.Score}");
    }

    [Fact]
    public void A_see_through_button_over_a_different_background_fails_normally_but_matches_with_ignore_background()
    {
        var template = SeeThroughButton(Textured(60, 50, Wood, seed: 1));       // captured over the pier
        var screen = Textured(400, 300, Water, seed: 2);                         // now it spawned over water
        TestImages.Paste(screen, SeeThroughButton(Textured(60, 50, Water, seed: 3)), 150, 120);

        var normal = TemplateMatcher.Find(screen, template, new TemplateMatchOptions { MinScore = 0.85 });
        Assert.Null(normal.Best); // the brown inside the ring doesn't match blue water

        var foreground = TemplateMatcher.Find(screen, template, new TemplateMatchOptions { MinScore = 0.85, MatchForeground = true });
        var best = Assert.IsType<VisualMatch>(foreground.Best);
        Assert.Equal(new ScreenRect(150, 120, 60, 50), best.Bounds);
        Assert.True(best.Score >= 0.9, $"score {best.Score}");
    }

    [Fact]
    public void Ignore_background_does_not_match_plain_water()
    {
        var template = SeeThroughButton(Textured(60, 50, Wood, seed: 1));
        var screen = Textured(400, 300, Water, seed: 4);

        var result = TemplateMatcher.Find(screen, template, new TemplateMatchOptions { MinScore = 0.85, MatchForeground = true });

        Assert.Null(result.Best);
        Assert.True(result.BestScore < 0.5, $"best {result.BestScore}");
    }

    [Fact]
    public void Ignore_background_still_compares_the_foreground_colour()
    {
        var template = SeeThroughButton(Textured(60, 50, Wood, seed: 1)); // white strokes
        var screen = Textured(400, 300, Water, seed: 2);
        var yellow = SeeThroughButton(Textured(60, 50, Water, seed: 3));
        // Turn the white strokes yellow (blue channel down).
        for (var y = 0; y < yellow.Height; y++)
        for (var x = 0; x < yellow.Width; x++)
        {
            var p = yellow.GetPixel(x, y);
            if (p.R > 200) yellow.SetPixel(x, y, new RgbColor(p.R, p.G, 40));
        }

        TestImages.Paste(screen, yellow, 150, 120);

        Assert.Null(TemplateMatcher.Find(screen, template, new TemplateMatchOptions { MinScore = 0.85, MatchForeground = true }).Best);
        Assert.NotNull(TemplateMatcher.Find(screen, template,
            new TemplateMatchOptions { MinScore = 0.85, MatchForeground = true, IgnoreColor = true }).Best);
    }

    [Fact]
    public void Patterned_icons_are_still_found_exactly_with_ignore_background_on()
    {
        var screen = TestImages.Blocky(300, 200, seed: 1);
        var icon = TestImages.Icon(24, 24, seed: 2);
        TestImages.Paste(screen, icon, 100, 50);

        var result = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { MatchForeground = true });

        Assert.Equal(new ScreenRect(100, 50, 24, 24), Assert.IsType<VisualMatch>(result.Best).Bounds);
    }

    [Fact]
    public async Task Find_image_with_ignore_background_clicks_the_button_over_water()
    {
        var template = SeeThroughButton(Textured(60, 50, Wood, seed: 1));
        var screen = Textured(400, 300, Water, seed: 2);
        TestImages.Paste(screen, SeeThroughButton(Textured(60, 50, Water, seed: 3)), 150, 120);
        var step = new FindImageStep
        {
            TemplateWidth = 60, TemplateHeight = 50, TemplateData = TemplateCodec.Encode(template),
            MinSimilarityPercent = 85, MatchForeground = true, TimeoutMs = 0, Action = FoundTextAction.Click,
        };
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 180,145", "mouse Left down", "mouse Left up" }, input.Calls);
    }
}

// ---------------- Bug 5: Repeat grace window, found-click timing, move away ----------------

public class RepeatGraceAndFoundClickTests
{
    private static RepeatStep WhileGreen(int? keepCheckingForMs = null)
    {
        var repeat = Flow.While(Flow.Green());
        repeat.KeepCheckingForMs = keepCheckingForMs;
        return repeat;
    }

    [Fact]
    public async Task Repeat_while_on_screen_survives_a_short_gap_between_buttons()
    {
        // Seen, missing for one check (the next Shake button is still spawning), seen again, then gone for good.
        var capture = new FakeCapture(Flow.Screen(green: true), Flow.Screen(green: false), Flow.Screen(green: true), Flow.Screen(green: false));
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(
            WhileGreen(), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task Grace_window_zero_leaves_on_the_first_miss_like_before()
    {
        var capture = new FakeCapture(Flow.Screen(green: true), Flow.Screen(green: false), Flow.Screen(green: true), Flow.Screen(green: false));
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Flow.Macro(
            WhileGreen(keepCheckingForMs: 0), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(new[] { "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public async Task The_loop_still_ends_when_the_target_stays_away_for_the_whole_window()
    {
        var clock = new FakeClock();
        var capture = new FakeCapture(Flow.Screen(green: true), Flow.Screen(green: false));
        var input = new FakeInput { Clock = clock };

        var result = await new PlaybackEngine(input, clock, capture: capture).RunAsync(Flow.Macro(
            WhileGreen(), Flow.Move(1), new EndRepeatStep(), Flow.Move(2)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 2,0" }, input.Calls);
        Assert.True(input.Times[1] - input.Times[0] >= TimeSpan.FromMilliseconds(RepeatStep.DefaultKeepCheckingForMs));
    }

    [Fact]
    public async Task Until_not_on_screen_also_waits_out_a_gap_but_until_on_screen_ends_at_once()
    {
        var gap = new FakeCapture(Flow.Screen(green: true), Flow.Screen(green: false), Flow.Screen(green: true), Flow.Screen(green: false));
        var input = new FakeInput();
        await new PlaybackEngine(input, new FakeClock(), capture: gap).RunAsync(Flow.Macro(
            Flow.Until(Flow.Green(negate: true)), Flow.Move(1), new EndRepeatStep()));
        Assert.Equal(new[] { "move 1,0", "move 1,0" }, input.Calls);

        var appears = new FakeCapture(Flow.Screen(green: false), Flow.Screen(green: true));
        var input2 = new FakeInput();
        await new PlaybackEngine(input2, new FakeClock(), capture: appears).RunAsync(Flow.Macro(
            Flow.Until(Flow.Green()), Flow.Move(1), new EndRepeatStep()));
        Assert.Equal(new[] { "move 1,0" }, input2.Calls);
        Assert.Equal(2, appears.Captures); // seeing the target is trusted right away: no extra checks
    }

    [Fact]
    public void Grace_window_defaults_and_validation()
    {
        Assert.Equal(RepeatStep.DefaultKeepCheckingForMs, Flow.While(Flow.Green()).EffectiveKeepCheckingForMs);
        Assert.Equal(0, Flow.While(Flow.Text("Go")).EffectiveKeepCheckingForMs); // OCR is slow: no automatic grace
        Assert.True(Flow.While(Flow.Green()).EndsWhenTargetMissing);
        Assert.False(Flow.While(Flow.Green(negate: true)).EndsWhenTargetMissing);
        Assert.True(Flow.Until(Flow.Green(negate: true)).EndsWhenTargetMissing);
        Assert.False(Flow.Until(Flow.Green()).EndsWhenTargetMissing);

        var bad = Flow.While(Flow.Green());
        bad.KeepCheckingForMs = -1;
        Assert.Contains(MacroValidator.ValidateStep(bad), i => i.Severity == IssueSeverity.Error && i.Property == nameof(RepeatStep.KeepCheckingForMs));
        bad.KeepCheckingForMs = 1500;
        Assert.DoesNotContain(MacroValidator.ValidateStep(bad), i => i.Property == nameof(RepeatStep.KeepCheckingForMs));
    }

    private static FindColorStep GreenClick(FoundTextAction action) => new()
    {
        Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0, CheckIntervalMs = 50, Action = action,
    };

    [Fact]
    public async Task Found_clicks_hold_the_button_and_double_clicks_leave_a_gap()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };

        await new PlaybackEngine(input, clock, capture: new FakeCapture(Flow.Screen(green: true)))
            .RunAsync(Flow.Macro(GreenClick(FoundTextAction.DoubleClick)));

        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(PlaybackEngine.FoundClickHoldMs, (input.Times[2] - input.Times[1]).TotalMilliseconds, precision: 3);
        Assert.Equal(PlaybackEngine.DoubleClickGapMs, (input.Times[3] - input.Times[2]).TotalMilliseconds, precision: 3);
        Assert.Equal(PlaybackEngine.FoundClickHoldMs, (input.Times[4] - input.Times[3]).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Move_away_after_clicking_leaves_the_search_area()
    {
        var full = GreenClick(FoundTextAction.Click);
        full.MoveCursorAwayAfterAction = true;
        var area = GreenClick(FoundTextAction.Click);
        area.MoveCursorAwayAfterAction = true;
        area.SearchFullScreen = false;
        (area.RegionX, area.RegionY, area.RegionWidth, area.RegionHeight) = (150, 50, 100, 100);
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true))).RunAsync(Flow.Macro(full, area));

        Assert.Equal(new[]
        {
            "move 205,105", "mouse Left down", "mouse Left up", $"move 205,{105 + PlaybackEngine.MoveAwayDistance}",
            "move 205,105", "mouse Left down", "mouse Left up", "move 258,158",
        }, input.Calls);
    }

    [Fact]
    public void New_options_need_format_9_and_round_trip()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 9); // 10 since the next-gen QoL batch
        Assert.Equal(9, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindImageStep { MatchForeground = true })));
        Assert.Equal(9, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindColorStep { MoveCursorAwayAfterAction = true })));
        var withGrace = Flow.While(Flow.Green());
        withGrace.KeepCheckingForMs = 0;
        Assert.Equal(9, MacroDocument.RequiredSchemaVersion(Flow.Macro(withGrace, Flow.Move(1), new EndRepeatStep())));
        var imageCondition = Flow.If(new Condition { Check = new FindImageStep { MatchForeground = true } });
        Assert.Equal(9, MacroDocument.RequiredSchemaVersion(Flow.Macro(imageCondition, new EndIfStep())));
        // Untouched macros keep their older format.
        Assert.Equal(5, MacroDocument.RequiredSchemaVersion(Flow.Macro(Flow.While(Flow.Green()), Flow.Move(1), new EndRepeatStep())));
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(Flow.Macro(new FindImageStep())));

        var json = MacroFileFormat.Serialize(Flow.Macro(new FindImageStep { MatchForeground = true, MoveCursorAwayAfterAction = true },
            withGrace, Flow.Move(1), new EndRepeatStep()));
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var image = Assert.IsType<FindImageStep>(loaded!.Steps[0]);
        Assert.True(image.MatchForeground && image.MoveCursorAwayAfterAction);
        Assert.Equal(0, Assert.IsType<RepeatStep>(loaded.Steps[1]).KeepCheckingForMs);

        // Files from before: options off, grace automatic.
        Assert.True(MacroFileFormat.TryParse(MacroFileFormat.Serialize(Flow.Macro(new FindImageStep(), Flow.While(Flow.Green()), Flow.Move(1), new EndRepeatStep())),
            out var old, out error), error);
        Assert.False(Assert.IsType<FindImageStep>(old!.Steps[0]).MatchForeground);
        Assert.Null(Assert.IsType<RepeatStep>(old.Steps[1]).KeepCheckingForMs);
    }
}

// ---------------- Bug 6: smooth playback of moves and drags ----------------

public class SmoothMouseTests
{
    [Fact]
    public async Task A_jump_after_a_delay_glides_there_without_changing_the_total_time()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock) { SmoothMouseMoves = true };

        await engine.RunAsync(Flow.Macro(Flow.Move(0), new DelayStep { DurationMs = 300 }, Flow.Move(100)));

        var calls = input.Calls;
        Assert.Equal("move 0,0", calls[0]);
        Assert.Equal("move 100,0", calls[^1]);
        var xs = calls.Select(c => int.Parse(c.Split(' ', ',')[1])).ToList();
        Assert.True(xs.Count >= 20, $"{xs.Count} moves");
        Assert.Equal(xs.OrderBy(x => x), xs);          // always forward, no jumps back
        Assert.True(xs.Zip(xs.Skip(1)).All(p => p.Second - p.First <= 15), string.Join(",", xs)); // no big jumps
        Assert.Equal(300, input.Times[^1].TotalMilliseconds, precision: 3);   // timing unchanged
        Assert.Equal(300 - PlaybackEngine.MaxGlideMs + 8, input.Times[1].TotalMilliseconds, precision: 3); // glide = end of the wait
    }

    [Fact]
    public async Task Without_the_setting_moves_jump_exactly_as_before()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(Flow.Move(0), new DelayStep { DurationMs = 300 }, Flow.Move(100)));

        Assert.Equal(new[] { "move 0,0", "move 100,0" }, input.Calls);
    }

    [Fact]
    public async Task No_glide_when_the_start_is_unknown()
    {
        var input = new FakeInput(); // can't report the cursor position
        var engine = new PlaybackEngine(input, new FakeClock()) { SmoothMouseMoves = true };

        await engine.RunAsync(Flow.Macro(new DelayStep { DurationMs = 100 }, Flow.Move(50)));

        Assert.Equal(new[] { "move 50,0" }, input.Calls);
    }

    [Fact]
    public async Task Clicks_at_a_position_glide_there_too()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock()) { SmoothMouseMoves = true };

        await engine.RunAsync(Flow.Macro(Flow.Move(0), new DelayStep { DurationMs = 50 }, new MouseClickStep { X = 80, Y = 40 }));

        Assert.True(input.Calls.Count > 4);
        Assert.Equal(new[] { "move 80,40", "mouse Left down", "mouse Left up" }, input.Calls.TakeLast(3));
    }

    [Fact]
    public void Drags_keep_a_point_at_least_every_50_ms_and_their_reversals()
    {
        var events = new List<RawInputEvent> { new(0, RawInputKind.MouseDown, 500, 400) };
        var t = 0L;
        for (var x = 501; x <= 580; x++) events.Add(new RawInputEvent(t += 8, RawInputKind.MouseMove, x, 400));
        for (var x = 579; x >= 500; x--) events.Add(new RawInputEvent(t += 8, RawInputKind.MouseMove, x, 400));
        events.Add(new RawInputEvent(t + 8, RawInputKind.MouseUp, 500, 400));

        var kept = MousePathSimplifier.Simplify(events, tolerancePx: 3, restMs: 120);

        var moves = kept.Where(e => e.Kind == RawInputKind.MouseMove).ToList();
        Assert.Contains(moves, m => m.X == 580);
        Assert.Equal(500, moves[^1].X);
        Assert.All(moves.Zip(moves.Skip(1)), p => Assert.True(p.Second.TimeMs - p.First.TimeMs <= MousePathSimplifier.DragKeepEveryMs + 8));

        // Free movement (no button held) is still thinned hard.
        var free = events.Where(e => e.Kind == RawInputKind.MouseMove).ToList();
        Assert.True(MousePathSimplifier.Simplify(free, 3, 120).Count <= 4);
    }
}

public class Commit6852889RegressionTests
{
    private const int W = 0x57;

    [Fact]
    public async Task Holding_W_for_1000ms_emits_repeated_KeyDown_pulses_before_final_KeyUp()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = new Macro
        {
            Name = "Hold W",
            Steps = { new KeyPressStep { VirtualKey = W, Action = KeyAction.Press, HoldMs = 1000 } },
        };

        var result = await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal("key 87 down", input.Calls[0]);
        Assert.Equal("key 87 up", input.Calls[^1]);
        // All intermediate calls must be repeated KeyDown pulses with no KeyUp in between.
        var intermediate = input.Calls.Skip(1).SkipLast(1).ToList();
        Assert.True(intermediate.Count >= 20, $"Expected >= 20 repeat KeyDown pulses, got {intermediate.Count}");
        Assert.All(intermediate, call => Assert.Equal("key 87 down", call));
        Assert.Equal(1000.0, clock.Elapsed.TotalMilliseconds, precision: 2);
    }

    [Fact]
    public void Left_Right_Left_Right_mouse_movement_in_DesktopOnly_mode_preserves_all_reversals_and_coordinates()
    {
        // Simulate moving the cursor: (100, 500) -> (400, 500) -> (100, 500) -> (400, 500) -> (100, 500)
        // followed by pressing the stop hotkey (Ctrl+Shift+F9).
        var raw = new List<RawInputEvent>();
        long t = 0;
        int[] waypoints = { 100, 400, 100, 400, 100 };
        for (var seg = 0; seg < waypoints.Length - 1; seg++)
        {
            var from = waypoints[seg];
            var to = waypoints[seg + 1];
            var step = from < to ? 10 : -10;
            for (var x = from; x != to; x += step)
            {
                raw.Add(new RawInputEvent(t, RawInputKind.MouseMove, x, 500));
                t += 8;
            }
        }
        raw.Add(new RawInputEvent(t, RawInputKind.MouseMove, 100, 500));
        // Stop hotkey at the end
        raw.Add(new RawInputEvent(t + 20, RawInputKind.KeyDown, VirtualKey: 0xA2));
        raw.Add(new RawInputEvent(t + 30, RawInputKind.KeyDown, VirtualKey: 0xA0));
        raw.Add(new RawInputEvent(t + 40, RawInputKind.KeyDown, VirtualKey: 0x78));

        var steps = RecordingConverter.Convert(raw, new RecordingOptions
        {
            RecordMouseMoves = true,
            SimplifyMousePath = true,
            CollapseMousePaths = true, // Even with CollapseMousePaths on, pure mouse moves must not be discarded
            TrimTrailingKeys = new[] { 0x11, 0xA2, 0x10, 0xA0, 0x78 },
        });

        var moves = steps.OfType<MouseMoveStep>().ToList();
        Assert.True(moves.Count >= 5, $"Expected at least 5 move points for L-R-L-R oscillation, got {moves.Count}");
        Assert.Equal(100, moves[0].X);
        Assert.Equal(400, moves[1].X);
        Assert.Equal(100, moves[2].X);
        Assert.Equal(400, moves[3].X);
        Assert.Equal(100, moves[^1].X);
    }

    [Fact]
    public async Task Rhythm_game_200_key_presses_over_10_seconds_with_overlapping_keys_has_under_5ms_total_error()
    {
        // 6-key rhythm game (Q, W, E, R, T, Y) with 200 notes across 10,000 ms, including overlapping chords/streams.
        int[] rhythmKeys = { 0x51, 0x57, 0x45, 0x52, 0x54, 0x59 };
        int[] scanCodes = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 };
        var captured = new List<CapturedInputEvent>();
        var expectedEvents = new List<(long TimeUs, int Vk, bool Down)>();

        for (var i = 0; i < 200; i++)
        {
            var keyIdx = i % rhythmKeys.Length;
            var vk = rhythmKeys[keyIdx];
            var sc = scanCodes[keyIdx];
            // Notes spaced every 50 ms (0 ms .. 9950 ms); every 3rd note holds 65 ms so it overlaps the next note!
            var downUs = i * 50_000L;
            var holdUs = (i % 3 == 0 && i < 199) ? 65_000L : 35_000L;
            var upUs = downUs + holdUs;
            if (i == 199) upUs = 10_000_000L; // last release at exactly 10,000 ms

            expectedEvents.Add((downUs, vk, true));
            expectedEvents.Add((upUs, vk, false));
        }

        foreach (var ev in expectedEvents.OrderBy(e => e.TimeUs).ThenBy(e => e.Down ? 1 : 0))
        {
            var sc = scanCodes[Array.IndexOf(rhythmKeys, ev.Vk)];
            captured.Add(new CapturedInputEvent(ev.Down
                ? InputEvent.KeyDown(ev.TimeUs, ev.Vk, sc)
                : InputEvent.KeyUp(ev.TimeUs, ev.Vk, sc)));
        }

        var converted = SmartRecordingConverter.Convert(captured);
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = new Macro { Name = "Rhythm 200", Steps = converted.Steps };

        var result = await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(400, input.Calls.Count);
        var totalTimelineErrorMs = Math.Abs(clock.Elapsed.TotalMilliseconds - 10_000.0);
        Assert.True(totalTimelineErrorMs < 5.0, $"Expected < 5 ms total timeline error, got {totalTimelineErrorMs:F3} ms");
    }

    [Fact]
    public async Task ReleaseAllHeldInputs_is_called_at_the_end_of_each_repeat_iteration()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = new Macro
        {
            Name = "Infinite Loop Sim",
            RepeatCount = 3,
            Steps =
            {
                new KeyPressStep { VirtualKey = W, Action = KeyAction.Down },
                new DelayStep { DurationMs = 50 },
            },
        };

        var result = await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.IterationsCompleted);
        // ReleaseAllHeldInputs is called at the end of each of the 3 iterations plus the final finally block.
        Assert.True(input.ReleaseAllCount >= 3, $"Expected ReleaseAllHeldInputs >= 3 times, got {input.ReleaseAllCount}");
        // And the held W key is released at the end of every iteration before the next iteration starts.
        Assert.Equal(
            new[] { "key 87 down", "key 87 up", "key 87 down", "key 87 up", "key 87 down", "key 87 up" },
            input.Calls);
    }

    [Fact]
    public async Task Fast_camera_flick_in_InputSequenceStep_is_subdivided_losslessly()
    {
        var step = new InputSequenceStep
        {
            Events = new List<InputEvent> { InputEvent.Move(0, 120, -45) },
        };
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(Flow.Macro(step));

        Assert.True(input.Calls.Count >= 5, $"Expected fast flick to be subdivided into >= 5 micro-steps, got {input.Calls.Count}");
        int sumDx = 0, sumDy = 0;
        foreach (var call in input.Calls)
        {
            var parts = call.Substring("rel ".Length).Split(',');
            var dx = int.Parse(parts[0]);
            var dy = int.Parse(parts[1]);
            Assert.True(Math.Abs(dx) <= 30 && Math.Abs(dy) <= 30, $"Micro-step ({dx}, {dy}) exceeded 30px");
            sumDx += dx;
            sumDy += dy;
        }
        Assert.Equal((120, -45), (sumDx, sumDy));
    }
}

