using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

internal sealed class FakeUiLocator : IUiElementLocator
{
    public bool IsAvailable { get; set; } = true;
    public UiElementSnapshot? SnapshotToReturn { get; set; }
    public UiElementMatch? MatchToReturn { get; set; }
    public int FindCalls { get; private set; }
    public ScreenRect? LastWindowBounds { get; private set; }
    public (int X, int Y)? LastExpectedPoint { get; private set; }

    public UiElementSnapshot? InspectPoint(int x, int y, CancellationToken cancellationToken = default) => SnapshotToReturn;

    public Task<UiElementMatch?> FindAsync(
        TargetFingerprint target,
        ScreenRect? windowBounds,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FindCalls++;
        LastWindowBounds = windowBounds;
        LastExpectedPoint = expectedPoint;
        return Task.FromResult(MatchToReturn);
    }
}

public class MultiScaleImageMatchingTests
{
    [Fact]
    public void Resize_preserves_dimensions_and_corner_colors()
    {
        var src = BgraImage.Create(20, 20);
        src.FillRectangle(new ScreenRect(0, 0, 10, 10), new RgbColor(220, 40, 40));
        src.FillRectangle(new ScreenRect(10, 0, 10, 10), new RgbColor(40, 220, 40));
        src.FillRectangle(new ScreenRect(0, 10, 10, 10), new RgbColor(40, 40, 220));
        src.FillRectangle(new ScreenRect(10, 10, 10, 10), new RgbColor(240, 220, 30));

        var up = src.Resize(30, 30);
        Assert.Equal((30, 30), (up.Width, up.Height));
        Assert.Equal(new RgbColor(220, 40, 40), up.GetPixel(2, 2));
        Assert.Equal(new RgbColor(40, 220, 40), up.GetPixel(27, 2));
        Assert.Equal(new RgbColor(40, 40, 220), up.GetPixel(2, 27));
        Assert.Equal(new RgbColor(240, 220, 30), up.GetPixel(27, 27));
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    public void Multi_scale_matcher_finds_icon_across_dpi_scales(double scale)
    {
        var template = TestImages.Icon(48, 48, seed: 77);
        var scaledW = (int)Math.Round(template.Width * scale);
        var scaledH = (int)Math.Round(template.Height * scale);
        var onScreenIcon = template.Resize(scaledW, scaledH);

        var screen = TestImages.Blocky(420, 320, seed: 91);
        TestImages.Paste(screen, onScreenIcon, 180, 110);

        var result = TemplateMatcher.Find(screen, template, new TemplateMatchOptions
        {
            MinScore = 0.80,
            AllowMultiScale = true,
        });

        Assert.NotNull(result.Best);
        var best = result.Best!;
        Assert.InRange(best.Bounds.CenterX, 180 + scaledW / 2 - 3, 180 + scaledW / 2 + 3);
        Assert.InRange(best.Bounds.CenterY, 110 + scaledH / 2 - 3, 110 + scaledH / 2 + 3);
        Assert.InRange(best.MatchedScale, scale - 0.12, scale + 0.12);
    }

    [Fact]
    public void Fixed_scale_misses_150_percent_icon_until_multi_scale_is_enabled()
    {
        var template = TestImages.Icon(48, 48, seed: 42);
        var screen = TestImages.Blocky(420, 320, seed: 19);
        TestImages.Paste(screen, template.Resize(72, 72), 160, 90);

        var fixedResult = TemplateMatcher.Find(screen, template, new TemplateMatchOptions
        {
            MinScore = 0.85,
            AllowMultiScale = false,
        });
        Assert.Null(fixedResult.Best);

        var multiResult = TemplateMatcher.Find(screen, template, new TemplateMatchOptions
        {
            MinScore = 0.82,
            AllowMultiScale = true,
        });
        Assert.NotNull(multiResult.Best);
    }

    [Fact]
    public async Task FindImageStep_with_AllowMultiScale_clicks_125_percent_scaled_button()
    {
        var template = TestImages.Icon(48, 48, seed: 105);
        var scaled = template.Resize(60, 60); // 125%
        var screen = TestImages.Blocky(400, 300, seed: 12);
        TestImages.Paste(screen, scaled, 210, 120);

        var step = Aaa.FindImage(template, s =>
        {
            s.AllowMultiScale = true;
            s.MinSimilarityPercent = 80;
            s.Action = FoundTextAction.Click;
        });

        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen));
        var res = await engine.RunAsync(Aaa.MacroWith(step));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        var moveCall = Assert.Single(input.Calls, c => c.StartsWith("move ", StringComparison.Ordinal));
        var coords = moveCall["move ".Length..].Split(',').Select(int.Parse).ToArray();
        Assert.InRange(coords[0], 240 - 3, 240 + 3);
        Assert.InRange(coords[1], 150 - 3, 150 + 3);
    }
}

