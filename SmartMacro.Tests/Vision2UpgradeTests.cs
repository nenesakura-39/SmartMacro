using System.Diagnostics;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

public class Vision2UpgradeTests
{
    // ---------------- 1. Partial Visibility / Particle Occlusion ----------------

    [Fact]
    public void Partial_visibility_finds_target_when_35_percent_covered_by_particles()
    {
        var screen = TestImages.Blocky(360, 260, seed: 101);
        var icon = TestImages.Icon(48, 48, seed: 202);
        TestImages.Paste(screen, icon, 140, 90);

        // Cover ~35% of the pasted icon (bottom-right 4x4 cells) with bright magenta/white "particle sparks".
        screen.FillRectangle(new ScreenRect(140 + 24, 90 + 18, 24, 30), new RgbColor(255, 20, 240));
        screen.FillRectangle(new ScreenRect(140 + 28, 90 + 22, 16, 16), RgbColor.White);

        // Standard whole-template match at 0.88 fails because 35% of pixels are corrupted by particles.
        var standard = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions { MinScore = 0.88 });
        Assert.Null(standard.Best);

        // Vision 2.0 partial-visibility matcher (MinVisibleFraction = 0.60) scores the unoccluded sub-patches and locks on!
        var occlusionTolerant = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions
        {
            MinScore = 0.88,
            MinVisibleFraction = 0.60,
        });

        Assert.NotNull(occlusionTolerant.Best);
        Assert.InRange(occlusionTolerant.Best!.Bounds.X, 138, 142);
        Assert.InRange(occlusionTolerant.Best.Bounds.Y, 88, 92);
        Assert.True(occlusionTolerant.Best.VisibleFraction >= 0.60);
    }

    // ---------------- 2. Edge / Shape Matcher & Rotation ----------------

    [Fact]
    public void EdgeShape_detector_matches_when_colors_are_recolored_but_contours_remain()
    {
        var screen = TestImages.Solid(300, 220, new RgbColor(25, 30, 40));
        var template = TestImages.Solid(44, 44, new RgbColor(20, 20, 20));
        // Draw a distinctive hollow cross / diamond emblem in the template (gold on dark).
        template.FillRectangle(new ScreenRect(18, 6, 8, 32), new RgbColor(250, 210, 60));
        template.FillRectangle(new ScreenRect(6, 18, 32, 8), new RgbColor(250, 210, 60));

        // On screen, draw the exact same emblem at (110, 75), but tinted cyan-violet over a different background.
        var onScreenEmblem = TestImages.Solid(44, 44, new RgbColor(45, 25, 65));
        onScreenEmblem.FillRectangle(new ScreenRect(18, 6, 8, 32), new RgbColor(80, 240, 255));
        onScreenEmblem.FillRectangle(new ScreenRect(6, 18, 32, 8), new RgbColor(80, 240, 255));
        TestImages.Paste(screen, onScreenEmblem, 110, 75);

        var result = TemplateMatcher.Find(screen, template, new TemplateMatchOptions
        {
            MinScore = 0.75,
            Detector = ImageDetectorKind.EdgeShape,
        });

        Assert.NotNull(result.Best);
        Assert.InRange(result.Best!.Bounds.X, 108, 112);
        Assert.InRange(result.Best.Bounds.Y, 73, 77);
    }

    [Fact]
    public void Rotation_tolerance_finds_tilted_template()
    {
        var screen = TestImages.Solid(260, 200, new RgbColor(30, 30, 30));
        var icon = TestImages.Icon(36, 36, seed: 77, new RgbColor(30, 30, 30), new RgbColor(240, 220, 80), cell: 6);
        var rotatedOnScreen = EdgeShapeMatcher.Rotate(icon, 12);
        TestImages.Paste(screen, rotatedOnScreen, 100, 70);

        var result = TemplateMatcher.Find(screen, icon, new TemplateMatchOptions
        {
            MinScore = 0.72,
            IgnoreEdgeFraction = 0.15,
            RotationRangeDeg = 15,
        });

        Assert.NotNull(result.Best);
        Assert.InRange(result.Best!.Bounds.CenterX, 114, 122);
        Assert.InRange(result.Best.Bounds.CenterY, 84, 92);
    }

    // ---------------- 3. 2D Kalman Velocity Tracker & Predictive Lead Click ----------------

    [Fact]
    public void TargetTracker_estimates_velocity_and_computes_predictive_lead_click()
    {
        var tracker = new TargetTracker();
        var stepId = Guid.NewGuid();

        // Frame 1 at t = 100 ms: target at (100, 100), 40x40 (center 120, 120)
        tracker.RecordHit(stepId, new ScreenRect(100, 100, 40, 40), timestampMs: 100);
        // Frame 2 at t = 200 ms (+100 ms): target moved right by +50 px -> (150, 100) (velocity = +500 px/s)
        var state = tracker.RecordHit(stepId, new ScreenRect(150, 100, 40, 40), timestampMs: 200);

        Assert.True(state.VelocityX > 350, $"vx={state.VelocityX}");
        Assert.InRange(state.VelocityY, -10, 10);

        // Predict click with 60 ms lead: should lead ahead of current center (170) by ~22 px
        var virtualScreen = new ScreenRect(0, 0, 1920, 1080);
        var (clickX, clickY) = state.PredictClickPoint(170, 120, leadMs: 60, virtualScreen);
        Assert.True(clickX > 185, $"Expected predictive lead click > 185, got {clickX}");
        Assert.Equal(120, clickY);

        // Predicted search ROI at t = 300 ms should include (210, 120)
        var roi = tracker.PredictSearchArea(stepId, nowMs: 300, virtualScreen);
        Assert.True(roi.X <= 210 && roi.Right >= 210 && roi.Y <= 120 && roi.Bottom >= 120);
    }

    [Fact]
    public async Task Playback_with_TrackTarget_and_PredictiveClick_leads_moving_icon()
    {
        var icon = TestImages.Icon(32, 32, seed: 303);
        var frame1 = TestImages.Blocky(500, 300, seed: 1);
        var frame2 = TestImages.Blocky(500, 300, seed: 1);
        TestImages.Paste(frame1, icon, 100, 120);
        TestImages.Paste(frame2, icon, 160, 120);

        var capture = new FakeCapture(frame1, frame2);
        var step = new FindImageStep
        {
            TemplateWidth = icon.Width,
            TemplateHeight = icon.Height,
            TemplateData = TemplateCodec.Encode(icon),
            Scope = SearchScope.PrimaryMonitor,
            TrackTarget = true,
            PredictiveClick = true,
            PredictiveLeadMs = 80,
            Action = FoundTextAction.Click,
            TimeoutMs = 1000,
            CheckIntervalMs = 25,
        };

        var probes = new ScreenProbes(ocr: null, capture: capture, layout: null);
        var search = probes.Prepare(step);

        // First look: initializes track at (100, 120)
        var first = await search.LookAsync(null, CancellationToken.None);
        Assert.True(first.Found);

        await Task.Delay(35);

        // Second look: target moved +60 px right -> velocity is positive, so predictive click X > center (160 + 16 = 176)
        var second = await search.LookAsync(null, CancellationToken.None);
        Assert.True(second.Found);
        Assert.True(second.X > 176, $"Expected predictive X > 176, got {second.X}");
    }

    // ---------------- 4. Temporal N-Frame Confirmation (ConfirmFrames) ----------------

    [Fact]
    public async Task ConfirmFrames_ignores_single_frame_particle_flash_and_clicks_persistent_target()
    {
        var icon = TestImages.Icon(30, 30, seed: 404);
        var empty = TestImages.Blocky(300, 200, seed: 10);
        var withTarget = TestImages.Blocky(300, 200, seed: 10);
        TestImages.Paste(withTarget, icon, 120, 80);

        // Sequence: Frame 1 = 1-frame flash, Frame 2 = empty (resets confirm counter), Frame 3 & 4 = persistent target!
        var capture = new FakeCapture(withTarget, empty, withTarget, withTarget);
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: capture);

        var macro = new Macro { Name = "ConfirmFramesTest" };
        macro.Steps.Add(new FindImageStep
        {
            TemplateWidth = icon.Width,
            TemplateHeight = icon.Height,
            TemplateData = TemplateCodec.Encode(icon),
            Scope = SearchScope.PrimaryMonitor,
            ConfirmFrames = 2,
            CheckIntervalMs = 50,
            TimeoutMs = 1500,
            Action = FoundTextAction.Click,
        });

        var result = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        // Must have captured all 4 frames (rejected the 1-frame flash at frame 1, then confirmed across frames 3 & 4)
        Assert.Equal(4, capture.Captures);
        Assert.Contains("move 135,95", input.Calls);
    }

    // ---------------- 5. 1,000 Shuffling Words & Find-Once-Then-Track ----------------

    [Fact]
    public void OcrWordIndex_finds_word_nice_among_1000_shuffling_words_in_under_15ms()
    {
        var words = new List<OcrWordBox>(1000);
        var rng = new Random(42);
        string[] distractors =
        {
            "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
            "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango",
            "uniform", "victor", "whiskey", "xray", "yankee", "zulu", "spark", "flame", "shadow", "blade",
        };

        for (var i = 0; i < 999; i++)
        {
            var col = i % 40;
            var row = i / 40;
            var text = distractors[rng.Next(distractors.Length)] + (i % 7);
            words.Add(new OcrWordBox(text, new ScreenRect(col * 45, row * 24, 40, 18)));
        }

        // Put "nice!" at word #777
        var targetRect = new ScreenRect(777 % 40 * 45, 777 / 40 * 24, 40, 18);
        words.Insert(777, new OcrWordBox("nice!", targetRect));

        var frame = new OcrReadResult(new ScreenRect(0, 0, 1920, 1080), new[] { new OcrLineBox(words) });

        // Warmup
        _ = OcrTextMatcher.Find(frame, "nice", TextMatchMode.WholeWords, caseSensitive: false, minSimilarity: 0.90);

        var sw = Stopwatch.StartNew();
        var matches = OcrTextMatcher.Find(frame, "nice", TextMatchMode.WholeWords, caseSensitive: false, minSimilarity: 0.90);
        sw.Stop();

        var match = Assert.Single(matches);
        Assert.Equal(targetRect, match.Bounds);
        Assert.True(sw.Elapsed.TotalMilliseconds < 15.0, $"Expected instant lookup, took {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public void OcrTextMatcher_supports_regex_pattern_matching()
    {
        var words = new[]
        {
            new OcrWordBox("Player", new ScreenRect(10, 10, 50, 20)),
            new OcrWordBox("Lv.99", new ScreenRect(70, 10, 45, 20)),
            new OcrWordBox("Ready", new ScreenRect(130, 10, 50, 20)),
        };
        var frame = new OcrReadResult(new ScreenRect(0, 0, 400, 100), new[] { new OcrLineBox(words) });

        var matches = OcrTextMatcher.Find(
            frame,
            @"Lv\.\d+",
            TextMatchMode.Contains,
            caseSensitive: false,
            minSimilarity: 0.85,
            useRegexPattern: true);

        var match = Assert.Single(matches);
        Assert.Equal(new ScreenRect(70, 10, 45, 20), match.Bounds);
    }

    [Fact]
    public async Task TrackTextAsImage_finds_word_once_via_OCR_then_tracks_visual_crop_without_OCR()
    {
        // Draw a distinctive visual badge for the word "nice" on Frame 1 at (120, 80) and moved to (170, 95) on Frame 2.
        var wordPatch = TestImages.Icon(44, 22, seed: 555);
        var screen1 = TestImages.Blocky(400, 260, seed: 70);
        var screen2 = TestImages.Blocky(400, 260, seed: 70);
        TestImages.Paste(screen1, wordPatch, 120, 80);
        TestImages.Paste(screen2, wordPatch, 170, 95);

        var capture = new FakeCapture(screen1, screen2);
        var ocrCalls = 0;
        var ocr = new CountingOcrService(region =>
        {
            ocrCalls++;
            // Only return OCR text on the first call! On the second call, return empty to prove visual tracking found it!
            if (ocrCalls == 1)
            {
                var w = new OcrWordBox("nice", new ScreenRect(120, 80, wordPatch.Width, wordPatch.Height));
                return new OcrReadResult(region, new[] { new OcrLineBox(new[] { w }) });
            }

            return new OcrReadResult(region, Array.Empty<OcrLineBox>());
        });

        var step = new FindTextStep
        {
            Text = "nice",
            Scope = SearchScope.PrimaryMonitor,
            TrackTextAsImage = true,
            TrackTarget = true,
        };

        var probes = new ScreenProbes(ocr: ocr, capture: capture, layout: null);
        var search = probes.Prepare(step);

        // Look 1: OCR runs once, finds "nice" at (120, 80), and crops + stores the visual template
        var hit1 = await search.LookAsync(null, CancellationToken.None);
        Assert.True(hit1.Found);
        Assert.Equal(1, ocrCalls);
        Assert.Equal(120 + wordPatch.Width / 2, hit1.X);
        Assert.Equal(80 + wordPatch.Height / 2, hit1.Y);

        // Look 2: "nice" moved to (170, 95); visual tracker locks onto the moved word without calling OCR!
        var hit2 = await search.LookAsync(null, CancellationToken.None);
        Assert.True(hit2.Found, hit2.MissText);
        Assert.Equal(1, ocrCalls); // OCR was NOT called a second time!
        Assert.InRange(hit2.X, 170 + wordPatch.Width / 2 - 2, 170 + wordPatch.Width / 2 + 2);
        Assert.InRange(hit2.Y, 95 + wordPatch.Height / 2 - 2, 95 + wordPatch.Height / 2 + 2);
    }

    // ---------------- 6. Color 2.0: HSV, CIE Lab, Circularity & NearestToLastSpot ----------------

    [Fact]
    public void ColorFinder_filters_out_thin_particle_streaks_with_MinCircularity_and_supports_Hsv_and_Lab()
    {
        var image = TestImages.Solid(240, 160, RgbColor.Black);
        var targetColor = new RgbColor(240, 60, 40);

        // Draw a long thin particle streak (80x2 = 160 pixels, larger pixel count but very low circularity)
        image.FillRectangle(new ScreenRect(20, 20, 80, 2), targetColor);

        // Draw a compact round orb (radius 8 around (160, 90) ~ 190 pixels, high circularity & fill)
        for (var y = -8; y <= 8; y++)
        {
            for (var x = -8; x <= 8; x++)
            {
                if (x * x + y * y <= 64)
                    image.SetPixel(160 + x, 90 + y, new RgbColor(235, 65, 45));
            }
        }

        // Filter with MinCircularityPercent = 55 in LabDeltaE color space
        var labResult = ColorFinder.FindObjects(image, targetColor, null, tolerance: 25, new ColorSearchOptions
        {
            MinPixels = 20,
            ColorSpace = ColorSpaceKind.LabDeltaE,
            MinCircularityPercent = 55,
        });

        var orb = Assert.Single(labResult.Blobs);
        Assert.InRange(orb.Bounds.CenterX, 158, 162);
        Assert.InRange(orb.Bounds.CenterY, 88, 92);

        // Verify HSV mode also finds the orb
        var hsvResult = ColorFinder.FindObjects(image, targetColor, null, tolerance: 30, new ColorSearchOptions
        {
            MinPixels = 20,
            ColorSpace = ColorSpaceKind.Hsv,
            MinCircularityPercent = 55,
        });
        Assert.Single(hsvResult.Blobs);
    }

    // ---------------- 7. Scenario Presets, Auto-Tuner & Validation ----------------

    [Fact]
    public void TemplateAutoTuner_ApplyScenario_and_AnalyzeSamples_configure_optimal_settings()
    {
        var imgStep = new FindImageStep();
        TemplateAutoTuner.ApplyScenario(imgStep, VisionScenario.BusyParticles);

        Assert.Equal(VisionScenario.BusyParticles, imgStep.Scenario);
        Assert.Equal(60, imgStep.MinVisiblePercent);
        Assert.Equal(2, imgStep.ConfirmFrames);
        Assert.True(imgStep.TrackTarget);

        var textStep = new FindTextStep();
        TemplateAutoTuner.ApplyScenario(textStep, VisionScenario.ScrollingText);
        Assert.True(textStep.TrackTextAsImage);
        Assert.True(textStep.TrackTarget);

        var sample = TestImages.Icon(40, 40, seed: 99);
        var rec = TemplateAutoTuner.AnalyzeSamples(new[] { sample });
        Assert.InRange(rec.MinSimilarityPercent, 75, 95);
        Assert.False(string.IsNullOrWhiteSpace(rec.Summary));
    }

    [Fact]
    public void MacroValidator_validates_Vision2_properties()
    {
        var badImg = new FindImageStep
        {
            TemplateWidth = 20,
            TemplateHeight = 20,
            TemplateData = TemplateCodec.Encode(TestImages.Icon(20, 20)),
            MinVisiblePercent = 20, // invalid: must be 0 or 40..100
            RotationRangeDeg = 60,  // invalid: max 45
            ConfirmFrames = 15,     // invalid: max 10
        };

        var issues = MacroValidator.ValidateStep(badImg);
        Assert.Contains(issues, i => i.Property == nameof(FindImageStep.MinVisiblePercent));
        Assert.Contains(issues, i => i.Property == nameof(FindImageStep.RotationRangeDeg));
        Assert.Contains(issues, i => i.Property == nameof(ScreenSearchStep.ConfirmFrames));
    }

    private sealed class CountingOcrService : IOcrService
    {
        private readonly Func<ScreenRect, OcrReadResult> _read;
        public CountingOcrService(Func<ScreenRect, OcrReadResult> read) => _read = read;
        public bool IsAvailable => true;
        public string StatusText => "Fake OCR";
        public ScreenRect GetVirtualScreen() => new(0, 0, 1920, 1080);
        public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken) =>
            Task.FromResult(_read(region));
    }
}
