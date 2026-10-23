using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

// ---------------------------------------------------------------------------------------------------------------------
// "SmartMacro AAA Studio Upgrade", parts 1 and 2:
//   1. Smart Click Snapshots: a 56×56 PNG of what each recorded click hit, Smart-Aim, "Convert to Find image & click".
//   2. "Last found target" memory: If "When found, also:" + offset, and Mouse click / Move mouse "on the last found spot".
// These prove the Core logic with fake screens and fake input; they don't prove a real game reacts.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Shared helpers for the AAA upgrade tests.</summary>
internal static class Aaa
{
    public static Macro MacroWith(params MacroStep[] steps)
    {
        var m = new Macro { Name = "AAA" };
        m.Steps.AddRange(steps);
        return m;
    }

    /// <summary>A busy 400×300 screen (seed 20) with a 56×56 "button" pasted at (x, y), or none.</summary>
    public static BgraImage Screen(int? iconX = null, int? iconY = null, params (int X, int Y)[] extraCopies)
    {
        var screen = TestImages.Blocky(400, 300, seed: 20);
        if (iconX is { } x && iconY is { } y) TestImages.Paste(screen, Button, x, y);
        foreach (var copy in extraCopies) TestImages.Paste(screen, Button, copy.X, copy.Y);
        return screen;
    }

    /// <summary>The 56×56 "button" (random two-colour cells, so it has exactly one true position).</summary>
    public static BgraImage Button { get; } = TestImages.Icon(56, 56, seed: 33);

    /// <summary>
    /// A recorded click at (x, y) on <paramref name="recordedScreen"/>, with the Smart Click Snapshot the recorder would
    /// have taken (the 56×56 around the click, kept on screen).
    /// </summary>
    public static MouseClickStep RecordedClick(BgraImage recordedScreen, int x, int y)
    {
        var virtualScreen = new ScreenRect(0, 0, recordedScreen.Width, recordedScreen.Height);
        var rect = ClickSnapshots.RectAround(x, y, virtualScreen);
        var snapshot = ClickSnapshots.Create(recordedScreen.Crop(rect), rect, x, y);
        var step = new MouseClickStep { X = x, Y = y };
        ClickSnapshots.Attach(step, snapshot);
        return step;
    }

    public static FindImageStep FindImage(BgraImage image, Action<FindImageStep>? configure = null)
    {
        var step = new FindImageStep
        {
            TemplateWidth = image.Width,
            TemplateHeight = image.Height,
            TemplateData = TemplateCodec.Encode(image),
            TimeoutMs = 0,
        };
        configure?.Invoke(step);
        return step;
    }

    public static Condition ImageCondition(BgraImage image, bool negate = false)
    {
        var condition = Condition.Create(ConditionKind.ImageVisible);
        var check = (FindImageStep)condition.Check!;
        check.TemplateWidth = image.Width;
        check.TemplateHeight = image.Height;
        check.TemplateData = TemplateCodec.Encode(image);
        condition.Negate = negate;
        return condition;
    }
}

public class PngCodecTests
{
    // 13×9 test pictures made with every PNG row filter (row y uses filter y % 5) and checked with Pillow.
    // Pixel (x, y) = R (x*37 + y*11) % 256, G (x*5 + y*71 + 13) % 256, B (x*x + y*3) % 256, A (x*29 + y*7) % 256.
    private const string AllFiltersRgb =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAAJCAIAAABmGDE9AAAA4UlEQVR42mNg4GVQFWL0EmfJl+GcoiiwU03ynrYKs4GhhqmDr1VgkX3KdJfKPZ4TGLlDmFVZGVVZgSSrKiu7KiunKiu3KiuvKiu/KqugKquwKquoKqs4E7c7MzGIWWwqm4Qak4Qas4Qai4Qaq4QakMs+YxmHxDJOiWVcEsu4JZbxSCzjZQEpZ2XkZmVGIHegG5i5URGDeQF/TKlAY5XwsnqJ0y3yHzo1RPuMrSY7xM/wb5mbsHJR8bnlHZ/XzGd02i5EVX8YHeND9scMsD8k1Dgk1Dgl1Lgk1Lgl1HhmqPECALEgORFPO+zKAAAAAElFTkSuQmCC";

    private const string AllFiltersRgba =
        "iVBORw0KGgoAAAANSUhEUgAAAA0AAAAJCAYAAADpeqZqAAABBElEQVR42mNg4GVgUBVilPUSZ7HKl+EMn6IoULJTTXLiPW2VdcwGhqc1TB1e+FoFshbZpyhNd6m03+M5IYaRO4SZXZWVUVaVlRmIWYGYHYg5gZgbiHmBmB+IBYFYGIhFgVhclonbnZmdVMwsNpWNT0KNSUhCjRmIWYCYFYjZgJhdaMYyDiGJZZxAzDVJYhk3kOYBYl4hFrBuVkYgZkbF7iBnw/myyHIM5gX8yjGlAg6NVcKxy+olqk63yE//0KmxRbTP+KLVZId38TP8uVvmJmisXFTsem55R9LnNfPrGZ22C2nRJyCMjvEpoAfEDGhASKgBA0INGBBqXEAMDAg1HqAcrxAAC/1J8sWqoRsAAAAASUVORK5CYII=";

    private static RgbColor Expected(int x, int y) =>
        new((byte)((x * 37 + y * 11) % 256), (byte)((x * 5 + y * 71 + 13) % 256), (byte)((x * x + y * 3) % 256));

    [Fact]
    public void Round_trip_keeps_every_pixel_and_makes_it_opaque()
    {
        var image = TestImages.Icon(56, 56, seed: 5);
        image.Pixels[3] = 0; // GDI captures leave alpha at 0: the PNG must still be visible

        var png = PngCodec.Encode(image);
        Assert.True(PngCodec.TryDecode(png, out var decoded, out var error), error);
        var result = decoded!;

        Assert.Equal((56, 56), (result.Width, result.Height));
        for (var y = 0; y < 56; y++)
            for (var x = 0; x < 56; x++)
                Assert.Equal(image.GetPixel(x, y), result.GetPixel(x, y));
        for (var i = 0; i < 56 * 56; i++)
            Assert.Equal((byte)255, result.Pixels[i * 4 + 3]);
    }

    [Fact]
    public void Base64_round_trip_works()
    {
        var image = TestImages.Icon(20, 11, seed: 9);
        Assert.True(PngCodec.TryDecodeBase64(PngCodec.EncodeBase64(image), out var decoded, out var error), error);
        Assert.Equal(image.GetPixel(19, 10), decoded!.GetPixel(19, 10));
    }

    [Theory]
    [InlineData(AllFiltersRgb, false)]
    [InlineData(AllFiltersRgba, true)]
    public void Reads_pngs_that_use_every_row_filter(string base64, bool hasAlpha)
    {
        Assert.True(PngCodec.TryDecodeBase64(base64, out var image, out var error), error);
        Assert.Equal((13, 9), (image!.Width, image.Height));
        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 13; x++)
            {
                Assert.Equal(Expected(x, y), image.GetPixel(x, y));
                Assert.Equal((byte)(hasAlpha ? (x * 29 + y * 7) % 256 : 255), image.Pixels[(y * 13 + x) * 4 + 3]);
            }
        }
    }

    [Fact]
    public void Damaged_or_foreign_data_is_refused_with_a_reason()
    {
        var png = PngCodec.Encode(TestImages.Icon(10, 10, seed: 1));
        var flipped = (byte[])png.Clone();
        flipped[45] ^= 0xFF; // inside the compressed pixels: the checksum no longer matches

        Assert.False(PngCodec.TryDecode(flipped, out _, out var checksum));
        Assert.Contains("checksum", checksum);
        Assert.False(PngCodec.TryDecode(new byte[] { 1, 2, 3 }, out _, out var tooShort));
        Assert.NotNull(tooShort);
        Assert.False(PngCodec.TryDecodeBase64("not base64 !!", out _, out var notBase64));
        Assert.NotNull(notBase64);
        Assert.False(PngCodec.TryDecodeBase64(null, out _, out _));
    }
}

public class SmartClickSnapshotTests
{
    [Theory]
    [InlineData(200, 150, 172, 122)] // middle of the screen: centred
    [InlineData(5, 10, 0, 0)]        // top-left corner: moved onto the screen
    [InlineData(399, 299, 344, 244)] // bottom-right corner
    public void Snapshot_rectangle_is_centred_and_kept_on_screen(int x, int y, int left, int top)
    {
        var rect = ClickSnapshots.RectAround(x, y, new ScreenRect(0, 0, 400, 300));
        Assert.Equal(new ScreenRect(left, top, 56, 56), rect);
    }

