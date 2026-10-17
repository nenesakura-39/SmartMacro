using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

public class QolUpgradesTests
{
    // =========================================================================
    // Upgrade 1: Multi-Color / Dual-State Support for Feedback Loop & FindColor
    // =========================================================================

    [Fact]
    public void ZoneFinder_DetectsControlBar_WithPrimaryOrSecondaryColor()
    {
        var brown = new RgbColor(139, 90, 43);
        var white = new RgbColor(245, 245, 250);
        var bg = new RgbColor(25, 30, 40);

        // Frame 1: Control bar is brown (outside target) from 30% to 60%
        var imgBrown = TestImages.Solid(201, 20, bg);
        imgBrown.FillRectangle(new ScreenRect(60, 0, 61, 20), brown);

        var resBrown = ZoneFinder.Find(imgBrown, brown, white, tolerance: 20, minPixels: 5, BarOrientation.Horizontal);
        Assert.True(resBrown.Found);
        Assert.InRange(resBrown.StartPercent, 29.0, 31.0);
        Assert.InRange(resBrown.EndPercent, 59.0, 61.0);

        // Frame 2: Control bar turns white (overlapping target) from 40% to 70%
        var imgWhite = TestImages.Solid(201, 20, bg);
        imgWhite.FillRectangle(new ScreenRect(80, 0, 61, 20), white);

        var resWhite = ZoneFinder.Find(imgWhite, brown, white, tolerance: 20, minPixels: 5, BarOrientation.Horizontal);
        Assert.True(resWhite.Found);
        Assert.InRange(resWhite.StartPercent, 39.0, 41.0);
        Assert.InRange(resWhite.EndPercent, 69.0, 71.0);
    }

    [Fact]
    public async Task FeedbackSensor_SmartOverlap_KeepsTargetFoundWhenMarkerOccludedByActiveBar()
    {
        var brown = new RgbColor(139, 90, 43);
        var white = new RgbColor(250, 250, 255);
        var fishColor = new RgbColor(60, 180, 220);
        var bg = new RgbColor(20, 24, 32);

        // Bar is 201x20 at (0,0). The control bar is currently overlapping the fish at 40%..70% (x = 80..140, center = 55%)
        // Because the bar turned white on overlap, the fish marker is completely occluded by the white bar!
        var screen = TestImages.Solid(201, 20, bg);
        screen.FillRectangle(new ScreenRect(80, 0, 61, 20), white);

        var capture = new StaticFrameCapture(screen);
        var step = new FeedbackLoopStep
        {
            RegionX = 0,
            RegionY = 0,
            RegionWidth = 201,
            RegionHeight = 20,
            Orientation = BarOrientation.Horizontal,
            MarkerColor = fishColor.ToString(),
            MarkerTolerance = 20,
            MarkerMinPixels = 3,
            ZoneMode = FeedbackZoneMode.DetectedByColor,
            ZoneColor = brown.ToString(),
            UseSecondaryZoneColor = true,
            SecondaryZoneColor = white.ToString(),
            ZoneTolerance = 20,
            ZoneMinPixels = 5,
        };

        var sensor = new FeedbackSensor(step, capture);
        var frame = await sensor.ReadAsync(CancellationToken.None);

        // Smart Overlap must detect that SecondaryZoneColor (white) is active on screen and use its centroid (~55%)
        // instead of declaring MarkerStatus.Missing!
        Assert.Equal(MarkerStatus.Found, frame.Marker.Status);
        Assert.True(frame.Zone.Found);
        Assert.InRange(frame.Zone.StartPercent, 39.0, 41.0);
        Assert.InRange(frame.Zone.EndPercent, 69.0, 71.0);
        Assert.NotNull(frame.Marker.PositionPercent);
        Assert.InRange(frame.Marker.PositionPercent!.Value, 53.0, 57.0);
    }

