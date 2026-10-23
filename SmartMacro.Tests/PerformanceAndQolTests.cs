using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

// "AAA Performance, QoL & Studio UI Upgrade" tests. Upgrade 1 (Vision Engine Turbo) first; the other upgrades add their
// test classes below.

/// <summary>
/// A fake screen that behaves like the real GDI capture: it copies only the requested rectangle and says so
/// (<see cref="IScreenCapture.PrefersSmallCaptures"/> = true). The picture on "screen" can be swapped between looks.
/// </summary>
internal sealed class RegionCapture : IScreenCapture
{
    public RegionCapture(BgraImage screen)
    {
        Screen = screen;
    }

    public BgraImage Screen { get; set; }
    public List<ScreenRect> Regions { get; } = new();
    public bool PrefersSmallCaptures => true;

    public ScreenRect GetVirtualScreen() => new(0, 0, Screen.Width, Screen.Height);

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Regions) Regions.Add(region);
        var area = region.Intersect(GetVirtualScreen());
        if (area.IsEmpty) throw new ArgumentException("The search area is outside the screen.");
        return Task.FromResult(new ScreenFrame(area, Screen.Crop(area)));
    }
}

internal static class Turbo
{
    /// <summary>The 32×32 "game button" used by the Hot-Spot tests (random two-colour cells: one true position).</summary>
    public static BgraImage Target { get; } = TestImages.Icon(32, 32, seed: 77);

    /// <summary>A busy 1920×1080 screen with <see cref="Target"/> pasted at (x, y).</summary>
    public static BgraImage FullHdScreenWithTarget(int x, int y)
    {
        var screen = TestImages.Blocky(1920, 1080, seed: 41);
        TestImages.Paste(screen, Target, x, y);
        return screen;
    }

    public static FindImageStep FindTarget(Action<FindImageStep>? configure = null) => Aaa.FindImage(Target, s =>
    {
        s.Scope = SearchScope.AllMonitors;
        s.SearchFullScreen = true;
        configure?.Invoke(s);
    });

    /// <summary>True when the rectangle is a small Hot-Spot capture (the target ± the margin), not a full-screen one.</summary>
    public static bool IsHotSpotSized(ScreenRect r) =>
        r.Width <= Target.Width + 2 * ScreenProbes.HotSpotMarginPx && r.Height <= Target.Height + 2 * ScreenProbes.HotSpotMarginPx;
}

public class TemplateCacheTests
{
    [Fact]
    public void Same_template_text_returns_the_same_decoded_instance_without_decoding_again()
    {
        var cache = new TemplateCache();
        var image = TestImages.Icon(40, 30, seed: 5);
        var data = TemplateCodec.Encode(image);

        Assert.True(cache.TryGetTemplate(data, 40, 30, out var first, out var error), error);
        // An equal string that is a different object (as after loading the macro file again) hits the cache too.
        var copy = new string(data.AsSpan());
        Assert.False(ReferenceEquals(copy, data));
        Assert.True(cache.TryGetTemplate(copy, 40, 30, out var second, out _));
        Assert.True(cache.TryGetTemplate(data, 40, 30, out var third, out _));

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, cache.Decodes);
        Assert.Equal(2, cache.Hits);
        Assert.Equal(1, cache.Count);
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 40; x++)
                Assert.Equal(image.GetPixel(x, y), first!.GetPixel(x, y));
    }

    [Fact]
    public void Same_base64_png_returns_the_same_instance_without_decoding_again()
    {
        var cache = new TemplateCache();
        var png = Convert.ToBase64String(PngCodec.Encode(TestImages.Icon(56, 56, seed: 9)));

        var first = cache.GetPng(png);
        var second = cache.GetPng(png);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, cache.Decodes);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void The_shared_cache_is_used_by_the_static_shortcuts()
    {
        var png = Convert.ToBase64String(PngCodec.Encode(TestImages.Icon(24, 24, seed: 4242)));
        var data = TemplateCodec.Encode(TestImages.Icon(20, 12, seed: 4243));

        Assert.Same(TemplateCache.GetOrDecode(png), TemplateCache.GetOrDecode(png));
        Assert.Same(TemplateCache.GetOrDecode(data, 20, 12), TemplateCache.GetOrDecode(data, 20, 12));
        Assert.True(TemplateCache.TryGetOrDecode(data, 20, 12, out var viaTry, out _));
        Assert.Same(TemplateCache.GetOrDecode(data, 20, 12), viaTry);
    }

    [Fact]
    public void Same_text_with_another_size_is_a_different_entry()
    {
        var cache = new TemplateCache();
        var data = TemplateCodec.Encode(TestImages.Icon(20, 10));

        Assert.True(cache.TryGetTemplate(data, 20, 10, out _, out _));
        Assert.False(cache.TryGetTemplate(data, 20, 11, out var wrong, out var error));

        Assert.Null(wrong);
        Assert.NotNull(error);
        Assert.Equal(2, cache.Decodes);
    }

    [Fact]
    public void Damaged_pictures_are_remembered_with_their_error()
    {
        var cache = new TemplateCache();

        Assert.False(cache.TryGetPng("this is not base64!", out _, out var first));
        Assert.False(cache.TryGetPng("this is not base64!", out _, out var second));
        Assert.False(cache.TryGetTemplate("AAAA", 10, 10, out _, out var damaged));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.NotNull(damaged);
        Assert.Equal(2, cache.Decodes); // the second PNG lookup was answered from the cache
    }

    [Fact]
    public void Empty_text_is_answered_without_filling_the_cache()
    {
        var cache = new TemplateCache();

        Assert.Null(cache.GetPng(null));
        Assert.Null(cache.GetPng("  "));
        Assert.False(cache.TryGetTemplate("", 10, 10, out _, out var error));

        Assert.NotNull(error);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Least_recently_used_pictures_are_dropped_first()
    {
        var cache = new TemplateCache(capacity: 2);
        var a = TemplateCodec.Encode(TestImages.Icon(12, 12, seed: 1));
        var b = TemplateCodec.Encode(TestImages.Icon(12, 12, seed: 2));
        var c = TemplateCodec.Encode(TestImages.Icon(12, 12, seed: 3));

        cache.TryGetTemplate(a, 12, 12, out var a1, out _);
        cache.TryGetTemplate(b, 12, 12, out _, out _);
        cache.TryGetTemplate(a, 12, 12, out _, out _); // a is now the most recently used
        cache.TryGetTemplate(c, 12, 12, out _, out _); // pushes b out
        Assert.Equal(3, cache.Decodes);
        Assert.Equal(2, cache.Count);

        cache.TryGetTemplate(a, 12, 12, out var a2, out _);
        Assert.Same(a1, a2);
        Assert.Equal(3, cache.Decodes);

        cache.TryGetTemplate(b, 12, 12, out _, out _);
        Assert.Equal(4, cache.Decodes); // b had to be decoded again
    }

    [Fact]
    public void The_byte_limit_is_kept_but_the_newest_picture_always_stays()
    {
        var cache = new TemplateCache(capacity: 100, maxBytes: 20 * 20 * 4 + 1);
        var small = TemplateCodec.Encode(TestImages.Icon(20, 20, seed: 1));
        var big = TemplateCodec.Encode(TestImages.Icon(40, 40, seed: 2));

        cache.TryGetTemplate(small, 20, 20, out _, out _);
        Assert.Equal(20 * 20 * 4, cache.CachedBytes);

        cache.TryGetTemplate(big, 40, 40, out var bigImage, out _);
        Assert.NotNull(bigImage);
        Assert.Equal(1, cache.Count);
        Assert.Equal(40 * 40 * 4, cache.CachedBytes);
    }

    [Fact]
    public async Task Many_threads_get_one_instance()
    {
        var cache = new TemplateCache();
        var data = TemplateCodec.Encode(TestImages.Icon(64, 64, seed: 8));

        var images = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            cache.TryGetTemplate(data, 64, 64, out var image, out _);
            return image;
        })));

        Assert.All(images, i => Assert.Same(images[0], i));
        Assert.Equal(1, cache.Count);
    }
}

