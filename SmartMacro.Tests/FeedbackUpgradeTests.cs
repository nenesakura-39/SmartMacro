using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

public class FeedbackUpgradeTests
{
    private static OcrReadResult MakeBoard(params (string Text, int X, int Y)[] words) =>
        new(
            new ScreenRect(0, 0, 1600, 900),
            new[]
            {
                new OcrLineBox(words.Select(w => new OcrWordBox(w.Text, new ScreenRect(w.X, w.Y, 60, 24))).ToList()),
            });

    [Fact]
    public void FindText_prefers_exact_word_nice_over_nice_with_exclamation_and_look_alikes()
    {
        var board = MakeBoard(
            ("nlce", 20, 40),
            ("mice", 100, 40),
            ("niece", 180, 40),
            ("Nice", 260, 40),
            ("nice!", 340, 40),
            ("n1ce", 420, 40),
            ("rice", 500, 40),
            ("nice", 580, 120),
            ("nice!", 660, 120));

        var matches = OcrTextMatcher.Find(
            board,
            "nice",
            TextMatchMode.WholeWords,
            caseSensitive: true,
            minSimilarity: 0.90,
            normalizeLookAlikes: true,
            useRegexPattern: false,
            strictPunctuation: false);

        Assert.NotEmpty(matches);
        Assert.Equal("nice", matches[0].Text);
        Assert.Equal(580, matches[0].Bounds.X);

        // With strictPunctuation enabled, "nice!" and look-alike "n1ce"/"nlce" are completely excluded.
        var strictMatches = OcrTextMatcher.Find(
            board,
            "nice",
            TextMatchMode.WholeWords,
            caseSensitive: true,
            minSimilarity: 0.90,
            normalizeLookAlikes: true,
            useRegexPattern: false,
            strictPunctuation: true);

        var single = Assert.Single(strictMatches);
        Assert.Equal("nice", single.Text);
    }

    [Fact]
    public void FindText_prefers_real_coin_over_c01n_on_busy_background()
    {
        var board = MakeBoard(
            ("c01n", 40, 40),
            ("co1n", 140, 40),
            ("coin", 240, 80));

        var matches = OcrTextMatcher.Find(
            board,
            "coin",
            TextMatchMode.WholeWords,
            caseSensitive: true,
            minSimilarity: 0.85,
            normalizeLookAlikes: true);

        Assert.NotEmpty(matches);
        Assert.Equal("coin", matches[0].Text);
        Assert.Equal(240, matches[0].Bounds.X);
    }

