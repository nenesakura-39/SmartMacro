using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

/// <summary>Fake OCR: returns the queued screens one per read (the last one repeats).</summary>
internal sealed class FakeOcr : IOcrService
{
    private readonly Queue<OcrReadResult> _screens = new();
    private OcrReadResult _last = new(new ScreenRect(0, 0, 1920, 1080), Array.Empty<OcrLineBox>());

    public bool IsAvailable { get; set; } = true;
    public string StatusText => IsAvailable ? "ready" : "no OCR language installed";
    public int Reads { get; private set; }
    public List<ScreenRect> Regions { get; } = new();

    public ScreenRect GetVirtualScreen() => new(0, 0, 1920, 1080);

    public FakeOcr Then(params (string Text, int X, int Y)[] words)
    {
        var line = new OcrLineBox(words.Select(w => new OcrWordBox(w.Text, new ScreenRect(w.X, w.Y, w.Text.Length * 10, 20))).ToList());
        _screens.Enqueue(new OcrReadResult(new ScreenRect(0, 0, 1920, 1080), new[] { line }));
        return this;
    }

    public FakeOcr ThenNothing()
    {
        _screens.Enqueue(new OcrReadResult(new ScreenRect(0, 0, 1920, 1080), Array.Empty<OcrLineBox>()));
        return this;
    }

    public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken)
    {
        Reads++;
        Regions.Add(region);
        if (_screens.Count > 0) _last = _screens.Dequeue();
        return Task.FromResult(_last);
    }
}

public class OcrTextMatcherTests
{
    private static OcrReadResult Screen(params string[] lines)
    {
        var y = 0;
        return new OcrReadResult(new ScreenRect(0, 0, 1000, 1000), lines.Select(line =>
        {
            var x = 0;
            var words = line.Split(' ').Select(w =>
            {
                var box = new OcrWordBox(w, new ScreenRect(x, y, w.Length * 10, 20));
                x += w.Length * 10 + 10;
                return box;
            }).ToList();
            y += 40;
            return new OcrLineBox(words);
        }).ToList());
    }

    [Fact]
    public void Finds_exact_word_case_insensitive()
    {
        var match = Assert.Single(OcrTextMatcher.Find(Screen("Click OK to continue"), "ok", TextMatchMode.WholeWords, false, 0.85));
        Assert.Equal("OK", match.Text);
        Assert.Equal(1.0, match.Similarity);
        Assert.Equal(new ScreenRect(60, 0, 20, 20), match.Bounds);
    }

    [Fact]
    public void Case_sensitive_respects_case()
    {
        Assert.Empty(OcrTextMatcher.Find(Screen("Click OK"), "ok", TextMatchMode.WholeWords, true, 1.0));
    }

    [Fact]
    public void Multi_word_phrase_gets_combined_bounds()
    {
        var match = OcrTextMatcher.Find(Screen("Please Sign in now"), "sign in", TextMatchMode.WholeWords, false, 0.9).First();
        Assert.Equal("Sign in", match.Text);
        Assert.Equal(new ScreenRect(70, 0, 70, 20), match.Bounds);
    }

    [Fact]
    public void Contains_matches_inside_longer_words_but_whole_words_does_not()
    {
        Assert.NotEmpty(OcrTextMatcher.Find(Screen("File Saved"), "save", TextMatchMode.Contains, false, 1.0));
        Assert.Empty(OcrTextMatcher.Find(Screen("File Saved"), "save", TextMatchMode.WholeWords, false, 1.0));
    }

    [Fact]
    public void Fuzzy_match_tolerates_ocr_mistakes_down_to_the_limit()
    {
        var screen = Screen("Log1n");
        Assert.NotEmpty(OcrTextMatcher.Find(screen, "Login", TextMatchMode.WholeWords, false, 0.79)); // 4/5 = 0.8
        Assert.Empty(OcrTextMatcher.Find(screen, "Login", TextMatchMode.WholeWords, false, 0.9));
    }

    [Fact]
    public void Multiple_matches_are_ordered_top_to_bottom()
    {
        var matches = OcrTextMatcher.Find(Screen("OK", "Cancel", "OK"), "OK", TextMatchMode.WholeWords, false, 1.0);
        Assert.Equal(2, matches.Count);
        Assert.True(matches[0].Bounds.Y < matches[1].Bounds.Y);
    }

    [Fact]
    public void Empty_query_finds_nothing()
    {
        Assert.Empty(OcrTextMatcher.Find(Screen("anything"), "   ", TextMatchMode.Contains, false, 0.5));
    }

    [Fact]
    public void Similarity_basics()
    {
        Assert.Equal(1.0, OcrTextMatcher.Similarity("ABC", "ABC"));
        Assert.Equal(0.0, OcrTextMatcher.Similarity("ABC", "XYZ"));
    }
}

public class FindTextPlaybackTests
{
    private static Macro MacroWith(params MacroStep[] steps)
    {
        var m = new Macro { Name = "Ocr" };
        m.Steps.AddRange(steps);
        return m;
    }