public class IntentTargetResolverTests
{
    [Fact]
    public async Task Tier1_UiAutomation_wins_and_preserves_normalized_spot_inside_control()
    {
        var ui = new FakeUiLocator
        {
            // Button moved from (100, 100, 80×40) to (500, 300, 160×80).
            MatchToReturn = new UiElementMatch(new ScreenRect(500, 300, 160, 80), 0.98, "AutomationId=\"SaveBtn\""),
        };
        var step = new MouseClickStep
        {
            X = 120,
            Y = 130,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Save",
                AutomationId = "SaveBtn",
                ControlType = "Button",
                ElementNormalizedX = 0.25, // 25% from left -> 500 + 40 = 540
                ElementNormalizedY = 0.75, // 75% from top  -> 300 + 60 = 360
            },
        };

        var resolver = new TargetResolver(ui);
        var res = await resolver.ResolveAsync(step);

        Assert.Equal(LocatorStrategyKind.UiAutomation, res.WinningStrategy);
        // Normalized 0..1 maps onto [0..Width-1] / [0..Height-1] so 1.0 stays strictly inside the control bounds.
        Assert.Equal((540, 359), (res.X, res.Y));
        Assert.Single(res.Diagnostics); // short-circuited right after Tier 1 succeeded!
    }

    [Fact]
    public async Task Tier2_OcrText_relocates_when_UIA_misses()
    {
        var ui = new FakeUiLocator { MatchToReturn = null };
        var ocr = new FakeOcr().Then(("Submit", 600, 410)); // Width = 6*10 = 60, Height = 20 -> center (630, 420)
        var layout = new FakeLayout();
        layout.Windows.Add(new WindowInfo(101, "Checkout", new ScreenRect(400, 250, 500, 400), "chrome"));

        var step = new MouseClickStep
        {
            X = 150,
            Y = 120,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Submit",
                NearbyText = "Submit",
                NearbyTextOffsetX = 10, // click 10 px right of text center -> 640
                NearbyTextOffsetY = -5, // click 5 px above text center -> 415
                WindowTitle = "Checkout",
                WindowProcessName = "chrome",
                WindowWidth = 500,
                WindowHeight = 400,
                WindowRelativeX = 100,
                WindowRelativeY = 80,
            },
        };

        var resolver = new TargetResolver(ui, ocr, capture: null, layout);
        var res = await resolver.ResolveAsync(step);

        Assert.Equal(LocatorStrategyKind.OcrText, res.WinningStrategy);
        Assert.Equal((640, 415), (res.X, res.Y));
    }

    [Fact]
    public async Task Tier3_ImageSnapshot_relocates_across_moved_and_rescaled_window()
    {
        var recordedScreen = Aaa.Screen(iconX: 80, iconY: 70);
        var step = Aaa.RecordedClick(recordedScreen, 108, 98);
        step.UseSmartIntent = true;
        step.Target = new TargetFingerprint
        {
            WindowTitle = "Shop Window",
            WindowProcessName = "gameclient",
            WindowWidth = 400,
            WindowHeight = 300,
            WindowRelativeX = 108,
            WindowRelativeY = 98,
        };

        // Now the window moved to (300, 200) and the button scaled to 125% (70×70)!
        var liveScreen = TestImages.Blocky(900, 700, seed: 55);
        var scaledButton = Aaa.Button.Resize(70, 70);
        TestImages.Paste(liveScreen, scaledButton, 420, 310); // center at (455, 345)

        var layout = new FakeLayout();
        layout.Windows.Add(new WindowInfo(202, "Shop Window", new ScreenRect(300, 200, 500, 375), "gameclient"));

        var resolver = new TargetResolver(uiLocator: null, ocr: null, new FakeCapture(liveScreen), layout);
        var res = await resolver.ResolveAsync(step);

        Assert.Equal(LocatorStrategyKind.ImageSnapshot, res.WinningStrategy);
        Assert.InRange(res.X, 455 - 4, 455 + 4);
        Assert.InRange(res.Y, 345 - 4, 345 + 4);
    }

    [Fact]
    public async Task Tier4_WindowRelative_relocates_even_when_document_title_changed_via_process_name()
    {
        var layout = new FakeLayout();
        // Recorded in "Untitled - Notepad" at (0, 0, 800×600), click at (200, 150) => 25%, 25%.
        // Now the window is "notes.txt - Notepad" at (400, 100, 1200×800) => 25%, 25% is (400 + 300, 100 + 200) = (700, 300)!
        layout.Windows.Add(new WindowInfo(303, "notes.txt - Notepad", new ScreenRect(400, 100, 1200, 800), "notepad"));

        var step = new MouseClickStep
        {
            X = 200,
            Y = 150,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                WindowTitle = "Untitled - Notepad",
                WindowProcessName = "notepad",
                WindowWidth = 800,
                WindowHeight = 600,
                WindowRelativeX = 200,
                WindowRelativeY = 150,
            },
        };

        var resolver = new TargetResolver(uiLocator: null, ocr: null, capture: null, layout);
        var res = await resolver.ResolveAsync(step);

        Assert.Equal(LocatorStrategyKind.WindowRelative, res.WinningStrategy);
        Assert.Equal((700, 300), (res.X, res.Y));
    }

    [Fact]
    public async Task Tier5_Coordinates_falls_back_cleanly_when_window_is_closed()
    {
        var layout = new FakeLayout(); // empty: target window is closed
        var step = new MouseClickStep
        {
            X = 333,
            Y = 222,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "OK",
                WindowTitle = "Dialog",
                WindowProcessName = "app",
                WindowWidth = 400,
                WindowHeight = 300,
                WindowRelativeX = 200,
                WindowRelativeY = 250,
            },
        };

        var resolver = new TargetResolver(new FakeUiLocator(), ocr: null, capture: null, layout);
        var res = await resolver.ResolveAsync(step);

        Assert.Equal(LocatorStrategyKind.Coordinates, res.WinningStrategy);
        Assert.Equal((333, 222), (res.X, res.Y));
    }

    [Fact]
    public async Task DiagnoseAllAsync_evaluates_all_five_tiers_for_Inspector_LocateTarget()
    {
        var ui = new FakeUiLocator
        {
            MatchToReturn = new UiElementMatch(new ScreenRect(500, 300, 80, 30), 0.96, "Name=\"Apply\""),
        };
        var layout = new FakeLayout();
        layout.Windows.Add(new WindowInfo(1, "Settings", new ScreenRect(400, 200, 600, 400), "app"));

        var step = new MouseClickStep
        {
            X = 120,
            Y = 140,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Apply",
                WindowTitle = "Settings",
                WindowProcessName = "app",
                WindowWidth = 600,
                WindowHeight = 400,
                WindowRelativeX = 140,
                WindowRelativeY = 115,
            },
        };

        var resolver = new TargetResolver(ui, ocr: null, capture: null, layout);
        var res = await resolver.DiagnoseAllAsync(step);

        Assert.Equal(LocatorStrategyKind.UiAutomation, res.WinningStrategy);
        Assert.Equal(5, res.Diagnostics.Count);
        Assert.Equal(
            new[]
            {
                LocatorStrategyKind.UiAutomation,
                LocatorStrategyKind.OcrText,
                LocatorStrategyKind.ImageSnapshot,
                LocatorStrategyKind.WindowRelative,
                LocatorStrategyKind.Coordinates,
            },
            res.Diagnostics.Select(d => d.Strategy).ToArray());
        Assert.True(res.Diagnostics[0].Succeeded);  // UIA matched
        Assert.False(res.Diagnostics[1].Succeeded); // No OCR text
        Assert.False(res.Diagnostics[2].Succeeded); // No PNG thumbnail
        Assert.True(res.Diagnostics[3].Succeeded);  // Window-Relative also matched!
        Assert.True(res.Diagnostics[4].Succeeded);  // Coordinates always available
    }

    [Fact]
    public async Task PlaybackEngine_clicks_SmartIntent_relocated_element_and_raises_TargetDetected()
    {
        var ui = new FakeUiLocator
        {
            MatchToReturn = new UiElementMatch(new ScreenRect(700, 500, 100, 40), 0.99, "AutomationId=\"ConfirmBtn\""),
        };
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), uiLocator: ui);
        TargetDetection? detected = null;
        engine.TargetDetected += (_, d) => detected = d;

        var click = new MouseClickStep
        {
            X = 100,
            Y = 100,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Confirm",
                AutomationId = "ConfirmBtn",
                ControlType = "Button",
                ElementNormalizedX = 0.5,
                ElementNormalizedY = 0.5,
            },
        };

        var res = await engine.RunAsync(Aaa.MacroWith(click));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Contains("move 750,520", input.Calls);
        Assert.NotNull(detected);
        Assert.Equal((750, 520), (detected!.X, detected.Y));
    }

    [Fact]
    public void ExpandInputSequence_preserves_intent_only_TargetFingerprint_on_expanded_clicks()
    {
        var seq = new InputSequenceStep
        {
            Events = new List<InputEvent>
            {
                InputEvent.ButtonDown(0, MacroMouseButton.Left, 250, 180),
                InputEvent.ButtonUp(30_000, MacroMouseButton.Left, 250, 180),
            },
            MouseSegments = new List<MouseSegment>
            {
                new() { StartUs = 0, Detected = MouseMovementMode.CursorPosition },
            },
            ClickTargets = new List<SequenceClickTarget>
            {
                new()
                {
                    EventIndex = 0,
                    Target = new TargetFingerprint
                    {
                        ElementName = "Next",
                        ControlType = "Button",
                        WindowTitle = "Installer",
                        WindowWidth = 600,
                        WindowHeight = 400,
                        WindowRelativeX = 250,
                        WindowRelativeY = 180,
                    },
                },
            },
        };

        Assert.Empty(MacroValidator.Validate(Aaa.MacroWith(seq)));
        var expanded = StepCleanup.ExpandInputSequence(seq);
        var click = Assert.Single(expanded.OfType<MouseClickStep>());
        Assert.True(click.UseSmartIntent);
        Assert.Equal("Next", click.Target?.ElementName);
        Assert.Equal("Installer", click.Target?.WindowTitle);
    }
}