    [Fact]
    public async Task FindColorStep_And_IfStep_MatchSecondaryColor()
    {
        var primary = new RgbColor(220, 40, 40);
        var secondary = new RgbColor(40, 220, 80);
        var bg = new RgbColor(15, 15, 15);

        // Screen only contains the secondary color at (50, 50)
        var screen = TestImages.Solid(100, 100, bg);
        screen.FillRectangle(new ScreenRect(45, 45, 10, 10), secondary);
        var capture = new StaticFrameCapture(screen);

        var findColor = new FindColorStep
        {
            Color = primary.ToString(),
            UseSecondaryColor = true,
            SecondaryColor = secondary.ToString(),
            Tolerance = 15,
            MinPixels = 10,
            SearchFullScreen = true,
        };

        var probes = new ScreenProbes(null, capture);
        var hit = await probes.Prepare(findColor).LookAsync(null, CancellationToken.None);
        Assert.True(hit.Found);
        Assert.InRange(hit.X, 45, 55);
        Assert.InRange(hit.Y, 45, 55);
    }

    // =========================================================================
    // Upgrade 3: Day/Night Lighting-Invariant Image Matching
    // =========================================================================

    [Fact]
    public void TemplateMatcher_AdaptToLighting_MatchesDayTemplateOnNightScreen_AndRejectsDifferentColor()
    {
        // Create a daytime button template (40x24): green button with a bright yellow icon inside
        var dayBg = new RgbColor(40, 180, 70);
        var dayIcon = new RgbColor(250, 240, 90);
        var dayBorder = new RgbColor(20, 90, 35);

        var template = TestImages.Solid(40, 24, dayBg);
        template.FillRectangle(new ScreenRect(0, 0, 40, 3), dayBorder);
        template.FillRectangle(new ScreenRect(0, 21, 40, 3), dayBorder);
        template.FillRectangle(new ScreenRect(12, 6, 16, 12), dayIcon);

        // Create a nighttime screen (200x120) where everything is darkened (0.45x brightness + cool night tint)
        static RgbColor Nightify(RgbColor c) => new(
            (byte)Math.Clamp((int)(c.R * 0.42), 0, 255),
            (byte)Math.Clamp((int)(c.G * 0.45), 0, 255),
            (byte)Math.Clamp((int)(c.B * 0.55 + 12), 0, 255));

        var nightScreen = TestImages.Solid(200, 120, new RgbColor(12, 16, 28));
        // Place the night-shaded button at (80, 50)
        nightScreen.FillRectangle(new ScreenRect(80, 50, 40, 24), Nightify(dayBg));
        nightScreen.FillRectangle(new ScreenRect(80, 50, 40, 3), Nightify(dayBorder));
        nightScreen.FillRectangle(new ScreenRect(80, 71, 40, 3), Nightify(dayBorder));
        nightScreen.FillRectangle(new ScreenRect(92, 56, 16, 12), Nightify(dayIcon));

        var result = TemplateMatcher.Find(
            nightScreen,
            template,
            new TemplateMatchOptions { MinScore = 0.80, AdaptToLighting = true });

        Assert.NotNull(result.Best);
        Assert.InRange(result.Best!.Bounds.X, 78, 82);
        Assert.InRange(result.Best!.Bounds.Y, 48, 52);

        // Now place a RED button of the exact same shape on a screen and verify Chroma check rejects it
        var redScreen = TestImages.Solid(200, 120, new RgbColor(20, 20, 25));
        redScreen.FillRectangle(new ScreenRect(80, 50, 40, 24), new RgbColor(190, 40, 45));
        redScreen.FillRectangle(new ScreenRect(80, 50, 40, 3), new RgbColor(95, 20, 22));
        redScreen.FillRectangle(new ScreenRect(80, 71, 40, 3), new RgbColor(95, 20, 22));
        redScreen.FillRectangle(new ScreenRect(92, 56, 16, 12), new RgbColor(90, 200, 250));

        var wrongColorResult = TemplateMatcher.Find(
            redScreen,
            template,
            new TemplateMatchOptions { MinScore = 0.80, AdaptToLighting = true });

        Assert.Null(wrongColorResult.Best);
    }

