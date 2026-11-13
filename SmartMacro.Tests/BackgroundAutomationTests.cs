using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

public sealed class BackgroundAutomationTests
{
    private sealed class InstantClock : IPlaybackClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingInputSimulator : IInputSimulator
    {
        public List<(string Title, MacroMouseButton Button, MouseClickAction Action, int X, int Y, int HoldMs)> BackgroundClicks { get; } = new();
        public List<(string Title, string Text, int DelayMs)> BackgroundTexts { get; } = new();
        public bool ReturnFound { get; set; } = true;

        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void MouseButton(MacroMouseButton button, bool down) { }
        public void Scroll(int delta, bool horizontal) { }
        public void Key(int virtualKey, bool down) { }
        public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) { }
        public void TypeCharacters(string text) { }

        public bool SendBackgroundMouseClick(string windowTitle, MacroMouseButton button, MouseClickAction action, int screenX, int screenY, int holdMs)
        {
            BackgroundClicks.Add((windowTitle, button, action, screenX, screenY, holdMs));
            return ReturnFound;
        }

        public bool SendBackgroundText(string windowTitle, string text, int characterDelayMs)
        {
            BackgroundTexts.Add((windowTitle, text, characterDelayMs));
            return ReturnFound;
        }
    }

    private sealed class FakeBackgroundUiLocator : IUiElementLocator
    {
        public bool IsAvailable { get; set; } = true;
        public UiBackgroundActionResult ClickResult { get; set; } = UiBackgroundActionResult.NotSupported;
        public UiBackgroundActionResult SetValueResult { get; set; } = UiBackgroundActionResult.NotSupported;
        public List<(string WindowTitle, TargetFingerprint? Target, (int X, int Y)? Expected)> ClickCalls { get; } = new();
        public List<(string WindowTitle, string Text)> SetValueCalls { get; } = new();

        public UiElementSnapshot? InspectPoint(int x, int y, CancellationToken cancellationToken = default) => null;

        public Task<UiElementMatch?> FindAsync(TargetFingerprint target, ScreenRect? windowBounds, (int X, int Y)? expectedPoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<UiElementMatch?>(null);

        public Task<UiBackgroundActionResult> TryBackgroundClickAsync(string windowTitle, TargetFingerprint? target, (int X, int Y)? expectedPoint, CancellationToken cancellationToken = default)
        {
            ClickCalls.Add((windowTitle, target, expectedPoint));
            return Task.FromResult(ClickResult);
        }

        public Task<UiBackgroundActionResult> TryBackgroundSetValueAsync(string windowTitle, string text, CancellationToken cancellationToken = default)
        {
            SetValueCalls.Add((windowTitle, text));
            return Task.FromResult(SetValueResult);
        }
    }

    private sealed class FakeWindowLayout(params WindowInfo[] windows) : IScreenLayout
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [new MonitorInfo(1, new ScreenRect(0, 0, 1920, 1080), true, "Primary")];
        public IReadOnlyList<WindowInfo> GetWindows() => windows;
    }

    private sealed class FakeWindowOcrService : IOcrService
    {
        public bool IsAvailable => true;
        public string StatusText => "English (Fake)";
        public List<long> WindowCaptureHandles { get; } = new();
        public List<ScreenRect> ScreenCaptureCalls { get; } = new();
        public Func<ScreenRect, OcrReadResult>? ResultFactory { get; set; }

        public ScreenRect GetVirtualScreen() => new(0, 0, 1920, 1080);

        public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken)
        {
            ScreenCaptureCalls.Add(region);
            return Task.FromResult(ResultFactory?.Invoke(region) ?? new OcrReadResult(region, []));
        }

        public Task<OcrReadResult> ReadWindowAsync(long windowHandle, ScreenRect region, CancellationToken cancellationToken)
        {
            WindowCaptureHandles.Add(windowHandle);
            return Task.FromResult(ResultFactory?.Invoke(region) ?? new OcrReadResult(region, []));
        }
    }

    private sealed class FakeWindowCapture : IScreenCapture
    {
        private readonly BgraImage _image;

        public FakeWindowCapture(BgraImage image) => _image = image;

        public List<long> WindowCaptureHandles { get; } = new();
        public List<ScreenRect> ScreenCaptureCalls { get; } = new();

        public ScreenRect GetVirtualScreen() => new(0, 0, 1920, 1080);

        public Task<ScreenFrame> CaptureAsync(ScreenRect area, CancellationToken cancellationToken)
        {
            ScreenCaptureCalls.Add(area);
            return Task.FromResult(CreateFrame(area));
        }

        public Task<ScreenFrame> CaptureWindowAsync(long windowHandle, ScreenRect area, CancellationToken cancellationToken)
        {
            WindowCaptureHandles.Add(windowHandle);
            return Task.FromResult(CreateFrame(area));
        }

        private ScreenFrame CreateFrame(ScreenRect area)
        {
            var clamped = area.Intersect(new ScreenRect(0, 0, _image.Width, _image.Height));
            if (clamped.IsEmpty)
                return new ScreenFrame(area, new BgraImage(area.Width, area.Height, new byte[area.Width * area.Height * 4]));
            return new ScreenFrame(clamped, _image.Crop(clamped));
        }
    }

    [Fact]
    public void HeldInputTracker_TracksAndReleasesBackgroundInputsInSafeOrder()
    {
        var tracker = new HeldInputTracker();
        Assert.True(tracker.IsEmpty);

        nint hwnd1 = 0x1001;
        nint hwnd2 = 0x2002;
        const int vkShift = 0x10;
        const int vkRight = 0x27;
        tracker.NoteBackgroundButton(hwnd1, MacroMouseButton.Left, 42, 84, down: true);
        tracker.NoteBackgroundKey(hwnd1, vkShift, scanCode: 0x2A, extended: false, down: true);
        tracker.NoteBackgroundKey(hwnd2, vkRight, scanCode: 0x4D, extended: true, down: true);
        Assert.False(tracker.IsEmpty);
        Assert.Single(tracker.HeldBackgroundButtons);
        Assert.Equal(2, tracker.HeldBackgroundKeys.Count);

        var releasedButtons = new List<TrackedBackgroundMouseButton>();
        var releasedKeys = new List<TrackedBackgroundKey>();

        var error = tracker.ReleaseAll(
            _ => { },
            _ => { },
            b => releasedButtons.Add(b),
            k =>
            {
                releasedKeys.Add(k);
                if (k.WindowHandle == hwnd2) throw new InvalidOperationException("Window closed");
            });

        Assert.NotNull(error);
        Assert.Contains("Window closed", error);
        Assert.Single(releasedButtons);
        Assert.Equal(new TrackedBackgroundMouseButton(hwnd1, MacroMouseButton.Left, 42, 84), releasedButtons[0]);
        // Keys release in reverse order, and continue releasing earlier keys even if one fails.
        Assert.Equal(2, releasedKeys.Count);
        Assert.Equal(hwnd2, releasedKeys[0].WindowHandle);
        Assert.True(releasedKeys[0].Extended);
        Assert.Equal(vkShift, releasedKeys[1].VirtualKey);
        Assert.True(tracker.IsEmpty);
    }

    [Fact]
    public void KeyboardInputEncoder_BuildsWin32MessageLParamAndSplitsSurrogatePairs()
    {
        var downExtended = KeyboardInputEncoder.BuildMessageLParam(scanCode: 0x4D, extended: true, down: true);
        Assert.Equal(1, downExtended & 0xFFFF);
        Assert.Equal(0x4D, (downExtended >> 16) & 0xFF);
        Assert.NotEqual(0, downExtended & (1 << 24));
        Assert.Equal(0, downExtended & unchecked((int)0xC0000000));

        var upNormal = KeyboardInputEncoder.BuildMessageLParam(scanCode: 0x1E, extended: false, down: false);
        Assert.Equal(0x1E, (upNormal >> 16) & 0xFF);
        Assert.Equal(0, upNormal & (1 << 24));
        Assert.Equal(unchecked((int)0xC0000000), upNormal & unchecked((int)0xC0000000));

        var tokens = KeyboardInputEncoder.SplitBackgroundText("A😀\r\nB🎉");
        Assert.Equal(5, tokens.Count);
        Assert.False(tokens[0].IsNewline);
        Assert.Equal('A', tokens[0].Char1);

        Assert.True(tokens[1].IsSurrogatePair);
        Assert.Equal(0x1F600, tokens[1].Utf32CodePoint);
        Assert.Equal("\U0001F600"[0], tokens[1].Char1);
        Assert.Equal("\U0001F600"[1], tokens[1].Char2);

        Assert.True(tokens[2].IsNewline);
        Assert.Equal('B', tokens[3].Char1);
        Assert.True(tokens[4].IsSurrogatePair);
        Assert.Equal(0x1F389, tokens[4].Utf32CodePoint);
    }

    [Fact]
    public void BackgroundBackend_SerializesCompactlyAndTracksSchemaVersion()
    {
        var defaultClick = new MouseClickStep { X = 10, Y = 20 };
        var defaultMacro = new Macro { Steps = [defaultClick] };
        var defaultJson = MacroFileFormat.Serialize(defaultMacro);
        Assert.DoesNotContain("backgroundBackend", defaultJson);

        var uiaMacro = new Macro
        {
            Steps =
            [
                new MouseClickStep
                {
                    X = 100,
                    Y = 200,
                    BackgroundWindowTitle = "App",
                    BackgroundBackend = BackgroundInputBackend.UiAutomationInvoke,
                },
                new TypeTextStep
                {
                    Text = "hello",
                    BackgroundWindowTitle = "App",
                    BackgroundBackend = BackgroundInputBackend.Win32PostMessage,
                },
            ],
        };

        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(uiaMacro));
        var json = MacroFileFormat.Serialize(uiaMacro);
        Assert.True(MacroFileFormat.TryParse(json, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        var click = Assert.IsType<MouseClickStep>(parsed.Steps[0]);
        var type = Assert.IsType<TypeTextStep>(parsed.Steps[1]);
        Assert.Equal(BackgroundInputBackend.UiAutomationInvoke, click.BackgroundBackend);
        Assert.Equal(BackgroundInputBackend.Win32PostMessage, type.BackgroundBackend);
    }

    [Fact]
    public async Task PlaybackEngine_RoutesBackgroundClicksAndTypingAccordingToBackendPolicy()
    {
        var sim = new RecordingInputSimulator();
        var ui = new FakeBackgroundUiLocator
        {
            ClickResult = new UiBackgroundActionResult(Succeeded: true, PatternUsed: "InvokePattern"),
            SetValueResult = new UiBackgroundActionResult(Succeeded: true, PatternUsed: "ValuePattern"),
        };
        var engine = new PlaybackEngine(sim, new InstantClock(), uiLocator: ui);
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var macro = new Macro
        {
            Name = "Background Backend Routing Test",
            Steps =
            [
                // 0: Auto Left click -> UIA succeeds, no PostMessage fallback
                new MouseClickStep { X = 10, Y = 20, BackgroundWindowTitle = "WinA", BackgroundBackend = BackgroundInputBackend.Auto },
                // 1: Auto Right click -> skips UIA, goes directly to PostMessage
                new MouseClickStep { X = 15, Y = 25, Button = MacroMouseButton.Right, BackgroundWindowTitle = "WinA", BackgroundBackend = BackgroundInputBackend.Auto },
                // 2: Forced Win32PostMessage click -> skips UIA
                new MouseClickStep { X = 30, Y = 40, BackgroundWindowTitle = "WinA", BackgroundBackend = BackgroundInputBackend.Win32PostMessage },
                // 3: Auto type -> UIA ValuePattern succeeds, no PostMessage fallback
                new TypeTextStep { Text = "uia-ok", BackgroundWindowTitle = "WinA", BackgroundBackend = BackgroundInputBackend.Auto },
                // 4: Forced Win32PostMessage type -> skips UIA
                new TypeTextStep { Text = "msg-ok", BackgroundWindowTitle = "WinA", BackgroundBackend = BackgroundInputBackend.Win32PostMessage },
            ],
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Single(ui.ClickCalls);
        Assert.Equal(2, sim.BackgroundClicks.Count);
        Assert.Equal(MacroMouseButton.Right, sim.BackgroundClicks[0].Button);
        Assert.Equal((30, 40), (sim.BackgroundClicks[1].X, sim.BackgroundClicks[1].Y));
        Assert.Single(ui.SetValueCalls);
        Assert.Single(sim.BackgroundTexts);
        Assert.Equal("msg-ok", sim.BackgroundTexts[0].Text);

        // Strict UiAutomationInvoke failure must NOT fall back to PostMessage; Auto failure DOES fall back.
        sim.BackgroundClicks.Clear();
        sim.BackgroundTexts.Clear();
        ui.ClickResult = UiBackgroundActionResult.NotSupported;
        ui.SetValueResult = UiBackgroundActionResult.NotSupported;
        messages.Clear();

        var fallbackAndStrictMacro = new Macro
        {
            Name = "Background Backend Strict vs Auto Test",
            Steps =
            [
                new MouseClickStep { X = 50, Y = 60, BackgroundWindowTitle = "WinB", BackgroundBackend = BackgroundInputBackend.Auto },
                new MouseClickStep { X = 70, Y = 80, BackgroundWindowTitle = "WinB", BackgroundBackend = BackgroundInputBackend.UiAutomationInvoke },
                new TypeTextStep { Text = "fallback-text", BackgroundWindowTitle = "WinB", BackgroundBackend = BackgroundInputBackend.Auto },
                new TypeTextStep { Text = "strict-text", BackgroundWindowTitle = "WinB", BackgroundBackend = BackgroundInputBackend.UiAutomationInvoke },
            ],
        };

        var result2 = await engine.RunAsync(fallbackAndStrictMacro);
        Assert.Equal(PlaybackOutcome.Completed, result2.Outcome);
        Assert.Single(sim.BackgroundClicks);
        Assert.Equal((50, 60), (sim.BackgroundClicks[0].X, sim.BackgroundClicks[0].Y));
        Assert.Single(sim.BackgroundTexts);
        Assert.Equal("fallback-text", sim.BackgroundTexts[0].Text);
        Assert.Contains(messages, m => m.Contains("Background click (UI Automation)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Background type (UI Automation)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetResolver_UsesWindowCaptureForOccludedBackgroundOcrAndImageTargets()
    {
        const long occludedHwnd = 0x1234;
        var winBounds = new ScreenRect(100, 100, 500, 400);
        var layout = new FakeWindowLayout(new WindowInfo(occludedHwnd, "OccludedApp", winBounds, "app"));

        // 1. OCR tier in background mode must use ReadWindowAsync(hwnd, ...), never ReadAsync.
        var ocr = new FakeWindowOcrService
        {
            ResultFactory = _ => new OcrReadResult(winBounds, [new OcrLineBox([new OcrWordBox("Deploy", new ScreenRect(240, 210, 80, 24))])]),
        };
        var ocrStep = new MouseClickStep
        {
            X = 200,
            Y = 200,
            UseSmartIntent = true,
            BackgroundWindowTitle = "OccludedApp",
            Target = new TargetFingerprint
            {
                NearbyText = "Deploy",
                WindowTitle = "OccludedApp",
                WindowRelativeX = 100,
                WindowRelativeY = 100,
                WindowWidth = 500,
                WindowHeight = 400,
            },
        };

        var ocrResolver = new TargetResolver(ocr: ocr, layout: layout);
        var ocrResolution = await ocrResolver.ResolveAsync(ocrStep, "OccludedApp", CancellationToken.None);
        Assert.Equal(LocatorStrategyKind.OcrText, ocrResolution.WinningStrategy);
        Assert.Equal((280, 222), (ocrResolution.X, ocrResolution.Y));
        Assert.NotEmpty(ocr.WindowCaptureHandles);
        Assert.All(ocr.WindowCaptureHandles, h => Assert.Equal(occludedHwnd, h));
        Assert.Empty(ocr.ScreenCaptureCalls);

        // 2. Smart-Aim / ImageSnapshot tier in background mode must use CaptureWindowAsync(hwnd, ...).
        var canvas = CreatePatternImage(600, 500, markerX: 310, markerY: 260, markerSize: 20);
        var template = canvas.Crop(new ScreenRect(310, 260, 20, 20));
        var capture = new FakeWindowCapture(canvas);
        var imageStep = new MouseClickStep
        {
            X = 290,
            Y = 250,
            SmartAimAtTarget = true,
            UseSmartIntent = false,
            BackgroundWindowTitle = "OccludedApp",
            BackgroundBackend = BackgroundInputBackend.Win32PostMessage,
            TargetThumbnailBase64 = PngCodec.EncodeBase64(template),
            TargetAnchorX = 10,
            TargetAnchorY = 10,
        };

        var sim = new RecordingInputSimulator();
        var engine = new PlaybackEngine(sim, new InstantClock(), capture: capture, layout: layout);
        var runResult = await engine.RunAsync(new Macro { Name = "Occluded SmartAim Test", Steps = [imageStep] });
        Assert.Equal(PlaybackOutcome.Completed, runResult.Outcome);
        Assert.NotEmpty(capture.WindowCaptureHandles);
        Assert.All(capture.WindowCaptureHandles, h => Assert.Equal(occludedHwnd, h));
        Assert.Empty(capture.ScreenCaptureCalls);
        Assert.Single(sim.BackgroundClicks);
        Assert.Equal((320, 270), (sim.BackgroundClicks[0].X, sim.BackgroundClicks[0].Y));
    }

    private static BgraImage CreatePatternImage(int width, int height, int markerX, int markerY, int markerSize)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 24;
            pixels[i + 1] = 24;
            pixels[i + 2] = 28;
            pixels[i + 3] = 255;
        }

        for (var dy = 0; dy < markerSize; dy++)
        {
            for (var dx = 0; dx < markerSize; dx++)
            {
                var x = markerX + dx;
                var y = markerY + dy;
                var idx = (y * width + x) * 4;
                pixels[idx] = (byte)(30 + dx * 9);
                pixels[idx + 1] = (byte)(210 - dy * 7);
                pixels[idx + 2] = (byte)((dx + dy) * 5);
                pixels[idx + 3] = 255;
            }
        }

        return new BgraImage(width, height, pixels);
    }
}