public class HotSpotTests
{
    [Fact]
    public async Task A_stationary_target_is_found_at_its_last_spot_in_under_2_ms()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var memory = new HotSpotMemory();
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, memory);
        var step = Turbo.FindTarget();

        // First look: nothing remembered yet → the normal search of the whole screen.
        var first = await probes.ProbeImageAsync(step);
        Assert.True(first.Found, first.MissText);
        Assert.False(first.FromHotSpot);
        Assert.Equal(new ScreenRect(900, 500, 32, 32), first.Match);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), capture.Regions[0]);
        Assert.Equal(1, memory.Count);

        // Next looks: only the small spot around the last match is captured and checked.
        var times = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var again = await probes.ProbeImageAsync(step);
            Assert.True(again.Found);
            Assert.True(again.FromHotSpot);
            Assert.Equal(new ScreenRect(900, 500, 32, 32), again.Match);
            Assert.Equal((916, 516), (again.X, again.Y));
            Assert.True(again.Confidence >= 0.99, $"confidence {again.Confidence}");
            Assert.True(again.ElapsedMs > 0);
            times.Add(again.ElapsedMs);
        }

        Assert.Equal(7, capture.Regions.Count); // one capture per look, no full-screen fallback
        Assert.All(capture.Regions.Skip(1), r => Assert.True(Turbo.IsHotSpotSized(r), $"captured {r}"));
        // The best of several looks (the first one also pays for JIT compiling the fast path).
        Assert.True(times.Min() < 2.0, $"fastest hot-spot look took {times.Min():0.000} ms ({string.Join(", ", times.Select(t => t.ToString("0.000")))})");
    }

    [Fact]
    public async Task A_target_that_moved_far_away_is_still_found_by_the_full_search()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var memory = new HotSpotMemory();
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, memory);
        var step = Turbo.FindTarget();
        Assert.True((await probes.ProbeImageAsync(step)).Found);

        // The target jumps to the other corner of the screen.
        capture.Screen = Turbo.FullHdScreenWithTarget(120, 940);
        capture.Regions.Clear();
        var moved = await probes.ProbeImageAsync(step);

        Assert.True(moved.Found, moved.MissText);
        Assert.False(moved.FromHotSpot);
        Assert.Equal(new ScreenRect(120, 940, 32, 32), moved.Match);
        Assert.Equal(2, capture.Regions.Count);
        Assert.True(Turbo.IsHotSpotSized(capture.Regions[0]));                 // quick re-check at the old spot: not there
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), capture.Regions[1]);    // so the whole screen was searched

        // The new place is remembered: the next look is a hot-spot hit there.
        var next = await probes.ProbeImageAsync(step);
        Assert.True(next.FromHotSpot);
        Assert.Equal(new ScreenRect(120, 940, 32, 32), next.Match);
    }

    [Fact]
    public async Task A_target_that_moved_a_little_is_found_inside_the_hot_spot()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget();
        Assert.True((await probes.ProbeImageAsync(step)).Found);

        capture.Screen = Turbo.FullHdScreenWithTarget(915, 489); // 15 px right, 11 px up
        capture.Regions.Clear();
        var probe = await probes.ProbeImageAsync(step);

        Assert.True(probe.FromHotSpot);
        Assert.Equal(new ScreenRect(915, 489, 32, 32), probe.Match);
        Assert.Single(capture.Regions);
    }

    [Fact]
    public async Task A_target_that_disappeared_gives_exactly_the_normal_miss()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget();
        Assert.True((await probes.ProbeImageAsync(step)).Found);

        capture.Screen = TestImages.Blocky(1920, 1080, seed: 41);
        var gone = await probes.ProbeImageAsync(step);
        var plain = await new ScreenProbes(null, capture, new FakeClock()).ProbeImageAsync(step);

        Assert.False(gone.Found);
        Assert.Equal(plain.MissText, gone.MissText);
    }

    [Fact]
    public async Task The_hot_spot_stays_inside_the_search_area()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget(s =>
        {
            s.Scope = SearchScope.CustomArea;
            (s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight) = (890, 490, 60, 60);
        });
        Assert.True((await probes.ProbeImageAsync(step)).Found);

        capture.Regions.Clear();
        var again = await probes.ProbeImageAsync(step);

        Assert.True(again.FromHotSpot);
        var area = new ScreenRect(890, 490, 60, 60);
        Assert.Equal(area, area.Union(capture.Regions[0])); // never looked outside the step's own area
    }

    [Fact]
    public void Hot_spot_accuracy_is_at_least_88_percent_or_the_steps_own()
    {
        Assert.Equal(0.88, ScreenProbes.HotSpotScore(0.7), 6);
        Assert.Equal(0.88, ScreenProbes.HotSpotScore(0.88), 6);
        Assert.Equal(0.95, ScreenProbes.HotSpotScore(0.95), 6);
        Assert.Equal(new ScreenRect(76, 76, 80, 80), ScreenProbes.HotSpotArea(new ScreenRect(100, 100, 32, 32), new ScreenRect(0, 0, 1920, 1080)));
        Assert.Equal(new ScreenRect(0, 0, 56, 56), ScreenProbes.HotSpotArea(new ScreenRect(0, 0, 32, 32), new ScreenRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public async Task Without_hot_spot_memory_every_look_searches_the_whole_area()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var probes = new ScreenProbes(null, capture, new FakeClock()); // e.g. the editor's "Test detection now"
        var step = Turbo.FindTarget();

        await probes.ProbeImageAsync(step);
        var second = await probes.ProbeImageAsync(step);

        Assert.False(second.FromHotSpot);
        Assert.All(capture.Regions, r => Assert.Equal(new ScreenRect(0, 0, 1920, 1080), r));
    }

    [Fact]
    public async Task Captures_that_dont_prefer_small_areas_still_take_one_capture_per_look()
    {
        // The usual test fake: one queued screen per capture. The hot spot is checked inside the one capture.
        var screen = Turbo.FullHdScreenWithTarget(900, 500);
        var capture = new FakeCapture(screen, screen, Turbo.FullHdScreenWithTarget(120, 940));
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget();

        var first = await probes.ProbeImageAsync(step);
        var second = await probes.ProbeImageAsync(step);
        var third = await probes.ProbeImageAsync(step);

        Assert.Equal(3, capture.Captures);
        Assert.False(first.FromHotSpot);
        Assert.True(second.FromHotSpot);
        Assert.False(third.FromHotSpot);
        Assert.Equal(new ScreenRect(120, 940, 32, 32), third.Match);
    }

    [Fact]
    public async Task The_engine_keeps_hot_spots_across_loop_turns_and_repeats_and_reports_the_timing()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture);
        var detections = new List<TargetDetection>();
        engine.TargetDetected += (_, d) => detections.Add(d);
        var macro = Aaa.MacroWith(Turbo.FindTarget());
        macro.RepeatCount = 4;

        var result = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(4, detections.Count);
        Assert.False(detections[0].FromHotSpot);
        Assert.All(detections.Skip(1), d => Assert.True(d.FromHotSpot));
        Assert.All(detections, d => Assert.True(d.ElapsedMs > 0));
        Assert.All(detections, d => Assert.Matches(@"^🎯 \d+% · \d+(\.\d)?ms$", d.Badge));
        Assert.True(engine.LastProbeElapsedMs > 0);
        Assert.Equal(1, engine.HotSpots.Count);
        Assert.All(capture.Regions.Skip(1), r => Assert.True(Turbo.IsHotSpotSized(r), $"captured {r}"));

        // A new playback starts with an empty memory (the first look searches the whole screen again).
        capture.Regions.Clear();
        detections.Clear();
        await engine.RunAsync(Aaa.MacroWith(Turbo.FindTarget()));
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), capture.Regions[0]);
    }

    [Fact]
    public async Task Image_conditions_use_the_hot_spot_too()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture);
        var detections = new List<TargetDetection>();
        engine.TargetDetected += (_, d) => detections.Add(d);
        var condition = Aaa.ImageCondition(Turbo.Target);
        var check = (FindImageStep)condition.Check!;
        check.Scope = SearchScope.AllMonitors;
        check.SearchFullScreen = true;
        check.TimeoutMs = 0;
        var macro = Aaa.MacroWith(new IfStep { Condition = condition }, new MouseMoveStep { X = 1, Y = 1 }, new EndIfStep());
        macro.RepeatCount = 3;

        var result = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, detections.Count);
        Assert.All(detections, d => Assert.Equal(new ScreenRect(900, 500, 32, 32), d.Bounds));
        Assert.All(capture.Regions.Skip(1), r => Assert.True(Turbo.IsHotSpotSized(r), $"captured {r}"));
    }

    [Fact]
    public void Badge_text_is_short_and_culture_independent()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("🎯 99% · 0.6ms", new TargetDetection(new ScreenRect(0, 0, 10, 10), 0.991, "Find image", 5, 5, 0.61).Badge);
            Assert.Equal("🎯 87% · 14ms", new TargetDetection(new ScreenRect(0, 0, 10, 10), 0.87, "If", 5, 5, 13.6).Badge);
            Assert.Equal("🎯 100%", new TargetDetection(new ScreenRect(0, 0, 10, 10), 1.0, "Smart-Aim", 5, 5).Badge);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task Smart_aim_reports_its_search_time()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(Aaa.Screen(140, 80)));
        var detections = new List<TargetDetection>();
        engine.TargetDetected += (_, d) => detections.Add(d);

        await engine.RunAsync(Aaa.MacroWith(click));

        var hit = Assert.Single(detections);
        Assert.Equal("Smart-Aim", hit.Label);
        Assert.True(hit.ElapsedMs > 0);
        Assert.True(engine.LastProbeElapsedMs > 0);
    }
}

