using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

/// <summary>A fake window list that can also "focus" windows (records what it was asked to bring to the front).</summary>
internal sealed class FakeFocusLayout : IScreenLayout, IWindowFocuser
{
    public List<MonitorInfo> Monitors { get; } = new();
    public List<WindowInfo> Windows { get; } = new();
    public List<(WindowInfo Window, bool Restore)> Focused { get; } = new();
    public bool FocusSucceeds { get; set; } = true;

    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;
    public IReadOnlyList<WindowInfo> GetWindows() => Windows;

    public bool TryFocus(WindowInfo window, bool restoreIfMinimized)
    {
        Focused.Add((window, restoreIfMinimized));
        return FocusSucceeds;
    }
}

/// <summary>
/// Upgrade 3: "Wait for screen change" (VISION &amp; AI) and the UTILITY &amp; GAME steps "Focus window" and "Take
/// screenshot": the change detector, playback with fake screens / windows, validation, the file format and the palette.
/// </summary>
public class UtilityStepsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Macro MacroWith(params MacroStep[] steps)
    {
        var macro = new Macro { Name = "Utility" };
        macro.Steps.AddRange(steps);
        return macro;
    }

    private static BgraImage Screen(int width = 100, int height = 80)
    {
        var image = BgraImage.Create(width, height);
        image.FillRectangle(new ScreenRect(0, 0, width, height), new RgbColor(30, 40, 50));
        return image;
    }

    /// <summary>The same screen with a white 20×10 block at (40, 30): its middle is (50, 35) after rounding 49.5 / 34.5 up.</summary>
    private static BgraImage ScreenWithBlock()
    {
        var image = Screen();
        image.FillRectangle(new ScreenRect(40, 30, 20, 10), RgbColor.White);
        return image;
    }

    // ---------------- Change detector ----------------

    [Fact]
    public void The_change_detector_finds_what_changed_and_its_centre()
    {
        var before = new ScreenFrame(new ScreenRect(100, 200, 100, 80), Screen());
        var after = new ScreenFrame(new ScreenRect(100, 200, 100, 80), ScreenWithBlock());

        var change = ScreenChangeDetector.Compare(before, after, tolerance: 30);

        Assert.True(change.Any);
        Assert.Equal(200, change.ChangedPixels);
        Assert.Equal(8000, change.ComparedPixels);
        Assert.Equal(2.5, change.ChangedPercent, precision: 6);
        Assert.Equal(new ScreenRect(140, 230, 20, 10), change.Bounds); // screen coordinates
        Assert.Equal((150, 235), (change.CenterX, change.CenterY));
    }

    [Fact]
    public void Identical_pictures_and_small_colour_shifts_are_no_change()
    {
        var frame = new ScreenFrame(new ScreenRect(0, 0, 100, 80), Screen());
        Assert.False(ScreenChangeDetector.Compare(frame, frame, 0).Any);

        var shifted = Screen();
        shifted.FillRectangle(new ScreenRect(0, 0, 100, 80), new RgbColor(45, 55, 65)); // +15 on every channel
        var shiftedFrame = new ScreenFrame(new ScreenRect(0, 0, 100, 80), shifted);
        Assert.False(ScreenChangeDetector.Compare(frame, shiftedFrame, 30).Any);
        Assert.Equal(8000, ScreenChangeDetector.Compare(frame, shiftedFrame, 10).ChangedPixels);
    }

    // ---------------- Wait for screen change: playback ----------------

    [Fact]
    public async Task Wait_for_change_detects_the_change_remembers_its_centre_and_clicks_it()
    {
        var input = new FakeInput();
        var capture = new FakeCapture(Screen(), Screen(), ScreenWithBlock());
        var engine = new PlaybackEngine(input, new FakeClock(), capture: capture);
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(MacroWith(new WaitForChangeStep
        {
            RegionX = 0, RegionY = 0, RegionWidth = 100, RegionHeight = 80, Action = FoundTextAction.Click,
        }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(3, capture.Captures); // starting picture, an unchanged look, the changed look
        Assert.Equal((50, 35), (engine.LastFoundRect!.Value.CenterX, engine.LastFoundRect.Value.CenterY));
        Assert.Equal(new ScreenRect(40, 30, 20, 10), engine.LastFoundRect.Value.Bounds);
        Assert.Contains("move 50,35", input.Calls);
        Assert.Contains("mouse Left down", input.Calls);
        Assert.Contains(messages, m => m.StartsWith("The watched area changed: 2.5 %", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_found_change_can_be_clicked_by_a_later_mouse_click_step()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Screen(), ScreenWithBlock()));

        var result = await engine.RunAsync(MacroWith(
            new WaitForChangeStep { RegionWidth = 100, RegionHeight = 80 },
            new MouseClickStep { UseLastFoundText = true }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Contains("move 50,35", input.Calls);
        Assert.Equal(1, input.Calls.Count(c => c == "mouse Left down"));
        Assert.Equal(1, input.Calls.Count(c => c == "mouse Left up"));
    }

    [Fact]
    public async Task No_change_in_time_continues_or_stops_as_chosen()
    {
        var step = new WaitForChangeStep { RegionWidth = 100, RegionHeight = 80, TimeoutMs = 300, WhenNotFound = TextNotFoundBehavior.Continue };
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(Screen()));
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        Assert.Equal(PlaybackOutcome.Completed, (await engine.RunAsync(MacroWith(step))).Outcome);
        Assert.Contains(messages, m => m.Contains("didn't change within 0.3 s", StringComparison.Ordinal) && m.EndsWith("Continuing.", StringComparison.Ordinal));
        Assert.Null(engine.LastFoundRect);

        step.WhenNotFound = TextNotFoundBehavior.StopMacro;
        var failed = await new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(Screen())).RunAsync(MacroWith(step));
        Assert.Equal(PlaybackOutcome.Failed, failed.Outcome);
        Assert.Contains("didn't change", failed.Error ?? string.Empty);
    }

    // ---------------- Focus window ----------------

    [Fact]
    public async Task Focus_window_finds_the_window_by_title_and_brings_it_to_the_front()
    {
        var layout = new FakeFocusLayout();
        layout.Windows.Add(new WindowInfo(7, "Notepad", new ScreenRect(0, 0, 50, 50)));
        layout.Windows.Add(new WindowInfo(42, "Roblox", default, IsMinimized: true));
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), layout: layout);
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(MacroWith(new FocusWindowStep { WindowTitle = "roblox" }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var (window, restore) = Assert.Single(layout.Focused);
        Assert.Equal(42, window.Handle);
        Assert.True(restore);
        Assert.Contains("Brought the window \"Roblox\" to the front.", messages);
    }

    [Fact]
    public async Task A_missing_window_continues_or_stops_as_chosen()
    {
        var layout = new FakeFocusLayout();
        var step = new FocusWindowStep { WindowTitle = "Roblox", WaitForWindowMs = 1_000, WhenNotFound = TextNotFoundBehavior.Continue };
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), layout: layout);
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        Assert.Equal(PlaybackOutcome.Completed, (await engine.RunAsync(MacroWith(step))).Outcome);
        Assert.Contains("No window with \"Roblox\" in its title or app name opened within 1 s. Continuing.", messages);
        Assert.Empty(layout.Focused);

        step.WhenNotFound = TextNotFoundBehavior.StopMacro;
        var failed = await new PlaybackEngine(new FakeInput(), new FakeClock(), layout: layout).RunAsync(MacroWith(step));
        Assert.Equal(PlaybackOutcome.Failed, failed.Outcome);
    }

    // ---------------- Take screenshot ----------------

    [Fact]
    public async Task Screenshot_saves_a_png_of_the_area()
    {
        var screen = Screen();
        screen.FillRectangle(new ScreenRect(10, 10, 30, 20), new RgbColor(200, 100, 0));
        var folder = _dir.Combine("shots");
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(screen));

        var result = await engine.RunAsync(MacroWith(new ScreenshotStep
        {
            Area = ScreenshotArea.CustomArea, RegionX = 10, RegionY = 10, RegionWidth = 30, RegionHeight = 20,
            FolderPath = folder, FileNamePrefix = "Catch",
        }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var file = Assert.Single(Directory.GetFiles(folder, "Catch_*.png"));
        Assert.Equal(file, engine.LastScreenshotPath);
        Assert.True(PngCodec.TryDecode(await File.ReadAllBytesAsync(file), out var image, out var error), error);
        Assert.Equal((30, 20), (image!.Width, image.Height));
        Assert.Equal(new RgbColor(200, 100, 0), image.GetPixel(5, 5));
    }

    [Fact]
    public async Task A_screenshot_that_cant_be_taken_is_reported_and_the_macro_carries_on()
    {
        var folder = _dir.Combine("none");
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(Screen()), layout: new FakeFocusLayout());
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(MacroWith(
            new ScreenshotStep { Area = ScreenshotArea.Window, WindowTitle = "Roblox", FolderPath = folder },
            new DelayStep { DurationMs = 10 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Contains("📸 No screenshot taken: The window \"Roblox\" isn't open.", messages);
        Assert.False(Directory.Exists(folder));
        Assert.Null(engine.LastScreenshotPath);
    }

    [Fact]
    public void Screenshot_file_names_are_safe_sortable_and_never_overwrite()
    {
        var time = new DateTime(2026, 10, 24, 18, 5, 11, 42);
        Assert.Equal("Catch_20261024_180511_042.png", ScreenshotFiles.FileName("Catch", time));
        Assert.Equal("a_b_c_20261024_180511_042.png", ScreenshotFiles.FileName("a/b:c", time));
        Assert.Equal("SmartMacro", ScreenshotFiles.CleanPrefix("   "));
        Assert.Equal("SmartMacro", ScreenshotFiles.CleanPrefix(null));
        Assert.True(ScreenshotFiles.HasInvalidFileNameChars("rare?drop"));
        Assert.False(ScreenshotFiles.HasInvalidFileNameChars("Rare drop #1"));

        var folder = _dir.Combine("same");
        var first = ScreenshotFiles.Save(Screen(4, 4), folder, "Shot", time);
        var second = ScreenshotFiles.Save(Screen(4, 4), folder, "Shot", time);
        Assert.Equal("Shot_20261024_180511_042.png", Path.GetFileName(first));
        Assert.Equal("Shot_20261024_180511_042_2.png", Path.GetFileName(second));
        Assert.Equal(ScreenshotFiles.DefaultFolder, ScreenshotFiles.FolderOf(new ScreenshotStep()));
        Assert.EndsWith(ScreenshotFiles.DefaultFolderName, ScreenshotFiles.DefaultFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void Screenshot_areas_resolve_to_the_monitor_window_or_area()
    {
        var layout = new FakeFocusLayout();
        layout.Monitors.AddRange(FakeLayout.ThreeMonitors().Monitors);
        layout.Windows.Add(new WindowInfo(1, "Roblox", new ScreenRect(20, 10, 60, 40)));
        layout.Windows.Add(new WindowInfo(2, "Hidden", default, IsMinimized: true));
        var desktop = new ScreenRect(-100, 0, 300, 100);

        Assert.Equal(new ScreenRect(0, 0, 100, 100), ScreenshotFiles.ResolveArea(new ScreenshotStep(), desktop, layout));
        Assert.Equal(desktop, ScreenshotFiles.ResolveArea(new ScreenshotStep { Area = ScreenshotArea.AllMonitors }, desktop, layout));
        Assert.Equal(new ScreenRect(20, 10, 60, 40),
            ScreenshotFiles.ResolveArea(new ScreenshotStep { Area = ScreenshotArea.Window, WindowTitle = "rob" }, desktop, layout));
        Assert.Equal(new ScreenRect(150, 50, 50, 50), ScreenshotFiles.ResolveArea(
            new ScreenshotStep { Area = ScreenshotArea.CustomArea, RegionX = 150, RegionY = 50, RegionWidth = 100, RegionHeight = 100 }, desktop, layout));
        Assert.Throws<SearchAreaUnavailableException>(() =>
            ScreenshotFiles.ResolveArea(new ScreenshotStep { Area = ScreenshotArea.Window, WindowTitle = "Hidden" }, desktop, layout));
    }

    // ---------------- Validation ----------------

    private static bool HasError(MacroStep step, string property) =>
        MacroValidator.ValidateStep(step).Any(i => i.Severity == IssueSeverity.Error && i.Property == property);

    [Fact]
    public void New_steps_from_the_toolbox_are_ready_or_say_what_is_missing()
    {
        Assert.Empty(MacroValidator.ValidateStep(MacroFactory.CreateStep(StepKind.WaitForChange)));
        Assert.Empty(MacroValidator.ValidateStep(MacroFactory.CreateStep(StepKind.Screenshot)));
        Assert.True(HasError(MacroFactory.CreateStep(StepKind.FocusWindow), nameof(FocusWindowStep.WindowTitle)));
        Assert.Empty(MacroValidator.ValidateStep(new FocusWindowStep { WindowTitle = "Roblox" }));
    }

    [Fact]
    public void Bad_settings_are_reported()
    {
        Assert.True(HasError(new WaitForChangeStep { RegionWidth = 3 }, "Region"));
        Assert.True(HasError(new WaitForChangeStep { MinChangedPercent = 0 }, nameof(WaitForChangeStep.MinChangedPercent)));
        Assert.True(HasError(new WaitForChangeStep { MinChangedPercent = double.NaN }, nameof(WaitForChangeStep.MinChangedPercent)));
        Assert.True(HasError(new WaitForChangeStep { PixelTolerance = 300 }, nameof(WaitForChangeStep.PixelTolerance)));
        Assert.True(HasError(new WaitForChangeStep { CheckIntervalMs = 10 }, nameof(WaitForChangeStep.CheckIntervalMs)));
        Assert.True(HasError(new WaitForChangeStep { Action = FoundTextAction.Hold, ActionHoldMs = 0 }, nameof(WaitForChangeStep.ActionHoldMs)));

        Assert.True(HasError(new FocusWindowStep { WindowTitle = "x", WaitForWindowMs = -1 }, nameof(FocusWindowStep.WaitForWindowMs)));
        Assert.True(HasError(new FocusWindowStep { WindowTitle = "x", SettleMs = 70_000 }, nameof(FocusWindowStep.SettleMs)));

        Assert.True(HasError(new ScreenshotStep { Area = ScreenshotArea.Window }, nameof(ScreenshotStep.WindowTitle)));
        Assert.True(HasError(new ScreenshotStep { Area = ScreenshotArea.CustomArea, RegionHeight = 2 }, "Region"));
        Assert.True(HasError(new ScreenshotStep { FolderPath = "relative\\folder" }, nameof(ScreenshotStep.FolderPath)));
        Assert.True(HasError(new ScreenshotStep { FileNamePrefix = new string('a', 61) }, nameof(ScreenshotStep.FileNamePrefix)));
        Assert.Contains(MacroValidator.ValidateStep(new ScreenshotStep { FileNamePrefix = "a?b" }),
            i => i.Severity == IssueSeverity.Warning && i.Property == nameof(ScreenshotStep.FileNamePrefix));
        Assert.False(HasError(new ScreenshotStep { FolderPath = _dir.Path }, nameof(ScreenshotStep.FolderPath)));
    }

    // ---------------- File format ----------------

    [Fact]
    public void The_new_steps_round_trip_through_a_file_and_need_format_13()
    {
        var macro = MacroWith(
            new WaitForChangeStep
            {
                RegionX = 5, RegionY = 6, RegionWidth = 70, RegionHeight = 40, MinChangedPercent = 0.25, PixelTolerance = 18,
                TimeoutMs = 2_500, CheckIntervalMs = 75, Action = FoundTextAction.DoubleClick, WhenNotFound = TextNotFoundBehavior.SkipNextStep,
            },
            new FocusWindowStep { WindowTitle = "Roblox", RestoreIfMinimized = false, WaitForWindowMs = 9_000, SettleMs = 400, WhenNotFound = TextNotFoundBehavior.Continue },
            new ScreenshotStep { Area = ScreenshotArea.Window, WindowTitle = "Roblox", FolderPath = _dir.Path, FileNamePrefix = "Rare" });

        var json = MacroFileFormat.Serialize(macro);
        Assert.Contains("\"type\": \"waitForChange\"", json);
        Assert.Contains("\"type\": \"focusWindow\"", json);
        Assert.Contains("\"type\": \"screenshot\"", json);
        Assert.Equal(13, MacroDocument.RequiredSchemaVersion(macro));
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var error), error);

        var wait = Assert.IsType<WaitForChangeStep>(loaded!.Steps[0]);
        Assert.Equal((5, 6, 70, 40, 18, 2_500, 75), (wait.RegionX, wait.RegionY, wait.RegionWidth, wait.RegionHeight, wait.PixelTolerance, wait.TimeoutMs, wait.CheckIntervalMs));
        Assert.Equal(0.25, wait.MinChangedPercent, precision: 6);
        Assert.Equal((FoundTextAction.DoubleClick, TextNotFoundBehavior.SkipNextStep), (wait.Action, wait.WhenNotFound));

        var focus = Assert.IsType<FocusWindowStep>(loaded.Steps[1]);
        Assert.Equal(("Roblox", false, 9_000, 400, TextNotFoundBehavior.Continue),
            (focus.WindowTitle, focus.RestoreIfMinimized, focus.WaitForWindowMs, focus.SettleMs, focus.WhenNotFound));

        var shot = Assert.IsType<ScreenshotStep>(loaded.Steps[2]);
        Assert.Equal((ScreenshotArea.Window, "Roblox", _dir.Path, "Rare"), (shot.Area, shot.WindowTitle, shot.FolderPath, shot.FileNamePrefix));
    }

    // ---------------- Editor helpers ----------------

    [Fact]
    public void The_steps_are_described_in_plain_words()
    {
        Assert.Equal("Wait up to 10 s for 1 % of (0, 0) 300×200 to change → click it",
            StepDescriber.Describe(new WaitForChangeStep { Action = FoundTextAction.Click }));
        Assert.Equal("Bring \"Roblox\" to the front (restore if minimized)", StepDescriber.Describe(new FocusWindowStep { WindowTitle = "Roblox" }));
        Assert.StartsWith("Save a screenshot of the main monitor as SmartMacro_….png in ", StepDescriber.Describe(new ScreenshotStep()));
        Assert.Equal("Wait for screen change", StepDescriber.DisplayName(StepKind.WaitForChange));
        Assert.Equal("Focus window", StepDescriber.DisplayName(StepKind.FocusWindow));
        Assert.Equal("Take screenshot", StepDescriber.DisplayName(StepKind.Screenshot));
        Assert.Equal("Taking a screenshot", StepDescriber.Activity(new ScreenshotStep()));
    }

    [Fact]
    public void The_command_palette_adds_the_new_steps()
    {
        Assert.Equal(StepKind.Screenshot, StudioCommandCatalog.Filter("screenshot")[0].Step);
        Assert.Equal(StepKind.FocusWindow, StudioCommandCatalog.Filter("focus window")[0].Step);
        Assert.Contains(StudioCommandCatalog.Filter("screen change"), c => c.Kind == StudioCommandKind.AddStep && c.Step == StepKind.WaitForChange);
        Assert.True(StepListView.MatchesFilter(new WaitForChangeStep(), StepFilter.VisionAndLogic));
    }

    [Fact]
    public void Target_window_scaling_moves_the_watched_and_captured_areas()
    {
        var wait = new WaitForChangeStep { RegionX = 100, RegionY = 100, RegionWidth = 50, RegionHeight = 40 };
        var shot = new ScreenshotStep { Area = ScreenshotArea.CustomArea, RegionX = 100, RegionY = 100, RegionWidth = 50, RegionHeight = 40 };
        var mapping = new CoordinateMapping(new ScreenRect(0, 0, 1000, 500), new ScreenRect(200, 100, 2000, 1000));

        CoordinateScaling.ApplyInPlace(new MacroStep[] { wait, shot }, mapping);

        Assert.Equal(new ScreenRect(400, 300, 100, 80), wait.Region);
        Assert.Equal(new ScreenRect(400, 300, 100, 80), shot.Region);
    }
}