    [Fact]
    public void Snapshot_rectangle_works_on_a_monitor_left_of_the_main_one()
    {
        var rect = ClickSnapshots.RectAround(-1915, 500, new ScreenRect(-1920, 0, 3840, 1080));
        Assert.Equal(new ScreenRect(-1920, 472, 56, 56), rect);
        Assert.True(ClickSnapshots.RectAround(10, 10, default).IsEmpty);
    }

    [Fact]
    public void Snapshot_remembers_where_in_the_picture_the_click_was()
    {
        var screen = Aaa.Screen();
        var click = Aaa.RecordedClick(screen, 10, 100); // near the left edge: picture (0, 72), click 10 px in

        Assert.True(click.HasTargetThumbnail);
        Assert.Equal(10, click.TargetAnchorX);
        Assert.Equal(28, click.TargetAnchorY);
        var thumbnail = SmartClickConversion.TryDecodeThumbnail(click, out var error);
        Assert.NotNull(thumbnail);
        Assert.Null(error);
        Assert.Equal(screen.GetPixel(0, 72), thumbnail!.GetPixel(0, 0));
        Assert.Equal(screen.GetPixel(55, 127), thumbnail.GetPixel(55, 55));
    }

    [Fact]
    public void Desktop_recording_puts_the_snapshot_on_the_click_step()
    {
        var snapshot = new ClickTargetSnapshot(PngCodec.Encode(Aaa.Button), 28, 28);
        var raw = new List<RawInputEvent>
        {
            new(0, RawInputKind.MouseMove, 100, 100),
            new(100, RawInputKind.MouseDown, 128, 78, ClickedTarget: snapshot),
            new(150, RawInputKind.MouseUp, 128, 78),
            new(400, RawInputKind.MouseDown, 300, 200, ClickedTarget: snapshot), // held 900 ms: a press, not a click
            new(1300, RawInputKind.MouseUp, 300, 200),
        };

        var steps = RecordingConverter.Convert(raw, new RecordingOptions { MaxTapDurationMs = 500 });

        var click = steps.OfType<MouseClickStep>().Single(c => c.Action == MouseClickAction.Click);
        Assert.Equal((128, 78, 50), (click.X, click.Y, click.HoldMs));
        Assert.Equal(Convert.ToBase64String(snapshot.Png), click.TargetThumbnailBase64);
        Assert.Equal(28, click.TargetAnchorX);
        Assert.Equal(28, click.TargetAnchorY);
        Assert.False(click.SmartAimAtTarget); // Smart-Aim is the user's choice
        Assert.All(steps.OfType<MouseClickStep>().Where(c => c.Action != MouseClickAction.Click), c => Assert.False(c.HasTargetThumbnail));
    }

    [Fact]
    public void Clicks_without_a_snapshot_are_recorded_exactly_as_before()
    {
        var steps = RecordingConverter.Convert(new List<RawInputEvent>
        {
            new(0, RawInputKind.MouseDown, 10, 20),
            new(40, RawInputKind.MouseUp, 10, 20),
        });

        var click = Assert.Single(steps.OfType<MouseClickStep>());
        Assert.False(click.HasTargetThumbnail);
        Assert.Null(click.TargetAnchorX);
        Assert.True(MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(steps.ToArray())) < 12);
    }

    [Fact]
    public void Smart_recording_keeps_the_snapshot_through_the_cursor_part()
    {
        var snapshot = new ClickTargetSnapshot(PngCodec.Encode(Aaa.Button), 20, 30);
        var events = new List<CapturedInputEvent>();
        for (var k = 0; k < 10; k++)
            events.Add(new CapturedInputEvent(InputEvent.Move(k * 10_000L, 5, 0, 150 + 5 * (k + 1), 100)));
        events.Add(new CapturedInputEvent(InputEvent.ButtonDown(200_000, MacroMouseButton.Left, 200, 100), CursorFlags.None, snapshot));
        events.Add(new CapturedInputEvent(InputEvent.ButtonUp(280_000, MacroMouseButton.Left, 200, 100)));

        var result = SmartRecordingConverter.Convert(events);

        var click = Assert.Single(result.Steps.OfType<MouseClickStep>());
        Assert.Equal((MouseClickAction.Click, 200, 100), (click.Action, click.X, click.Y));
        Assert.Equal(Convert.ToBase64String(snapshot.Png), click.TargetThumbnailBase64);
        Assert.Equal(20, click.TargetAnchorX);
        Assert.Equal(30, click.TargetAnchorY);
    }

    [Fact]
    public void Snapshot_settings_are_saved_only_when_used_and_need_format_12()
    {
        Assert.Equal(12, MacroDocument.CurrentSchemaVersion);

        var plainJson = MacroJson.Serialize<MacroStep>(new MouseClickStep { X = 1, Y = 2 });
        foreach (var name in new[] { "targetThumbnail", "targetAnchor", "smartAim", "offsetX", "offsetY", "targetMode" })
            Assert.DoesNotContain(name, plainJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetMode", MacroJson.Serialize<MacroStep>(new MouseMoveStep { X = 1, Y = 2 }), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(new MouseClickStep { X = 1, Y = 2 }, new MouseMoveStep())));

        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        click.SmartAimAccuracy = 0.9;
        click.SmartAimRadiusPx = 80;
        Assert.Equal(12, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(click)));

        var json = MacroJson.Serialize<MacroStep>(click);
        Assert.Contains("smartAimMinMatch", json);
        var copy = Assert.IsType<MouseClickStep>(MacroJson.CloneStep(click));
        Assert.Equal(click.TargetThumbnailBase64, copy.TargetThumbnailBase64);
        Assert.Equal((true, 0.9, 80, 28, 28), (copy.SmartAimAtTarget, copy.SmartAimAccuracy, copy.SmartAimRadiusPx, copy.TargetAnchorX!.Value, copy.TargetAnchorY!.Value));

        // Back to the defaults: the overrides disappear from the file again.
        copy.SmartAimAccuracy = MouseClickStep.DefaultSmartAimAccuracy;
        copy.SmartAimRadiusPx = MouseClickStep.DefaultSmartAimRadiusPx;
        Assert.DoesNotContain("smartAimMinMatch", MacroJson.Serialize<MacroStep>(copy));
        Assert.DoesNotContain("smartAimRadius", MacroJson.Serialize<MacroStep>(copy));
    }

    [Fact]
    public void Smart_aim_settings_are_validated()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(), 128, 78);
        click.SmartAimAtTarget = true;
        Assert.DoesNotContain(MacroValidator.ValidateStep(click), i => i.Severity == IssueSeverity.Error);

        click.SmartAimAccuracy = 0.3;
        click.SmartAimRadiusPx = 5_000;
        var issues = MacroValidator.ValidateStep(click);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Property == nameof(MouseClickStep.SmartAimAccuracy));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Property == nameof(MouseClickStep.SmartAimRadiusPx));

        var noPicture = new MouseClickStep { X = 5, Y = 5, SmartAimAtTarget = true };
        Assert.Contains(MacroValidator.ValidateStep(noPicture), i => i.Severity == IssueSeverity.Warning && i.Property == nameof(MouseClickStep.SmartAimAtTarget));

        var damaged = new MouseClickStep { X = 5, Y = 5, SmartAimAtTarget = true, TargetThumbnailBase64 = "AAAA" };
        Assert.Contains(MacroValidator.ValidateStep(damaged), i => i.Severity == IssueSeverity.Warning && i.Message.Contains("can't use the recorded picture"));
    }

    // ---------------- Smart-Aim playback ----------------

    [Fact]
    public async Task Smart_aim_clicks_the_target_after_it_moved_by_40_30()
    {
        // Recorded: the button at (100, 50), clicked in its middle (128, 78). Now it is at (140, 80).
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen(140, 80)));
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Aaa.MacroWith(click));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 168,108", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Contains(messages, m => m.Contains("moved by (+40, +30)"));
    }

    [Fact]
    public async Task Smart_aim_falls_back_to_the_recorded_spot_when_the_target_is_gone()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();
        var messages = new List<string>();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen()));
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Aaa.MacroWith(click));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 128,78", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Contains(messages, m => m.Contains("wasn't found within 120 px"));
    }

    [Fact]
    public async Task Smart_aim_only_looks_within_its_distance()
    {
        // Moved by 200 px: further than the 120 px Smart-Aim looks, so the recorded spot is clicked.
        var click = Aaa.RecordedClick(Aaa.Screen(20, 50), 48, 78);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen(220, 50))).RunAsync(Aaa.MacroWith(click));

        Assert.Equal("move 48,78", input.Calls[0]);
    }

    [Fact]
    public async Task Smart_aim_is_off_unless_turned_on()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        var capture = new FakeCapture(Aaa.Screen(140, 80));
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: capture).RunAsync(Aaa.MacroWith(click));

        Assert.Equal(new[] { "move 128,78", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(0, capture.Captures); // nothing was looked at
    }

    [Fact]
    public async Task Smart_aim_picks_the_copy_nearest_the_recorded_spot()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen(140, 80, (60, 150))))
            .RunAsync(Aaa.MacroWith(click));

        Assert.Equal("move 168,108", input.Calls[0]);
    }

    [Fact]
    public async Task Smart_aim_keeps_an_off_centre_click_off_centre()
    {
        // Clicked 10 px from the left edge of the screen: the picture starts at x 0, the click is 10 px into it.
        var recorded = Aaa.Screen(0, 72);
        var click = Aaa.RecordedClick(recorded, 10, 100);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen(30, 90)))
            .RunAsync(Aaa.MacroWith(click));

        Assert.Equal("move 40,118", input.Calls[0]);
    }

    [Fact]
    public async Task Smart_aim_without_screen_capture_clicks_the_recorded_spot()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock()).RunAsync(Aaa.MacroWith(click));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal("move 128,78", input.Calls[0]);
    }

    // ---------------- 🔍 Convert step to full "Find image & click" ----------------

    [Fact]
    public void Convert_builds_a_find_image_step_from_the_snapshot()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.Comment = "Buy button";
        click.HoldMs = 60;

        var find = SmartClickConversion.ToFindImageStep(click);

        Assert.Equal((56, 56), (find.TemplateWidth, find.TemplateHeight));
        Assert.True(TemplateCodec.TryDecode(find.TemplateData, 56, 56, out var template, out _));
        Assert.Equal(Aaa.Button.GetPixel(13, 40), template!.GetPixel(13, 40));
        Assert.Equal(FoundTextAction.Click, find.Action);
        Assert.Equal(5_000, find.TimeoutMs);
        Assert.Equal(SearchScope.AllMonitors, find.EffectiveScope);
        Assert.Equal((0, 0), (find.OffsetX, find.OffsetY));
        Assert.Equal(85, find.MinSimilarityPercent);
        Assert.Equal(60, find.ClickHoldMs);
        Assert.Equal("Buy button", find.Comment);
        Assert.NotEqual(click.Id, find.Id);
        Assert.DoesNotContain(MacroValidator.ValidateStep(find), i => i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData(MouseClickAction.DoubleClick, MacroMouseButton.Left, FoundTextAction.DoubleClick)]
    [InlineData(MouseClickAction.Click, MacroMouseButton.Right, FoundTextAction.RightClick)]
    [InlineData(MouseClickAction.Hold, MacroMouseButton.Left, FoundTextAction.Hold)]
    public void Convert_keeps_the_kind_of_click(MouseClickAction action, MacroMouseButton button, FoundTextAction expected)
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.Action = action;
        click.Button = button;
        click.HoldMs = 900;

        var find = SmartClickConversion.ToFindImageStep(click);

        Assert.Equal(expected, find.Action);
        if (expected == FoundTextAction.Hold) Assert.Equal(900, find.ActionHoldMs);
    }

    [Fact]
    public void Convert_refuses_clicks_it_cannot_turn_into_a_find_step()
    {
        Assert.NotNull(SmartClickConversion.WhyNotConvertible(new MouseClickStep { X = 1, Y = 1 })); // no picture

        var press = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        press.Action = MouseClickAction.Down;
        Assert.NotNull(SmartClickConversion.WhyNotConvertible(press));
        Assert.Throws<InvalidOperationException>(() => SmartClickConversion.ToFindImageStep(press));
    }

    [Fact]
    public async Task Converted_step_clicks_the_same_spot_on_the_found_picture()
    {
        // Off-centre click near the screen edge: the Find step's offset must put the click back on that spot.
        var recorded = Aaa.Screen(0, 72);
        var find = SmartClickConversion.ToFindImageStep(Aaa.RecordedClick(recorded, 10, 100));
        Assert.Equal((-18, 0), (find.OffsetX, find.OffsetY));
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(Aaa.Screen(200, 150))).RunAsync(Aaa.MacroWith(find));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 210,178", "mouse Left down", "mouse Left up" }, input.Calls);
    }
}