// ---------------- Upgrade 4: block-aware duplicate, Space toggle, Play from here ----------------

public class BlockAwareDuplicateTests
{
    // 0 Move 1 | 1 If | 2 Move 2 | 3 Else | 4 Move 3 | 5 End If | 6 Move 4
    private static List<MacroStep> IfElseList() => new()
    {
        Flow.Move(1), Flow.If(Flow.Text("OK")), Flow.Move(2), new ElseStep(), Flow.Move(3), new EndIfStep(), Flow.Move(4),
    };

    [Fact]
    public void Duplicating_an_If_copies_the_whole_If_Else_End_If_block_right_after_it()
    {
        var steps = IfElseList();

        var plan = StepListEditing.PlanDuplicate(steps, 1, 1);
        var after = StepListEditing.ApplyDuplicate(steps, plan);

        Assert.True(plan.Allowed, plan.Problem);
        Assert.True(plan.TookWholeBlocks);
        Assert.Equal((1, 5, 6), (plan.First, plan.Last, plan.InsertAt));
        Assert.Equal(12, after.Count);
        Assert.Equal(
            new[] { StepKind.MouseMove, StepKind.If, StepKind.MouseMove, StepKind.Else, StepKind.MouseMove, StepKind.EndIf,
                    StepKind.If, StepKind.MouseMove, StepKind.Else, StepKind.MouseMove, StepKind.EndIf, StepKind.MouseMove },
            after.Select(s => s.Kind));

        var blocks = BlockStructure.Analyze(after);
        Assert.True(blocks.IsValid, string.Join("; ", blocks.Problems.Select(p => p.Message)));
        Assert.Equal(8, blocks.ElseOf(6));
        Assert.Equal(10, blocks.EndOf(6));
        Assert.Equal(after.Count, after.Select(s => s.Id).Distinct().Count()); // every copy has a fresh ID
        Assert.Equal(new[] { 2, 3 }, new[] { ((MouseMoveStep)after[7]).X, ((MouseMoveStep)after[9]).X });
        Assert.NotSame(after[1], after[6]);
        Assert.Equal(4, ((MouseMoveStep)after[11]).X); // the step after the block is still last
    }

    [Theory]
    [InlineData(5)] // End If
    [InlineData(1)] // If
    public void Selecting_any_block_marker_copies_the_same_whole_block(int selected)
    {
        var plan = StepListEditing.PlanDuplicate(IfElseList(), selected, selected);

        Assert.True(plan.Allowed);
        Assert.Equal((1, 5), (plan.First, plan.Last));
    }

    [Fact]
    public void A_lone_Else_is_not_duplicated()
    {
        var plan = StepListEditing.PlanDuplicate(IfElseList(), 3, 3);

        Assert.False(plan.Allowed);
        Assert.Contains("only one Else", plan.Problem);
        Assert.Equal(0, plan.Count);
    }

    [Fact]
    public void A_normal_step_or_a_range_of_normal_steps_is_copied_as_is()
    {
        var steps = IfElseList();

        var one = StepListEditing.PlanDuplicate(steps, 4, 4);
        var range = StepListEditing.PlanDuplicate(new List<MacroStep> { Flow.Move(1), Flow.Move(2), Flow.Move(3) }, 0, 1);

        Assert.Equal((4, 4, 5, false), (one.First, one.Last, one.InsertAt, one.TookWholeBlocks));
        Assert.Equal((0, 1, 2), (range.First, range.Last, range.InsertAt));
        Assert.True(BlockStructure.Analyze(StepListEditing.ApplyDuplicate(steps, one)).IsValid);
    }

    [Fact]
    public void Duplicating_a_Repeat_copies_the_loop_with_its_nested_If()
    {
        var steps = new List<MacroStep>
        {
            Flow.Times(3), Flow.If(Flow.Text("A")), Flow.Move(1), new EndIfStep(), Flow.Move(2), new EndRepeatStep(),
        };

        var plan = StepListEditing.PlanDuplicate(steps, 0, 0);
        var after = StepListEditing.ApplyDuplicate(steps, plan);

        Assert.Equal((0, 5, 6), (plan.First, plan.Last, plan.InsertAt));
        Assert.Equal(12, after.Count);
        Assert.True(BlockStructure.Analyze(after).IsValid);
        Assert.Equal(3, ((RepeatStep)after[6]).Count);
    }

    [Fact]
    public void A_step_range_that_cuts_into_a_block_grows_to_the_whole_block()
    {
        // Move 1 + If selected: the copy takes Move 1 and the complete If block.
        var plan = StepListEditing.PlanDuplicate(IfElseList(), 0, 1);

        Assert.Equal((0, 5, 6), (plan.First, plan.Last, plan.InsertAt));
        Assert.True(BlockStructure.Analyze(StepListEditing.ApplyDuplicate(IfElseList(), plan)).IsValid);
    }