public class AdaptiveTimingTests
{
    [Fact]
    public void RecordingConverter_turns_post_click_pause_into_UntilScreenSettles_when_SmartTiming_is_on()
    {
        var raw = new List<RawInputEvent>
        {
            new(0, RawInputKind.MouseDown, X: 200, Y: 150, Button: MacroMouseButton.Left),
            new(35, RawInputKind.MouseUp, X: 200, Y: 150, Button: MacroMouseButton.Left),
            // 800 ms pause waiting for UI after click -> should become UntilScreenSettles!
            new(835, RawInputKind.KeyDown, VirtualKey: 0x0D),
            new(870, RawInputKind.KeyUp, VirtualKey: 0x0D),
            // 600 ms pause after key press -> stays Fixed!
            new(1470, RawInputKind.MouseDown, X: 300, Y: 220, Button: MacroMouseButton.Left),
            new(1500, RawInputKind.MouseUp, X: 300, Y: 220, Button: MacroMouseButton.Left),
        };

        var smartSteps = RecordingConverter.Convert(raw, new RecordingOptions
        {
            Fidelity = RecordingFidelity.Exact1To1,
            SmartTiming = true,
        });

        var delays = smartSteps.OfType<DelayStep>().ToList();
        Assert.Equal(2, delays.Count);
        Assert.Equal(DelayMode.UntilScreenSettles, delays[0].Mode);
        Assert.Equal(800, delays[0].DurationMs);
        Assert.InRange(delays[0].MinDurationMs, 60, 250);
        Assert.Equal(2400, delays[0].MaxDurationMs);
        Assert.Equal(DelayMode.Fixed, delays[1].Mode);

        var fixedSteps = RecordingConverter.Convert(raw, new RecordingOptions
        {
            Fidelity = RecordingFidelity.Exact1To1,
            SmartTiming = false,
        });
        Assert.All(fixedSteps.OfType<DelayStep>(), d => Assert.Equal(DelayMode.Fixed, d.Mode));
    }

