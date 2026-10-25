using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

/// <summary>
/// Unit tests for the AAA Closed-Loop PWM PD+FeedForward Servo Controller and 1D Bar/Target Vision Scanner:
/// 1. Bar Split / Occlusion Reconstruction
/// 2. Vertical Axis Auto-Inversion
/// 3. Ankle-Break Braking
/// 4. Serialization Round-Trip
/// </summary>
public class FeedbackServoTests
{
    [Fact]
    public void Bar_Split_And_Occlusion_Reconstruction_Bridges_Middle_Gap_And_Returns_True_Midpoint()
    {
        // Simulate a 1D pixel row (300px wide, 1px tall) with a white bar from x=100..200
        // and a 15px gap in the middle (x=142..157 covered by an orange fish).
        const int width = 300;
        const int height = 1;
        var pixels = new byte[width * height * 4];
        var white = new RgbColor(255, 255, 255);
        var fishColor = new RgbColor(255, 128, 0);

        for (var x = 100; x <= 200; x++)
        {
            var isFishGap = x >= 142 && x <= 157;
            var c = isFishGap ? fishColor : white;
            var idx = x * 4;
            pixels[idx] = c.B;
            pixels[idx + 1] = c.G;
            pixels[idx + 2] = c.R;
            pixels[idx + 3] = 255;
        }

        var image = new BgraImage(width, height, pixels);
        var tracker = new FeedbackLoopTracker();

        var targetScan = tracker.ScanTarget(image, fishColor, tolerance: 20, BarOrientation.Horizontal);
        var indicatorScan = tracker.ScanIndicator(image, white, tolerance: 20, BarOrientation.Horizontal, targetScan);

        Assert.True(targetScan.Found);
        Assert.InRange(targetScan.CenterPos, 148.5, 150.5);

        Assert.True(indicatorScan.Found);
        Assert.Equal(100.0, indicatorScan.MinEdge, precision: 1);
        Assert.Equal(200.0, indicatorScan.MaxEdge, precision: 1);
        Assert.Equal(100.0, indicatorScan.SpanLength, precision: 1);
        Assert.InRange(indicatorScan.CenterPos, 149.5, 150.5);
    }

    [Fact]
    public void Edge_Occlusion_Reconstructs_True_Center_From_Estimated_Bar_Width()
    {
        var tracker = new FeedbackLoopTracker { EstimatedBarWidth = 100.0 };

        // Simulate fish sitting on the left edge of the bar (around x=115), occluding x=100..145 so only x=146..200 (54px < 70px) is visible.
        var counts = new int[300];
        for (var x = 146; x <= 200; x++)
            counts[x] = 5;

        var fishScan = new BarScanResult(Found: true, CenterPos: 120.0, MinEdge: 105.0, MaxEdge: 145.0, SpanLength: 40.0, MatchCount: 80);
        var barScan = tracker.ScanIndicatorCounts(counts, fishScan);

        Assert.True(barScan.Found);
        // Reconstructed from MaxEdge (200) minus EstimatedBarWidth / 2 (50) => 150.0
        Assert.InRange(barScan.CenterPos, 149.0, 151.0);
    }

    [Fact]
    public void Vertical_Axis_Auto_Inversion_Outputs_DutyCycle_Above_Neutral_When_Target_Is_Higher_On_Screen()
    {
        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Vertical,
            InvertDirection = false,
            RegionWidth = 30,
            RegionHeight = 300,
            NeutralHoverDutyCycle = 0.45,
            Kp = 1.30,
            Kd = 0.60,
            DerivativeTargetGain = 0.35,
            BoundaryLockThresholdPercent = 10.0,
        };

        var controller = new FeedbackServoController(step);

        // TargetY = 100 (higher on screen, smaller Y), IndicatorY = 200 (lower on screen, larger Y)
        var targetScan = new BarScanResult(Found: true, CenterPos: 100.0, MinEdge: 95.0, MaxEdge: 105.0, SpanLength: 10.0, MatchCount: 30);
        var indicatorScan = new BarScanResult(Found: true, CenterPos: 200.0, MinEdge: 170.0, MaxEdge: 230.0, SpanLength: 60.0, MatchCount: 180);