    [Fact]
    public void Markers_are_not_duplicated_while_the_blocks_are_broken()
    {
        var broken = new List<MacroStep> { Flow.If(Flow.Text("A")), Flow.Move(1) }; // no End If

        Assert.False(StepListEditing.PlanDuplicate(broken, 0, 0).Allowed);
        Assert.True(StepListEditing.PlanDuplicate(broken, 1, 1).Allowed); // a normal step is still fine
    }

    [Fact]
    public void Space_on_an_If_switches_its_whole_block()
    {
        var steps = IfElseList();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, StepListEditing.ToggleEnabledTargets(steps, new[] { 1 }));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, StepListEditing.ToggleEnabledTargets(steps, new[] { 5 }));
        Assert.Equal(new[] { 0, 4 }, StepListEditing.ToggleEnabledTargets(steps, new[] { 4, 0 }));
        Assert.Equal(new[] { 0 }, StepListEditing.ToggleEnabledTargets(new List<MacroStep> { Flow.If(Flow.Text("A")) }, new[] { 0 }));
    }
}

public class PlayFromHereTests
{
    // 0 Move 1 | 1 If green | 2 Move 2 | 3 Else | 4 Move 3 | 5 End If | 6 Repeat ×2 | 7 Move 5 | 8 End Repeat | 9 Move 6
    private static Macro Workflow() => Flow.Macro(
        Flow.Move(1), Flow.If(Flow.Green()), Flow.Move(2), new ElseStep(), Flow.Move(3), new EndIfStep(),
        Flow.Times(2), Flow.Move(5), new EndRepeatStep(), Flow.Move(6));

    [Theory]
    [InlineData(0, 0)]  // a normal step: right there
    [InlineData(2, 2)]  // inside an If: right there
    [InlineData(3, 4)]  // an Else: its "otherwise" part
    [InlineData(7, 7)]  // inside a Repeat: right there (the rest of the loop body plays once; its End Repeat is left out)
    [InlineData(8, 9)]  // an End Repeat whose Repeat is above: left out, so the slice starts after it
    [InlineData(9, 9)]
    public void Builds_the_slice_from_the_selected_step_to_the_end(int selected, int expectedStart)
    {
        var steps = Workflow().Steps;

        var (start, end) = PlaybackRanges.PlayFromHere(steps, selected);

        Assert.Equal(expectedStart, start);
        Assert.Equal(steps.Count, end);
    }

    [Fact]
    public async Task Playing_from_inside_the_true_branch_skips_the_otherwise_part_and_runs_to_the_end()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true)));

        var result = await engine.PlayFromStepAsync(Workflow(), 2);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 2,0", "move 5,0", "move 5,0", "move 6,0" }, input.Calls);
    }

    [Fact]
    public async Task Playing_from_inside_a_loop_plays_the_rest_of_the_body_once_and_continues()
    {
        // Fix 1 of the 6de423b audit: "Play from here" plays a block-safe slice from the selected step; the End Repeat
        // whose Repeat is above the start is left out (a single, predictable pass instead of re-entering the loop).
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: false)));

        var result = await engine.PlayFromStepAsync(Workflow(), 7);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 5,0", "move 6,0" }, input.Calls);
    }

    [Fact]
    public async Task Playing_from_the_otherwise_part_runs_it_and_continues_after_End_If()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Flow.Screen(green: true)));

        await engine.PlayFromStepAsync(Workflow(), 4);

        Assert.Equal(new[] { "move 3,0", "move 5,0", "move 5,0", "move 6,0" }, input.Calls);
    }
}

// ---------------- Upgrade 4 / 5: search, filter pills and folded blocks (view state only) ----------------

public class StepListViewTests
{
    // 0 Move 1 | 1 Repeat ×3 | 2 If | 3 Move 2 | 4 Else | 5 Key | 6 End If | 7 Click | 8 End Repeat | 9 Type text
    private static List<MacroStep> Nested() => new()
    {
        Flow.Move(1), Flow.Times(3), Flow.If(Flow.Text("Shop")), Flow.Move(2), new ElseStep(),
        new KeyPressStep { VirtualKey = 0x20, Comment = "jump" }, new EndIfStep(), new MouseClickStep { X = 5, Y = 5, Enabled = false },
        new EndRepeatStep(), new TypeTextStep { Text = "hello world" },
    };

    [Fact]
    public void Folding_an_If_hides_its_inner_steps_and_End_If_but_not_the_If()
    {
        var steps = Nested();

        var fold = StepListView.Collapse(steps, new HashSet<Guid> { steps[2].Id });

        Assert.Equal(new[] { false, false, false, true, true, true, true, false, false, false }, fold.Hidden);
        Assert.Equal(2, fold.HiddenCount[2]); // Move 2 + Key (its own Else / End If don't count)
        Assert.Equal("[2 steps hidden]", StepListView.HiddenBadge(fold.HiddenCount[2]));
    }

    [Fact]
    public void Folding_a_Repeat_hides_everything_inside_including_nested_blocks()
    {
        var steps = Nested();

        var fold = StepListView.Collapse(steps, new HashSet<Guid> { steps[1].Id, steps[2].Id });

        Assert.Equal(new[] { false, false, true, true, true, true, true, true, true, false }, fold.Hidden);
        Assert.Equal(6, fold.HiddenCount[1]);
        Assert.Equal(7, fold.HiddenTotal);
    }

    [Fact]
    public async Task Folded_blocks_keep_every_step_for_playback_and_saving()
    {
        var macro = Flow.Macro(Flow.Times(2), Flow.Move(1), Flow.Move(2), new EndRepeatStep(), Flow.Move(3));
        var json = MacroJson.Serialize(macro);
        var ids = macro.Steps.Select(s => s.Id).ToList();

        var fold = StepListView.Collapse(macro.Steps, new HashSet<Guid> { macro.Steps[0].Id });
        Assert.Equal(3, fold.HiddenTotal);

        // Nothing about the macro changed: same steps, same order, same file, and every step plays.
        Assert.Equal(ids, macro.Steps.Select(s => s.Id));
        Assert.Equal(json, MacroJson.Serialize(macro));
        var input = new FakeInput();
        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 2,0", "move 1,0", "move 2,0", "move 3,0" }, input.Calls);
    }

    [Fact]
    public void A_block_without_its_End_does_not_fold_and_normal_steps_ignore_folding()
    {
        var broken = new List<MacroStep> { Flow.If(Flow.Text("A")), Flow.Move(1) };
        var move = Flow.Move(2);

        Assert.All(StepListView.Collapse(broken, new HashSet<Guid> { broken[0].Id }).Hidden, Assert.False);
        Assert.All(StepListView.Collapse(new List<MacroStep> { move }, new HashSet<Guid> { move.Id }).Hidden, Assert.False);
        Assert.False(StepListView.CanCollapse(move));
        Assert.True(StepListView.CanCollapse(broken[0]));
    }

    [Fact]
    public void Enclosing_blocks_are_the_ones_to_unfold_for_a_hidden_step()
    {
        var steps = Nested();

        Assert.Equal(new[] { 1, 2 }, StepListView.EnclosingBlocks(steps, 5));
        Assert.Equal(new[] { 1 }, StepListView.EnclosingBlocks(steps, 7));
        Assert.Equal(new[] { 1 }, StepListView.EnclosingBlocks(steps, 8)); // its own End Repeat
        Assert.Empty(StepListView.EnclosingBlocks(steps, 9));
    }