    [Fact]
    public async Task UntilScreenSettles_finishes_early_when_screen_is_already_calm()
    {
        var calmFrame = TestImages.Blocky(500, 400, seed: 7);
        var clock = new FakeClock();
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: new FakeCapture(calmFrame));

        var res = await engine.RunAsync(Aaa.MacroWith(
            new MouseClickStep { X = 250, Y = 200 },
            new DelayStep
            {
                DurationMs = 2000,
                Mode = DelayMode.UntilScreenSettles,
                MinDurationMs = 100,
                MaxDurationMs = 5000,
                QuietWindowMs = 120,
            }));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        // Should finish right after MinDurationMs (100) + QuietWindowMs (~120..160), nowhere near 2000 ms!
        Assert.InRange(clock.TotalDelayed.TotalMilliseconds, 220, 450);
    }

    [Fact]
    public async Task UntilScreenSettles_keeps_waiting_while_screen_animates_then_finishes_once_calm()
    {
        // 5 distinct changing frames (t = 100, 140, 180, 220, 260), then frame6 repeats calmly forever!
        var frames = Enumerable.Range(1, 6).Select(seed => TestImages.Blocky(500, 400, seed * 13)).ToArray();
        var clock = new FakeClock();
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: new FakeCapture(frames));

        var res = await engine.RunAsync(Aaa.MacroWith(
            new MouseClickStep { X = 250, Y = 200 },
            new DelayStep
            {
                DurationMs = 400,
                Mode = DelayMode.UntilScreenSettles,
                MinDurationMs = 100,
                MaxDurationMs = 3000,
                QuietWindowMs = 120,
            }));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        // Must wait past the 5 changing frames (> 350 ms) and finish once frame6 settles (< 700 ms).
        Assert.InRange(clock.TotalDelayed.TotalMilliseconds, 360, 700);
    }

    [Fact]
    public async Task UntilScreenChanges_wakes_immediately_when_frame_changes()
    {
        var before = TestImages.Blocky(500, 400, seed: 3);
        var after = TestImages.Blocky(500, 400, seed: 99);
        // Baseline = before, 1st poll = before, 2nd poll = after -> wakes at ~MinDurationMs + 80 ms!
        var clock = new FakeClock();
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: new FakeCapture(before, before, after));

        var res = await engine.RunAsync(Aaa.MacroWith(
            new MouseClickStep { X = 250, Y = 200 },
            new DelayStep
            {
                DurationMs = 1500,
                Mode = DelayMode.UntilScreenChanges,
                MinDurationMs = 50,
                MaxDurationMs = 4000,
            }));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.InRange(clock.TotalDelayed.TotalMilliseconds, 90, 220);
    }

    [Fact]
    public async Task Adaptive_delay_falls_back_to_DurationMs_when_capture_is_unavailable()
    {
        var clock = new FakeClock();
        var engine = new PlaybackEngine(new FakeInput(), clock, capture: null);

        var res = await engine.RunAsync(Aaa.MacroWith(new DelayStep
        {
            DurationMs = 650,
            Mode = DelayMode.UntilScreenSettles,
            MinDurationMs = 100,
            MaxDurationMs = 2000,
        }));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Equal(650, clock.TotalDelayed.TotalMilliseconds);
    }
}