    // =========================================================================
    // Upgrade 4: Nested If / Else / End If & Repeat Block Structure Pairing
    // =========================================================================

    [Fact]
    public void BlockStructure_CorrectlyPairsNestedIfElseAndRepeatBlocks()
    {
        var steps = new List<MacroStep>
        {
            new IfStep(),          // 0: If #1 (depth 0)
            new DelayStep(),       // 1: inside If #1 (depth 1)
            new IfStep(),          // 2: If #2 (depth 1)
            new DelayStep(),       // 3: inside If #2 (depth 2)
            new EndIfStep(),       // 4: End If #2 (depth 1)
            new ElseStep(),        // 5: Else #1 (depth 0)
            new RepeatStep(),      // 6: Repeat #1 (depth 1)
            new DelayStep(),       // 7: inside Repeat #1 (depth 2)
            new EndRepeatStep(),   // 8: End Repeat #1 (depth 1)
            new EndIfStep(),       // 9: End If #1 (depth 0)
        };

        var structure = BlockStructure.Analyze(steps);
        Assert.True(structure.IsValid);

        // Outer If #1 at index 0 pairs with Else at 5 and EndIf at 9
        Assert.Equal(5, structure.ElseOf(0));
        Assert.Equal(9, structure.EndOf(0));
        Assert.Equal(0, structure.Depth[0]);
        Assert.Equal(0, structure.Depth[5]);
        Assert.Equal(0, structure.Depth[9]);

        // Inner If #2 at index 2 has no Else (-1) and pairs with EndIf at 4
        Assert.Equal(-1, structure.ElseOf(2));
        Assert.Equal(4, structure.EndOf(2));
        Assert.Equal(1, structure.Depth[2]);
        Assert.Equal(1, structure.Depth[4]);

        // Inner Repeat #1 at index 6 pairs with EndRepeat at 8
        Assert.Equal(8, structure.EndOf(6));
        Assert.Equal(1, structure.Depth[6]);
        Assert.Equal(1, structure.Depth[8]);
    }

    // =========================================================================
    // Upgrade 5: Rhythm / Exact 1:1 Recording Mode
    // =========================================================================

    [Fact]
    public void SmartRecordingConverter_RhythmMode_PreservesExact1To1TimingAndPoints()
    {
        var events = new List<CapturedInputEvent>
        {
            new(InputEvent.KeyDown(0, 0x51)),       // Q down at 0 us
            new(InputEvent.KeyUp(42_000, 0x51)),    // Q up at 42 ms
            new(InputEvent.KeyDown(85_000, 0x57)),  // W down at 85 ms
            new(InputEvent.KeyUp(127_000, 0x57)),   // W up at 127 ms
            new(InputEvent.Move(150_000, 2, 1, 102, 101)),
            new(InputEvent.Move(158_000, 3, 1, 105, 102)),
            new(InputEvent.Move(166_000, 4, 2, 109, 104)),
        };

        var result = SmartRecordingConverter.Convert(events, new SmartRecordingOptions { ExactTimeline = true });
        var seqStep = Assert.Single(result.Steps.OfType<InputSequenceStep>());
        // Zero simplification: all 7 events are preserved 1:1 with exact microsecond timestamps
        Assert.Equal(7, seqStep.Events.Count);
        Assert.Equal(0, seqStep.Events[0].TimeUs);
        Assert.Equal(42_000, seqStep.Events[1].TimeUs);
        Assert.Equal(85_000, seqStep.Events[2].TimeUs);
        Assert.Equal(166_000, seqStep.Events[6].TimeUs);
    }

    private sealed class StaticFrameCapture(BgraImage image) : IScreenCapture
    {
        public ScreenRect GetVirtualScreen() => new(0, 0, image.Width, image.Height);

        public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken)
        {
            var area = region.Intersect(GetVirtualScreen());
            return Task.FromResult(new ScreenFrame(area, image.Crop(area)));
        }
    }
}