    [Fact]
    public void Search_finds_steps_by_name_description_and_comment_ignoring_case()
    {
        var steps = Nested();

        Assert.Equal(new[] { 9 }, StepListView.FindMatches(steps, "HELLO"));
        Assert.Equal(new[] { 5 }, StepListView.FindMatches(steps, "jump"));
        Assert.Equal(new[] { 9 }, StepListView.FindMatches(steps, "world hello")); // every word, any order
        Assert.Equal(new[] { 0, 3 }, StepListView.FindMatches(steps, "move mouse"));
        Assert.Empty(StepListView.FindMatches(steps, "   "));
        Assert.Empty(StepListView.FindMatches(steps, "zzz-not-there"));
    }

    [Fact]
    public void Filter_pills_pick_their_group_and_combine_with_the_search()
    {
        var steps = Nested();

        Assert.Equal(new[] { 1, 2, 4, 6, 8 }, StepListView.FindMatches(steps, null, StepFilter.VisionAndLogic));
        Assert.Equal(new[] { 0, 3, 7 }, StepListView.FindMatches(steps, "", StepFilter.Mouse));
        Assert.Equal(new[] { 5, 9 }, StepListView.FindMatches(steps, null, StepFilter.Keyboard));
        Assert.Equal(new[] { 7 }, StepListView.FindMatches(steps, null, StepFilter.Disabled));
        Assert.Equal(new[] { 9 }, StepListView.FindMatches(steps, "hello", StepFilter.Keyboard));
        Assert.Empty(StepListView.FindMatches(steps, "hello", StepFilter.Mouse));
        Assert.Empty(StepListView.FindMatches(steps, null, StepFilter.All)); // nothing to highlight
    }

    [Fact]
    public void Navigator_texts()
    {
        Assert.Equal("No matches", StepListView.MatchCountText(0));
        Assert.Equal("1 match", StepListView.MatchCountText(1));
        Assert.Equal("3 matches", StepListView.MatchCountText(3));
        Assert.Equal("2 / 3", StepListView.MatchPosition(1, 3));
        Assert.Equal("", StepListView.MatchPosition(-1, 3));
        Assert.Equal("[1 step hidden]", StepListView.HiddenBadge(1));
        Assert.Equal("[empty]", StepListView.HiddenBadge(0));
    }
}

// ---------------- Audit of 6de423b: Fixes 1, 2 and 4 ----------------

public class PlayFromHereSliceTests
{
    // 0 Move 1 | 1 If A | 2 Move 2 | 3 Move 3 | 4 Else | 5 Move 4 | 6 End If | 7 Move 5
    private static List<MacroStep> IfElse() => new()
    {
        Flow.Move(1), Flow.If(Flow.Text("A")), Flow.Move(2), Flow.Move(3), new ElseStep(), Flow.Move(4), new EndIfStep(), Flow.Move(5),
    };

    // 0 Repeat ×3 | 1 If A | 2 Move 1 | 3 If B | 4 Move 2 | 5 End If B | 6 Else (A) | 7 Move 3 | 8 End If A | 9 Move 4
    // | 10 End Repeat | 11 Move 5
    private static List<MacroStep> Nested() => new()
    {
        Flow.Times(3), Flow.If(Flow.Text("A")), Flow.Move(1), Flow.If(Flow.Text("B")), Flow.Move(2), new EndIfStep(),
        new ElseStep(), Flow.Move(3), new EndIfStep(), Flow.Move(4), new EndRepeatStep(), Flow.Move(5),
    };

    private static void AssertValid(IReadOnlyList<MacroStep> steps)
    {
        var blocks = BlockStructure.Analyze(steps);
        Assert.True(blocks.IsValid, string.Join("; ", blocks.Problems.Select(p => $"{p.StepIndex}: {p.Message}")));
    }

    [Fact]
    public void Starting_inside_the_then_branch_strips_the_unmatched_Else_part_and_its_End_If()
    {
        var steps = IfElse();

        var slice = PlaybackRanges.BuildPlayFromHereSlice(steps, 2);

        Assert.Equal(new[] { 2, 3, 7 }, slice.SourceIndexes);
        Assert.Equal(new[] { 2, 3, 5 }, slice.Steps.Select(s => ((MouseMoveStep)s).X));
        Assert.Equal(2, slice.DroppedMarkers); // Else + End If
        Assert.Equal(1, slice.DroppedSteps);   // Move 4 (the "otherwise" part)
        AssertValid(slice.Steps);
        Assert.Same(steps[2], slice.Steps[0]); // the same objects (same IDs for the running-step highlight)
    }

    [Fact]
    public void Starting_right_before_the_Else_strips_the_Else_part()
    {
        var slice = PlaybackRanges.BuildPlayFromHereSlice(IfElse(), 3);

        Assert.Equal(new[] { 3, 7 }, slice.SourceIndexes);
        AssertValid(slice.Steps);
    }

    [Fact]
    public void Starting_on_an_Else_plays_its_otherwise_part_and_drops_the_orphan_End_If()
    {
        var slice = PlaybackRanges.BuildPlayFromHereSlice(IfElse(), 4);

        Assert.Equal(new[] { 5, 7 }, slice.SourceIndexes);
        Assert.Equal(1, slice.DroppedMarkers);
        AssertValid(slice.Steps);
    }

    [Theory]
    [InlineData(2, new[] { 2, 3, 4, 5, 9, 11 })] // inside If A: inner If B stays whole, A's Else part + End If and the End Repeat go
    [InlineData(4, new[] { 4, 9, 11 })]          // inside If B (inside If A inside the Repeat)
    [InlineData(5, new[] { 9, 11 })]             // B's End If: orphan, left out
    [InlineData(6, new[] { 7, 9, 11 })]          // A's Else: its otherwise part
    [InlineData(9, new[] { 9, 11 })]             // last step of the loop body
    [InlineData(10, new[] { 11 })]               // the End Repeat itself
    [InlineData(0, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })] // the Repeat: everything, unchanged
    public void Nested_blocks_always_give_a_valid_slice(int start, int[] expected)
    {
        var slice = PlaybackRanges.BuildPlayFromHereSlice(Nested(), start);

        Assert.Equal(expected, slice.SourceIndexes);
        AssertValid(slice.Steps);
    }

    [Fact]
    public void Every_start_in_every_list_gives_a_valid_block_structure()
    {
        var lists = new[]
        {
            IfElse(),
            Nested(),
            new List<MacroStep>
            {
                Flow.If(Flow.Text("A")), Flow.Times(2), Flow.If(Flow.Text("B")), Flow.Move(1), new ElseStep(), Flow.Times(2),
                Flow.Move(2), new EndRepeatStep(), new EndIfStep(), new EndRepeatStep(), new ElseStep(), Flow.Move(3), new EndIfStep(),
            },
        };

        foreach (var steps in lists)
        {
            AssertValid(steps);
            for (var start = 0; start < steps.Count; start++)
                AssertValid(PlaybackRanges.BuildPlayFromHereSlice(steps, start).Steps);
        }
    }

    [Fact]
    public async Task The_engine_plays_the_slice_once_from_deep_inside_nested_blocks()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var macro = Flow.Macro(Nested().ToArray());
        macro.RepeatCount = 5;
        var progress = new List<(int Number, int Count)>();
        engine.ProgressChanged += (_, p) => progress.Add((p.StepNumber, p.StepCount));

        // Step 5 (inside If B inside If A inside Repeat ×3): no condition is checked (no OCR needed), no orphan marker.
        var result = await engine.PlayFromStepAsync(macro, 4);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 2,0", "move 4,0", "move 5,0" }, input.Calls);
        Assert.Equal(1, result.IterationsCompleted);
        Assert.Equal(new[] { (5, 12), (10, 12), (12, 12) }, progress); // the macro's own step numbers
    }

    [Fact]
    public void Validation_uses_the_macros_step_numbers_and_ignores_steps_that_wont_play()
    {
        var macro = Flow.Macro(IfElse().ToArray());
        ((MouseMoveStep)macro.Steps[5]).X = 999_999; // in the "otherwise" part, which is skipped
        macro.Steps.Add(new DelayStep { DurationMs = -5 }); // step 9: plays → its error counts

        var issues = PlaybackRanges.ValidatePlayFromHere(macro, 2);

        var error = Assert.Single(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(8, error.StepIndex);
    }

    [Fact]
    public void Broken_blocks_in_the_macro_are_still_refused()
    {
        var macro = Flow.Macro(Flow.Move(1), new EndIfStep(), Flow.Move(2));

        var issues = PlaybackRanges.ValidatePlayFromHere(macro, 0);

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Property == "Block");
    }
}