public class LastFoundTargetTests
{
    // A 40×30 icon at (100, 50) on a 400×300 screen: center (120, 65).
    private static (BgraImage Screen, BgraImage Icon) ScreenWithIcon()
    {
        var screen = TestImages.Blocky(400, 300, seed: 20);
        var icon = TestImages.Icon(40, 30, seed: 21);
        TestImages.Paste(screen, icon, 100, 50);
        return (screen, icon);
    }

    [Fact]
    public void If_when_found_action_is_the_same_setting_as_then_action()
    {
        var step = new IfStep { WhenFoundAction = IfFoundAction.DoubleClickFoundSpot, FoundOffsetX = 7, FoundOffsetY = -3 };
        Assert.Equal((FoundTextAction.DoubleClick, 7, -3), (step.ThenAction, step.ThenOffsetX, step.ThenOffsetY));

        step.ThenAction = FoundTextAction.Move;
        Assert.Equal(IfFoundAction.MoveToFoundSpot, step.WhenFoundAction);
        step.WhenFoundAction = IfFoundAction.None;
        Assert.Equal(FoundTextAction.None, step.ThenAction);

        // Stored once (as before), so files stay readable by older SmartMacro builds.
        var json = MacroJson.Serialize<MacroStep>(new IfStep { WhenFoundAction = IfFoundAction.ClickFoundSpot });
        Assert.DoesNotContain("whenFoundAction", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"thenAction\": \"Click\"", json);
    }

