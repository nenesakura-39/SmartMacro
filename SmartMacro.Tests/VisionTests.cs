using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Fake screen: each capture uses the next queued screen picture (the last one repeats) and crops the requested
/// area out of it. The screen's top-left corner can be moved (e.g. -1920 for a monitor left of the main one).
/// These tests prove the detection logic and coordinate maths, not that a real game shows up in GDI captures.
/// </summary>
internal sealed class FakeCapture : IScreenCapture
{
    private readonly BgraImage[] _screens;

    public FakeCapture(params BgraImage[] screens) : this(0, 0, screens)
    {
    }

    public FakeCapture(int left, int top, params BgraImage[] screens)
    {
        if (screens.Length == 0) throw new ArgumentException("Give at least one screen.", nameof(screens));
        _screens = screens;
        Left = left;
        Top = top;
    }

    public int Left { get; }
    public int Top { get; }
    public int Captures { get; private set; }
    public List<ScreenRect> Regions { get; } = new();

    public ScreenRect GetVirtualScreen() => new(Left, Top, _screens[0].Width, _screens[0].Height);

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = _screens[Math.Min(Captures, _screens.Length - 1)];
        Captures++;
        Regions.Add(region);

        var area = region.Intersect(new ScreenRect(Left, Top, image.Width, image.Height));
        if (area.IsEmpty) throw new ArgumentException("The search area is outside the screen.");
        var local = new ScreenRect(area.X - Left, area.Y - Top, area.Width, area.Height);
        return Task.FromResult(new ScreenFrame(area, image.Crop(local)));
    }
}

/// <summary>Builds deterministic test pictures.</summary>
internal static class TestImages
{
    /// <summary>A busy "UI-like" background: lots of random coloured rectangles.</summary>
    public static BgraImage Blocky(int width, int height, int seed)
    {
        var image = BgraImage.Create(width, height);
        image.FillRectangle(new ScreenRect(0, 0, width, height), new RgbColor(40, 40, 40));
        var random = new Random(seed);
        var count = width * height / 300;
        for (var i = 0; i < count; i++)
        {
            var rect = new ScreenRect(random.Next(width), random.Next(height), random.Next(4, 30), random.Next(4, 30));
            image.FillRectangle(rect, new RgbColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }

        return image;
    }

    /// <summary>A small "icon": random cells of two colours (not periodic, so it has one true position).</summary>
    public static BgraImage Icon(int width, int height, int seed, RgbColor dark, RgbColor light, int cell = 4)
    {
        var image = BgraImage.Create(width, height);
        var random = new Random(seed);
        for (var y = 0; y < height; y += cell)
            for (var x = 0; x < width; x += cell)
                image.FillRectangle(new ScreenRect(x, y, cell, cell), random.Next(2) == 0 ? dark : light);
        return image;
    }

    public static BgraImage Icon(int width, int height, int seed = 7) =>
        Icon(width, height, seed, new RgbColor(30, 60, 200), new RgbColor(250, 230, 90));

    public static void Paste(BgraImage target, BgraImage source, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
            for (var col = 0; col < source.Width; col++)
                target.SetPixel(x + col, y + row, source.GetPixel(col, row));
    }

    public static BgraImage Solid(int width, int height, RgbColor color)
    {
        var image = BgraImage.Create(width, height);
        image.FillRectangle(new ScreenRect(0, 0, width, height), color);
        return image;
    }

    public static BgraImage Brighter(BgraImage source, int amount)
    {
        var copy = source.Clone();
        for (var y = 0; y < copy.Height; y++)
        {
            for (var x = 0; x < copy.Width; x++)
            {
                var c = copy.GetPixel(x, y);
                copy.SetPixel(x, y, new RgbColor(Clamp(c.R + amount), Clamp(c.G + amount), Clamp(c.B + amount)));
            }
        }

        return copy;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}

public class TemplateCodecTests
{
    [Fact]
    public void Round_trip_keeps_every_pixel()
    {
        var image = TestImages.Icon(21, 13);
        var data = TemplateCodec.Encode(image);

        Assert.True(TemplateCodec.TryDecode(data, 21, 13, out var decoded, out var error), error);
        for (var y = 0; y < 13; y++)
            for (var x = 0; x < 21; x++)
                Assert.Equal(image.GetPixel(x, y), decoded!.GetPixel(x, y));
    }

    [Fact]
    public void Wrong_size_is_rejected()
    {
        var data = TemplateCodec.Encode(TestImages.Icon(20, 10));
        Assert.False(TemplateCodec.TryDecode(data, 20, 11, out _, out var tooSmall));
        Assert.NotNull(tooSmall);
        Assert.False(TemplateCodec.TryDecode(data, 20, 9, out _, out var tooBig));
        Assert.NotNull(tooBig);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!!")]
    [InlineData("AAAAAAAAAAAA")]
    public void Damaged_data_is_rejected(string data)
    {
        Assert.False(TemplateCodec.TryDecode(data, 10, 10, out var image, out var error));
        Assert.Null(image);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Truncated_data_is_rejected()
    {
        var data = TemplateCodec.Encode(TestImages.Icon(30, 30));
        var bytes = Convert.FromBase64String(data);
        var cut = Convert.ToBase64String(bytes[..(bytes.Length / 2)]);
        Assert.False(TemplateCodec.TryDecode(cut, 30, 30, out _, out _));
    }

    [Fact]
    public void Colors_parse_and_print_as_hex()
    {
        Assert.True(RgbColor.TryParse("#1a2B3c", out var color));
        Assert.Equal(new RgbColor(0x1A, 0x2B, 0x3C), color);
        Assert.Equal("#1A2B3C", color.ToString());
        Assert.True(RgbColor.TryParse("FF0000", out _));
        Assert.False(RgbColor.TryParse("#FF00", out _));
        Assert.False(RgbColor.TryParse("#GG0000", out _));
        Assert.False(RgbColor.TryParse(null, out _));
    }
}

public class TemplateMatcherTests
{
    [Fact]
    public void Finds_the_exact_position_on_a_small_area()
    {
        var screen = TestImages.Blocky(200, 150, seed: 1);
        var icon = TestImages.Icon(20, 16);
        TestImages.Paste(screen, icon, 73, 41);

        var result = TemplateMatcher.Find(screen, icon);

        Assert.Equal(1, result.Scale);
        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(73, 41, 20, 16), best.Bounds);
        Assert.True(best.Score > 0.99, $"score {best.Score}");
    }

    [Fact]
    public void Finds_the_exact_position_on_a_big_screen_using_the_coarse_pass()
    {
        var screen = TestImages.Blocky(1600, 900, seed: 2);
        var icon = TestImages.Icon(64, 48, seed: 3);
        TestImages.Paste(screen, icon, 701, 413); // not a multiple of the shrink factor on purpose

        var result = TemplateMatcher.Find(screen, icon);

        Assert.True(result.Scale > 1, "the big search should use the coarse pass");
        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(701, 413, 64, 48), best.Bounds);
        Assert.True(best.Score > 0.99, $"score {best.Score}");
    }

    [Fact]
    public void Finds_every_copy_best_first_without_duplicates()
    {
        var screen = TestImages.Blocky(600, 400, seed: 4);
        var icon = TestImages.Icon(24, 24, seed: 5);
        TestImages.Paste(screen, icon, 40, 40);
        TestImages.Paste(screen, icon, 300, 120);
        TestImages.Paste(screen, icon, 500, 330);

        var result = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { MaxResults = 10 });

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(
            new[] { (40, 40), (300, 120), (500, 330) },
            result.Matches.Select(m => (m.Bounds.X, m.Bounds.Y)).OrderBy(p => p.X).ToArray());
        Assert.True(result.Matches.Zip(result.Matches.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));

        var limited = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { MaxResults = 2 });
        Assert.Equal(2, limited.Matches.Count);
    }

