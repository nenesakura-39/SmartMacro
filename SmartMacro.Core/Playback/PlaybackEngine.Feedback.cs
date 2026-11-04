using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- Feedback loop (Stage 4) ----------------

    /// <summary>
    /// Runs a Feedback loop step: every frame (at most "captures per second"):
    /// 1. Pause / Stop / Esc are checked (pause releases the key / button and presses it again on resume, like every step).
    /// 2. Max runtime (paused time not counted).
    /// 3. The "failed when" / "done when" conditions, each at most once per its "check every".
    /// 4. Capture the bar area, find the marker and zone (<see cref="FeedbackSensor"/>).
    /// 5. Decide (<see cref="FeedbackController"/>) and send input only when the decision changes.
    /// When the loop ends for any reason (done, failed, time up, target lost, Stop / Esc, an error) the key / button is
    /// released and ONE summary line is logged (frames, frame rate, capture / detection time, input changes, why it
    /// ended). Nothing is logged per frame.
    /// </summary>
    private async Task RunFeedbackLoopAsync(RunContext run, int index, List<LoopFrame> loops, FeedbackLoopStep step, CancellationToken token)
    {
        _inFeedbackLoop = true;
        try
        {
            await ClearTargetOverlayAsync(token).ConfigureAwait(false);
            if (step.ControlMode == FeedbackControlMode.SmartPredictive)
                await ExecuteFeedbackLoopAsync(run, index, loops, step, token).ConfigureAwait(false);
            else
                await RunFeedbackLoopCoreAsync(run, index, loops, step, token).ConfigureAwait(false);
        }
        finally
        {
            _inFeedbackLoop = false;
            try { FeedbackTelemetryUpdated?.Invoke(this, null); } catch { }
        }
    }

    /// <summary>
    /// Closed-Loop Adaptive PD+FeedForward Servo Controller execution for horizontal and vertical bar minigames (Feedback Loop 3.0).
    /// Uses <see cref="FeedbackLoopTracker"/> (single-pass 1D projection with gap bridging and smooth edge-occlusion reconstruction)
    /// and <see cref="FeedbackServoController"/> (Alpha-Beta kinematic filtering, latency compensation, relative-velocity damping,
    /// comfort zone, kinematic stopping-distance braking, frame-safe Sigma-Delta modulation, and online auto-calibration).
    /// </summary>
    private async Task ExecuteFeedbackLoopAsync(RunContext run, int index, List<LoopFrame> loops, FeedbackLoopStep step, CancellationToken token)
    {
        var number = run.StepNumberOf(index);
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Feedback loop steps can't run.");
        if (!RgbColor.TryParse(step.MarkerColor, out _))
            throw new InvalidOperationException($"The marker color \"{step.MarkerColor}\" is not valid. Use #RRGGBB.");
        if (step.ZoneMode == FeedbackZoneMode.DetectedByColor && !RgbColor.TryParse(step.ZoneColor, out _))
            throw new InvalidOperationException($"The zone color \"{step.ZoneColor}\" is not valid. Use #RRGGBB.");

        var region = step.Region;
        var tracker = new FeedbackLoopTracker();
        var servo = new FeedbackServoController(step);
        var stats = new FeedbackStats();
        var held = run.Held;
        var key = new HeldKey(step.VirtualKey);
        var maxRuntime = TimeSpan.FromMilliseconds(step.MaxRuntimeMs);
        var targetFps = Math.Clamp(step.MaxCapturesPerSecond, 30, MacroValidator.MaxCapturesPerSecond);
        var frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / targetFps);
        var startedAt = ActiveTime;
        TimeSpan? lastFrameTime = null;
        TimeSpan? lastCompleteCheck = null;
        TimeSpan? lastFailCheck = null;
        TimeSpan nextTelemetryUpdate = TimeSpan.Zero;
        var telemetryInterval = TimeSpan.FromMilliseconds(16); // ~60 FPS throttled UI updates
        var nextProgress = TimeSpan.Zero;
        var waiting = false;
        string? ending = null;
        Exception? failure = null;

        TimeSpan Now() => ActiveTime - startedAt;

        bool IsDown() => step.InputKind == FeedbackInputKind.MouseButton
            ? held.Buttons.Contains(step.MouseButton)
            : held.Keys.Contains(key);

        void SetInput(bool down)
        {
            if (step.InputKind == FeedbackInputKind.MouseButton)
            {
                _input.MouseButton(step.MouseButton, down);
                if (down) held.Buttons.Add(step.MouseButton);
                else held.Buttons.Remove(step.MouseButton);
            }
            else
            {
                _input.Key(step.VirtualKey, down);
                if (down) held.Keys.Add(key);
                else held.Keys.Remove(key);
            }
        }

        void End(string what, LoopLimitBehavior behavior)
        {
            if (behavior == LoopLimitBehavior.StopMacro)
            {
                ending = what;
                failure = new FeedbackLoopException($"Feedback loop (step {number}) {what}. The macro was stopped.");
            }
            else
            {
                ending = what + "; continuing with the next step";
            }
        }

        try
        {
            if (IsDown()) SetInput(false);

            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var frameStart = Now();

                if (frameStart >= maxRuntime)
                {
                    End($"reached its max runtime of {step.MaxRuntimeMs / 1000.0:0.###} s", step.WhenTimeUp);
                    break;
                }

                if (step.FailWhen is { } fail && IsConditionDue(fail, lastFailCheck, frameStart))
                {
                    lastFailCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, fail, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        End($"failed: its \"failed when\" condition is true ({check.Details.TrimEnd('.')})", step.WhenFailed);
                        break;
                    }
                }

                if (step.CompleteWhen is { } complete && IsConditionDue(complete, lastCompleteCheck, frameStart))
                {
                    lastCompleteCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, complete, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        ending = $"finished: its \"done when\" condition is true ({check.Details.TrimEnd('.')})";
                        break;
                    }
                }

                var t0 = _clock.Elapsed;
                var screenFrame = await capture.CaptureAsync(region, token).ConfigureAwait(false);
                var t1 = _clock.Elapsed;
                if (screenFrame.Bounds != region)
                    throw new InvalidOperationException($"The bar area {region} is partly outside the screen (only {screenFrame.Bounds} is visible). Pick the area again.");

                var (targetScan, indicatorScan) = tracker.Scan(screenFrame.Image, step);
                var t2 = _clock.Elapsed;
                stats.AddServoFrame(targetScan, indicatorScan, t1 - t0, t2 - t1);

                var now = Now();
                var dtSeconds = lastFrameTime is { } prev ? Math.Max(0.001, (now - prev).TotalSeconds) : frameInterval.TotalSeconds;
                lastFrameTime = now;

                var result = servo.Step(targetScan, indicatorScan, dtSeconds, tracker.EstimatedBarWidth);
                stats.Brakes = servo.AnkleBrakes;
                stats.OnTargetPercent = servo.TotalTrackedFrames > 0 ? servo.OnTargetPercent : null;

                if (step.ShowTelemetryHud && now >= nextTelemetryUpdate)
                {
                    nextTelemetryUpdate = now + telemetryInterval;
                    try { FeedbackTelemetryUpdated?.Invoke(this, result.Telemetry); } catch { }
                }

                // Auto-Exit When Minigame Finishes
                if (result.ShouldAutoExit)
                {
                    if (IsDown())
                    {
                        SetInput(false);
                        stats.Releases++;
                    }
                    ending = $"finished: minigame bar disappeared for {step.DisappearExitMs} ms after completion";
                    break;
                }

                var isSearching = result.State == ServoTelemetryState.Searching;
                if (isSearching && !waiting) stats.LostEpisodes++;
                waiting = isSearching;

                if (frameStart >= nextProgress)
                {
                    var stateText = string.Format(
                        CultureInfo.InvariantCulture,
                        "servo [{0}], err {1:+0;-0;0}px, pwm {2:P0}, on-tgt {3:0}%",
                        result.Telemetry.StateLabel,
                        result.RawErrorPx,
                        result.DutyCycle,
                        servo.OnTargetPercent);
                    Report(run, index, loops, $"feedback {frameStart.TotalSeconds:0.0} s, {stateText}");
                    nextProgress = frameStart + FeedbackProgressInterval;
                }

                // Frame-safe Sigma-Delta hold/release application (never sleeps blind across multiple frames)
                if (result.HoldInput)
                {
                    if (!IsDown())
                    {
                        SetInput(true);
                        stats.Presses++;
                    }
                }
                else
                {
                    if (IsDown())
                    {
                        SetInput(false);
                        stats.Releases++;
                    }
                }

                var rest = frameInterval - (Now() - frameStart);
                if (rest > TimeSpan.Zero)
                    await WaitAsync(rest, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (IsDown()) TryRelease(() => SetInput(false));
            try { FeedbackTelemetryUpdated?.Invoke(this, null); } catch { }
            ResetTimeline();

            var why = ending ?? (token.IsCancellationRequested ? "was stopped" : "ended because of an error");
            Message?.Invoke(this, $"Feedback loop (step {number}) {why}. {stats.Describe(Now(), frameInterval)}");
        }

        if (failure is not null) throw failure;
    }

    private async Task RunFeedbackLoopCoreAsync(RunContext run, int index, List<LoopFrame> loops, FeedbackLoopStep step, CancellationToken token)
    {
        var number = run.StepNumberOf(index);
        var capture = _capture ?? throw new ScreenCaptureUnavailableException("Screen capture isn't available, so Feedback loop steps can't run.");
        var sensor = new FeedbackSensor(step, capture, _clock);
        // Classic region rules, or Feedback Loop 2.0 Smart Predictive (PD lead + hover pulse + reversal brake).
        var controller = FeedbackControllers.Create(step);
        var stats = new FeedbackStats();
        var held = run.Held;
        var key = new HeldKey(step.VirtualKey);
        var maxRuntime = TimeSpan.FromMilliseconds(step.MaxRuntimeMs);
        var frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Math.Clamp(step.MaxCapturesPerSecond, 1, MacroValidator.MaxCapturesPerSecond));
        var startedAt = ActiveTime;
        TimeSpan? lastCompleteCheck = null;
        TimeSpan? lastFailCheck = null;
        var nextProgress = TimeSpan.Zero;
        var waiting = false;
        string? ending = null;        // why the loop ended, for the summary line
        Exception? failure = null;    // thrown after the summary when the loop ends as an error

        // Time since the loop started, without paused time (the controller, runtime and rate limit all use this).
        TimeSpan Now() => ActiveTime - startedAt;

        bool IsDown() => step.InputKind == FeedbackInputKind.MouseButton
            ? held.Buttons.Contains(step.MouseButton)
            : held.Keys.Contains(key);

        // All input goes through here, so the engine always knows what is held (Stop / pause / errors release it).
        void SetInput(bool down)
        {
            if (step.InputKind == FeedbackInputKind.MouseButton)
            {
                _input.MouseButton(step.MouseButton, down);
                if (down) held.Buttons.Add(step.MouseButton);
                else held.Buttons.Remove(step.MouseButton);
            }
            else
            {
                _input.Key(step.VirtualKey, down);
                if (down) held.Keys.Add(key);
                else held.Keys.Remove(key);
            }
        }

        // "Stop the macro" or "continue": sets the ending text and, for stop, the error to throw.
        void End(string what, LoopLimitBehavior behavior)
        {
            if (behavior == LoopLimitBehavior.StopMacro)
            {
                ending = what;
                failure = new FeedbackLoopException($"Feedback loop (step {number}) {what}. The macro was stopped.");
            }
            else
            {
                ending = what + "; continuing with the next step";
            }
        }

        try
        {
            // The loop takes over this key / button and starts from "released".
            if (IsDown()) SetInput(false);

            while (true)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var frameStart = Now();

                if (frameStart >= maxRuntime)
                {
                    End($"reached its max runtime of {step.MaxRuntimeMs / 1000.0:0.###} s", step.WhenTimeUp);
                    break;
                }

                // Stop conditions: failure first (if both are true, the failure wins).
                if (step.FailWhen is { } fail && IsConditionDue(fail, lastFailCheck, frameStart))
                {
                    lastFailCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, fail, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        End($"failed: its \"failed when\" condition is true ({check.Details.TrimEnd('.')})", step.WhenFailed);
                        break;
                    }
                }

                if (step.CompleteWhen is { } complete && IsConditionDue(complete, lastCompleteCheck, frameStart))
                {
                    lastCompleteCheck = frameStart;
                    var check = await CheckFeedbackConditionAsync(run, complete, stats, token).ConfigureAwait(false);
                    if (check.IsTrue)
                    {
                        ending = $"finished: its \"done when\" condition is true ({check.Details.TrimEnd('.')})";
                        break;
                    }
                }

                // Look, decide, act.
                var frame = await sensor.ReadAsync(token).ConfigureAwait(false);
                stats.Add(frame);
                var decision = controller.Update(frame.Observation, Now());
                if (decision.Deferred) stats.Deferred++;
                if (decision.Braked) stats.Brakes++;

                switch (decision.Command)
                {
                    case FeedbackCommand.Press:
                        if (!IsDown()) SetInput(true);
                        stats.Presses++;
                        break;
                    case FeedbackCommand.Release:
                        if (IsDown()) SetInput(false);
                        stats.Releases++;
                        break;
                    case FeedbackCommand.Tap:
                        if (IsDown()) SetInput(false);
                        SetInput(true);
                        await WaitAsync(TimeSpan.FromMilliseconds(step.TapMs), token).ConfigureAwait(false);
                        if (IsDown()) SetInput(false);
                        stats.Taps++;
                        break;
                }

                // "Finish when it reaches the zone": do the inside rule once (if this frame didn't already) and end.
                if (step.FinishWhenInZone && decision.Region == FeedbackRegion.Inside)
                {
                    switch (step.WhenInsideZone)
                    {
                        case FeedbackAction.Tap when decision.Command != FeedbackCommand.Tap:
                            if (IsDown()) SetInput(false);
                            SetInput(true);
                            await WaitAsync(TimeSpan.FromMilliseconds(step.TapMs), token).ConfigureAwait(false);
                            if (IsDown()) SetInput(false);
                            stats.Taps++;
                            break;
                        case FeedbackAction.Release when IsDown():
                            SetInput(false);
                            stats.Releases++;
                            break;
                    }

                    ending = $"finished: the marker reached the zone after {frameStart.TotalSeconds:0.00} s";
                    break;
                }

                if (controller.IsWaiting && !waiting) stats.LostEpisodes++;
                waiting = controller.IsWaiting;

                if (decision.TargetLost)
                {
                    var what = $"lost its target: the marker{(step.ZoneMode == FeedbackZoneMode.DetectedByColor ? " or zone" : "")} wasn't seen clearly for {step.LostTimeoutMs} ms";
                    End(what, step.WhenTargetLost == FeedbackLostAction.StopMacro ? LoopLimitBehavior.StopMacro : LoopLimitBehavior.ExitLoop);
                    break;
                }

                if (frameStart >= nextProgress)
                {
                    var state = waiting ? "waiting for the target" : controller.IsHeld ? "holding" : "released";
                    if (controller is PredictiveFeedbackController smart && !waiting)
                        state += SmartTelemetry(smart);
                    Report(run, index, loops, $"feedback {frameStart.TotalSeconds:0.0} s, {state}");
                    nextProgress = frameStart + FeedbackProgressInterval;
                }

                // Rate limit: at most "captures per second" frames.
                var rest = frameInterval - (Now() - frameStart);
                if (rest > TimeSpan.Zero) await WaitAsync(rest, token).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever ended the loop, let go of the key / button (Stop / Esc / errors: the engine releases everything too).
            if (IsDown()) TryRelease(() => SetInput(false));
            ResetTimeline(); // the loop took as long as it took; start timing again from here

            var why = ending ?? (token.IsCancellationRequested ? "was stopped" : "ended because of an error");
            Message?.Invoke(this, $"Feedback loop (step {number}) {why}. {stats.Describe(Now(), frameInterval)}");
        }

        if (failure is not null) throw failure;
    }

    /// <summary>", gap 12 px, bar 340 px/s, hover 55%" for the progress text of a Smart Predictive loop (invariant culture).</summary>
    internal static string SmartTelemetry(PredictiveFeedbackController smart)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = string.Format(culture, ", gap {0:0} px, bar {1:0} px/s", smart.PredictedError, smart.PlayerVelocity);
        if (smart.Duty is { } duty) text += string.Format(culture, ", hover {0:0}%", duty * 100);
        return text;
    }

    private static bool IsConditionDue(Condition condition, TimeSpan? lastCheck, TimeSpan now) =>
        lastCheck is not { } last || now - last >= TimeSpan.FromMilliseconds(Math.Max(1, condition.Search?.CheckIntervalMs ?? 250));

    /// <summary>Looks once for a feedback loop's stop condition. A condition that sees its target also sets the "last found" spot.</summary>
    private async Task<ConditionCheck> CheckFeedbackConditionAsync(RunContext run, Condition condition, FeedbackStats stats, CancellationToken token)
    {
        var check = await run.Conditions.CheckAsync(condition, null, token).ConfigureAwait(false);
        if (condition.Kind is not (ConditionKind.TextVisible or ConditionKind.VariableCompare)) RecordProbeTime(check.ElapsedMs);
        stats.ConditionChecks++;
        if (check.Seen) RememberFound(check.X, check.Y, check.Match);

        return check;
    }
}