    [Fact]
    public async Task If_when_found_clicks_the_match_plus_offset_before_the_inside_steps()
    {
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen));

        var result = await engine.RunAsync(Aaa.MacroWith(
            new IfStep { Condition = Aaa.ImageCondition(icon), WhenFoundAction = IfFoundAction.ClickFoundSpot, FoundOffsetX = 10, FoundOffsetY = -5 },
            new KeyPressStep { VirtualKey = 0x41 },
            new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 130,60", "mouse Left down", "mouse Left up", "key 65 down", "key 65 up" }, input.Calls);
        Assert.Equal(new FoundRect(100, 50, 40, 30, 120, 65), engine.LastFoundRect);
    }

    [Fact]
    public async Task If_with_is_not_on_screen_never_clicks()
    {
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).RunAsync(Aaa.MacroWith(
            new IfStep { Condition = Aaa.ImageCondition(icon, negate: true), WhenFoundAction = IfFoundAction.ClickFoundSpot },
            new KeyPressStep { VirtualKey = 0x41 },
            new EndIfStep()));

        Assert.Empty(input.Calls); // the image IS on screen, so "is NOT on screen" is false: nothing runs, nothing is clicked
    }

    [Fact]
    public async Task Click_on_the_last_found_spot_uses_the_offset_after_a_find_image_step()
    {
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen));

        var result = await engine.RunAsync(Aaa.MacroWith(
            Aaa.FindImage(icon),
            new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 100, OffsetY = -20 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 220,45", "mouse Left down", "mouse Left up" }, input.Calls);
        Assert.Equal(new FoundRect(100, 50, 40, 30, 120, 65), engine.LastFoundRect);
    }

    [Fact]
    public async Task Click_and_move_inside_an_if_use_the_spot_the_condition_found()
    {
        // Tic-Tac-Toe style: "If the board is on screen → click the cell right of its center, then move above it".
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).RunAsync(Aaa.MacroWith(
            new IfStep { Condition = Aaa.ImageCondition(icon) },
            new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 15 },
            new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetY = -40 },
            new EndIfStep()));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 135,65", "mouse Left down", "mouse Left up", "move 120,25" }, input.Calls);
    }

    [Fact]
    public async Task Last_found_spot_before_anything_was_found_is_skipped_with_a_warning()
    {
        // Changed on purpose (AAA audit fix 4): it used to stop the macro; now the move is skipped and the run goes on.
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Aaa.MacroWith(
            new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch },
            new MouseMoveStep { X = 7, Y = 8 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 7,8" }, input.Calls);
        Assert.Contains(PlaybackEngine.SkippedMoveNoTarget, messages);
    }

    [Fact]
    public async Task Move_by_an_offset_from_the_cursor()
    {
        var input = new FakeInput();

        await new PlaybackEngine(input, new FakeClock()).RunAsync(Aaa.MacroWith(
            new MouseMoveStep { X = 50, Y = 60 },
            new MouseMoveStep { TargetMode = ClickTargetMode.CurrentCursor, OffsetX = 5, OffsetY = -10 }));

        Assert.Equal(new[] { "move 50,60", "move 55,50" }, input.Calls);
    }

    [Fact]
    public async Task A_find_step_that_continues_after_a_miss_forgets_the_last_found_target()
    {
        var (screen, icon) = ScreenWithIcon();
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: new FakeCapture(screen));

        await engine.RunAsync(Aaa.MacroWith(
            Aaa.FindImage(icon),
            Aaa.FindImage(TestImages.Icon(30, 30, seed: 99), s => s.WhenNotFound = TextNotFoundBehavior.Continue)));

        Assert.Null(engine.LastFoundRect);
    }

    [Fact]
    public void Click_target_mode_is_the_same_setting_as_the_old_switches()
    {
        var click = new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch };
        Assert.Equal((true, false), (click.UseLastFoundText, click.UseCurrentPosition));
        click.TargetMode = ClickTargetMode.CurrentCursor;
        Assert.Equal((false, true), (click.UseLastFoundText, click.UseCurrentPosition));
        Assert.Equal(ClickTargetMode.Coordinates, new MouseClickStep().TargetMode);

        // An old file with "useCurrentPosition" still loads as "where the cursor is".
        var old = Assert.IsType<MouseClickStep>(MacroJson.Deserialize<MacroStep>("""{ "type": "mouseClick", "useCurrentPosition": true }"""));
        Assert.Equal(ClickTargetMode.CurrentCursor, old.TargetMode);
    }

    [Fact]
    public void Moves_and_clicks_on_the_last_found_spot_are_validated_and_described()
    {
        var macro = Aaa.MacroWith(new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 10_000 });
        var issues = MacroValidator.Validate(macro);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == nameof(MouseMoveStep.TargetMode));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Property == "Offset");

        Assert.Equal("Move to the last found spot (+10, -5)",
            StepDescriber.Describe(new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 10, OffsetY = -5 }));
        Assert.Equal("Move the cursor by (+5, 0)", StepDescriber.Describe(new MouseMoveStep { TargetMode = ClickTargetMode.CurrentCursor, OffsetX = 5 }));
        Assert.Contains("at the last found spot (+100, 0)",
            StepDescriber.Describe(new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 100 }));
        Assert.Equal(12, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch })));
    }

    [Fact]
    public void Pick_offset_reference_is_the_nearest_search_above()
    {
        var find = Aaa.FindImage(TestImages.Icon(20, 20));
        var condition = Aaa.ImageCondition(TestImages.Icon(20, 20, seed: 3));
        var negated = Aaa.ImageCondition(TestImages.Icon(20, 20, seed: 4), negate: true);
        var steps = new List<MacroStep>
        {
            find,                                   // 0
            new MouseClickStep(),                   // 1 → find
            new IfStep { Condition = condition },   // 2
            new MouseClickStep(),                   // 3 → the If's condition
            new IfStep { Condition = negated },     // 4 ("is NOT on screen" never finds anything)
            new MouseClickStep(),                   // 5 → still the first If's condition
            new EndIfStep(),
            new EndIfStep(),
        };

        Assert.Null(SmartClickConversion.LastFoundReference(steps, 0));
        Assert.Same(find, SmartClickConversion.LastFoundReference(steps, 1));
        Assert.Same(condition.Search, SmartClickConversion.LastFoundReference(steps, 3));
        Assert.Same(condition.Search, SmartClickConversion.LastFoundReference(steps, 5));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// AAA Studio Upgrade, parts 3 and 4:
//   3. Pause / Resume (F8), playback speed (0.5×–5×, holds ≥ 25 ms, live changes), Humanize (±4 % / ±2 px / arcs).
//   4. TargetDetected (the live target-lock overlay's data source).
// ---------------------------------------------------------------------------------------------------------------------

internal static class Waits
{
    /// <summary>Waits (really) until the condition is true, up to 5 s.</summary>
    public static async Task Until(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The condition didn't become true in time.");
            await Task.Delay(5);
        }
    }

    public static bool Has(FakeInput input, string call)
    {
        lock (input.Calls) return input.Calls.Contains(call);
    }
}

public class PauseResumeTests
{
    [Fact]
    public async Task Pause_releases_held_input_freezes_the_clock_and_resume_keeps_every_delay()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        var pausedEvents = new List<bool>();
        engine.PausedChanged += (_, paused) => { lock (pausedEvents) pausedEvents.Add(paused); };
        var releasesWhenPaused = -1;
        var first = true;
        input.OnCall = call =>
        {
            if (call != "key 65 down" || !first) return;
            first = false;
            Assert.True(engine.Pause());
            releasesWhenPaused = input.ReleaseAllCount; // released right away, not at the next check
        };

        var run = engine.RunAsync(Aaa.MacroWith(
            new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Down },
            new DelayStep { DurationMs = 1000 },
            new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Up },
            new DelayStep { DurationMs = 500 },
            new MouseMoveStep { X = 1, Y = 1 }));

        // Paused: the held A key was let go.
        await Waits.Until(() => Waits.Has(input, "key 65 up"));
        Assert.True(engine.IsPaused);
        Assert.Equal(1, releasesWhenPaused);

        clock.Advance(TimeSpan.FromSeconds(10)); // 10 s pass while paused
        Assert.True(engine.Resume());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "key 65 down", "key 65 up", "key 65 down", "key 65 up", "move 1,1" }, input.Calls);
        // Resumed from the same step: A is pressed again and the 1 s and 0.5 s delays still last their full time.
        Assert.Equal(10_000, input.Times[2].TotalMilliseconds, precision: 3);
        Assert.Equal(1_000, (input.Times[3] - input.Times[2]).TotalMilliseconds, precision: 3);
        Assert.Equal(500, (input.Times[4] - input.Times[3]).TotalMilliseconds, precision: 3);
        Assert.Equal(new[] { true, false }, pausedEvents);
        Assert.False(engine.IsPaused);
    }

    [Fact]
    public async Task Toggle_pause_switches_between_paused_and_playing()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        Assert.False(engine.TogglePause()); // nothing plays
        input.OnCall = call =>
        {
            if (call != "move 1,0") return;
            Assert.True(engine.TogglePause());
            Assert.True(engine.IsPaused);
            _ = Task.Run(async () => { await Task.Delay(30); engine.TogglePause(); });
        };

        var result = await engine.RunAsync(Aaa.MacroWith(new MouseMoveStep { X = 1 }, new MouseMoveStep { X = 2 })).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 1,0", "move 2,0" }, input.Calls);
    }

    [Fact]
    public void Pause_shortcut_defaults_to_F8_and_old_settings_move_to_it_once()
    {
        Assert.Equal(new HotkeyBinding(KeyModifiers.None, 0x77), HotkeyBinding.DefaultPause);
        Assert.Equal("F8", HotkeyBinding.DefaultPause.ToString());
        Assert.Null(HotkeyBinding.ProblemWithSet(HotkeyBinding.DefaultRecord, HotkeyBinding.DefaultPlay, HotkeyBinding.DefaultPause));

        var old = new AppSettings { SchemaVersion = 2, PauseHotkey = HotkeyBinding.LegacyDefaultPause };
        old.Sanitize();
        Assert.Equal(HotkeyBinding.DefaultPause, old.PauseHotkey);
        Assert.Equal(AppSettings.CurrentSchemaVersion, old.SchemaVersion);

        var custom = new AppSettings { SchemaVersion = 2, PauseHotkey = new HotkeyBinding(KeyModifiers.Alt, 0x50) };
        custom.Sanitize();
        Assert.Equal(new HotkeyBinding(KeyModifiers.Alt, 0x50), custom.PauseHotkey); // the user's own choice is kept

        var already = new AppSettings { SchemaVersion = 3, PauseHotkey = HotkeyBinding.LegacyDefaultPause };
        already.Sanitize();
        Assert.Equal(HotkeyBinding.LegacyDefaultPause, already.PauseHotkey); // picked again after the move: kept
    }
}

public class PlaybackSpeedTests
{
    private static List<(string Call, TimeSpan At)> Timed(FakeInput input) => input.Calls.Zip(input.Times, (c, t) => (c, t)).ToList();