    [Fact]
    public void OcrImagePreprocessor_BinarizeOtsu_and_UpscaleBilinear2x_produce_clean_output()
    {
        var pixels = new byte[40 * 20 * 4];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                byte v = (x is >= 10 and <= 30 && y is >= 5 and <= 15) ? (byte)240 : (byte)25;
                var idx = (y * 40 + x) * 4;
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = 255;
            }
        }
        var img = new BgraImage(40, 20, pixels);

        var upscaled = OcrImagePreprocessor.UpscaleBilinear2x(img);
        Assert.Equal(80, upscaled.Width);
        Assert.Equal(40, upscaled.Height);

        var binarized = OcrImagePreprocessor.Apply(upscaled, OcrPreprocessMode.BinarizeOtsu);
        Assert.Equal(80, binarized.Width);
        Assert.Equal(40, binarized.Height);
    }

    [Fact]
    public void FeedbackPresets_CastingHoldRelease_configures_hold_until_zone_and_release_to_finish()
    {
        var step = new FeedbackLoopStep();
        FeedbackPresets.Apply(step, FeedbackPreset.CastingHoldRelease);

        Assert.Equal(FeedbackPreset.CastingHoldRelease, step.Preset);
        Assert.Equal(FeedbackControlMode.ClassicSimpleHold, step.ControlMode);
        Assert.Equal(FeedbackAction.Hold, step.WhenBeforeZone);
        Assert.Equal(FeedbackAction.Release, step.WhenInsideZone);
        Assert.Equal(FeedbackAction.Hold, step.WhenAfterZone);
        Assert.True(step.FinishWhenInZone);
        Assert.Equal(0, step.DeadBandPercent);
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
    }

    [Fact]
    public async Task Expanded_FoundTextActions_execute_expected_inputs()
    {
        var ocr = new FakeOcr().Then(("Loot", 200, 300));
        var input = new FakeInput();

        var step = new FindTextStep
        {
            Text = "Loot",
            Action = FoundTextAction.ClickAndPressKey,
            FoundVirtualKey = 0x45, // E
            TimeoutMs = 0,
        };

        var macro = new Macro { Name = "ActionTest", Steps = { step } };
        var result = await new PlaybackEngine(input, new FakeClock(), ocr).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Contains("mouse Left down", input.Calls);
        Assert.Contains("mouse Left up", input.Calls);
        Assert.Contains("key 69 down", input.Calls);
        Assert.Contains("key 69 up", input.Calls);
    }

    [Fact]
    public void TargetResolver_FindLiveWindow_disambiguates_two_Roblox_windows_by_recorded_click_position()
    {
        var leftRoblox = new WindowInfo(101, "Roblox", new ScreenRect(0, 0, 960, 1080), "RobloxPlayerBeta");
        var rightRoblox = new WindowInfo(202, "Roblox", new ScreenRect(960, 0, 960, 1080), "RobloxPlayerBeta");
        var layout = new FakeLayout(new[] { leftRoblox, rightRoblox });

        var target = new TargetFingerprint
        {
            WindowTitle = "Roblox",
            WindowProcessName = "RobloxPlayerBeta",
            WindowWidth = 960,
            WindowHeight = 1080,
            WindowRelativeX = 388,
            WindowRelativeY = 605,
        };

        var matchedFirst = TargetResolver.FindLiveWindow(target, layout, preferredX: 535, preferredY: 718);
        var matchedSecond = TargetResolver.FindLiveWindow(target, layout, preferredX: 1348, preferredY: 605);

        Assert.NotNull(matchedFirst);
        Assert.NotNull(matchedSecond);
        Assert.Equal(101, matchedFirst!.Handle);
        Assert.Equal(202, matchedSecond!.Handle);
    }

    [Fact]
    public void RecordingConverter_rapid_spam_clicks_preserve_fixed_delays_and_disable_per_click_SmartIntent()
    {
        var fp = new TargetFingerprint { WindowTitle = "Roblox", WindowProcessName = "RobloxPlayerBeta", WindowWidth = 800, WindowHeight = 600, WindowRelativeX = 400, WindowRelativeY = 300 };
        var snap = ClickTargetSnapshot.FromIntentOnly(fp);

        var events = new[]
        {
            new RawInputEvent(0, RawInputKind.MouseDown, 500, 500, ClickedTarget: snap),
            new RawInputEvent(30, RawInputKind.MouseUp, 500, 500),
            new RawInputEvent(200, RawInputKind.MouseDown, 501, 500, ClickedTarget: snap),
            new RawInputEvent(230, RawInputKind.MouseUp, 501, 500),
            new RawInputEvent(410, RawInputKind.MouseDown, 500, 501, ClickedTarget: snap),
            new RawInputEvent(440, RawInputKind.MouseUp, 500, 501),
        };

        var steps = RecordingConverter.Convert(events, new RecordingOptions { SmartTiming = true });
        var delays = steps.OfType<DelayStep>().ToList();
        var clicks = steps.OfType<MouseClickStep>().ToList();

        Assert.Equal(3, clicks.Count);
        Assert.All(delays, d => Assert.Equal(DelayMode.Fixed, d.Mode));
        Assert.False(clicks[1].UseSmartIntent);
        Assert.False(clicks[2].UseSmartIntent);
    }

    [Fact]
    public void MacroFileFormat_reports_version_incompatibility_and_migrates_legacy_check_intervals()
    {
        var futureJson = """{ "schemaVersion": 99, "appVersion": "9.9.0", "macro": { "name": "Future", "steps": [ { "type": "unknownFutureStep" } ] } }""";
        Assert.False(MacroFileFormat.TryParseBundle(futureJson, out _, out _, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("newer version of SmartMacro", error);
        Assert.Contains("9.9.0", error);

        var legacyJson = """{ "schemaVersion": 4, "macro": { "name": "OldMacro", "steps": [ { "type": "findText", "text": "Ready", "checkIntervalMs": 5 } ] } }""";
        Assert.True(MacroFileFormat.TryParseBundle(legacyJson, out var migrated, out _, out var notes, out var legacyErr), legacyErr);
        Assert.NotNull(migrated);
        Assert.NotEmpty(notes);
        var findStep = Assert.IsType<FindTextStep>(Assert.Single(migrated!.Steps));
        Assert.Equal(MacroValidator.MinCheckIntervalMs, findStep.CheckIntervalMs);
    }

    private sealed class FakeLayout : IScreenLayout
    {
        private readonly IReadOnlyList<WindowInfo> _windows;
        public FakeLayout(IReadOnlyList<WindowInfo> windows) => _windows = windows;
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public IReadOnlyList<WindowInfo> GetWindows() => _windows;
    }
}