public class SchemaVersion15AndValidationTests
{
    [Fact]
    public void Schema_version_is_15_only_when_v1_1_features_are_used()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 15); // 16 since v1.2 Brain

        var simple = Aaa.MacroWith(new DelayStep { DurationMs = 250 }, new MouseClickStep { X = 10, Y = 20 });
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(simple));

        var pngOnlyClick = Aaa.RecordedClick(Aaa.Screen(100, 100), 128, 128);
        Assert.False(pngOnlyClick.UseSmartIntent);
        Assert.Equal(12, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(pngOnlyClick)));

        var multiScale = Aaa.FindImage(Aaa.Button, s => s.AllowMultiScale = true);
        Assert.Equal(15, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(multiScale)));

        var adaptiveDelay = new DelayStep { DurationMs = 500, Mode = DelayMode.UntilScreenSettles, MinDurationMs = 100, MaxDurationMs = 1500 };
        Assert.Equal(15, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(adaptiveDelay)));

        var smartIntentClick = new MouseClickStep
        {
            X = 50,
            Y = 60,
            UseSmartIntent = true,
            Target = new TargetFingerprint { ElementName = "Play", ControlType = "Button" },
        };
        Assert.Equal(15, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(smartIntentClick)));
    }

    [Fact]
    public void Json_round_trip_preserves_TargetFingerprint_AllowMultiScale_and_AdaptiveDelay()
    {
        var original = Aaa.MacroWith(
            new MouseClickStep
            {
                X = 320,
                Y = 240,
                UseSmartIntent = true,
                Target = new TargetFingerprint
                {
                    ElementName = "Sign in",
                    AutomationId = "login-submit",
                    ControlType = "Button",
                    ClassName = "Button",
                    ElementNormalizedX = 0.4,
                    ElementNormalizedY = 0.6,
                    NearbyText = "Sign in",
                    NearbyTextOffsetX = 4,
                    NearbyTextOffsetY = -2,
                    WindowTitle = "Portal",
                    WindowProcessName = "msedge",
                    WindowWidth = 1280,
                    WindowHeight = 720,
                    WindowRelativeX = 320,
                    WindowRelativeY = 240,
                },
            },
            new DelayStep
            {
                DurationMs = 450,
                Mode = DelayMode.UntilScreenSettles,
                MinDurationMs = 110,
                MaxDurationMs = 1350,
                QuietWindowMs = 140,
            },
            Aaa.FindImage(Aaa.Button, s => s.AllowMultiScale = true));

        var doc = new MacroDocument
        {
            SchemaVersion = MacroDocument.RequiredSchemaVersion(original),
            Macro = original,
        };
        Assert.Equal(15, doc.SchemaVersion);

        var json = MacroJson.Serialize(doc);
        var loaded = MacroJson.Deserialize<MacroDocument>(json)!;
        Assert.NotNull(loaded.Macro);
        Assert.Empty(MacroValidator.Validate(loaded.Macro!));

        var click = (MouseClickStep)loaded.Macro!.Steps[0];
        Assert.True(click.UseSmartIntent);
        Assert.Equal("login-submit", click.Target?.AutomationId);
        Assert.Equal("msedge", click.Target?.WindowProcessName);
        Assert.Equal(0.4, click.Target?.ElementNormalizedX);

        var delay = (DelayStep)loaded.Macro.Steps[1];
        Assert.Equal(DelayMode.UntilScreenSettles, delay.Mode);
        Assert.Equal(110, delay.MinDurationMs);
        Assert.Equal(1350, delay.MaxDurationMs);
        Assert.Equal(140, delay.QuietWindowMs);

        var find = (FindImageStep)loaded.Macro.Steps[2];
        Assert.True(find.AllowMultiScale);
    }

    [Fact]
    public void Validator_catches_invalid_adaptive_delay_and_invalid_fingerprint_bounds()
    {
        var badDelay = Aaa.MacroWith(new DelayStep
        {
            DurationMs = 300,
            Mode = DelayMode.UntilScreenSettles,
            MinDurationMs = 1000,
            MaxDurationMs = 500, // Min > Max!
        });
        Assert.Contains(MacroValidator.Validate(badDelay), i => i.Severity == IssueSeverity.Error);

        var badFingerprint = Aaa.MacroWith(new MouseClickStep
        {
            X = 100,
            Y = 100,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Save",
                ElementNormalizedX = 1.5, // outside 0..1!
            },
        });
        Assert.Contains(MacroValidator.Validate(badFingerprint), i => i.Severity == IssueSeverity.Error);
    }
}