    [Fact]
    public async Task Speed_2_halves_the_delays()
    {
        var clock = new FakeClock();
        var macro = Aaa.MacroWith(new DelayStep { DurationMs = 1000 }, new DelayStep { DurationMs = 400 }, new MouseMoveStep());
        macro.PlaybackSpeed = 2.0;

        await new PlaybackEngine(new FakeInput(), clock).RunAsync(macro);

        Assert.Equal(700, clock.Elapsed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Fast_playback_never_holds_a_click_shorter_than_25_ms()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Aaa.MacroWith(
            new MouseClickStep { X = 5, Y = 5, HoldMs = 35 },   // 35 / 2 = 17.5 ms → kept at 25 ms
            new MouseClickStep { X = 5, Y = 5, HoldMs = 200 },  // 200 / 2 = 100 ms
            new KeyPressStep { VirtualKey = 0x41, HoldMs = 10 }); // shorter than 25 ms already: kept at 10 ms
        macro.PlaybackSpeed = 2.0;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        var calls = Timed(input);
        var downs = calls.Where(c => c.Call == "mouse Left down").Select(c => c.At).ToList();
        var ups = calls.Where(c => c.Call == "mouse Left up").Select(c => c.At).ToList();
        Assert.Equal(25, (ups[0] - downs[0]).TotalMilliseconds, precision: 3);
        Assert.Equal(100, (ups[1] - downs[1]).TotalMilliseconds, precision: 3);
        var keyDown = calls.Single(c => c.Call == "key 65 down").At;
        var keyUp = calls.Single(c => c.Call == "key 65 up").At;
        Assert.Equal(10, (keyUp - keyDown).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Key_taps_and_recorded_clicks_keep_25_ms_at_5x()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Aaa.MacroWith(
            new KeyPressStep { VirtualKey = 0x57, HoldMs = 120 }, // 120 / 5 = 24 ms → 25 ms
            new InputSequenceStep { Events = { InputEvent.ButtonDown(0, MacroMouseButton.Left), InputEvent.ButtonUp(60_000, MacroMouseButton.Left) } });
        macro.PlaybackSpeed = 5.0;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        var calls = Timed(input);
        Assert.Equal(25, (calls.Single(c => c.Call == "key 87 up").At - calls.Single(c => c.Call == "key 87 down").At).TotalMilliseconds, precision: 3);
        Assert.Equal(25, (calls.Single(c => c.Call == "mouse Left up").At - calls.Single(c => c.Call == "mouse Left down").At).TotalMilliseconds, precision: 3);
    }

    [Theory]
    [InlineData(35, 1.0, 35)]
    [InlineData(35, 2.0, 25)]
    [InlineData(200, 2.0, 100)]
    [InlineData(10, 5.0, 10)]
    [InlineData(100, 0.5, 200)]
    [InlineData(0, 2.0, 0)]
    public void Scaled_hold_times(int holdMs, double speed, double expected) =>
        Assert.Equal(expected, PlaybackEngine.ScaledHoldMs(holdMs, speed), precision: 6);

    [Fact]
    public void Playback_speed_is_the_macro_speed_and_is_clamped()
    {
        var macro = new Macro { PlaybackSpeed = 1.5 };
        Assert.Equal(1.5, macro.Speed);
        macro.PlaybackSpeed = 50;
        Assert.Equal(Macro.MaxPlaybackSpeed, macro.Speed);
        macro.PlaybackSpeed = 0.01;
        Assert.Equal(Macro.MinPlaybackSpeed, macro.Speed);
        Assert.Equal(new[] { 0.5, 1.0, 1.5, 2.0, 5.0 }, Macro.SpeedPresets);
        Assert.DoesNotContain("playbackSpeed", MacroJson.Serialize(macro)); // stored once, as "speed"
    }

    [Fact]
    public async Task Speed_can_change_while_playing_from_the_next_step()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);
        input.OnCall = call => { if (call == "move 1,0") engine.SetPlaybackSpeed(4); };

        await engine.RunAsync(Aaa.MacroWith(new MouseMoveStep { X = 1 }, new DelayStep { DurationMs = 1000 }, new MouseMoveStep { X = 2 }));

        Assert.Equal(250, input.Times[1].TotalMilliseconds, precision: 3);
        Assert.Equal(4, engine.CurrentSpeed);
        Assert.Contains(messages, m => m.Contains("speed changed to 4×"));
    }
}

public class HumanizeAaaTests
{
    [Fact]
    public async Task Short_recorded_gaps_keep_their_exact_rhythm()
    {
        var macro = Aaa.MacroWith(Enumerable.Range(0, 30).Select(_ => (MacroStep)new DelayStep { DurationMs = 20 }).Append(new MouseMoveStep()).ToArray());
        macro.HumanizePlayback = true;
        var clock = new FakeClock();

        await new PlaybackEngine(new FakeInput(), clock) { HumanizeRandom = new Random(2) }.RunAsync(macro);

        Assert.Equal(600, clock.Elapsed.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task Default_humanize_is_plus_minus_2_px_and_4_percent()
    {
        Assert.Equal(2, Macro.DefaultClickJitterPx);
        Assert.Equal(4, Macro.DefaultDelayJitterPercent);

        var macro = Aaa.MacroWith(Enumerable.Range(0, 20).Select(_ => (MacroStep)new DelayStep { DurationMs = 1000 })
            .Concat(Enumerable.Range(0, 30).Select(_ => (MacroStep)new MouseClickStep { X = 500, Y = 300 })).ToArray());
        macro.HumanizePlayback = true;
        var clock = new FakeClock();
        var input = new FakeInput();

        await new PlaybackEngine(input, clock) { HumanizeRandom = new Random(4) }.RunAsync(macro);

        Assert.InRange(clock.Elapsed.TotalMilliseconds, 19_200, 20_800);
        Assert.NotEqual(20_000, Math.Round(clock.Elapsed.TotalMilliseconds));
        var moves = input.Calls.Where(c => c.StartsWith("move ")).Select(c => c[5..].Split(',').Select(int.Parse).ToArray()).ToList();
        Assert.All(moves, m =>
        {
            Assert.InRange(m[0], 498, 502);
            Assert.InRange(m[1], 298, 302);
        });
    }

    [Fact]
    public async Task Humanized_smooth_glides_bend_into_a_slight_arc()
    {
        var steps = new MacroStep[]
        {
            new MouseMoveStep { X = 0, Y = 0 },
            new MouseMoveStep { X = 300, Y = 0, MoveStyle = MouseMoveStyle.SmoothGlide, MoveDurationMs = 120 },
        };
        var straight = new FakeInput();
        await new PlaybackEngine(straight, new FakeClock()).RunAsync(Aaa.MacroWith(steps));
        Assert.All(straight.Calls, c => Assert.EndsWith(",0", c));

        var macro = Aaa.MacroWith(MacroJson.Clone(Aaa.MacroWith(steps)).Steps.ToArray());
        macro.HumanizePlayback = true;
        var human = new FakeInput();
        await new PlaybackEngine(human, new FakeClock()) { HumanizeRandom = new Random(5) }.RunAsync(macro);

        var ys = human.Calls.Select(c => int.Parse(c.Split(',')[1])).ToList();
        Assert.Equal("move 300,0", human.Calls[^1]);
        Assert.Contains(ys, y => y != 0);
        Assert.All(ys, y => Assert.InRange(Math.Abs(y), 0, (int)PlaybackEngine.HumanizeMaxArcPixels));
    }

    [Fact]
    public void Glide_arc_bows_in_the_middle_and_ends_exactly()
    {
        var points = MouseMotion.Plan((0, 0), (100, 0), MouseMoveStyle.SmoothGlide, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(8), glideArcPixels: 10);

        Assert.Equal(10, points.Count);
        Assert.Equal((50, 10), (points[4].X, points[4].Y)); // halfway: the full 10 px to the side
        Assert.Equal((100, 0), (points[^1].X, points[^1].Y));
        Assert.All(MouseMotion.Plan((0, 0), (100, 0), MouseMoveStyle.SmoothGlide, TimeSpan.FromMilliseconds(80)), p => Assert.Equal(0, p.Y));
    }
}

public class TargetDetectedTests
{
    private static (BgraImage Screen, BgraImage Icon) ScreenWithIcon()
    {
        var screen = TestImages.Blocky(400, 300, seed: 20);
        var icon = TestImages.Icon(40, 30, seed: 21);
        TestImages.Paste(screen, icon, 100, 50);
        return (screen, icon);
    }

    private static async Task<List<TargetDetection>> Detections(IScreenCapture capture, params MacroStep[] steps)
    {
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture);
        var found = new List<TargetDetection>();
        engine.TargetDetected += (_, d) => found.Add(d);
        var result = await engine.RunAsync(Aaa.MacroWith(steps));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        return found;
    }

    [Fact]
    public async Task Find_image_reports_the_locked_target()
    {
        var (screen, icon) = ScreenWithIcon();

        var found = Assert.Single(await Detections(new FakeCapture(screen), Aaa.FindImage(icon)));

        Assert.Equal(new ScreenRect(100, 50, 40, 30), found.Bounds);
        Assert.Equal((120, 65), (found.X, found.Y));
        Assert.True(found.Confidence >= 0.9, $"confidence {found.Confidence}");
        Assert.Equal("Find image", found.Label);
    }

    [Fact]
    public async Task If_condition_reports_what_it_saw_but_not_a_miss()
    {
        var (screen, icon) = ScreenWithIcon();

        var found = Assert.Single(await Detections(new FakeCapture(screen),
            new IfStep { Condition = Aaa.ImageCondition(icon) }, new MouseMoveStep(), new EndIfStep(),
            new IfStep { Condition = Aaa.ImageCondition(TestImages.Icon(30, 30, seed: 77)) }, new MouseMoveStep(), new EndIfStep()));

        Assert.Equal("If", found.Label);
        Assert.Equal(new ScreenRect(100, 50, 40, 30), found.Bounds);
    }

    [Fact]
    public async Task Find_color_and_smart_aim_report_too()
    {
        var colorScreen = TestImages.Solid(300, 200, RgbColor.Black);
        colorScreen.FillRectangle(new ScreenRect(200, 100, 11, 11), new RgbColor(0, 200, 0));
        var color = Assert.Single(await Detections(new FakeCapture(colorScreen), new FindColorStep { Color = "#00C800", Tolerance = 10, MinPixels = 20, TimeoutMs = 0 }));
        Assert.Equal(("Find color", 1.0), (color.Label, color.Confidence));
        Assert.Equal(new ScreenRect(200, 100, 11, 11), color.Bounds);

        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        click.SmartAimAtTarget = true;
        var aim = Assert.Single(await Detections(new FakeCapture(Aaa.Screen(140, 80)), click));
        Assert.Equal("Smart-Aim", aim.Label);
        Assert.Equal(new ScreenRect(140, 80, 56, 56), aim.Bounds);
    }

    [Fact]
    public async Task A_failing_overlay_never_breaks_playback()
    {
        var (screen, icon) = ScreenWithIcon();
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen));
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);
        engine.TargetDetected += (_, _) => throw new InvalidOperationException("overlay broke");

        var result = await engine.RunAsync(Aaa.MacroWith(Aaa.FindImage(icon, s => s.Action = FoundTextAction.Click)));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Contains("mouse Left down", input.Calls);
        Assert.Contains(messages, m => m.Contains("overlay broke"));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// AAA Studio Upgrade, part 5: "✨ Auto-Fix block structure" (StepCleanup.RepairBlockStructure).