public class UniqueIdAndHotSpotTests
{
    private static List<MacroStep> ImageIfBlock() => new()
    {
        Flow.Move(1), new IfStep { Condition = Aaa.ImageCondition(Turbo.Target) }, Aaa.FindImage(Turbo.Target), new EndIfStep(), Flow.Move(2),
    };

    private static IEnumerable<Guid> AllIds(IEnumerable<MacroStep> steps) =>
        steps.SelectMany(s => new[] { s.Id }.Concat(WorkflowSteps.ConditionsOf(s).Where(c => c.Check is not null).Select(c => c.Check!.Id)));

    [Fact]
    public void Duplicating_an_If_block_gives_every_copy_and_its_condition_search_a_fresh_ID()
    {
        var steps = ImageIfBlock();
        var plan = StepListEditing.PlanDuplicate(steps, 1, 1);

        var after = StepListEditing.ApplyDuplicate(steps, plan);

        Assert.Equal((1, 3, 4), (plan.First, plan.Last, plan.InsertAt));
        var ids = AllIds(after).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count()); // no step and no nested condition search shares an ID
        var originalCheck = (FindImageStep)((IfStep)after[1]).Condition!.Check!;
        var copiedCheck = (FindImageStep)((IfStep)after[4]).Condition!.Check!;
        Assert.NotEqual(HotSpotMemory.KeyOf(originalCheck), HotSpotMemory.KeyOf(copiedCheck));
        Assert.NotEqual(HotSpotMemory.KeyOf((FindImageStep)after[2]), HotSpotMemory.KeyOf((FindImageStep)after[5]));
    }

    [Fact]
    public void Pasted_steps_and_their_condition_searches_get_fresh_IDs()
    {
        var steps = ImageIfBlock();
        var text = StepClipboard.Serialize(steps.GetRange(1, 3));

        Assert.True(StepClipboard.TryParse(text, out var pasted, out var error), error);

        var before = AllIds(steps).ToHashSet();
        Assert.All(AllIds(pasted), id => Assert.DoesNotContain(id, before));
    }

    [Fact]
    public void A_recaptured_template_never_reuses_the_old_hot_spot()
    {
        var step = Turbo.FindTarget();
        var oldKey = HotSpotMemory.KeyOf(step);

        step.TemplateData = TemplateCodec.Encode(TestImages.Icon(32, 32, seed: 78)); // same size, new picture

        Assert.NotEqual(oldKey, HotSpotMemory.KeyOf(step));
        Assert.Equal(oldKey.StepId, HotSpotMemory.KeyOf(step).StepId);
    }

    [Fact]
    public async Task Hot_spot_matches_are_global_virtual_screen_coordinates_on_a_monitor_left_of_the_main_one()
    {
        // Virtual screen starts at (-1920, -200): the target sits at (900, 500) inside the picture = (-1020, 300) on screen.
        var capture = new OffsetCapture(Turbo.FullHdScreenWithTarget(900, 500), -1920, -200);
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget();

        var first = await probes.ProbeImageAsync(step);
        var second = await probes.ProbeImageAsync(step);

        Assert.Equal(new ScreenRect(-1020, 300, 32, 32), first.Match);
        Assert.True(second.FromHotSpot);
        Assert.Equal(new ScreenRect(-1020, 300, 32, 32), second.Match); // translated exactly once
        Assert.Equal((-1004, 316), (second.X, second.Y));
        Assert.True(Turbo.IsHotSpotSized(capture.Regions[1]), $"captured {capture.Regions[1]}");
    }

    [Fact]
    public async Task A_capture_that_reports_the_wrong_origin_can_never_produce_a_hot_spot_click()
    {
        // Simulates the double-offset bug: small captures claim to start at (0, 0). The guard throws such a "hit" away
        // and the normal full search gives the right place.
        var capture = new OffsetCapture(Turbo.FullHdScreenWithTarget(900, 500), 0, 0) { LieAboutSmallOrigins = true };
        var probes = new ScreenProbes(null, capture, new FakeClock(), null, new HotSpotMemory());
        var step = Turbo.FindTarget();
        Assert.True((await probes.ProbeImageAsync(step)).Found);

        var again = await probes.ProbeImageAsync(step);

        Assert.True(again.Found, again.MissText);
        Assert.False(again.FromHotSpot);
        Assert.Equal(new ScreenRect(900, 500, 32, 32), again.Match);
    }

    /// <summary>A screen whose virtual-screen origin is (<c>left</c>, <c>top</c>), copying only the requested pixels.</summary>
    private sealed class OffsetCapture : IScreenCapture
    {
        private readonly BgraImage _screen;
        private readonly int _left;
        private readonly int _top;

        public OffsetCapture(BgraImage screen, int left, int top)
        {
            _screen = screen;
            _left = left;
            _top = top;
        }

        public bool LieAboutSmallOrigins { get; init; }
        public List<ScreenRect> Regions { get; } = new();
        public bool PrefersSmallCaptures => true;

        public ScreenRect GetVirtualScreen() => new(_left, _top, _screen.Width, _screen.Height);

        public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
        {
            Regions.Add(region);
            var area = region.Intersect(GetVirtualScreen());
            if (area.IsEmpty) throw new ArgumentException("The search area is outside the screen.");
            var pixels = _screen.Crop(new ScreenRect(area.X - _left, area.Y - _top, area.Width, area.Height));
            var bounds = LieAboutSmallOrigins && area.Width < 200 ? new ScreenRect(0, 0, area.Width, area.Height) : area;
            return Task.FromResult(new ScreenFrame(bounds, pixels));
        }
    }
}

public class CollapsedBlockMoveTests
{
    // 0 Move 1 | 1 If | 2 Move 2 | 3 Else | 4 Move 3 | 5 End If | 6 Move 4
    private static List<MacroStep> Sample() => new()
    {
        Flow.Move(1), Flow.If(Flow.Text("OK")), Flow.Move(2), new ElseStep(), Flow.Move(3), new EndIfStep(), Flow.Move(4),
    };

    private static HashSet<Guid> Folded(params MacroStep[] steps) => steps.Select(s => s.Id).ToHashSet();

    private static int[] Xs(IEnumerable<MacroStep> steps) => steps.OfType<MouseMoveStep>().Select(m => m.X).ToArray();

