using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

// Tests for the Vision / OCR bug-fix pass: Find text whole word + exact case (Fix 5), Find image foreground masking +
// margin trimming (Fix 3), Find colour biggest object + reliable game click + presets (Fix 4).

// ---------------- Fix 5: Find text (OCR) – whole word + exact case ----------------

public class FindTextWholeWordAndCaseTests
{
    /// <summary>One OCR line; every word box is 10 px per character, 10 px apart, at y = 200.</summary>
    private static OcrReadResult Line(string text)
    {
        var x = 100;
        var words = text.Split(' ').Select(w =>
        {
            var box = new OcrWordBox(w, new ScreenRect(x, 200, w.Length * 10, 20));
            x += w.Length * 10 + 10;
            return box;
        }).ToList();
        return new OcrReadResult(new ScreenRect(0, 0, 1920, 1080), new[] { new OcrLineBox(words) });
    }

    private static FindTextStep Search(string text) => new() { Text = text, TimeoutMs = 0 };

    [Fact]
    public void New_find_text_steps_match_whole_words_with_exact_case()
    {
        var step = new FindTextStep();
        Assert.True(step.MatchWholeWord);
        Assert.True(step.MatchCase);
        Assert.Equal(TextMatchMode.WholeWords, step.MatchMode);
        Assert.True(step.CaseSensitive);
    }

    [Fact]
    public void ARt_is_found_in_click_aRt_now_and_its_own_word_box_is_returned()
    {
        var match = ScreenProbes.ProbeText(Line("Click aRt now"), Search("aRt"));

        var found = Assert.IsType<TextMatch>(match);
        Assert.Equal("aRt", found.Text);
        Assert.Equal(1.0, found.Similarity);
        // "Click" = 100..150, "aRt" = 160..190: the word itself, not the whole line.
        Assert.Equal(new ScreenRect(160, 200, 30, 20), found.Bounds);
    }

    [Theory]
    [InlineData("Click apart now")]
    [InlineData("the apartment is big")]
    [InlineData("Apart")]
    public void Art_never_matches_inside_a_longer_word(string screen)
    {
        Assert.Null(ScreenProbes.ProbeText(Line(screen), Search("art")));
    }

    [Theory]
    [InlineData("Click art now")]
    [InlineData("Click Art now")]
    [InlineData("Click ART now")]
    public void ARt_with_exact_case_rejects_other_cases(string screen)
    {
        Assert.Null(ScreenProbes.ProbeText(Line(screen), Search("aRt")));
    }

    [Fact]
    public void Exact_case_is_never_bypassed_by_the_similarity_tolerance()
    {
        // "Settings" vs "settings" is 7 / 8 = 87.5 % similar – above the 85 % default – but only the case differs.
        var step = Search("settings");
        Assert.Null(ScreenProbes.ProbeText(Line("Open Settings"), step));
        Assert.NotNull(ScreenProbes.ProbeText(Line("Open settings"), step));
    }

    [Fact]
    public void Punctuation_around_a_screen_word_is_ignored_for_whole_words()
    {
        var match = Assert.IsType<TextMatch>(ScreenProbes.ProbeText(Line("Draw art, then (OK)"), Search("art")));
        Assert.Equal(new ScreenRect(150, 200, 40, 20), match.Bounds); // the box of "art," ("Draw" is 100..140)

        Assert.NotNull(ScreenProbes.ProbeText(Line("Draw art, then (OK)"), Search("OK")));
    }

    [Fact]
    public void Turning_both_options_off_gives_the_old_contains_and_ignore_case_matching()
    {
        var step = Search("art");
        step.MatchWholeWord = false;
        step.MatchCase = false;

        Assert.Equal(TextMatchMode.Contains, step.MatchMode);
        Assert.False(step.CaseSensitive);
        Assert.NotNull(ScreenProbes.ProbeText(Line("the APARTMENT is big"), step));

        var anyCase = Search("aRt");
        anyCase.MatchCase = false;
        Assert.NotNull(ScreenProbes.ProbeText(Line("Click Art now"), anyCase));
    }