// Parts 2–4 of the part 6 test list (If "when found" click + offset, last found spot, Smart-Aim 40/30, pause / speed)
// are covered by LastFoundTargetTests, SmartClickSnapshotTests, PauseResumeTests and PlaybackSpeedTests above.
// ---------------------------------------------------------------------------------------------------------------------

public class BlockRepairTests
{
    // Short names so the expected lists read like the step list in the editor.
    private static string Name(MacroStep step) => step switch
    {
        IfStep => "If",
        ElseStep => "Else",
        EndIfStep => "EndIf",
        RepeatStep => "Repeat",
        EndRepeatStep => "EndRepeat",
        _ => "x",
    };

    private static string[] Names(IEnumerable<MacroStep> steps) => steps.Select(Name).ToArray();

    private static bool IsFine(IReadOnlyList<MacroStep> steps) =>
        BlockStructure.Analyze(steps).Problems.All(p => p.Message.Contains("nested too deeply"));

    [Fact]
    public void Leading_orphan_end_ifs_are_removed()
    {
        // Like "tic tac toe auto v1": two stray End Ifs at steps 1 and 2.
        var @if = new IfStep();
        var end = new EndIfStep();
        var steps = new List<MacroStep> { new EndIfStep(), new EndIfStep(), @if, end };

        var result = StepCleanup.RepairBlockStructure(steps);

        Assert.Equal(new[] { "If", "EndIf" }, Names(steps));
        Assert.Same(@if, steps[0]);
        Assert.Same(end, steps[1]);
        Assert.True(result.Changed);
        Assert.Equal((2, 0, 0), (result.RemovedSteps, result.AddedSteps, result.FirstChangedIndex));
        Assert.Contains("Removed 2 orphan 'End If' steps", result.Summary);
        Assert.True(BlockStructure.Analyze(steps).IsValid);
    }

    [Fact]
    public void Unclosed_if_and_repeat_get_their_ends_added_at_the_end_innermost_first()
    {
        var click = new MouseClickStep();
        var steps = new List<MacroStep> { new RepeatStep(), new IfStep(), click };

        var result = StepCleanup.RepairBlockStructure(steps);

        Assert.Equal(new[] { "Repeat", "If", "x", "EndIf", "EndRepeat" }, Names(steps));
        Assert.Same(click, steps[2]);
        Assert.Equal((0, 2, 3), (result.RemovedSteps, result.AddedSteps, result.FirstChangedIndex));
        Assert.Contains("Added 1 missing 'End If' step at the end", result.Summary);
        Assert.Contains("Added 1 missing 'End Repeat' step at the end", result.Summary);
        Assert.True(BlockStructure.Analyze(steps).IsValid);
    }

    [Fact]
    public void Extra_else_and_orphan_else_and_end_repeat_are_removed()
    {
        var steps = new List<MacroStep>
        {
            new ElseStep(),                          // no If yet
            new IfStep(), new MouseClickStep(),
            new ElseStep(), new MouseClickStep(),
            new ElseStep(), new MouseClickStep(),    // second Else in the same If
            new EndIfStep(),
            new EndRepeatStep(),                     // no Repeat at all
        };

        var result = StepCleanup.RepairBlockStructure(steps);

        Assert.Equal(new[] { "If", "x", "Else", "x", "x", "EndIf" }, Names(steps));
        Assert.Equal(3, result.RemovedSteps);
        Assert.Contains("Removed 1 orphan 'Else' step", result.Summary);
        Assert.Contains("Removed 1 extra 'Else' step", result.Summary);
        Assert.Contains("Removed 1 orphan 'End Repeat' step", result.Summary);
        Assert.True(BlockStructure.Analyze(steps).IsValid);
    }

    [Fact]
    public void Overlapping_blocks_are_closed_in_the_right_order_and_no_step_is_lost()
    {
        // Repeat, If, click, End Repeat, delay, End If: the If was never closed inside the Repeat.
        var click = new MouseClickStep();
        var delay = new DelayStep { DurationMs = 100 };
        var steps = new List<MacroStep> { new RepeatStep(), new IfStep(), click, new EndRepeatStep(), delay, new EndIfStep() };

        var result = StepCleanup.RepairBlockStructure(steps);

        Assert.Equal(new[] { "Repeat", "If", "x", "EndIf", "EndRepeat", "x" }, Names(steps));
        Assert.Same(click, steps[2]);
        Assert.Same(delay, steps[5]);
        Assert.Equal((1, 1, 3), (result.RemovedSteps, result.AddedSteps, result.FirstChangedIndex));
        Assert.True(BlockStructure.Analyze(steps).IsValid);
    }

    [Fact]
    public void A_list_that_already_matches_up_is_left_alone()
    {
        var steps = new List<MacroStep>
        {
            new RepeatStep(), new IfStep(), new MouseClickStep(), new ElseStep(), new DelayStep(), new EndIfStep(), new EndRepeatStep(),
        };
        var before = steps.ToList();

        var result = StepCleanup.RepairBlockStructure(steps);

        Assert.False(result.Changed);
        Assert.Equal(-1, result.FirstChangedIndex);
        Assert.Equal(BlockRepairResult.NothingToFixText, result.Summary);
        Assert.Equal(before, steps); // same objects, same order
        Assert.False(StepCleanup.NeedsBlockRepair(steps));
        Assert.False(StepCleanup.NeedsBlockRepair(new List<MacroStep> { new MouseClickStep() }));
    }

    [Fact]
    public void Needs_repair_does_not_touch_the_list()
    {
        var steps = new List<MacroStep> { new EndIfStep(), new MouseClickStep() };
        Assert.True(StepCleanup.NeedsBlockRepair(steps));
        Assert.Equal(new[] { "EndIf", "x" }, Names(steps));
    }

    [Fact]
    public void After_the_fix_the_validator_has_no_block_errors()
    {
        var macro = Aaa.MacroWith(
            new EndIfStep(), new EndIfStep(),
            new IfStep(), new KeyPressStep { VirtualKey = 0x41 }, new EndIfStep(),
            new RepeatStep { Mode = RepeatMode.Times, Count = 2 }, new KeyPressStep { VirtualKey = 0x42 });
        Assert.Contains(MacroValidator.Validate(macro), i => i.Property == "Block" && i.Severity == IssueSeverity.Error);

        StepCleanup.RepairBlockStructure(macro.Steps);

        Assert.DoesNotContain(MacroValidator.Validate(macro), i => i.Property == "Block" && i.Severity == IssueSeverity.Error);
        Assert.Equal(new[] { "If", "x", "EndIf", "Repeat", "x", "EndRepeat" }, Names(macro.Steps));
    }