    [Fact]
    public void Reports_no_match_and_the_best_score_seen()
    {
        var screen = TestImages.Blocky(300, 200, seed: 6);
        var icon = TestImages.Icon(30, 30, seed: 8);

        var result = TemplateMatcher.Find(screen, icon);

        Assert.Empty(result.Matches);
        Assert.Null(result.Best);
        Assert.InRange(result.BestScore, 0, 0.9);
    }

    [Fact]
    public void Template_bigger_than_the_area_finds_nothing()
    {
        var result = TemplateMatcher.Find(TestImages.Blocky(20, 20, 1), TestImages.Icon(30, 10));
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Same_shape_in_other_colors_is_rejected_unless_colors_are_ignored()
    {
        // Same light/dark pattern (so the grey-level score is perfect) but clearly greener.
        var icon = TestImages.Icon(32, 24, 9, new RgbColor(60, 60, 60), new RgbColor(200, 200, 200));
        var green = TestImages.Icon(32, 24, 9, new RgbColor(0, 120, 0), new RgbColor(100, 255, 100));
        var screen = TestImages.Blocky(300, 200, seed: 10);
        TestImages.Paste(screen, green, 150, 90);

        var strict = TemplateMatcher.Find(screen, icon);
        Assert.Empty(strict.Matches);
        Assert.True(strict.RejectedForColor >= 1);

        var shapeOnly = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { IgnoreColor = true });
        Assert.Equal(new ScreenRect(150, 90, 32, 24), shapeOnly.Best?.Bounds);
    }

    [Fact]
    public void Slightly_brighter_screen_still_matches()
    {
        var screen = TestImages.Blocky(300, 200, seed: 11);
        var icon = TestImages.Icon(30, 20, seed: 12);
        TestImages.Paste(screen, icon, 120, 60);

        var result = TemplateMatcher.Find(TestImages.Brighter(screen, 20), icon);

        Assert.Equal(new ScreenRect(120, 60, 30, 20), result.Best?.Bounds);
        Assert.True(result.Best!.Score > 0.95);
    }

    [Fact]
    public void Flat_pictures_are_detected()
    {
        Assert.True(TemplateMatcher.IsFlat(TestImages.Solid(20, 20, new RgbColor(10, 200, 30))));
        Assert.False(TemplateMatcher.IsFlat(TestImages.Icon(20, 20)));
        Assert.Empty(TemplateMatcher.Find(TestImages.Blocky(100, 100, 1), TestImages.Solid(10, 10, RgbColor.White)).Matches);
    }