    [Fact]
    public void Old_files_keep_their_stored_contains_and_ignore_case_settings()
    {
        var json = MacroJson.Serialize<MacroStep>(new FindTextStep { Text = "art", MatchMode = TextMatchMode.Contains, CaseSensitive = false });
        Assert.Contains("\"matchMode\": \"Contains\"", json);
        Assert.Contains("\"caseSensitive\": false", json);
        Assert.DoesNotContain("matchWholeWord", json, StringComparison.OrdinalIgnoreCase); // a view of MatchMode, not saved twice

        var back = Assert.IsType<FindTextStep>(MacroJson.Deserialize<MacroStep>(json));
        Assert.False(back.MatchWholeWord);
        Assert.False(back.MatchCase);
    }

    [Fact]
    public void If_and_repeat_text_conditions_expose_the_same_options()
    {
        var ifStep = new IfStep { Condition = Flow.Text("aRt") };
        Assert.True(ifStep.MatchWholeWord);
        Assert.True(ifStep.MatchCase);
        ifStep.MatchCase = false;
        ifStep.MatchWholeWord = false;
        var check = Assert.IsType<FindTextStep>(ifStep.Condition.Check);
        Assert.False(check.CaseSensitive);
        Assert.Equal(TextMatchMode.Contains, check.MatchMode);

        var repeat = Flow.While(Flow.Text("Go"));
        repeat.MatchCase = false;
        Assert.False(Assert.IsType<FindTextStep>(repeat.Condition!.Check).MatchCase);
        Assert.True(new RepeatStep().MatchWholeWord); // Times loop: no text condition
    }

    [Fact]
    public async Task A_text_condition_with_whole_word_rejects_a_longer_word()
    {
        var probes = new ScreenProbes(new ScriptedOcr("apartment", _ => true), null);
        var evaluator = new ScreenConditionEvaluator(probes);

        Assert.False((await evaluator.CheckAsync(Flow.Text("art"), null, CancellationToken.None)).IsTrue);
    }