        var result = controller.Step(targetScan, indicatorScan, dtSeconds: 0.020, estimatedBarWidth: 60.0);

        Assert.True(result.RawErrorPx > 0, $"Expected positive error in Hold (UP) direction, got {result.RawErrorPx}");
        Assert.True(result.DutyCycle > step.NeutralHoverDutyCycle,
            $"Expected dutyCycle ({result.DutyCycle:F3}) > NeutralHoverDutyCycle ({step.NeutralHoverDutyCycle:F3}) to push bar UP toward TargetY=100");

        // When InvertDirection is true, polarity flips so dutyCycle < NeutralHoverDutyCycle
        step.InvertDirection = true;
        var invertedController = new FeedbackServoController(step);
        var invertedResult = invertedController.Step(targetScan, indicatorScan, dtSeconds: 0.020, estimatedBarWidth: 60.0);
        Assert.True(invertedResult.DutyCycle < step.NeutralHoverDutyCycle);
    }

    [Fact]
    public void Ankle_Break_Braking_Reduces_DutyCycle_Below_Neutral_When_Approaching_Target_At_High_Velocity()
    {
        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = 300,
            RegionHeight = 30,
            NeutralHoverDutyCycle = 0.45,
            Kp = 1.35,
            Kd = 0.65,
            DerivativeTargetGain = 0.40,
            AnkleBreakBrakingMultiplier = 1.75,
            BoundaryLockThresholdPercent = 10.0,
        };

        var controller = new FeedbackServoController(step);

        // Tick 1: Target at x=180, Bar at x=120
        var target1 = new BarScanResult(true, 180.0, 175.0, 185.0, 10.0, 20);
        var bar1 = new BarScanResult(true, 120.0, 95.0, 145.0, 50.0, 100);
        controller.Step(target1, bar1, dtSeconds: 0.020, estimatedBarWidth: 50.0);

        // Tick 2 (20ms later): Target still at x=180, Bar rushed forward to x=168 (velocity = +2400 px/s = +8.0 trackLengths/s)
        var bar2 = new BarScanResult(true, 168.0, 143.0, 193.0, 50.0, 100);
        var result = controller.Step(target1, bar2, dtSeconds: 0.020, estimatedBarWidth: 50.0);

        Assert.Equal(ServoTelemetryState.Braking, result.State);
        Assert.True(controller.AnkleBrakes >= 1);
        Assert.True(result.DutyCycle < step.NeutralHoverDutyCycle,
            $"Expected ankle-break braking to reduce dutyCycle ({result.DutyCycle:F3}) below NeutralHoverDutyCycle ({step.NeutralHoverDutyCycle:F3})");
    }

    [Fact]
    public void Serialization_Round_Trip_Preserves_All_New_FeedbackLoopStep_Properties()
    {
        var original = new FeedbackLoopStep
        {
            RegionX = 120,
            RegionY = 480,
            RegionWidth = 340,
            RegionHeight = 28,
            Orientation = BarOrientation.Vertical,
            ControlMode = FeedbackControlMode.SmartPredictive,
            InvertDirection = true,
            NeutralHoverDutyCycle = 0.48,
            PwmCycleMs = 18,
            Kp = 1.42,
            Kd = 0.68,
            DerivativeTargetGain = 0.41,
            AnkleBreakBrakingMultiplier = 1.85,
            BoundaryLockThresholdPercent = 12.5,
            AutoExitWhenBarDisappears = false,
            DisappearExitMs = 820,
            ShowTelemetryHud = false,
            AutoCalibrate = true,
            Responsiveness = 65,
            ComfortZonePercent = 18.5,
            MinPulseMs = 19,
            LearnedProfile = new BarProfile(
                RiseAccelPxPerSec2: 880.0,
                FallAccelPxPerSec2: 740.0,
                MaxSpeedPxPerSec: 540.0,
                GlidePx: 21.5,
                LatencyMs: 30.0,
                HoverDuty: 0.46,
                IsCalibrated: true),
        };

        var json = MacroFileFormat.Serialize(Flow.Macro(original));
        Assert.True(MacroFileFormat.TryParse(json, out var loadedMacro, out var error), error);
        Assert.NotNull(loadedMacro);

        var roundTripped = Assert.IsType<FeedbackLoopStep>(Assert.Single(loadedMacro!.Steps));
        Assert.Equal(original.Orientation, roundTripped.Orientation);
        Assert.Equal(original.InvertDirection, roundTripped.InvertDirection);
        Assert.Equal(original.NeutralHoverDutyCycle, roundTripped.NeutralHoverDutyCycle, precision: 6);
        Assert.Equal(original.PwmCycleMs, roundTripped.PwmCycleMs);
        Assert.Equal(original.Kp, roundTripped.Kp, precision: 6);
        Assert.Equal(original.Kd, roundTripped.Kd, precision: 6);
        Assert.Equal(original.DerivativeTargetGain, roundTripped.DerivativeTargetGain, precision: 6);
        Assert.Equal(original.Kv, roundTripped.Kv, precision: 6);
        Assert.Equal(original.AnkleBreakBrakingMultiplier, roundTripped.AnkleBreakBrakingMultiplier, precision: 6);
        Assert.Equal(original.BoundaryLockThresholdPercent, roundTripped.BoundaryLockThresholdPercent, precision: 6);
        Assert.Equal(original.AutoExitWhenBarDisappears, roundTripped.AutoExitWhenBarDisappears);
        Assert.Equal(original.DisappearExitMs, roundTripped.DisappearExitMs);
        Assert.Equal(original.ShowTelemetryHud, roundTripped.ShowTelemetryHud);
        Assert.Equal(original.AutoCalibrate, roundTripped.AutoCalibrate);
        Assert.Equal(original.Responsiveness, roundTripped.Responsiveness);
        Assert.Equal(original.ComfortZonePercent, roundTripped.ComfortZonePercent, precision: 6);
        Assert.Equal(original.MinPulseMs, roundTripped.MinPulseMs);
        Assert.Equal(original.LearnedProfile, roundTripped.LearnedProfile);
    }

    [Fact]
    public void Relative_Velocity_Damping_Produces_Zero_Counter_Brake_When_Moving_At_Target_Velocity()
    {
        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = 300,
            RegionHeight = 30,
            NeutralHoverDutyCycle = 0.45,
            Kp = 1.35,
            Kd = 0.85,
            DerivativeTargetGain = 0.55,
            AutoCalibrate = false,
        };

        var controller = new FeedbackServoController(step);

        // Both target and bar are at x=150 (rawErrorPx = 0) and both are moving right at +0.60 trackLengths/s.
        var (duty, state, braked) = controller.ComputeDutyCycle(
            targetPos: 150.0,
            indicatorPos: 150.0,
            rawErrorPx: 0.0,
            normError: 0.0,
            targetVelNorm: 0.60,
            indicatorVelNorm: 0.60,
            prevTargetVelNorm: 0.60,
            trackLength: 300.0,
            estimatedBarWidth: 60.0,
            axisSign: 1.0);

        Assert.False(braked);
        Assert.Equal(ServoTelemetryState.Locked, state);
        // Because relative velocity is 0 and targetVelNorm > 0, dutyCycle must be >= NeutralHoverDutyCycle (never braked below hover!)
        Assert.True(duty >= step.NeutralHoverDutyCycle,
            $"Expected duty ({duty:F3}) >= NeutralHoverDutyCycle ({step.NeutralHoverDutyCycle:F3}) when bar matches target velocity");
    }

    [Theory]
    [InlineData(0.30, 17)]
    [InlineData(0.45, 17)]
    [InlineData(0.68, 20)]
    public void SigmaDeltaModulator_Reproduces_DutyCycle_Accurately_And_Respects_MinPulseMs(double targetDuty, int minPulseMs)
    {
        var modulator = new SigmaDeltaModulator();
        const double dtMs = 10.0; // intentionally shorter than minPulseMs to verify frame-safe pulse locking
        const int totalSteps = 300;

        var heldSteps = 0;
        bool? prevHold = null;
        double currentRunMs = 0;

        for (var i = 0; i < totalSteps; i++)
        {
            var hold = modulator.Step(targetDuty, dtMs, minPulseMs, immediateOverride: false);
            if (hold) heldSteps++;

            if (prevHold is { } prev && hold != prev)
            {
                Assert.True(currentRunMs >= minPulseMs - 1e-6,
                    $"Pulse flipped after {currentRunMs} ms, which is shorter than MinPulseMs ({minPulseMs} ms)");
                currentRunMs = dtMs;
            }
            else
            {
                currentRunMs += dtMs;
            }

            prevHold = hold;
        }

        var actualDuty = heldSteps / (double)totalSteps;
        Assert.InRange(actualDuty, targetDuty - 0.035, targetDuty + 0.035);
    }

    [Theory]
    [InlineData(FeedbackQuickPreset.MomentumBar, 820.0, 680.0, 1.8)]
    [InlineData(FeedbackQuickPreset.MomentumBar, 780.0, 660.0, 0.75)]
    [InlineData(FeedbackQuickPreset.GravityBar, 760.0, 640.0, 2.2)]
    [InlineData(FeedbackQuickPreset.SnapBar, 1600.0, 1600.0, 5.5)]
    [InlineData(FeedbackQuickPreset.FloatyBar, 520.0, 450.0, 2.0)]
    public void Closed_Loop_Physics_Simulator_Achieves_Over_90_Percent_OnTarget_Rate_Without_Runaway_Overshoot(
        FeedbackQuickPreset preset,
        double riseAccel,
        double fallAccel,
        double dragFactor)
    {
        const double trackLength = 300.0;
        const double barWidth = 64.0;
        const double halfBar = barWidth * 0.5;
        const double dt = 1.0 / 60.0; // 60 FPS game loop

        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = (int)trackLength,
            RegionHeight = 28,
        };
        FeedbackPresets.ApplyQuickPreset(step, preset);

        var controller = new FeedbackServoController(step);

        double barPos = 150.0;
        double barVel = 0.0;
        double targetPos = 150.0;

        // Simulate 10 seconds (600 frames at 60 FPS) with a moving target that glides, pauses, and reverses direction
        const int totalFrames = 600;
        for (var frame = 0; frame < totalFrames; frame++)
        {
            var t = frame * dt;
            var desiredTarget = 150.0
                                + 65.0 * Math.Sin(t * 1.35)
                                + 25.0 * Math.Sin(t * 3.10);
            desiredTarget = Math.Clamp(desiredTarget, halfBar + 10.0, trackLength - halfBar - 10.0);
            targetPos += Math.Clamp(desiredTarget - targetPos, -220.0 * dt, 220.0 * dt);

            var targetScan = new BarScanResult(true, targetPos, targetPos - 5.0, targetPos + 5.0, 10.0, 25);
            var indicatorScan = new BarScanResult(true, barPos, barPos - halfBar, barPos + halfBar, barWidth, 120);

            var result = controller.Step(targetScan, indicatorScan, dt, barWidth);

            // Apply simulated 60 FPS bar physics from the Sigma-Delta HoldInput decision
            var thrustAccel = result.HoldInput ? riseAccel : -fallAccel;
            barVel += (thrustAccel - (barVel * dragFactor)) * dt;
            barVel = Math.Clamp(barVel, -520.0, 520.0);
            barPos += barVel * dt;

            if (barPos <= halfBar)
            {
                barPos = halfBar;
                if (barVel < 0) barVel = 0;
            }
            else if (barPos >= trackLength - halfBar)
            {
                barPos = trackLength - halfBar;
                if (barVel > 0) barVel = 0;
            }
        }

        Assert.True(controller.OnTargetPercent >= 90.0,
            $"Preset {preset} achieved {controller.OnTargetPercent:F1}% on-target rate (expected >= 90.0%)");
    }

    [Fact]
    public void User_Facing_Preset_And_Card_Labels_Contain_No_Third_Party_Game_Names()
    {
        var forbiddenNames = new[] { "Fisch", "Slayers", "Stardew", "GPO", "Deepwoken", "Roblox" };

        var allUserStrings = new List<string>();
        foreach (var card in FeedbackPresets.BehaviorCards)
        {
            allUserStrings.Add(card.Title);
            allUserStrings.Add(card.Subtitle);
        }

        foreach (var qp in FeedbackPresets.DistinctQuickPresets)
            allUserStrings.Add(FeedbackPresets.QuickPresetLabel(qp));

        foreach (var gp in Enum.GetValues<FeedbackGamePreset>())
            allUserStrings.Add(FeedbackPresets.Label(gp));

        foreach (var cm in Enum.GetValues<FeedbackControlMode>())
            allUserStrings.Add(FeedbackPresets.ControlModeLabel(cm));

        var sampleStep = new FeedbackLoopStep();
        FeedbackPresets.ApplyQuickPreset(sampleStep, FeedbackQuickPreset.MomentumBar);
        allUserStrings.AddRange(StepDescriber.ExplainFeedbackRules(sampleStep));
        allUserStrings.Add(StepDescriber.DescribeFeedback(sampleStep));

        foreach (var text in allUserStrings)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Inactive_Brown_Bar_With_Track_Borders_End_Brackets_And_Internal_Arrow_Finds_Exact_Bar_And_Vertical_Target()
    {
        const int width = 600;
        const int height = 28;
        var darkTrack = new RgbColor(36, 38, 38);
        var brownBar = new RgbColor(84, 54, 48);      // #543630
        var whiteActive = new RgbColor(241, 241, 241); // #F1F1F1
        var slateTarget = new RgbColor(67, 75, 91);    // #434B5B

        var image = TestImages.Solid(width, height, darkTrack);

        // 1. 2px top & bottom brown border across the entire 600px track + left/right 8px decorative brown brackets
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 0, width, 2), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, height - 2, width, 2), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 0, 8, height), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(width - 8, 0, 8, height), brownBar);

        // 2. 60px-wide top/bottom border shadow on the right that matches slateTarget color in outer rows
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(500, 0, 60, 3), slateTarget);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(500, height - 3, 60, 3), slateTarget);

        // 3. Inactive Brown Player Bar at x = 300..440 (width = 141, center = 370)
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(300, 2, 141, height - 4), brownBar);

        // 4. Internal grey '←' directional arrow inside the brown bar (x = 315..338, y = 9..18) matching slateTarget
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(315, 9, 24, 10), slateTarget);

        // 5. True vertical Fish Marker at x = 220..228 (width = 9, center = 224) spanning full interior height
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(220, 2, 9, height - 4), slateTarget);

        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = width,
            RegionHeight = height,
            MarkerColor = "#434B5B",
            MarkerTolerance = 24,
            MarkerMinPixels = 2,
            ZoneMode = FeedbackZoneMode.DetectedByColor,
            ZoneColor = "#543630",
            UseSecondaryZoneColor = true,
            SecondaryZoneColor = "#F1F1F1",
            ZoneTolerance = 24,
            ZoneMinPixels = 5,
        };

        var tracker = new FeedbackLoopTracker();
        var (target, indicator) = tracker.Scan(image, step);

        Assert.False(tracker.LastBarWasActiveSecondary);
        Assert.True(indicator.Found);
        Assert.InRange(indicator.MinEdge, 299.0, 301.0);
        Assert.InRange(indicator.MaxEdge, 439.0, 441.0);
        Assert.InRange(indicator.CenterPos, 369.0, 371.0);

        Assert.True(target.Found);
        Assert.InRange(target.CenterPos, 223.0, 225.0);
    }

    [Fact]
    public void Active_White_Overlapping_Bar_Split_By_Vertical_Target_And_Internal_Arrow_Does_Not_Merge_With_Brown_Track_Border()
    {
        const int width = 600;
        const int height = 28;
        var darkTrack = new RgbColor(36, 38, 38);
        var brownBar = new RgbColor(84, 54, 48);       // #543630
        var whiteActive = new RgbColor(241, 241, 241); // #F1F1F1
        var slateTarget = new RgbColor(67, 75, 91);    // #434B5B

        var image = TestImages.Solid(width, height, darkTrack);

        // 1. Full-width 2px brown track border + left/right 8px decorative brown brackets
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 0, width, 2), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, height - 2, width, 2), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 0, 8, height), brownBar);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(width - 8, 0, 8, height), brownBar);

        // 2. Active White Player Bar at x = 240..380 (width = 141, center = 310)
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(240, 2, 141, height - 4), whiteActive);

        // 3. Vertical Fish Marker slicing right through the white bar at x = 320..328 (center = 324)
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(320, 2, 9, height - 4), slateTarget);

        // 4. Internal grey '→' directional arrow inside the white bar at x = 342..362, y = 9..18
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(342, 9, 21, 10), slateTarget);

        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = width,
            RegionHeight = height,
            MarkerColor = "#434B5B",
            MarkerTolerance = 24,
            MarkerMinPixels = 2,
            ZoneMode = FeedbackZoneMode.DetectedByColor,
            ZoneColor = "#543630",
            UseSecondaryZoneColor = true,
            SecondaryZoneColor = "#F1F1F1",
            ZoneTolerance = 24,
            ZoneMinPixels = 5,
        };

        var tracker = new FeedbackLoopTracker();
        var (target, indicator) = tracker.Scan(image, step);

        Assert.True(tracker.LastBarWasActiveSecondary);
        Assert.True(indicator.Found);
        Assert.InRange(indicator.MinEdge, 239.0, 241.0);
        Assert.InRange(indicator.MaxEdge, 379.0, 381.0);
        Assert.InRange(indicator.CenterPos, 309.0, 311.0);

        Assert.True(target.Found);
        Assert.InRange(target.CenterPos, 323.0, 325.0);
    }

    [Fact]
    public void Loose_47Px_SearchRegion_Over_3D_Rock_Background_Tracks_SemiTransparent_Brown_Bar_Even_At_Tight_Tolerance()
    {
        const int width = 777;
        const int height = 47;
        var greyRock = new RgbColor(138, 141, 147);    // 3D rock above bar
        var blueWater = new RgbColor(49, 108, 152);    // 3D water below bar
        var darkTrack = new RgbColor(47, 52, 58);      // Track background over rock
        var brownBorder = new RgbColor(84, 54, 48);    // #543630 reference brown
        var rockBlendedBrown = new RgbColor(83, 63, 54); // #533F36 semi-transparent brown bar over bright rock!
        var slateTarget = new RgbColor(67, 75, 91);    // #434B5B vertical fish marker

        var image = TestImages.Solid(width, height, darkTrack);

        // 1. Loose SearchRegion has 7px of 3D rock above the bar (y=0..6) and 9px of water below the bar (y=38..46),
        //    plus a rock crack at x=600..625 in the top 7px that matches slateTarget!
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 0, width, 7), greyRock);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(600, 0, 25, 7), slateTarget);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 38, width, 9), blueWater);

        // 2. Minigame bar occupies y=7..37 (31px tall), with 2px brown track borders at y=7..8 and y=36..37
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 7, width, 2), brownBorder);
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(0, 36, width, 2), brownBorder);

        // 3. Semi-transparent Brown Player Bar at x=377..609 (y=9..35) blended with bright rock (#533F36)
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(377, 9, 233, 27), rockBlendedBrown);

        // 4. Internal '→' arrow inside the brown bar at x=575..592, y=18..26 matching slateTarget
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(575, 18, 18, 9), slateTarget);

        // 5. True vertical Fish Marker on the left at x=62..72, y=9..35
        image.FillRectangle(new SmartMacro.Core.Ocr.ScreenRect(62, 9, 11, 27), slateTarget);

        var step = new FeedbackLoopStep
        {
            Orientation = BarOrientation.Horizontal,
            RegionWidth = width,
            RegionHeight = height,
            MarkerColor = "#434B5B",
            MarkerTolerance = 10, // User lowered tolerance to 10!
            MarkerMinPixels = 2,
            ZoneMode = FeedbackZoneMode.DetectedByColor,
            ZoneColor = "#543630", // Picked over dark water, while bar is now #533F36 over bright rock
            UseSecondaryZoneColor = true,
            SecondaryZoneColor = "#F1F1F1",
            ZoneTolerance = 10,    // User lowered tolerance to 10!
            ZoneMinPixels = 5,
        };

        var tracker = new FeedbackLoopTracker();
        var (target, indicator) = tracker.Scan(image, step);

        Assert.False(tracker.LastBarWasActiveSecondary);
        Assert.True(indicator.Found);
        Assert.InRange(indicator.MinEdge, 376.0, 378.0);
        Assert.InRange(indicator.MaxEdge, 608.0, 610.0);

        Assert.True(target.Found);
        Assert.InRange(target.CenterPos, 66.0, 68.0);
    }
}