    [Fact]
    public void Cancellation_is_respected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            TemplateMatcher.Find(TestImages.Blocky(400, 300, 1), TestImages.Icon(20, 20), cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(500, 400)]
    [InlineData(640, 480)]
    public void Large_templates_over_400x400_match_accurately_and_fast(int templateWidth, int templateHeight)
    {
        var screen = TestImages.Blocky(1920, 1080, seed: 21);
        var template = TestImages.Icon(templateWidth, templateHeight, seed: 22);
        TestImages.Paste(screen, template, 613, 277);

        // Warm up JIT
        _ = TemplateMatcher.Find(screen, template);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = TemplateMatcher.Find(screen, template);
        sw.Stop();

        Assert.True(result.Scale >= 4, $"expected multi-scale coarse pass, got scale {result.Scale}");
        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(613, 277, templateWidth, templateHeight), best.Bounds);
        Assert.True(best.Score > 0.98, $"score {best.Score}");
        // Updated on purpose (live-test fix of 538326c): the old < 20 ms goal was only reachable by sampling every 4th
        // pixel, which lost 1-pixel lines (a 301×288 Tic-Tac-Toe grid scored 0 %). Every pixel now counts (FFT coarse
        // pass + 1/2 and full-size checks); this bound only catches a pathologically slow search in a Debug test run.
        Assert.True(sw.Elapsed.TotalMilliseconds < LineArtTemplateTests.LargeSearchBudgetMs,
            $"expected < {LineArtTemplateTests.LargeSearchBudgetMs} ms, took {sw.Elapsed.TotalMilliseconds:F2}ms");
    }

    [Fact]
    public void Large_foreground_masked_template_matches_accurately_and_fast()
    {
        // Semi-transparent UI element: bright white strokes captured over a dark background, searched over a blue-grey background.
        var screen = TestImages.Blocky(1920, 1080, seed: 31);
        screen.FillRectangle(new ScreenRect(500, 300, 520, 420), new RgbColor(70, 90, 120));
        var template = TestImages.Solid(520, 420, new RgbColor(20, 20, 20));
        var fgColor = new RgbColor(245, 245, 245);
        var badge = TestImages.Icon(260, 210, seed: 32, new RgbColor(20, 20, 20), fgColor, cell: 16);
        for (var y = 0; y < badge.Height; y++)
        {
            for (var x = 0; x < badge.Width; x++)
            {
                if (badge.GetPixel(x, y) != fgColor) continue;
                template.SetPixel(130 + x, 105 + y, fgColor);
                screen.SetPixel(500 + 130 + x, 300 + 105 + y, fgColor);
            }
        }

        var options = new TemplateMatchOptions { MatchForeground = true };

        // Warm up JIT
        _ = TemplateMatcher.Find(screen, template, options);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = TemplateMatcher.Find(screen, template, options);
        sw.Stop();

        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(500, 300, 520, 420), best.Bounds);
        Assert.True(best.Score >= 0.9, $"score {best.Score}");
        // Updated on purpose (live-test fix of 538326c): the old < 20 ms goal was only reachable by sampling every 4th
        // pixel, which lost 1-pixel lines (a 301×288 Tic-Tac-Toe grid scored 0 %). Every pixel now counts (FFT coarse
        // pass + 1/2 and full-size checks); this bound only catches a pathologically slow search in a Debug test run.
        Assert.True(sw.Elapsed.TotalMilliseconds < LineArtTemplateTests.LargeSearchBudgetMs,
            $"expected < {LineArtTemplateTests.LargeSearchBudgetMs} ms, took {sw.Elapsed.TotalMilliseconds:F2}ms");
    }
}

/// <summary>
/// The friend's live-test bug on 538326c: an If step looking for a 301×288 capture of a Tic-Tac-Toe grid (thin white
/// "#" lines on black) said "Not found in Main Monitor. The best spot was only 0% similar (needs 90%)" while the exact
/// grid was on screen. Cause: templates over 300 px used a stride-sampled path (every 4th pixel, a 32×32 "anchor"
/// patch, 2-point samples per 4×4 block) that skipped the 1-pixel lines. These tests use the same picture.
/// </summary>
public class LineArtTemplateTests
{
    /// <summary>Generous time limit for one large-template search in a Debug test run (it is much faster in Release).</summary>
    internal const double LargeSearchBudgetMs = 1500;

    /// <summary>A 301×288 black picture with a 1-pixel white "#" (two vertical and two horizontal lines).</summary>
    internal static BgraImage TicTacToeGrid(int width = 301, int height = 288)
    {
        var grid = TestImages.Solid(width, height, RgbColor.Black);
        foreach (var x in new[] { width / 3, 2 * width / 3 }) grid.FillRectangle(new ScreenRect(x, 0, 1, height), RgbColor.White);
        foreach (var y in new[] { height / 3, 2 * height / 3 }) grid.FillRectangle(new ScreenRect(0, y, width, 1), RgbColor.White);
        return grid;
    }

    /// <summary>
    /// A 1920×1080 black screen with the grid at (x, y). Alpha stays 0 everywhere, like a GDI screen capture, while
    /// a decoded template has alpha 255: alpha must never count.
    /// </summary>
    internal static BgraImage BlackScreenWith(BgraImage picture, int x, int y, int width = 1920, int height = 1080)
    {
        var screen = BgraImage.Create(width, height);
        for (var row = 0; row < picture.Height; row++)
        {
            for (var col = 0; col < picture.Width; col++)
            {
                var c = picture.GetPixel(col, row);
                var i = ((y + row) * width + x + col) * 4;
                screen.Pixels[i] = c.B;
                screen.Pixels[i + 1] = c.G;
                screen.Pixels[i + 2] = c.R;
                screen.Pixels[i + 3] = 0;
            }
        }

        return screen;
    }

    /// <summary>The grid as it comes out of a macro file (TemplateCodec), i.e. exactly what an If step searches for.</summary>
    internal static BgraImage StoredGrid()
    {
        var grid = TicTacToeGrid();
        Assert.True(TemplateCodec.TryDecode(TemplateCodec.Encode(grid), grid.Width, grid.Height, out var decoded, out var error), error);
        return decoded!;
    }