    [Fact]
    public async Task Waits_until_text_appears_then_clicks_it()
    {
        var ocr = new FakeOcr().ThenNothing().ThenNothing().Then(("Submit", 100, 200));
        var input = new FakeInput();
        var clock = new FakeClock();

        var result = await new PlaybackEngine(input, clock, ocr).RunAsync(MacroWith(
            new FindTextStep { Text = "submit", Action = FoundTextAction.Click, OffsetX = 5, TimeoutMs = 5000, CheckIntervalMs = 500 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, ocr.Reads);
        Assert.Equal(new[] { "move 135,210", "mouse Left down", "mouse Left up" }, input.Calls); // center (130,210) + offset 5
        // 2 waits of 500 ms + the found click's hold (35 ms, live-test fix) + the settle after the click.
        Assert.Equal(1000 + PlaybackEngine.FoundClickHoldMs + PlaybackEngine.ClickSettleMs, clock.TotalDelayed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Timeout_stops_the_macro_with_a_clear_error()
    {
        var ocr = new FakeOcr().ThenNothing();
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(MacroWith(
            new FindTextStep { Text = "Never", TimeoutMs = 2000, CheckIntervalMs = 500 },
            new MouseMoveStep { X = 1 }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("didn't appear within 2 s", result.Error);
        Assert.Contains("No text could be read", result.Error);
        Assert.Empty(input.Calls);
        Assert.Equal(5, ocr.Reads); // t = 0, 0.5, 1, 1.5, 2
    }

    [Fact]
    public async Task Timeout_can_continue_instead()
    {
        var ocr = new FakeOcr().ThenNothing();
        var input = new FakeInput();
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock(), ocr);
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(MacroWith(
            new FindTextStep { Text = "Never", TimeoutMs = 0, WhenNotFound = TextNotFoundBehavior.Continue },
            new MouseMoveStep { X = 1 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(1, ocr.Reads);
        Assert.Equal(new[] { "move 1,0" }, input.Calls);
        Assert.Contains(messages, m => m.Contains("Continuing"));
    }

    [Fact]
    public async Task Later_click_can_use_the_found_text()
    {
        var ocr = new FakeOcr().Then(("OK", 300, 400));
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(MacroWith(
            new FindTextStep { Text = "OK" },
            new MouseClickStep { UseLastFoundText = true, Button = MacroMouseButton.Right }));

        Assert.Equal(new[] { "move 310,410", "mouse Right down", "mouse Right up" }, input.Calls);
    }

    [Fact]
    public async Task Click_on_found_text_without_a_match_fails()
    {
        var result = await new PlaybackEngine(new FakeInput(), new FakeClock(), new FakeOcr()).RunAsync(MacroWith(
            new MouseClickStep { UseLastFoundText = true }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Missing_ocr_fails_with_explanation()
    {
        var result = await new PlaybackEngine(new FakeInput(), new FakeClock(), new FakeOcr { IsAvailable = false })
            .RunAsync(MacroWith(new FindTextStep { Text = "x" }));

        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.Contains("no OCR language", result.Error);
    }

    [Fact]
    public async Task Uses_the_configured_region()
    {
        var ocr = new FakeOcr().Then(("A", 10, 10));
        await new PlaybackEngine(new FakeInput(), new FakeClock(), ocr).RunAsync(MacroWith(
            new FindTextStep { Text = "A", SearchFullScreen = false, RegionX = 5, RegionY = 6, RegionWidth = 70, RegionHeight = 80 }));

        Assert.Equal(new ScreenRect(5, 6, 70, 80), Assert.Single(ocr.Regions));
    }

    [Fact]
    public async Task Stop_while_waiting_for_text_ends_quickly()
    {
        var ocr = new FakeOcr().ThenNothing();
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, clock: null, ocr); // real clock, so the wait really waits
        var macro = MacroWith(new FindTextStep { Text = "x", TimeoutMs = 3_600_000 });

        var run = engine.RunAsync(macro);
        await Task.Delay(50);
        engine.Stop();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Stopped, result.Outcome);
    }
}

public class FindTextValidationTests
{
    [Fact]
    public void Default_find_text_step_is_valid()
    {
        Assert.False(MacroValidator.ValidateStep(new FindTextStep { Text = "OK" }).HasErrors());
    }

    [Theory]
    [InlineData("", 85, 1000, 500, true)]
    [InlineData("OK", 49, 1000, 500, true)]
    [InlineData("OK", 100, 1000, 500, false)]
    [InlineData("OK", 85, -1, 500, true)]
    [InlineData("OK", 85, 0, 99, true)]
    public void Rules(string text, int similarity, int timeout, int interval, bool expectError)
    {
        var step = new FindTextStep { Text = text, MinSimilarityPercent = similarity, TimeoutMs = timeout, CheckIntervalMs = interval };
        Assert.Equal(expectError, MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public void Tiny_region_is_an_error()
    {
        var step = new FindTextStep { Text = "OK", SearchFullScreen = false, RegionWidth = 2, RegionHeight = 100 };
        Assert.True(MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public void Click_on_found_text_without_find_step_warns()
    {
        var macro = new Macro { Name = "x", Steps = { new MouseClickStep { UseLastFoundText = true } } };
        var issues = MacroValidator.Validate(macro);
        Assert.False(issues.HasErrors());
        Assert.Contains(issues, i => i.Property == nameof(MouseClickStep.UseLastFoundText));

        macro.Steps.Insert(0, new FindTextStep { Text = "OK" });
        Assert.DoesNotContain(MacroValidator.Validate(macro), i => i.Property == nameof(MouseClickStep.UseLastFoundText));
    }

    [Fact]
    public void Find_text_round_trips_through_json()
    {
        var step = new FindTextStep { Text = "Hi", SearchFullScreen = false, RegionX = 1, Action = FoundTextAction.RightClick, WhenNotFound = TextNotFoundBehavior.Continue };
        var copy = Assert.IsType<FindTextStep>(SmartMacro.Core.Storage.MacroJson.CloneStep(step));
        Assert.Equal("Hi", copy.Text);
        Assert.False(copy.SearchFullScreen);
        Assert.Equal(FoundTextAction.RightClick, copy.Action);
        Assert.Equal(TextNotFoundBehavior.Continue, copy.WhenNotFound);
    }
}