    [Fact]
    public void Moving_a_collapsed_If_down_moves_the_whole_If_Else_End_If_block()
    {
        var steps = Sample();

        var plan = StepListEditing.PlanMove(steps, 1, 1, MoveDirection.Down, Folded(steps[1]));
        var after = StepListEditing.Apply(steps, plan);

        Assert.True(plan.Allowed, plan.Problem);
        Assert.True(plan.TookWholeBlocks);
        Assert.Equal((1, 5, 2), (plan.First, plan.Last, plan.NewFirst));
        Assert.Equal(new[] { StepKind.MouseMove, StepKind.MouseMove, StepKind.If, StepKind.MouseMove, StepKind.Else, StepKind.MouseMove, StepKind.EndIf },
            after.Select(s => s.Kind));
        Assert.Equal(new[] { 1, 4, 2, 3 }, Xs(after));
        Assert.True(BlockStructure.Analyze(after).IsValid);
    }

    [Fact]
    public void Moving_a_collapsed_If_up_moves_the_whole_block()
    {
        var steps = Sample();

        var plan = StepListEditing.PlanMove(steps, 1, 1, MoveDirection.Up, Folded(steps[1]));
        var after = StepListEditing.Apply(steps, plan);

        Assert.True(plan.Allowed, plan.Problem);
        Assert.Equal(new[] { StepKind.If, StepKind.MouseMove, StepKind.Else, StepKind.MouseMove, StepKind.EndIf, StepKind.MouseMove, StepKind.MouseMove },
            after.Select(s => s.Kind));
        Assert.True(BlockStructure.Analyze(after).IsValid);
    }

    [Fact]
    public void A_step_moving_up_jumps_over_a_collapsed_block_instead_of_landing_inside_it()
    {
        var steps = Sample();

        var unfolded = StepListEditing.PlanMove(steps, 6, 6, MoveDirection.Up, new HashSet<Guid>());
        var folded = StepListEditing.PlanMove(steps, 6, 6, MoveDirection.Up, Folded(steps[1]));

        Assert.Equal(5, unfolded.NewFirst); // unfolded: one place up (into the visible block, as before)
        Assert.True(folded.Allowed, folded.Problem);
        Assert.Equal(1, folded.NewFirst);   // folded: above the whole block
        var after = StepListEditing.Apply(steps, folded);
        Assert.Equal(new[] { 1, 4, 2, 3 }, Xs(after));
        Assert.True(BlockStructure.Analyze(after).IsValid);
        Assert.Equal(-1, BlockStructure.Analyze(after).StartOf(1)); // not inside the If
        Assert.Equal(0, BlockStructure.Analyze(after).Depth[1]);
    }

    [Fact]
    public void A_step_moving_down_jumps_over_a_collapsed_block()
    {
        var steps = Sample();

        var plan = StepListEditing.PlanMove(steps, 0, 0, MoveDirection.Down, Folded(steps[1]));
        var after = StepListEditing.Apply(steps, plan);

        Assert.Equal(5, plan.NewFirst);
        Assert.Equal(new[] { 2, 3, 1, 4 }, Xs(after));
        Assert.Equal(0, BlockStructure.Analyze(after).Depth[5]);
    }

    [Fact]
    public void A_collapsed_block_jumps_over_another_collapsed_block_as_one_row()
    {
        var steps = new List<MacroStep>
        {
            Flow.Times(2), Flow.Move(1), new EndRepeatStep(),             // 0..2 (folded)
            Flow.If(Flow.Text("A")), Flow.Move(2), new EndIfStep(),         // 3..5 (folded)
            Flow.Move(3),
        };

        var plan = StepListEditing.PlanMove(steps, 3, 3, MoveDirection.Up, Folded(steps[0], steps[3]));
        var after = StepListEditing.Apply(steps, plan);

        Assert.True(plan.Allowed, plan.Problem);
        Assert.Equal((3, 5, 0), (plan.First, plan.Last, plan.NewFirst));
        Assert.Equal(new[] { StepKind.If, StepKind.MouseMove, StepKind.EndIf, StepKind.Repeat, StepKind.MouseMove, StepKind.EndRepeat, StepKind.MouseMove },
            after.Select(s => s.Kind));
    }

    [Fact]
    public void Deleting_a_collapsed_If_deletes_the_whole_folded_block()
    {
        var steps = Sample();

        var folded = StepListEditing.PlanDelete(steps, new[] { 1 }, Folded(steps[1]));
        var unfolded = StepListEditing.PlanDelete(steps, new[] { 1 }, new HashSet<Guid>());

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, folded.Indexes);
        Assert.Contains("folded block", folded.Note);
        Assert.Equal(new[] { 1, 3, 5 }, unfolded.Indexes); // unfolded: only the markers, inner steps kept (as before)
    }
}

// ---------------- AAA Studio UI Upgrades 1–3: guide rails, live step progress, Ctrl+K palette, run summary ----------------

public class CommandPaletteTests
{
    private static string[] Titles(string query) => StudioCommandCatalog.Filter(query).Select(c => c.Title).ToArray();

    [Fact]
    public void Find_matches_the_three_vision_step_insertions()
    {
        Assert.Equal(new[] { "➕ Add: Find Image on Screen", "➕ Add: Find Color on Screen", "➕ Add: Find Text (OCR)" }, Titles("find"));
        Assert.Equal(Titles("find"), Titles("  FIND "));
        Assert.All(StudioCommandCatalog.Filter("find"), c => Assert.Equal(StudioCommandKind.AddStep, c.Kind));
    }

    [Fact]
    public void Studio_commands_are_found_by_keyword()
    {
        var compact = Assert.Single(StudioCommandCatalog.Filter("compact"));
        Assert.Equal(StudioCommandKind.ToggleCompactView, compact.Kind);

        var fix = Assert.Single(StudioCommandCatalog.Filter("fix"));
        Assert.Equal(StudioCommandKind.AutoFixBlocks, fix.Kind);
        Assert.Equal("✨ Auto-Fix Block Structure", fix.Title);

        Assert.Equal(StudioCommandKind.ToggleHumanize, StudioCommandCatalog.Filter("humanize")[0].Kind);
        Assert.Equal(StudioCommandKind.CleanUp, StudioCommandCatalog.Filter("clean")[0].Kind);
        Assert.Equal(StudioCommandKind.PlayFromSelected, Assert.Single(StudioCommandCatalog.Filter("play selected")).Kind);
    }

    [Fact]
    public void Every_word_must_match_and_keywords_count_too()
    {
        var speed = Assert.Single(StudioCommandCatalog.Filter("speed 2x"));
        Assert.Equal(2.0, speed.Speed);
        Assert.Equal(StepKind.FindText, Assert.Single(StudioCommandCatalog.Filter("ocr")).Step);      // title
        Assert.Equal(StepKind.Delay, StudioCommandCatalog.Filter("sleep")[0].Step);                    // keyword only
        Assert.Equal(new[] { StepKind.Repeat, StepKind.FeedbackLoop }, StudioCommandCatalog.Filter("loop").Select(c => c.Step!.Value));
        Assert.Empty(StudioCommandCatalog.Filter("zzz-nothing"));
    }