    [Theory]
    [InlineData(811, 397, true)]
    [InlineData(811, 397, false)]
    [InlineData(812, 398, true)]
    [InlineData(813, 399, false)]
    [InlineData(814, 400, true)]
    [InlineData(0, 0, false)]
    [InlineData(1619, 792, true)] // touching the bottom-right corner of the screen
    public void Tic_tac_toe_grid_is_found_on_a_black_1080p_screen(int x, int y, bool adaptToLighting)
    {
        var template = StoredGrid();
        var screen = BlackScreenWith(template, x, y);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = TemplateMatcher.Find(screen, template, new TemplateMatchOptions { MinScore = 0.9, AdaptToLighting = adaptToLighting });
        sw.Stop();

        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(x, y, 301, 288), best.Bounds);
        Assert.True(best.Score >= 0.95, $"score {best.Score}");
        Assert.True(result.BestScore >= 0.95, $"best score {result.BestScore}");
        Assert.True(sw.Elapsed.TotalMilliseconds < LargeSearchBudgetMs * 2, $"took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 15)]
    [InlineData(true, 15)]
    public void Tic_tac_toe_grid_is_found_with_background_and_edge_options(bool matchForeground, int ignoreEdgesPercent)
    {
        var template = StoredGrid();
        var screen = BlackScreenWith(template, 813, 399);

        var result = TemplateMatcher.Find(screen, template, new TemplateMatchOptions
        {
            MinScore = 0.9,
            MatchForeground = matchForeground,
            IgnoreEdgeFraction = ignoreEdgesPercent / 100.0,
        });

        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(813, 399, 301, 288), best.Bounds);
        Assert.True(best.Score >= 0.95, $"score {best.Score}");
    }

    [Fact]
    public void Tic_tac_toe_grid_is_found_on_a_busy_screen()
    {
        var template = StoredGrid();
        var screen = TestImages.Blocky(1920, 1080, seed: 41);
        TestImages.Paste(screen, template, 777, 401);

        var result = TemplateMatcher.Find(screen, template);

        var best = Assert.IsType<VisualMatch>(result.Best);
        Assert.Equal(new ScreenRect(777, 401, 301, 288), best.Bounds);
        Assert.True(best.Score >= 0.95, $"score {best.Score}");
    }

    [Fact]
    public void A_blank_black_screen_does_not_match_the_mostly_black_grid()
    {
        // The direct colour comparison must not call an empty black area "98 % similar" just because the grid is
        // 98 % black pixels.
        var template = StoredGrid();
        var screen = BgraImage.Create(1920, 1080);
        screen.FillRectangle(new ScreenRect(600, 500, 1, 200), RgbColor.White); // one stray line elsewhere

        var result = TemplateMatcher.Find(screen, template);

        Assert.Empty(result.Matches);
        Assert.True(result.BestScore < 0.6, $"best score {result.BestScore}");
    }

