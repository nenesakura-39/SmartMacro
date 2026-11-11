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

    public FakeOcr ThenResult(OcrReadResult result)
    {
        _screens.Enqueue(result);
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
            // Updated on purpose (Find text bug-fix pass): new steps match exact case by default, so this case-insensitive
            // search ("submit" for "Submit") turns exact case off.
            new FindTextStep { Text = "submit", MatchCase = false, Action = FoundTextAction.Click, OffsetX = 5, TimeoutMs = 5000, CheckIntervalMs = 500 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, ocr.Reads);
        Assert.Equal(new[] { "move 135,210", "mouse Left down", "mouse Left up" }, input.Calls); // center (130,210) + offset 5
        // 2 waits of 500 ms + the TargetHoverMs rest on the target before the press (reliable game click) + the found click's hold
        // (35 ms, live-test fix) + the settle after the click.
        Assert.Equal(1000 + PlaybackEngine.TargetHoverMs + PlaybackEngine.FoundClickHoldMs + PlaybackEngine.ClickSettleMs,
            clock.TotalDelayed.TotalMilliseconds, precision: 3);
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
    public async Task Click_on_found_text_without_a_match_is_skipped()
    {
        // Changed on purpose (AAA audit fix 4): nothing found yet = the click is skipped with a warning, never sent to (0, 0).
        var input = new FakeInput();
        var result = await new PlaybackEngine(input, new FakeClock(), new FakeOcr()).RunAsync(MacroWith(
            new MouseClickStep { UseLastFoundText = true }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(input.Calls);
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
    [InlineData("OK", 85, 0, 9, true)]
    [InlineData("OK", 85, 0, 20, false)]
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

public class MultiMatchFindTextAndCountingTests
{
    /// <summary>
    /// Generates a dense 1,500-word screen (50 lines × 30 words per line) with "nice" (in mixed casing:
    /// "nice", "Nice", "NICE") placed at exact word indices, plus optional duplicate boxes along a tile seam.
    /// </summary>
    private static OcrReadResult Build1500WordScreenWithNice(IReadOnlyList<int> niceWordIndices, bool includeTileSeamDuplicates = false)
    {
        var niceSet = new HashSet<int>(niceWordIndices);
        var filler = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet" };
        var lines = new List<OcrLineBox>(50);
        var wordIdx = 0;

        for (var row = 0; row < 50; row++)
        {
            var y = 10 + row * 20;
            var words = new List<OcrWordBox>(32);
            for (var col = 0; col < 30; col++)
            {
                var x = 10 + col * 60;
                string text;
                if (niceSet.Contains(wordIdx))
                {
                    text = (wordIdx % 3) switch
                    {
                        0 => "nice",
                        1 => "Nice",
                        _ => "NICE",
                    };
                }
                else
                {
                    text = filler[wordIdx % filler.Length];
                }

                var rect = new ScreenRect(x, y, 40, 16);
                words.Add(new OcrWordBox(text, rect));

                // Simulate a 120 px tile overlap seam duplicate shifted by 1 px
                if (includeTileSeamDuplicates && niceSet.Contains(wordIdx) && col is 14 or 15)
                {
                    words.Add(new OcrWordBox(text, new ScreenRect(x + 1, y, 40, 16)));
                }

                wordIdx++;
            }

            lines.Add(new OcrLineBox(words));
        }

        var deduplicated = includeTileSeamDuplicates ? OcrWordDeduplicator.DeduplicateLines(lines) : lines;
        return new OcrReadResult(new ScreenRect(0, 0, 1920, 1080), deduplicated);
    }

    [Fact]
    public void Dense_1500_word_screen_detects_and_counts_all_occurrences_of_nice_with_tile_seam_deduplication()
    {
        // Place "nice" at 14 positions across the 1,500-word screen (including columns 14 & 15 where tile seams overlap)
        var targetIndices = new[] { 0, 14, 15, 75, 134, 299, 450, 614, 777, 915, 1044, 1200, 1364, 1499 };
        var screen = Build1500WordScreenWithNice(targetIndices, includeTileSeamDuplicates: true);

        Assert.Equal(1500, screen.TotalWordCount);

        var matches = OcrTextMatcher.Find(screen, "nice", TextMatchMode.WholeWords, caseSensitive: false, minSimilarity: 0.85);
        Assert.Equal(14, matches.Count);

        // Case-sensitive search for "Nice" (only indices where idx % 3 == 1: 1, 4, 7, 10, 13 -> here 75%3=0, 14%3=2, 134%3=2, 777%3=0, etc.)
        var expectedExactNice = targetIndices.Count(i => i % 3 == 1);
        var exactMatches = OcrTextMatcher.Find(screen, "Nice", TextMatchMode.WholeWords, caseSensitive: true, minSimilarity: 1.0);
        Assert.Equal(expectedExactNice, exactMatches.Count);
    }

    [Fact]
    public async Task Find_One_vs_Find_All_counts_occurrences_and_stores_in_variables()
    {
        var targetIndices = new[] { 5, 44, 120, 350, 780, 1100, 1450 }; // 7 occurrences of "nice" in 1,500 words
        var screen = Build1500WordScreenWithNice(targetIndices, includeTileSeamDuplicates: true);
        var ocr = new FakeOcr().ThenResult(screen);
        var input = new FakeInput();
        var clock = new FakeClock();
        var engine = new PlaybackEngine(input, clock, ocr);
        IReadOnlyList<VariableSnapshot> latestVars = Array.Empty<VariableSnapshot>();
        engine.VariablesChanged += (_, snap) => latestVars = snap;

        var macro = new Macro
        {
            Name = "Count Nice in 1500 Words",
            Steps =
            {
                new FindTextStep
                {
                    Text = "nice",
                    MatchCase = false,
                    TargetSelection = SearchMatchMode.AllMatches,
                    Action = FoundTextAction.None,
                    CountVariableName = "niceCount",
                },
                new SetVariableStep
                {
                    VariableName = "lastCount",
                    Source = VariableSourceKind.LastMatchCount,
                },
                new SetVariableStep
                {
                    VariableName = "screenWords",
                    Source = VariableSourceKind.LastOcrWordCount,
                },
            }
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal("7", latestVars.First(v => v.Name == "niceCount").Value);
        Assert.Equal("7", latestVars.First(v => v.Name == "lastCount").Value);
        Assert.Equal("1500", latestVars.First(v => v.Name == "screenWords").Value);
        Assert.Empty(input.Calls); // Action == None only counts/highlights without clicking
    }

    [Fact]
    public async Task Find_All_clicks_every_occurrence_in_configured_order_and_respects_MaxMatchesToActOn()
    {
        // 4 occurrences of "nice" at known positions:
        // #1 at (100, 100), #2 at (300, 100), #3 at (100, 300), #4 at (500, 300)
        var ocr = new FakeOcr().Then(
            ("nice", 100, 100),
            ("other", 200, 100),
            ("Nice", 300, 100),
            ("nice", 100, 300),
            ("NICE", 500, 300));
        var input = new FakeInput();
        var clock = new FakeClock();
        var engine = new PlaybackEngine(input, clock, ocr);
        IReadOnlyList<VariableSnapshot> latestVars = Array.Empty<VariableSnapshot>();
        engine.VariablesChanged += (_, snap) => latestVars = snap;

        // 1) ReadingOrder with MaxMatchesToActOn = 3
        var stepReading = new FindTextStep
        {
            Text = "nice",
            MatchCase = false,
            TargetSelection = SearchMatchMode.AllMatches,
            MatchOrder = MultiMatchOrder.ReadingOrder,
            MaxMatchesToActOn = 3,
            BetweenMatchesDelayMs = 50,
            Action = FoundTextAction.Click,
            CountVariableName = "totalFound",
        };

        var result = await engine.RunAsync(new Macro { Name = "Batch Click", Steps = { stepReading } });
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // Even though MaxMatchesToActOn capped actions at 3, totalFound still records all 4 matches on screen!
        Assert.Equal("4", latestVars.First(v => v.Name == "totalFound").Value);

        var moveCalls = input.Calls.Where(c => c.StartsWith("move ")).ToList();
        Assert.Equal(new[] { "move 120,110", "move 320,110", "move 120,310" }, moveCalls);

        // 2) BottomToTop order clicks bottom row before top row
        input.Calls.Clear();
        var stepBottomUp = new FindTextStep
        {
            Text = "nice",
            MatchCase = false,
            TargetSelection = SearchMatchMode.AllMatches,
            MatchOrder = MultiMatchOrder.BottomToTop,
            Action = FoundTextAction.Click,
        };
        await engine.RunAsync(new Macro { Name = "Bottom Up", Steps = { stepBottomUp } });
        var bottomUpMoves = input.Calls.Where(c => c.StartsWith("move ")).ToList();
        Assert.Equal(new[] { "move 520,310", "move 120,310", "move 320,110", "move 120,110" }, bottomUpMoves);
    }

    [Fact]
    public async Task ReadingOrderNth_and_MinMatchCount_gate_work_accurately()
    {
        var ocr = new FakeOcr().Then(
            ("nice", 100, 100),
            ("nice", 250, 100),
            ("nice", 400, 100));
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), ocr);

        // Pick 2nd match in reading order (center of (250, 100, 40, 20) is (270, 110))
        var pickSecond = new FindTextStep
        {
            Text = "nice",
            TargetSelection = SearchMatchMode.ReadingOrderNth,
            TargetMatchIndex = 2,
            MinMatchCount = 3,
            Action = FoundTextAction.Click,
        };
        var ok = await engine.RunAsync(new Macro { Name = "Nth", Steps = { pickSecond } });
        Assert.Equal(PlaybackOutcome.Completed, ok.Outcome);
        Assert.Contains("move 270,110", input.Calls);

        // Requiring MinMatchCount = 5 when only 3 exist should fail on timeout=0
        var requireFive = new FindTextStep
        {
            Text = "nice",
            MinMatchCount = 5,
            TimeoutMs = 0,
            WhenNotFound = TextNotFoundBehavior.StopMacro,
        };
        var fail = await engine.RunAsync(new Macro { Name = "Need 5", Steps = { requireFive } });
        Assert.Equal(PlaybackOutcome.Failed, fail.Outcome);
    }

    [Fact]
    public void Pipe_separated_multi_term_query_finds_all_variants_without_regex_flag()
    {
        var line = new OcrLineBox(new[]
        {
            new OcrWordBox("nice", new ScreenRect(10, 10, 40, 20)),
            new OcrWordBox("skip", new ScreenRect(60, 10, 40, 20)),
            new OcrWordBox("great", new ScreenRect(110, 10, 50, 20)),
            new OcrWordBox("awesome", new ScreenRect(170, 10, 70, 20)),
        });
        var screen = new OcrReadResult(new ScreenRect(0, 0, 500, 200), new[] { line });

        var matches = OcrTextMatcher.Find(screen, "nice | great | awesome", TextMatchMode.WholeWords, caseSensitive: false, minSimilarity: 0.9, useRegexPattern: false);
        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { "nice", "great", "awesome" }, matches.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task StepTester_reports_all_matches_and_total_words_on_1500_word_screen()
    {
        var targetIndices = new[] { 10, 20, 30, 40, 50 };
        var screen = Build1500WordScreenWithNice(targetIndices, includeTileSeamDuplicates: true);
        var ocr = new FakeOcr().ThenResult(screen);
        var probes = new ScreenProbes(ocr, null);

        var step = new FindTextStep
        {
            Text = "nice",
            MatchCase = false,
            TargetSelection = SearchMatchMode.AllMatches,
        };

        var testResult = await StepTester.TestAsync(probes, step);
        Assert.True(testResult.Passed);
        Assert.Equal(5, testResult.MatchCount);
        Assert.NotNull(testResult.AllMatches);
        Assert.Equal(5, testResult.AllMatches!.Count);
        Assert.Equal(1500, testResult.TotalWordsOnScreen);
        Assert.Contains("5 matches", testResult.Text);
        Assert.Contains("words", testResult.Text);
    }
}