    [Fact]
    public void An_empty_query_lists_every_command_including_the_requested_ones()
    {
        var all = StudioCommandCatalog.Filter("");

        Assert.Equal(StudioCommandCatalog.All.Count, all.Count);
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        foreach (var kind in new[] { StepKind.FindImage, StepKind.FindColor, StepKind.FindText, StepKind.If, StepKind.Repeat,
                     StepKind.MouseClick, StepKind.MouseMove, StepKind.KeyPress, StepKind.Delay, StepKind.FeedbackLoop })
            Assert.Contains(all, c => c.Kind == StudioCommandKind.AddStep && c.Step == kind);
        Assert.Equal(new[] { 0.5, 1.0, 2.0, 5.0 }, all.Where(c => c.Kind == StudioCommandKind.SetSpeed).Select(c => c.Speed));
        Assert.Contains(all, c => c.Kind == StudioCommandKind.Play);
        Assert.Contains(all, c => c.Kind == StudioCommandKind.PlayFromSelected && c.Shortcut == "Shift+F5");
    }
}

public class StudioRailAndProgressTests
{
    [Fact]
    public void Guide_rails_take_the_colour_of_the_innermost_block()
    {
        // 0 Move | 1 Repeat | 2 Move | 3 If | 4 Move | 5 Else | 6 Move | 7 End If | 8 End Repeat | 9 Move
        var steps = new List<MacroStep>
        {
            Flow.Move(1), Flow.Times(2), Flow.Move(2), Flow.If(Flow.Text("A")), Flow.Move(3), new ElseStep(), Flow.Move(4),
            new EndIfStep(), new EndRepeatStep(), Flow.Move(5),
        };

        var rails = StepListView.EnclosingBlockKinds(steps);

        Assert.Equal(new[]
        {
            BlockRailKind.None, BlockRailKind.None, BlockRailKind.Repeat, BlockRailKind.Repeat, BlockRailKind.If, BlockRailKind.Repeat,
            BlockRailKind.If, BlockRailKind.Repeat, BlockRailKind.None, BlockRailKind.None,
        }, rails);
        Assert.Equal("#F59E0B", StepListView.RailColorHex(BlockRailKind.If));
        Assert.Equal("#8B5CF6", StepListView.RailColorHex(BlockRailKind.Repeat));
        Assert.Equal("", StepListView.RailColorHex(BlockRailKind.None));
    }

    [Fact]
    public void Expected_step_durations_drive_the_progress_bar()
    {
        Assert.Equal((double?)1000, PlaybackEngine.ExpectedDurationMs(new DelayStep { DurationMs = 1000 }, 1));
        Assert.Equal((double?)500, PlaybackEngine.ExpectedDurationMs(new DelayStep { DurationMs = 1000 }, 2));
        Assert.Equal((double?)800, PlaybackEngine.ExpectedDurationMs(new KeyPressStep { HoldMs = 800 }, 1));
        Assert.Null(PlaybackEngine.ExpectedDurationMs(new KeyPressStep { HoldMs = 0 }, 1));
        Assert.Equal((double?)5000, PlaybackEngine.ExpectedDurationMs(Turbo.FindTarget(s => s.TimeoutMs = 5000), 2)); // real time, not scaled
        Assert.Equal((double?)30_000, PlaybackEngine.ExpectedDurationMs(new FeedbackLoopStep { MaxRuntimeMs = 30_000 }, 1));
        Assert.Equal((double?)3000, PlaybackEngine.ExpectedDurationMs(Flow.If(Flow.Text("A", timeoutMs: 3000)), 1));
        Assert.Null(PlaybackEngine.ExpectedDurationMs(Flow.Move(1), 1));
        Assert.False(new StepProgress(Guid.NewGuid(), 0.5, 150).ShowBar); // too short to bother
        Assert.True(new StepProgress(Guid.NewGuid(), 0.5, 250).ShowBar);
    }

    [Fact]
    public async Task The_engine_reports_live_progress_of_the_playing_step()
    {
        var delay = new DelayStep { DurationMs = 1000 };
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());
        var seen = new List<StepProgress?>();
        engine.ProgressChanged += (_, p) => seen.Add(engine.GetCurrentStepProgress());

        await engine.RunAsync(Flow.Macro(Flow.Move(1), delay));

        Assert.Null(seen[0]); // Move mouse: no known length
        var atStart = Assert.IsType<StepProgress>(seen[1]);
        Assert.Equal(delay.Id, atStart.StepId);
        Assert.Equal(1000, atStart.ExpectedMs);
        Assert.InRange(atStart.Fraction, 0, 0.01);
        Assert.Null(engine.GetCurrentStepProgress()); // nothing plays any more
    }
}

public class RunSummaryTests
{
    [Fact]
    public void Summary_text_formats()
    {
        var done = new PlaybackSessionStats(PlaybackOutcome.Completed, 3, 24, 6, 0.8, TimeSpan.FromSeconds(8.4));
        Assert.Equal("✓ 3 runs in 8.4s · 24 steps · 6 vision locks (avg 0.8ms)", done.SummaryText);

        var one = new PlaybackSessionStats(PlaybackOutcome.Completed, 1, 1, 0, 0, TimeSpan.FromMilliseconds(420));
        Assert.Equal("✓ 1 run in 0.4s · 1 step", one.SummaryText);

        var stopped = new PlaybackSessionStats(PlaybackOutcome.Stopped, 2, 17, 1, 12.4, TimeSpan.FromSeconds(65));
        Assert.Equal("⏹ Stopped after 1m 05s · 2 runs finished · 17 steps · 1 vision lock (avg 12ms)", stopped.SummaryText);

        Assert.StartsWith("⚠ Failed after", new PlaybackSessionStats(PlaybackOutcome.Failed, 0, 3, 0, 0, TimeSpan.FromSeconds(1)).SummaryText);
    }

    [Fact]
    public void Summary_text_is_culture_independent()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var stats = new PlaybackSessionStats(PlaybackOutcome.Completed, 1200, 24_000, 6, 0.8, TimeSpan.FromSeconds(8.4));
            Assert.Equal("✓ 1,200 runs in 8.4s · 24,000 steps · 6 vision locks (avg 0.8ms)", stats.SummaryText);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task The_engine_counts_runs_steps_vision_locks_and_time()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture);
        var macro = Aaa.MacroWith(Turbo.FindTarget(), new DelayStep { DurationMs = 1000 });
        macro.RepeatCount = 3;

        var result = await engine.RunAsync(macro);

        var stats = Assert.IsType<PlaybackSessionStats>(result.Stats);
        Assert.Same(stats, engine.LastSessionStats);
        Assert.Equal(PlaybackOutcome.Completed, stats.Outcome);
        Assert.Equal(3, stats.CompletedRuns);
        Assert.Equal(6, stats.TotalStepsExecuted);
        Assert.Equal(3, stats.VisionMatchesCount);
        Assert.True(stats.AverageVisionMatchMs > 0);
        Assert.InRange(stats.TotalElapsed.TotalSeconds, 2.9, 3.5); // 3 × 1 s of delays on the fake clock
        Assert.StartsWith("✓ 3 runs in 3.", stats.SummaryText);
        Assert.Contains("· 6 steps · 3 vision locks (avg ", stats.SummaryText);
    }

    [Fact]
    public async Task A_new_playback_starts_counting_from_zero()
    {
        var capture = new RegionCapture(Turbo.FullHdScreenWithTarget(900, 500));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture);
        await engine.RunAsync(Aaa.MacroWith(Turbo.FindTarget()));

        var second = await engine.RunAsync(Aaa.MacroWith(Flow.Move(1)));

        Assert.Equal(0, second.Stats!.VisionMatchesCount);
        Assert.Equal(1, second.Stats.TotalStepsExecuted);
        Assert.Equal("✓ 1 run in 0.0s · 1 step", second.Stats.SummaryText);
    }
}