    [Fact]
    public void Direct_comparison_ignores_alpha_and_never_returns_nan()
    {
        Assert.Equal(1.0, TemplateMatcher.CombinedScore(double.NaN, 1.0));
        Assert.Equal(0.0, TemplateMatcher.CombinedScore(double.NaN, double.PositiveInfinity));
        Assert.Equal(0.97, TemplateMatcher.CombinedScore(0.97, 0.2), 6);

        // Same colours, different alpha (GDI 0 vs decoded 255): an exact match on a small template too.
        var icon = TestImages.Icon(40, 30, seed: 5);
        var screen = BlackScreenWith(icon, 70, 50, 300, 200);
        var result = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { MinScore = 0.99 });
        Assert.Equal(new ScreenRect(70, 50, 40, 30), result.Best?.Bounds);
        Assert.True(result.Best!.Score >= 0.999, $"score {result.Best.Score}");
    }

    [Fact]
    public void The_fft_correlation_equals_the_direct_sum()
    {
        var random = new Random(3);
        const int mw = 37, mh = 23, kw = 9, kh = 6;
        var map = Enumerable.Range(0, mw * mh).Select(_ => (float)random.Next(256)).ToArray();
        var kernel = Enumerable.Range(0, kw * kh).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();

        var fft = FftCorrelator.CorrelateValid(map, mw, mh, kernel, kw, kh);

        var px = mw - kw + 1;
        var py = mh - kh + 1;
        Assert.Equal(px * py, fft.Length);
        for (var y = 0; y < py; y++)
        {
            for (var x = 0; x < px; x++)
            {
                double direct = 0;
                for (var j = 0; j < kh; j++)
                    for (var i = 0; i < kw; i++)
                        direct += kernel[j * kw + i] * map[(y + j) * mw + x + i];
                Assert.Equal(direct, fft[y * px + x], 0.05);
            }
        }
    }

    [Fact]
    public void Coarse_pass_never_shrinks_below_a_quarter()
    {
        Assert.True(TemplateMatcher.ChooseScale(3840, 2160, 301, 288) <= TemplateMatcher.MaxCoarseScale);
        Assert.True(TemplateMatcher.ChooseScale(3840, 1080, 250, 250) <= TemplateMatcher.MaxCoarseScale);
        Assert.Equal(4, TemplateMatcher.ChooseScale(1920, 1080, 301, 288));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task If_condition_finds_the_grid_on_the_main_monitor(bool adaptToLighting)
    {
        var grid = TicTacToeGrid();
        var condition = new Condition
        {
            Check = new FindImageStep
            {
                TemplateWidth = grid.Width,
                TemplateHeight = grid.Height,
                TemplateData = TemplateCodec.Encode(grid),
                MinSimilarityPercent = 90,
                AdaptToLighting = adaptToLighting,
                Scope = SearchScope.PrimaryMonitor,
                TimeoutMs = 0,
            },
        };
        var evaluator = new ScreenConditionEvaluator(new ScreenProbes(null, new FakeCapture(BlackScreenWith(grid, 813, 399)), new FakeClock()));

        var check = await evaluator.CheckAsync(condition, evaluator.NewTimer(condition), CancellationToken.None);

        Assert.True(check.Seen, check.Details);
        Assert.True(check.IsTrue);
        Assert.Equal((813 + 150, 399 + 144), (check.X, check.Y));
        Assert.Equal(new ScreenRect(813, 399, 301, 288), check.Match);
    }

    [Fact]
    public async Task If_step_takes_the_true_branch_when_the_grid_is_on_screen()
    {
        var grid = TicTacToeGrid();
        var condition = new Condition
        {
            Check = new FindImageStep
            {
                TemplateWidth = grid.Width,
                TemplateHeight = grid.Height,
                TemplateData = TemplateCodec.Encode(grid),
                MinSimilarityPercent = 90,
                Scope = SearchScope.PrimaryMonitor,
                TimeoutMs = 0,
            },
        };
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(BlackScreenWith(grid, 812, 398)));

        var result = await engine.RunAsync(Flow.Macro(
            Flow.If(condition), Flow.Move(1), new ElseStep(), Flow.Move(2), new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0" }, input.Calls);
    }

    [Fact]
    public void Tests_and_playback_build_the_same_match_settings()
    {
        var step = new FindImageStep { MinSimilarityPercent = 85, IgnoreColor = true, IgnoreEdgesPercent = 15, MatchForeground = true, AdaptToLighting = false };

        var options = ScreenProbes.ImageMatchOptions(step);

        Assert.Equal(0.85, options.MinScore, 6);
        Assert.True(options.IgnoreColor);
        Assert.Equal(0.15, options.IgnoreEdgeFraction, 6);
        Assert.True(options.MatchForeground);
        Assert.False(options.AdaptToLighting);
    }
}

public class ColorFinderTests
{
    private static readonly RgbColor Red = new(255, 0, 0);

    [Fact]
    public void Groups_touching_pixels_into_patches_biggest_first()
    {
        var image = TestImages.Solid(100, 60, RgbColor.Black);
        image.FillRectangle(new ScreenRect(10, 10, 10, 10), Red); // 100 pixels
        image.FillRectangle(new ScreenRect(70, 40, 4, 4), Red);   // 16 pixels

        var result = ColorFinder.Find(image, Red, tolerance: 0);

        Assert.Equal(116, result.MatchingPixels);
        Assert.Equal(2, result.Blobs.Count);
        var big = result.Blobs[0];
        Assert.Equal(new ScreenRect(10, 10, 10, 10), big.Bounds);
        Assert.Equal(100, big.PixelCount);
        Assert.Equal(14.5, big.CenterX, 3);
        Assert.Equal(14.5, big.CenterY, 3);
    }

    [Fact]
    public void Small_patches_are_ignored()
    {
        var image = TestImages.Solid(50, 50, RgbColor.Black);
        image.FillRectangle(new ScreenRect(5, 5, 3, 3), Red);
        image.SetPixel(40, 40, Red);

        var result = ColorFinder.Find(image, Red, 0, minPixels: 5);

        var only = Assert.Single(result.Blobs);
        Assert.Equal(9, only.PixelCount);
        Assert.Equal(10, result.MatchingPixels);
    }

    [Fact]
    public void Tolerance_is_per_channel()
    {
        var image = TestImages.Solid(20, 20, RgbColor.Black);
        image.FillRectangle(new ScreenRect(0, 0, 5, 5), new RgbColor(250, 10, 10));

        Assert.Empty(ColorFinder.Find(image, Red, tolerance: 5).Blobs);
        Assert.Single(ColorFinder.Find(image, Red, tolerance: 10).Blobs);
    }
}

public class MarkerTrackerTests
{
    private static readonly RgbColor Marker = new(255, 255, 255);

    private static BgraImage Bar(params int[] markerLefts)
    {
        var bar = TestImages.Solid(101, 10, new RgbColor(20, 40, 90));
        foreach (var left in markerLefts) bar.FillRectangle(new ScreenRect(left, 0, 3, 10), Marker);
        return bar;
    }

    [Fact]
    public void Reports_the_position_along_the_bar()
    {
        var tracker = new MarkerTracker(Marker, 10, BarOrientation.Horizontal);

        var reading = tracker.Update(Bar(49));

        Assert.Equal(MarkerStatus.Found, reading.Status);
        Assert.Equal(50, reading.PositionPercent!.Value, 3);
        Assert.Equal(1.0, reading.Confidence, 3);
    }

    [Fact]
    public void A_single_frame_jump_is_ignored_once_then_accepted()
    {
        var tracker = new MarkerTracker(Marker, 10, BarOrientation.Horizontal, maxJumpPercent: 35);
        tracker.Update(Bar(49));

        var glitch = tracker.Update(Bar(9));
        Assert.Equal(MarkerStatus.IgnoredJump, glitch.Status);
        Assert.Equal(50, glitch.PositionPercent!.Value, 3);

        var confirmed = tracker.Update(Bar(9));
        Assert.Equal(MarkerStatus.Found, confirmed.Status);
        Assert.Equal(10, confirmed.PositionPercent!.Value, 3);
    }

    [Fact]
    public void Two_similar_markers_are_ambiguous()
    {
        var tracker = new MarkerTracker(Marker, 10, BarOrientation.Horizontal);
        var reading = tracker.Update(Bar(10, 80));

        Assert.Equal(MarkerStatus.Ambiguous, reading.Status);
        Assert.Null(reading.PositionPercent); // nothing trusted yet
    }

    [Fact]
    public void Missing_marker_is_lost_and_keeps_the_last_position()
    {
        var tracker = new MarkerTracker(Marker, 10, BarOrientation.Horizontal);
        tracker.Update(Bar(49));

        var reading = tracker.Update(Bar());

        Assert.Equal(MarkerStatus.Lost, reading.Status);
        Assert.Equal(50, reading.PositionPercent!.Value, 3);
    }

    [Fact]
    public void Vertical_bars_measure_from_the_top()
    {
        var bar = TestImages.Solid(10, 101, RgbColor.Black);
        bar.FillRectangle(new ScreenRect(0, 74, 10, 3), Marker);
        var reading = new MarkerTracker(Marker, 10, BarOrientation.Vertical).Update(bar);
        Assert.Equal(75, reading.PositionPercent!.Value, 3);
    }
}

public class FindImagePlaybackTests
{
    private static Macro MacroWith(params MacroStep[] steps)
    {
        var m = new Macro { Name = "Test" };
        m.Steps.AddRange(steps);
        return m;
    }

    private static FindImageStep StepFor(BgraImage icon, Action<FindImageStep>? configure = null)
    {
        var step = new FindImageStep
        {
            TemplateWidth = icon.Width,
            TemplateHeight = icon.Height,
            TemplateData = TemplateCodec.Encode(icon),
        };
        configure?.Invoke(step);
        return step;
    }

    private static (BgraImage Screen, BgraImage Icon) ScreenWithIcon(int x = 100, int y = 50)
    {
        var screen = TestImages.Blocky(400, 300, seed: 20);
        var icon = TestImages.Icon(40, 30, seed: 21);
        TestImages.Paste(screen, icon, x, y);
        return (screen, icon);
    }

    [Fact]
    public async Task Clicks_the_center_of_the_found_image_plus_offset()
    {
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen));
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(MacroWith(StepFor(icon, s => { s.Action = FoundTextAction.Click; s.OffsetX = 5; })));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 125,65", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Contains(messages, m => m.Contains("Found the image at (120, 65)") && m.Contains("Checked 1 time"));
    }

    [Fact]
    public async Task Search_area_is_used_and_coordinates_stay_screen_pixels()
    {
        var (screen, icon) = ScreenWithIcon();
        var capture = new FakeCapture(screen);
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(MacroWith(
            StepFor(icon, s =>
            {
                s.SearchFullScreen = false;
                s.RegionX = 80; s.RegionY = 40; s.RegionWidth = 200; s.RegionHeight = 100;
                s.Action = FoundTextAction.Move;
            })));

        Assert.Equal(new ScreenRect(80, 40, 200, 100), Assert.Single(capture.Regions));
        Assert.Equal(new[] { "move 120,65" }, input.Calls);
    }

    [Fact]
    public async Task Works_on_a_monitor_left_of_the_main_one()
    {
        var (screen, icon) = ScreenWithIcon();
        var capture = new FakeCapture(-1920, -100, screen);
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(MacroWith(
            StepFor(icon, s => s.Action = FoundTextAction.Move)));

        Assert.Equal(new[] { $"move {-1920 + 120},{-100 + 65}" }, input.Calls);
    }

    [Fact]
    public async Task Times_out_with_a_helpful_message()
    {
        var screen = TestImages.Blocky(400, 300, seed: 22);
        var capture = new FakeCapture(screen);
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(MacroWith(
            StepFor(TestImages.Icon(40, 30, seed: 21), s => { s.TimeoutMs = 1000; s.CheckIntervalMs = 250; }),
            new MouseMoveStep { X = 1 }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("didn't appear within 1 s", result.Error);
        Assert.Contains("best similarity", result.Error);
        Assert.Empty(input.Calls);
        Assert.Equal(5, capture.Captures); // t = 0, 0.25, 0.5, 0.75, 1
    }

    [Fact]
    public async Task Can_continue_when_not_found()
    {
        var capture = new FakeCapture(TestImages.Blocky(400, 300, seed: 22));
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(MacroWith(
            StepFor(TestImages.Icon(40, 30), s => { s.TimeoutMs = 0; s.WhenNotFound = TextNotFoundBehavior.Continue; }),
            new MouseMoveStep { X = 1 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(1, capture.Captures);
        Assert.Equal(new[] { "move 1,0" }, input.Calls);
    }

    [Fact]
    public async Task Waits_until_the_image_disappears()
    {
        var (withIcon, icon) = ScreenWithIcon();
        var without = TestImages.Blocky(400, 300, seed: 20);
        var capture = new FakeCapture(withIcon, withIcon, without);
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(MacroWith(
            StepFor(icon, s => { s.Goal = WaitGoal.Disappear; s.TimeoutMs = 5000; s.CheckIntervalMs = 100; s.Action = FoundTextAction.Click; })));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, capture.Captures);
        Assert.Empty(input.Calls); // the "when found" action isn't used while waiting for it to go away
    }

    [Fact]
    public async Task Fails_if_the_image_stays()
    {
        var (screen, icon) = ScreenWithIcon();
        var result = await new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(screen)).RunAsync(MacroWith(
            StepFor(icon, s => { s.Goal = WaitGoal.Disappear; s.TimeoutMs = 1000; s.CheckIntervalMs = 100; })));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("still on screen after 1 s", result.Error);
    }

    [Fact]
    public async Task Later_click_can_use_the_found_color()
    {
        var screen = TestImages.Solid(300, 200, RgbColor.Black);
        screen.FillRectangle(new ScreenRect(200, 100, 11, 11), new RgbColor(0, 200, 0));
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).RunAsync(MacroWith(
            new FindColorStep { Color = "#00C800", Tolerance = 10, MinPixels = 20 },
            new MouseClickStep { UseLastFoundText = true }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 205,105", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    [Fact]
    public async Task Color_not_found_explains_why()
    {
        var screen = TestImages.Solid(100, 100, RgbColor.Black);
        screen.SetPixel(5, 5, RgbColor.White);

        var result = await new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(screen)).RunAsync(MacroWith(
            new FindColorStep { Color = "#FFFFFF", Tolerance = 0, MinPixels = 10, TimeoutMs = 0 }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("wasn't found on screen", result.Error);
        Assert.Contains("no patch had at least 10 pixels", result.Error);
    }

    [Fact]
    public async Task Missing_screen_capture_fails_with_explanation()
    {
        var result = await new PlaybackEngine(new FakeInput(), new FakeClock()).RunAsync(MacroWith(
            StepFor(TestImages.Icon(20, 20))));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("Screen capture isn't available", result.Error);
    }

    [Fact]
    public async Task Stop_while_waiting_for_an_image_ends_quickly()
    {
        var engine = new PlaybackEngine(new FakeInput(), clock: null, capture: new FakeCapture(TestImages.Blocky(200, 200, 1)));
        var run = engine.RunAsync(MacroWith(StepFor(TestImages.Icon(20, 20), s => s.TimeoutMs = 3_600_000)));
        await Task.Delay(100);
        engine.Stop();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
    }

    [Fact]
    public async Task Find_text_can_wait_until_text_disappears()
    {
        var ocr = new FakeOcr().Then(("Loading", 10, 10)).Then(("Loading", 10, 10)).ThenNothing();
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(MacroWith(
            new FindTextStep { Text = "Loading", Goal = WaitGoal.Disappear, TimeoutMs = 5000, CheckIntervalMs = 500 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, ocr.Reads);
        Assert.Empty(input.Calls);
    }
}

public class VisionValidationAndFileTests
{
    private static FindImageStep ValidImageStep() => new()
    {
        TemplateWidth = 20,
        TemplateHeight = 16,
        TemplateData = TemplateCodec.Encode(TestImages.Icon(20, 16)),
    };

    [Fact]
    public void New_image_step_needs_a_captured_image()
    {
        var step = MacroFactory.CreateStep(StepKind.FindImage);
        Assert.Equal(StepKind.FindImage, step.Kind);
        Assert.Contains(MacroValidator.ValidateStep(step), i => i.Severity == IssueSeverity.Error && i.Property == nameof(FindImageStep.TemplateData));
    }

    [Fact]
    public void Captured_image_step_is_valid()
    {
        Assert.False(MacroValidator.ValidateStep(ValidImageStep()).HasErrors());
    }

    [Fact]
    public void Damaged_or_flat_images_are_errors()
    {
        var damaged = ValidImageStep();
        damaged.TemplateHeight = 17;
        Assert.True(MacroValidator.ValidateStep(damaged).HasErrors());

        var flat = new FindImageStep { TemplateWidth = 10, TemplateHeight = 10, TemplateData = TemplateCodec.Encode(TestImages.Solid(10, 10, RgbColor.White)) };
        Assert.True(MacroValidator.ValidateStep(flat).HasErrors());

        var tooBig = new FindImageStep { TemplateWidth = MacroValidator.MaxTemplateSize + 1, TemplateHeight = 10, TemplateData = "x" };
        Assert.True(MacroValidator.ValidateStep(tooBig).HasErrors());
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(500, 400)]
    [InlineData(800, 600)]
    public void Large_captured_images_above_400x400_are_valid(int width, int height)
    {
        var icon = TestImages.Icon(width, height, seed: 42);
        var step = new FindImageStep
        {
            TemplateWidth = width,
            TemplateHeight = height,
            TemplateData = TemplateCodec.Encode(icon),
        };

        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public async Task Capture_image_above_400x400_works_across_find_image_if_repeat_and_clamps_multi_monitor_coords()
    {
        // Secondary monitor at negative physical coordinates (-1920, -200) and primary at (0, 0).
        var virtualBounds = new ScreenRect(-1920, -200, 3840, 1280);
        var desktop = TestImages.Blocky(3840, 1280, seed: 55);
        var bigPatch = TestImages.Icon(520, 440, seed: 56);
        // Place at (-1500, -100) in virtual screen coordinates -> local (420, 100) in the frozen desktop image.
        TestImages.Paste(desktop, bigPatch, 420, 100);
        var frozen = new ScreenFrame(virtualBounds, desktop);

        // Selection extending slightly past the top-left of the virtual screen clamps cleanly into the frozen bitmap.
        var clamped = frozen.CropFromScreen(new ScreenRect(-1930, -210, 500, 420));
        Assert.NotNull(clamped);
        Assert.Equal(new ScreenRect(-1920, -200, 490, 410), clamped!.Bounds);
        Assert.Equal(490, clamped.Image.Width);
        Assert.Equal(410, clamped.Image.Height);

        // Exact 520x440 crop on secondary monitor:
        var picked = frozen.CropFromScreen(new ScreenRect(-1500, -100, 520, 440));
        Assert.NotNull(picked);
        Assert.Equal((520, 440), (picked!.Image.Width, picked.Image.Height));
        var encoded = TemplateCodec.Encode(picked.Image);

        // 1. FindImageStep, 2. IfStep (ImageVisible), and 3. RepeatStep (ImageVisible) all validate and execute with a 520x440 image on a multi-monitor setup:
        FindImageStep MakeCheck() => new()
        {
            TemplateWidth = 520,
            TemplateHeight = 440,
            TemplateData = encoded,
            TimeoutMs = 0,
            Action = FoundTextAction.Move,
        };

        var macro = new Macro
        {
            Name = "Large multi-monitor image test",
            Steps =
            {
                MakeCheck(),
                new IfStep { Condition = new Condition { Check = MakeCheck() }, ThenAction = FoundTextAction.Click },
                new EndIfStep(),
                new RepeatStep
                {
                    Mode = RepeatMode.Until,
                    Condition = new Condition { Check = MakeCheck() },
                    MaxIterations = 3,
                },
                new MouseMoveStep { X = 10, Y = 10 },
                new EndRepeatStep(),
            },
        };

        Assert.False(MacroValidator.Validate(macro).HasErrors());

        var input = new FakeInput();
        var capture = new FakeCapture(virtualBounds.X, virtualBounds.Y, desktop);
        var result = await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // Center of (-1500, -100, 520, 440) is (-1240, 120)
        Assert.Contains("move -1240,120", input.Calls);
        Assert.DoesNotContain("move 10,10", input.Calls); // Repeat Until stopped immediately because the 520x440 image was seen
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void Image_steps_may_check_every_50_ms(int interval, bool expectError)
    {
        var step = ValidImageStep();
        step.CheckIntervalMs = interval;
        Assert.Equal(expectError, MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public void Area_smaller_than_the_image_is_an_error()
    {
        var step = ValidImageStep();
        step.SearchFullScreen = false;
        step.RegionWidth = 10;
        step.RegionHeight = 50;
        Assert.True(MacroValidator.ValidateStep(step).HasErrors());
    }

    [Theory]
    [InlineData("#00FF00", 24, 10, false)]
    [InlineData("green", 24, 10, true)]
    [InlineData("#00FF00", 256, 10, true)]
    [InlineData("#00FF00", 24, 0, true)]
    public void Color_step_rules(string color, int tolerance, int minPixels, bool expectError)
    {
        var step = new FindColorStep { Color = color, Tolerance = tolerance, MinPixels = minPixels };
        Assert.Equal(expectError, MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public void New_color_step_is_valid_and_warns_about_the_whole_screen()
    {
        var step = MacroFactory.CreateStep(StepKind.FindColor);
        var issues = MacroValidator.ValidateStep(step);
        Assert.False(issues.HasErrors());
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == "Region");
    }

    [Fact]
    public void Unknown_goal_value_is_an_error()
    {
        Assert.True(MacroValidator.ValidateStep(new FindTextStep { Text = "OK", Goal = (WaitGoal)42 }).HasErrors());
    }

    [Fact]
    public void Click_on_found_spot_after_an_image_step_has_no_warning()
    {
        var macro = new Macro { Name = "M" };
        macro.Steps.Add(ValidImageStep());
        macro.Steps.Add(new MouseClickStep { UseLastFoundText = true });
        Assert.DoesNotContain(MacroValidator.Validate(macro), i => i.Property == nameof(MouseClickStep.UseLastFoundText));
    }

    [Fact]
    public void Visual_steps_need_format_4_and_round_trip()
    {
        var macro = new Macro { Name = "Vision" };
        var image = ValidImageStep();
        image.MinSimilarityPercent = 87;
        image.IgnoreColor = true;
        image.Goal = WaitGoal.Disappear;
        macro.Steps.Add(image);
        macro.Steps.Add(new FindColorStep { Color = "#123456", Tolerance = 7, MinPixels = 3, SearchFullScreen = false, RegionX = -5, RegionWidth = 50, RegionHeight = 20 });

        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(macro));
        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"schemaVersion\": 4", json);
        Assert.Contains("\"findImage\"", json);
        Assert.Contains("\"findColor\"", json);

        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);
        var img = Assert.IsType<FindImageStep>(loaded!.Steps[0]);
        Assert.Equal(image.TemplateData, img.TemplateData);
        Assert.Equal((20, 16, 87, true, WaitGoal.Disappear), (img.TemplateWidth, img.TemplateHeight, img.MinSimilarityPercent, img.IgnoreColor, img.Goal));
        var color = Assert.IsType<FindColorStep>(loaded.Steps[1]);
        Assert.Equal(("#123456", 7, 3, false, -5), (color.Color, color.Tolerance, color.MinPixels, color.SearchFullScreen, color.RegionX));
    }

    [Fact]
    public void Find_text_waiting_to_disappear_needs_format_4_but_plain_find_text_does_not()
    {
        var plain = new Macro { Name = "Plain" };
        plain.Steps.Add(new FindTextStep { Text = "OK" });
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(plain));

        plain.Steps.Add(new FindTextStep { Text = "Loading", Goal = WaitGoal.Disappear });
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(plain));
    }

    [Fact]
    public void Highest_needed_format_wins_regardless_of_step_order()
    {
        var macro = new Macro { Name = "Mixed" };
        macro.Steps.Add(new InputSequenceStep
        {
            Events = { InputEvent.KeyDown(0, 0x41), InputEvent.KeyUp(1000, 0x41) },
        });
        macro.Steps.Add(new FindColorStep());
        Assert.Equal(4, MacroDocument.RequiredSchemaVersion(macro));
    }

    [Fact]
    public void Old_find_text_files_load_with_the_original_behavior()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "macro": {
                "id": "6a1d4c1e-7c6b-4b8e-9d55-0d1f6c7a1111",
                "name": "Old OCR macro",
                "steps": [
                  { "type": "findText", "id": "6a1d4c1e-7c6b-4b8e-9d55-0d1f6c7a2222", "text": "OK", "action": "Click" }
                ]
              }
            }
            """;

        Assert.True(MacroFileFormat.TryParse(json, out var macro, out var error), error);
        var step = Assert.IsType<FindTextStep>(Assert.Single(macro!.Steps));
        Assert.Equal(WaitGoal.Appear, step.Goal);
        Assert.Equal(500, step.CheckIntervalMs);
        Assert.Equal(85, step.MinSimilarityPercent);
        Assert.Equal(FoundTextAction.Click, step.Action);
        Assert.True(step.SearchFullScreen);
    }

    [Fact]
    public void Describes_visual_steps()
    {
        Assert.Equal("Find image (20×16) on screen → Continue when found (wait up to 10 s)", StepDescriber.Describe(ValidImageStep()));
        Assert.Equal("Find image (not captured yet) on screen → Continue when found (look once)", StepDescriber.Describe(new FindImageStep { TimeoutMs = 0 }));
        Assert.Equal("Wait up to 2 s until \"Loading\" is gone on screen",
            StepDescriber.Describe(new FindTextStep { Text = "Loading", Goal = WaitGoal.Disappear, TimeoutMs = 2000 }));
        Assert.StartsWith("Find color #FF0000", StepDescriber.Describe(new FindColorStep()));
    }
}