    [Fact]
    public async Task Clicking_found_text_clicks_the_exact_word()
    {
        var ocr = new FakeOcr().Then(("Click", 100, 200), ("aRt", 160, 200), ("now", 200, 200));
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(Flow.Macro(
            new FindTextStep { Text = "aRt", Action = FoundTextAction.Click, TimeoutMs = 0 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // The "aRt" box is (160, 200) 30×20, so its centre is (175, 210) – not the centre of the whole line.
        Assert.Equal(new[] { "move 175,210", "mouse Left down", "mouse Left up" }, input.Calls);
    }
}

// ---------------- Fix 3: Find image – foreground masking + margin trimming ----------------

public class FindImageBackgroundMaskTests
{
    private static readonly RgbColor Red = new(220, 30, 30);
    private static readonly RgbColor Box = new(235, 235, 235);
    private static readonly RgbColor Wall = new(20, 20, 20);

    /// <summary>Draws a filled circle (every pixel whose centre is within <paramref name="r"/> of (cx, cy)).</summary>
    internal static void Disc(BgraImage image, double cx, double cy, double r, RgbColor color)
    {
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                image.SetPixel(x, y, color);
    }

    /// <summary>The user's capture: a red circle (diameter 40) on a white 48×48 square.</summary>
    internal static BgraImage RedCircleTemplate()
    {
        var template = TestImages.Solid(48, 48, RgbColor.White);
        Disc(template, 23.5, 23.5, 20, Red);
        return template;
    }

    /// <summary>A light game box with the red circle placed where the template's top-left corner would be (tx, ty).</summary>
    private static BgraImage Scene(int tx, int ty, Action<BgraImage>? wallsBefore = null, Action<BgraImage>? wallsAfter = null)
    {
        var screen = TestImages.Solid(320, 240, Box);
        wallsBefore?.Invoke(screen);
        Disc(screen, tx + 23.5, ty + 23.5, 20, Red);
        wallsAfter?.Invoke(screen);
        return screen;
    }

    private static void AssertFoundAt(TemplateMatchResult result, int tx, int ty)
    {
        var best = Assert.IsType<VisualMatch>(result.Best);
        var all = string.Join("; ", result.Matches.Select(m => $"{m.Bounds} {m.Score:0.0000}"));
        Assert.True(best.Score >= 0.8, $"score {best.Score:P1} ({all})");
        Assert.True(Math.Abs(best.Bounds.X - tx) <= 1 && Math.Abs(best.Bounds.Y - ty) <= 1, $"expected ({tx}, {ty}), found {all}");
    }

    [Fact]
    public void A_red_circle_on_white_has_a_maskable_white_background()
    {
        var background = Assert.IsType<TemplateBackground>(TemplateBackground.Detect(RedCircleTemplate()));

        Assert.False(background.FromAlpha);
        Assert.Equal(RgbColor.White, background.Color);
        Assert.Equal(1.0, background.PerimeterShare, 3);
        Assert.InRange(background.ForegroundShare, 0.5, 0.6); // π·20² / 48² ≈ 55 %
        Assert.True(background.CanMask);
        Assert.True(background.IsForeground(24, 24));
        Assert.False(background.IsForeground(0, 0));
    }

    [Fact]
    public void Busy_pictures_have_no_background_to_mask()
    {
        Assert.Null(TemplateBackground.Detect(TestImages.Icon(32, 32, seed: 77)));
        Assert.Null(TemplateBackground.Detect(TestImages.Solid(20, 20, RgbColor.White))); // all background
    }

    [Fact]
    public void Still_found_next_to_a_black_wall_that_fills_the_white_corners()
    {
        // The wall's edge is 2 px left of the circle: it fills the template's left margin and both left corners.
        const int tx = 60, ty = 90;
        var screen = Scene(tx, ty, wallsBefore: s => s.FillRectangle(new ScreenRect(0, 0, tx + 2, 240), Wall));

        AssertFoundAt(TemplateMatcher.Find(screen, RedCircleTemplate(), new TemplateMatchOptions { MinScore = 0.8 }), tx, ty);
    }

    [Fact]
    public void Still_found_when_the_wall_covers_3_px_of_the_circle()
    {
        // The circle's leftmost pixels are at tx + 4; the wall (drawn on top) covers tx + 4 … tx + 6.
        const int tx = 60, ty = 90;
        var screen = Scene(tx, ty, wallsAfter: s => s.FillRectangle(new ScreenRect(0, 0, tx + 7, 240), Wall));

        AssertFoundAt(TemplateMatcher.Find(screen, RedCircleTemplate(), new TemplateMatchOptions { MinScore = 0.8 }), tx, ty);
    }

    [Fact]
    public void Still_found_in_the_corner_of_the_game_box()
    {
        // Walls on the left and at the top, both right against the circle (and covering 2 px of it).
        const int tx = 40, ty = 30;
        var screen = Scene(tx, ty, wallsAfter: s =>
        {
            s.FillRectangle(new ScreenRect(0, 0, tx + 6, 240), Wall);
            s.FillRectangle(new ScreenRect(0, 0, 320, ty + 6), Wall);
        });

        AssertFoundAt(TemplateMatcher.Find(screen, RedCircleTemplate(), new TemplateMatchOptions { MinScore = 0.8 }), tx, ty);
    }

    [Fact]
    public void The_middle_of_a_big_red_area_is_not_the_red_circle()
    {
        var screen = TestImages.Solid(320, 240, Red);

        var result = TemplateMatcher.Find(screen, RedCircleTemplate(), new TemplateMatchOptions { MinScore = 0.9 });

        Assert.Null(result.Best);
    }

    [Fact]
    public void A_blue_circle_is_not_the_red_circle()
    {
        var screen = TestImages.Solid(320, 240, Box);
        Disc(screen, 150.5, 100.5, 20, new RgbColor(30, 60, 220));

        Assert.Null(TemplateMatcher.Find(screen, RedCircleTemplate(), new TemplateMatchOptions { MinScore = 0.8 }).Best);
    }

    [Fact]
    public void Extra_blank_margin_is_trimmed_to_one_pixel_so_the_match_is_centred_on_the_target()
    {
        // A sloppy capture: the circle (radius 15, centre (40, 20)) sits in the top-right part of a 64×56 white picture.
        var template = TestImages.Solid(64, 56, RgbColor.White);
        Disc(template, 40, 20, 15, Red);

        var trimmed = TemplateBackground.TrimMargins(template, out var kept);

        Assert.Equal(new ScreenRect(24, 4, 33, 33), kept); // circle box 25..55 × 5..35, plus 1 px
        Assert.Equal((33, 33), (trimmed.Width, trimmed.Height));
        Assert.Same(trimmed, TemplateBackground.TrimMargins(template, out _)); // same picture every time (cached)
        Assert.True(TemplateBackground.Detect(trimmed)!.CanMask);              // still maskable although the margin is thin
    }

    [Fact]
    public async Task Find_image_clicks_the_centre_of_the_target_not_of_the_sloppy_capture()
    {
        var template = TestImages.Solid(64, 56, RgbColor.White);
        Disc(template, 40, 20, 15, Red);
        var screen = TestImages.Solid(400, 300, RgbColor.White);
        TestImages.Paste(screen, template, 100, 80); // the circle's centre is at (140, 100) on screen
        var step = new FindImageStep
        {
            TemplateWidth = 64, TemplateHeight = 56, TemplateData = TemplateCodec.Encode(template),
            MinSimilarityPercent = 90, TimeoutMs = 0,
        };

        var probe = await new ScreenProbes(null, new FakeCapture(screen), new FakeClock()).ProbeImageAsync(step);

        Assert.True(probe.Found, probe.MissText);
        Assert.Equal((140, 100), (probe.X, probe.Y));
    }

    [Fact]
    public void Pictures_that_are_one_object_with_no_extra_margin_are_left_alone()
    {
        var icon = TestImages.Icon(40, 30, seed: 21);
        Assert.Same(icon, TemplateBackground.TrimMargins(icon, out var kept));
        Assert.Equal(new ScreenRect(0, 0, 40, 30), kept);
    }

    [Fact]
    public void Masking_can_be_turned_off()
    {
        const int tx = 60, ty = 90;
        var screen = Scene(tx, ty, wallsAfter: s => s.FillRectangle(new ScreenRect(0, 0, tx + 7, 240), Wall));
        var options = new TemplateMatchOptions { MinScore = 0.8 };

        var masked = TemplateMatcher.Find(screen, RedCircleTemplate(), options);
        var plain = TemplateMatcher.Find(screen, RedCircleTemplate(), options with { AutoMaskBackground = false });

        Assert.NotNull(masked.Best);
        Assert.True(plain.Best is null || plain.Best.Score <= masked.Best!.Score);
    }
}

// ---------------- Fix 4: Find colour – biggest object, reliable game click, presets ----------------

public class FindColorObjectTests
{
    private static readonly RgbColor Red = new(220, 30, 30);

    /// <summary>
    /// A shaded red ball (radius 20, centre (150, 100)) whose pixels alternate in a checkerboard between red and a pale
    /// highlight that is outside the tolerance – so the red pixels only touch diagonally (one-pixel "patches" for a
    /// touching-pixels search) – plus a solid 4×4 red patch far away at (20, 20).
    /// </summary>
    private static BgraImage SpeckledBallScene()
    {
        var screen = TestImages.Solid(320, 240, RgbColor.Black);
        for (var y = 0; y < screen.Height; y++)
        for (var x = 0; x < screen.Width; x++)
        {
            if ((x - 150) * (x - 150) + (y - 100) * (y - 100) > 20 * 20) continue;
            screen.SetPixel(x, y, (x + y) % 2 == 0 ? Red : new RgbColor(255, 200, 200));
        }

        screen.FillRectangle(new ScreenRect(20, 20, 4, 4), Red);
        return screen;
    }

    [Fact]
    public void The_old_touching_pixels_search_would_pick_the_stray_patch()
    {
        var old = ColorFinder.Find(SpeckledBallScene(), Red, 28);
        Assert.Equal(new ScreenRect(20, 20, 4, 4), old.Largest!.Bounds); // why the click used to miss the ball
    }

    [Fact]
    public void Find_color_returns_the_centroid_of_the_biggest_cluster_not_a_stray_patch()
    {
        var result = ColorFinder.FindObjects(SpeckledBallScene(), Red, null, tolerance: 28, minPixels: 10);

        var ball = Assert.IsType<ColorBlob>(result.Largest);
        Assert.InRange(ball.CenterX, 149, 151);
        Assert.InRange(ball.CenterY, 99, 101);
        Assert.Equal(new ScreenRect(130, 80, 41, 41), ball.Bounds);
        Assert.True(ball.PixelCount > 500, $"{ball.PixelCount} px");
        Assert.Equal(2, result.Blobs.Count); // the ball and the 16 px patch
    }

    [Fact]
    public async Task Find_color_step_clicks_dead_centre_on_the_ball()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var step = new FindColorStep
        {
            Color = "#DC1E1E", Tolerance = 28, MinPixels = 10, TimeoutMs = 0, SearchFullScreen = true,
            Action = FoundTextAction.Click, ClickHoldMs = 0, PostActionDelayMs = 0,
        };

        var result = await new PlaybackEngine(input, clock, capture: new FakeCapture(SpeckledBallScene())).RunAsync(Flow.Macro(step));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 150,100", "mouse Left down", "mouse Left up" }, input.Calls);
        // Reliable game click: rest on the target 15 ms, then hold at least 25 ms (even with a 0 ms click hold).
        Assert.Equal(PlaybackEngine.TargetHoverMs, (input.Times[1] - input.Times[0]).TotalMilliseconds, precision: 3);
        Assert.Equal(PlaybackEngine.MinTargetClickHoldMs, (input.Times[2] - input.Times[1]).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Clusters_join_nearby_speckles_but_keep_far_objects_apart()
    {
        var screen = TestImages.Solid(200, 100, RgbColor.Black);
        // Two groups of 2×2 dots, 4 px apart inside each group; the groups are 60 px apart.
        foreach (var left in new[] { 20, 120 })
            for (var dy = 0; dy < 20; dy += 6)
            for (var dx = 0; dx < 20; dx += 6)
                screen.FillRectangle(new ScreenRect(left + dx, 40 + dy, 2, 2), Red);

        var result = ColorFinder.FindObjects(screen, Red, null, tolerance: 0);

        Assert.Equal(2, result.Blobs.Count);
        Assert.All(result.Blobs, b => Assert.Equal(64, b.PixelCount));
    }

    [Fact]
    public void Shade_variation_also_matches_the_dark_side_of_a_ball()
    {
        // Top half bright red, bottom half the same red at half brightness (the shadowed side).
        var screen = TestImages.Solid(320, 240, RgbColor.Black);
        for (var y = 80; y <= 120; y++)
        for (var x = 130; x <= 170; x++)
            if ((x - 150) * (x - 150) + (y - 100) * (y - 100) <= 400)
                screen.SetPixel(x, y, y < 100 ? Red : new RgbColor(110, 15, 15));

        var plain = ColorFinder.FindObjects(screen, Red, null, tolerance: 28).Largest!;
        var shades = ColorFinder.FindObjects(screen, Red, null, tolerance: 28, matchShades: true).Largest!;

        Assert.True(plain.CenterY < 93, $"plain centre {plain.CenterY}");   // only the bright half
        Assert.InRange(shades.CenterY, 99, 101);                             // the whole ball
        Assert.False(ColorFinder.IsShadeOf(255, 255, 255, 30, 30, 220, 28)); // white is not a shade of red
        Assert.False(ColorFinder.IsShadeOf(220, 60, 30, 30, 30, 220, 28));   // blue (BGR) is not a shade of red
    }

    [Fact]
    public void The_three_colour_presets_have_the_requested_values()
    {
        Assert.Equal((10, false), (FindColorStep.ExactUiPreset.Tolerance, FindColorStep.ExactUiPreset.MatchShades));
        Assert.Equal((28, true), (FindColorStep.GameTargetPreset.Tolerance, FindColorStep.GameTargetPreset.MatchShades));
        Assert.Equal((50, true), (FindColorStep.GlowingObjectPreset.Tolerance, FindColorStep.GlowingObjectPreset.MatchShades));
    }

    [Fact]
    public void Shade_variation_is_saved_only_when_on()
    {
        Assert.DoesNotContain("matchShades", MacroJson.Serialize<MacroStep>(new FindColorStep()));
        var json = MacroJson.Serialize<MacroStep>(new FindColorStep { MatchShades = true });
        Assert.True(Assert.IsType<FindColorStep>(MacroJson.Deserialize<MacroStep>(json)).MatchShades);
    }
}