    [Fact]
    public void Random_step_lists_always_come_out_valid_and_a_second_fix_changes_nothing()
    {
        var random = new Random(1234);
        Func<MacroStep>[] makers =
        {
            () => new IfStep(), () => new RepeatStep(), () => new ElseStep(), () => new EndIfStep(), () => new EndRepeatStep(),
            () => new MouseClickStep(), () => new DelayStep(),
        };

        for (var round = 0; round < 3_000; round++)
        {
            var steps = Enumerable.Range(0, random.Next(0, 15)).Select(_ => makers[random.Next(makers.Length)]()).ToList();
            var normal = steps.Where(s => s is not (ElseStep or EndIfStep or EndRepeatStep)).ToList();
            var wasFine = IsFine(steps);

            var result = StepCleanup.RepairBlockStructure(steps);

            Assert.True(IsFine(steps), $"Round {round}: {string.Join(", ", Names(steps))}");
            Assert.Equal(!wasFine, result.Changed);
            Assert.Equal(normal, steps.Where(s => s is not (ElseStep or EndIfStep or EndRepeatStep)).ToList()); // nothing else lost or moved
            Assert.False(StepCleanup.RepairBlockStructure(steps).Changed);
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Audit of a33c29f: pause inside long steps, speed on key holds / recorded input, "last found spot" before anything was
// found (skip, never (0, 0)), never scaling the found spot, click pictures inside recorded input + "✂ Break into steps",
// lazy PNG encoding of click snapshots and the shared Smart-Aim locator.
// ---------------------------------------------------------------------------------------------------------------------

public class AuditFixTests
{
    private static List<(string Call, TimeSpan At)> Timed(FakeInput input)
    {
        lock (input.Calls) return input.Calls.Zip(input.Times, (c, t) => (c, t)).ToList();
    }

    // ---------------- Fix 2: pause inside a step ----------------

    [Fact]
    public async Task Pause_inside_recorded_input_lets_go_and_resume_presses_again_with_the_rest_of_the_timing()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        input.OnCall = call =>
        {
            if (call == "rel 5,0") Assert.True(engine.Pause()); // F8 while W is held and the camera turns
        };
        var sequence = new InputSequenceStep
        {
            Events = { InputEvent.KeyDown(0, 0x57), InputEvent.Move(100_000, 5, 0), InputEvent.KeyUp(1_000_000, 0x57) },
        };

        var run = engine.RunAsync(Aaa.MacroWith(sequence));
        await Waits.Until(() => Waits.Has(input, "key 87 up"));
        Assert.True(engine.IsPaused);
        Assert.Equal(1, input.ReleaseAllCount); // everything let go the moment F8 was pressed

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(engine.Resume());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var calls = Timed(input);
        Assert.Equal(new[] { "key 87 down", "rel 5,0", "key 87 up", "key 87 down", "key 87 up" }, calls.Select(c => c.Call));
        Assert.Equal(100, calls[2].At.TotalMilliseconds, precision: 3);                  // W released while paused
        Assert.Equal(5_100, calls[3].At.TotalMilliseconds, precision: 3);                // W pressed again on resume
        Assert.Equal(900, (calls[4].At - calls[3].At).TotalMilliseconds, precision: 3);  // only the 900 ms that were left
    }

    [Fact]
    public async Task Pause_during_a_long_key_hold_releases_it_and_resume_finishes_the_hold()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var engine = new PlaybackEngine(input, clock);
        var first = true;
        input.OnCall = call =>
        {
            if (call != "key 87 down" || !first) return;
            first = false;
            Assert.True(engine.Pause());
        };

        var run = engine.RunAsync(Aaa.MacroWith(new KeyPressStep { VirtualKey = 0x57, HoldMs = 1000 }));
        await Waits.Until(() => Waits.Has(input, "key 87 up"));
        Assert.True(engine.IsPaused);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(engine.Resume());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var calls = Timed(input);
        Assert.Equal(("key 87 up", 0.0), (calls[1].Call, calls[1].At.TotalMilliseconds));      // released for the pause
        Assert.Equal(("key 87 down", 10_000.0), (calls[2].Call, calls[2].At.TotalMilliseconds)); // pressed again
        Assert.Equal("key 87 up", calls[^1].Call);
        Assert.Equal(11_000, calls[^1].At.TotalMilliseconds, precision: 3); // still held 1 s in total, paused time not counted
        Assert.Equal(2, calls.Count(c => c.Call == "key 87 up"));
    }

    // ---------------- Fix 5: speed on movement holds and recorded input ----------------

    [Fact]
    public async Task Speed_2_halves_a_1000_ms_movement_hold_and_keeps_short_taps_at_least_25_ms()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Aaa.MacroWith(
            new KeyPressStep { VirtualKey = 0x57, HoldMs = 1000 }, // walk forward 1 s
            new KeyPressStep { VirtualKey = 0x41, HoldMs = 30 });  // a 30 ms tap
        macro.PlaybackSpeed = 2.0;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        var calls = Timed(input);
        var walk = calls.Single(c => c.Call == "key 87 up").At - calls.First(c => c.Call == "key 87 down").At;
        var tap = calls.Single(c => c.Call == "key 65 up").At - calls.Single(c => c.Call == "key 65 down").At;
        Assert.Equal(500, walk.TotalMilliseconds, precision: 3);
        Assert.Equal(25, tap.TotalMilliseconds, precision: 3);
        Assert.True(tap.TotalMilliseconds >= PlaybackEngine.MinScaledHoldMs);
    }

    [Fact]
    public async Task Recorded_camera_turns_and_movement_keys_stay_locked_together_at_2x()
    {
        var clock = new FakeClock();
        var input = new FakeInput { Clock = clock };
        var macro = Aaa.MacroWith(new InputSequenceStep
        {
            Events = { InputEvent.KeyDown(0, 0x57), InputEvent.Move(400_000, 7, 0), InputEvent.KeyUp(1_000_000, 0x57) },
        });
        macro.PlaybackSpeed = 2.0;

        await new PlaybackEngine(input, clock).RunAsync(macro);

        Assert.Equal(new[] { ("key 87 down", 0.0), ("rel 7,0", 200.0), ("key 87 up", 500.0) },
            Timed(input).Select(c => (c.Call, Math.Round(c.At.TotalMilliseconds, 3))));
    }

    // ---------------- Fix 4: the last found spot ----------------

    [Fact]
    public async Task Click_on_the_last_found_spot_before_anything_was_found_is_skipped_never_at_0_0()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());
        var messages = new List<string>();
        engine.Message += (_, m) => messages.Add(m);

        var result = await engine.RunAsync(Aaa.MacroWith(
            new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 10 },
            new KeyPressStep { VirtualKey = 0x41 }));

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "key 65 down", "key 65 up" }, input.Calls); // no move, no click
        Assert.Contains(PlaybackEngine.SkippedClickNoTarget, messages);
        Assert.StartsWith("⚠", PlaybackEngine.SkippedClickNoTarget); // logged as a warning by the app
    }

    [Fact]
    public async Task Run_step_on_a_last_found_click_alone_is_skipped_too()
    {
        var screen = TestImages.Blocky(400, 300, seed: 20);
        var icon = TestImages.Icon(40, 30, seed: 21);
        TestImages.Paste(screen, icon, 100, 50);
        var input = new FakeInput();
        var macro = Aaa.MacroWith(Aaa.FindImage(icon), new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch });

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen)).PlaySingleStepAsync(macro, 1);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void Validator_flags_a_last_found_step_with_no_vision_step_before_it()
    {
        var lonely = MacroValidator.Validate(Aaa.MacroWith(new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch }));
        Assert.Contains(lonely, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 0 && i.Message.Contains("skipped"));

        var lonelyMove = MacroValidator.Validate(Aaa.MacroWith(new MouseMoveStep { TargetMode = ClickTargetMode.LastFoundMatch }));
        Assert.Contains(lonelyMove, i => i.Severity == IssueSeverity.Warning && i.StepIndex == 0 && i.Message.Contains("skipped"));

        // After an If with an image condition (or any Find step) it's fine.
        var guarded = MacroValidator.Validate(Aaa.MacroWith(
            new IfStep { Condition = Aaa.ImageCondition(Aaa.Button) },
            new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch },
            new EndIfStep()));
        Assert.DoesNotContain(guarded, i => i.Property == nameof(MouseClickStep.UseLastFoundText));
    }

    [Fact]
    public async Task Auto_adjust_coordinates_never_scales_the_found_spot_or_its_offset()
    {
        // The macro was made on a 200×150 screen, it plays on 400×300 (×2). Fixed positions double; the found spot is
        // live screen pixels already, and the offset belongs to a template matched at its recorded size.
        var screen = TestImages.Blocky(400, 300, seed: 20);
        var icon = TestImages.Icon(40, 30, seed: 21);
        TestImages.Paste(screen, icon, 100, 50); // center (120, 65)
        var layout = new FakeLayout();
        layout.Monitors.Add(new MonitorInfo(1, new ScreenRect(0, 0, 400, 300), true));
        var macro = Aaa.MacroWith(
            new MouseMoveStep { X = 50, Y = 50 },
            Aaa.FindImage(icon),
            new MouseClickStep { TargetMode = ClickTargetMode.LastFoundMatch, OffsetX = 10, OffsetY = -5 });
        macro.ScaleCoordinatesToWindow = true;
        (macro.ReferenceWidth, macro.ReferenceHeight) = (200, 150);
        var input = new FakeInput();

        var result = await new PlaybackEngine(input, new FakeClock(), capture: new FakeCapture(screen), layout: layout).RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "move 100,100", "move 130,60", "mouse Left down", "mouse Left up" }, input.Calls);
    }

    // ---------------- Fix 3: click pictures inside recorded input, "✂ Break into steps", lazy PNGs ----------------

    private static InputSequenceStep MenuRecording(string picture) => new()
    {
        Events =
        {
            InputEvent.Move(20_000, 3, 0, 100, 100),
            InputEvent.ButtonDown(250_000, MacroMouseButton.Left, 128, 78),
            InputEvent.ButtonUp(330_000, MacroMouseButton.Left, 128, 78),
            InputEvent.KeyDown(600_000, 0x41),
            InputEvent.KeyUp(680_000, 0x41),
        },
        MouseSegments = new List<MouseSegment> { new() { StartUs = 0, Detected = MouseMovementMode.CursorPosition } },
        ClickTargets = new List<SequenceClickTarget> { new() { EventIndex = 1, TargetThumbnailBase64 = picture, AnchorX = 28, AnchorY = 28 } },
        Comment = "open the menu",
    };

    [Fact]
    public void Break_into_steps_keeps_the_click_picture()
    {
        var picture = PngCodec.EncodeBase64(Aaa.Button);
        var sequence = MenuRecording(picture);
        Assert.Null(StepCleanup.WhyNotExpandable(sequence));

        var steps = StepCleanup.ExpandInputSequence(sequence);

        var click = Assert.Single(steps.OfType<MouseClickStep>());
        Assert.Equal((128, 78, MouseClickAction.Click), (click.X, click.Y, click.Action));
        Assert.Equal(picture, click.TargetThumbnailBase64);
        Assert.Equal((28, 28), (click.TargetAnchorX!.Value, click.TargetAnchorY!.Value));
        Assert.Contains(steps, s => s is KeyPressStep { VirtualKey: 0x41 });
        Assert.Equal(20, Assert.IsType<DelayStep>(steps[0]).DurationMs); // the time before the first event
        Assert.Equal("open the menu", steps[0].Comment);
        Assert.Null(SmartClickConversion.WhyNotConvertible(click)); // the picture is usable (Smart-Aim / Convert)
    }

    [Fact]
    public void Smart_recording_keeps_pictures_inside_recorded_input_and_break_into_steps_gives_them_back()
    {
        var snapshot = new ClickTargetSnapshot(PngCodec.Encode(Aaa.Button), 20, 30);
        var events = new List<CapturedInputEvent>();
        for (var k = 0; k < 10; k++)
            events.Add(new CapturedInputEvent(InputEvent.Move(k * 10_000L, 5, 0, 150 + 5 * (k + 1), 100)));
        events.Add(new CapturedInputEvent(InputEvent.ButtonDown(200_000, MacroMouseButton.Left, 200, 100), CursorFlags.None, snapshot));
        events.Add(new CapturedInputEvent(InputEvent.ButtonUp(280_000, MacroMouseButton.Left, 200, 100)));

        // Exact 1:1 mode keeps the whole recording as one "Recorded input" step.
        var result = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { ExactTimeline = true });

        var sequence = Assert.IsType<InputSequenceStep>(Assert.Single(result.Steps));
        var target = Assert.Single(sequence.ClickTargets!);
        Assert.Equal(InputEventKind.MouseDown, sequence.Events[target.EventIndex].Kind);
        Assert.Equal(Convert.ToBase64String(snapshot.Png), target.TargetThumbnailBase64);
        Assert.Equal((20, 30), (target.AnchorX, target.AnchorY));

        var click = Assert.Single(StepCleanup.ExpandInputSequence(sequence).OfType<MouseClickStep>());
        Assert.Equal((200, 100), (click.X, click.Y));
        Assert.Equal(target.TargetThumbnailBase64, click.TargetThumbnailBase64);
    }

    [Fact]
    public void Camera_recordings_cannot_be_broken_into_steps()
    {
        var game = new InputSequenceStep
        {
            Events = { InputEvent.Move(0, 5, 0), InputEvent.ButtonDown(10_000, MacroMouseButton.Left), InputEvent.ButtonUp(50_000, MacroMouseButton.Left) },
        };
        Assert.Contains("game mode", StepCleanup.WhyNotExpandable(game));
        Assert.Throws<InvalidOperationException>(() => StepCleanup.ExpandInputSequence(game));

        var camera = MenuRecording(PngCodec.EncodeBase64(Aaa.Button));
        camera.MouseSegments![0].Detected = MouseMovementMode.Relative;
        Assert.Contains("camera", StepCleanup.WhyNotExpandable(camera));

        var keysOnly = new InputSequenceStep { Events = { InputEvent.KeyDown(0, 0x57), InputEvent.KeyUp(500_000, 0x57) } };
        Assert.Null(StepCleanup.WhyNotExpandable(keysOnly));
        Assert.Equal(500, Assert.Single(StepCleanup.ExpandInputSequence(keysOnly).OfType<KeyPressStep>()).HoldMs);
    }

    [Fact]
    public void Pictures_inside_recorded_input_are_saved_need_format_12_and_are_checked()
    {
        var plain = new InputSequenceStep { Events = { InputEvent.KeyDown(0, 0x41), InputEvent.KeyUp(1000, 0x41) } };
        Assert.DoesNotContain("clickTargets", MacroJson.Serialize<MacroStep>(plain));
        Assert.True(MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(plain)) < 12);

        var sequence = MenuRecording(PngCodec.EncodeBase64(Aaa.Button));
        Assert.Equal(12, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(sequence)));
        var json = MacroJson.Serialize<MacroStep>(sequence);
        Assert.Contains("clickTargets", json);
        Assert.Contains("\"thumbnail\"", json);
        var copy = Assert.IsType<InputSequenceStep>(MacroJson.CloneStep(sequence));
        var target = Assert.Single(copy.ClickTargets!);
        Assert.Equal((1, 28, 28), (target.EventIndex, target.AnchorX, target.AnchorY));
        Assert.Equal(sequence.ClickTargets![0].TargetThumbnailBase64, target.TargetThumbnailBase64);
        Assert.DoesNotContain(MacroValidator.Validate(Aaa.MacroWith(sequence)), i => i.Property == nameof(InputSequenceStep.ClickTargets));

        target.EventIndex = 0; // a Move, not a button press
        Assert.Contains(MacroValidator.Validate(Aaa.MacroWith(copy)),
            i => i.Severity == IssueSeverity.Warning && i.Property == nameof(InputSequenceStep.ClickTargets));
    }

    [Fact]
    public async Task Click_snapshots_encode_their_png_later_and_only_once()
    {
        var snapshot = ClickSnapshots.Create(Aaa.Button, new ScreenRect(100, 50, 56, 56), 128, 78)!;
        Assert.False(snapshot.IsEncoded); // the hook thread only paid for the pixel copy
        Assert.Equal((28, 28), (snapshot.AnchorX, snapshot.AnchorY));

        snapshot.EncodeInBackground();
        await Waits.Until(() => snapshot.IsEncoded);
        var png = snapshot.Png;
        Assert.Same(png, snapshot.Png); // encoded once
        Assert.True(PngCodec.TryDecodeBase64(Convert.ToBase64String(png), out var image, out _));
        Assert.Equal(Aaa.Button.GetPixel(10, 40), image!.GetPixel(10, 40));

        var step = new MouseClickStep();
        ClickSnapshots.Attach(step, ClickSnapshots.Create(Aaa.Button, new ScreenRect(0, 0, 56, 56), 28, 28)); // encoded on demand
        Assert.True(step.HasTargetThumbnail);
    }

    // ---------------- Upgrade 7: the shared Smart-Aim locator ("🎯 Test Smart-Aim" uses it too) ----------------

    [Fact]
    public void Smart_aim_locator_finds_the_moved_target_like_playback()
    {
        var click = Aaa.RecordedClick(Aaa.Screen(100, 50), 128, 78);
        var template = SmartClickConversion.TryDecodeThumbnail(click, out _)!;

        Assert.Equal(new ScreenRect(100 - 120, 50 - 120, 56 + 240, 56 + 240), SmartAim.SearchArea(click, template));

        var (hit, _) = SmartAim.Locate(click, template, new ScreenFrame(new ScreenRect(0, 0, 400, 300), Aaa.Screen(140, 80)));
        Assert.NotNull(hit);
        Assert.Equal((168, 108, 40, 30), (hit!.X, hit.Y, hit.ShiftX, hit.ShiftY));
        Assert.True(hit.Moved);
        Assert.Equal(new ScreenRect(140, 80, 56, 56), hit.Bounds);

        var (miss, best) = SmartAim.Locate(click, template, new ScreenFrame(new ScreenRect(0, 0, 400, 300), Aaa.Screen()));
        Assert.Null(miss);
        Assert.True(best < click.SmartAimAccuracy);
    }
}
