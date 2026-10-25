using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

/// <summary>Live state of the Closed-Loop Adaptive PD+FeedForward Servo Controller.</summary>
public enum ServoTelemetryState
{
    Searching,
    Learning,
    Coasting,
    Tracking,
    Locked,
    Braking,
    WallDrop,
    WallHold,
}

/// <summary>
/// Snapshot of the servo controller's telemetry for the live <c>FeedbackTelemetryOverlay</c> HUD.
/// </summary>
public sealed record FeedbackTelemetryData(
    ScreenRect SearchRegion,
    BarOrientation Orientation,
    ServoTelemetryState State,
    string StateLabel,
    bool TargetFound,
    double TargetPos,
    double TargetVelocityPxPerSec,
    bool IndicatorFound,
    double IndicatorPos,
    double IndicatorMinEdge,
    double IndicatorMaxEdge,
    double IndicatorVelocityPxPerSec,
    double ErrorPx,
    double DutyCycle,
    double EstimatedBarWidth,
    double OnTargetPercent = 100.0);

/// <summary>
/// Output of a single control cycle from <see cref="FeedbackServoController"/>.
/// </summary>
public readonly record struct FeedbackServoStepResult(
    double DutyCycle,
    int CycleMs,
    int HoldMs,
    int ReleaseMs,
    bool ContinuousHold,
    bool ContinuousRelease,
    ServoTelemetryState State,
    double RawErrorPx,
    double NormError,
    double TargetVelNorm,
    double IndicatorVelNorm,
    bool ShouldAutoExit,
    FeedbackTelemetryData Telemetry,
    bool HoldInput = false,
    double RelativeVelNorm = 0.0,
    double StoppingDistancePx = 0.0);

/// <summary>
/// 1D Alpha-Beta (steady-state Kalman) kinematic filter for position and velocity estimation
/// with single-frame outlier jump rejection.
/// </summary>
public struct AlphaBetaFilter1D
{
    private readonly double _alpha;
    private readonly double _beta;
    private readonly double _maxVelNorm;
    private int _sampleCount;
    private int _outlierCount;

    public AlphaBetaFilter1D(double alpha = 0.72, double beta = 0.28, double maxVelNorm = 8.0)
    {
        _alpha = alpha;
        _beta = beta;
        _maxVelNorm = maxVelNorm;
        Position = 0;
        VelocityNorm = 0;
        _sampleCount = 0;
        _outlierCount = 0;
    }

    public double Position { get; private set; }
    public double VelocityNorm { get; private set; }
    public bool HasPosition => _sampleCount >= 1;
    public bool HasVelocity => _sampleCount >= 2;

    public void Reset()
    {
        Position = 0;
        VelocityNorm = 0;
        _sampleCount = 0;
        _outlierCount = 0;
    }

    public void Update(double measuredPos, double dtSeconds, double trackLength, double axisSign, double maxJumpRatio = 0.45)
    {
        if (_sampleCount == 0 || dtSeconds <= 1e-5 || trackLength <= 0)
        {
            Position = measuredPos;
            VelocityNorm = 0;
            _sampleCount = 1;
            _outlierCount = 0;
            return;
        }

        if (_sampleCount == 1)
        {
            var rawVel = Math.Clamp(((measuredPos - Position) * axisSign) / (trackLength * dtSeconds), -_maxVelNorm, _maxVelNorm);
            Position = measuredPos;
            VelocityNorm = rawVel;
            _sampleCount = 2;
            _outlierCount = 0;
            return;
        }

        // Predict forward along screen coordinate axis
        var predictedPos = Position + (VelocityNorm * trackLength * axisSign * dtSeconds);
        var residualPx = measuredPos - predictedPos;
        var maxJumpPx = Math.Max(16.0, trackLength * maxJumpRatio);

        // Single-frame glitch protection: damp a huge 1-frame teleport once, but accept it if it persists next frame.
        if (Math.Abs(residualPx) > maxJumpPx && _outlierCount == 0)
        {
            _outlierCount = 1;
            residualPx = Math.Sign(residualPx) * maxJumpPx * 0.35;
        }
        else
        {
            _outlierCount = 0;
        }

        Position = Math.Clamp(predictedPos + (_alpha * residualPx), 0.0, trackLength);
        var residualHoldNorm = (residualPx * axisSign) / trackLength;
        var velUpdate = VelocityNorm + ((_beta / dtSeconds) * residualHoldNorm);
        var finiteDiffVel = Math.Clamp(((measuredPos - (predictedPos - (VelocityNorm * trackLength * axisSign * dtSeconds))) * axisSign) / (trackLength * dtSeconds), -_maxVelNorm, _maxVelNorm);

        // Blend Alpha-Beta velocity with direct finite-difference for fast reflex tracking without ringing
        VelocityNorm = Math.Clamp((0.65 * velUpdate) + (0.35 * finiteDiffVel), -_maxVelNorm, _maxVelNorm);
        _sampleCount++;
    }

    public void Coast(double dtSeconds, double trackLength, double axisSign, double decay = 0.78)
    {
        if (_sampleCount == 0) return;
        VelocityNorm *= decay;
        Position = Math.Clamp(Position + (VelocityNorm * trackLength * axisSign * dtSeconds), 0.0, trackLength);
    }
}

/// <summary>
/// Frame-safe first-order Sigma-Delta (error-accumulator) modulator that converts a continuous duty cycle
/// <c>u in [0.0, 1.0]</c> into binary Hold/Release decisions while guaranteeing that no pulse is shorter than
/// <c>MinPulseMs</c> (preventing sub-frame input drops in 60 FPS games).
/// </summary>
public sealed class SigmaDeltaModulator
{
    private double _accumulator;
    private bool _isHeld;
    private double _currentStateDurationMs;
    private bool _initialized;

    public bool IsHeld => _isHeld;
    public double CurrentStateDurationMs => _currentStateDurationMs;

    public void Reset()
    {
        _accumulator = 0;
        _isHeld = false;
        _currentStateDurationMs = 0;
        _initialized = false;
    }

    /// <summary>
    /// Advances the Sigma-Delta modulator by one frame of duration <paramref name="dtMs"/>.
    /// </summary>
    public bool Step(double dutyCycle, double dtMs, int minPulseMs, bool immediateOverride = false)
    {
        var u = Math.Clamp(dutyCycle, 0.0, 1.0);
        var dt = Math.Max(1.0, dtMs);
        var minHold = Math.Max(0, minPulseMs);

        if (u >= 0.94)
        {
            if (!_initialized || (_isHeld == false && (immediateOverride || _currentStateDurationMs >= minHold)))
            {
                _isHeld = true;
                _currentStateDurationMs = 0;
                _initialized = true;
            }
            else if (!_isHeld)
            {
                _currentStateDurationMs += dt;
                _accumulator = Math.Clamp(_accumulator + u, -1.0, 1.5);
                return false;
            }

            _currentStateDurationMs += dt;
            _accumulator = 0.25;
            return true;
        }

        if (u <= 0.06)
        {
            if (!_initialized || (_isHeld == true && (immediateOverride || _currentStateDurationMs >= minHold)))
            {
                _isHeld = false;
                _currentStateDurationMs = 0;
                _initialized = true;
            }
            else if (_isHeld)
            {
                _currentStateDurationMs += dt;
                _accumulator = Math.Clamp(_accumulator + u - 1.0, -1.0, 1.5);
                return true;
            }

            _currentStateDurationMs += dt;
            _accumulator = 0.0;
            return false;
        }

        _accumulator += u;
        var desiredHold = _accumulator >= 0.5;

        bool actualHold;
        if (!_initialized)
        {
            actualHold = desiredHold;
            _isHeld = actualHold;
            _currentStateDurationMs = dt;
            _initialized = true;
        }
        else if (desiredHold != _isHeld)
        {
            if (immediateOverride || _currentStateDurationMs >= minHold)
            {
                actualHold = desiredHold;
                _isHeld = actualHold;
                _currentStateDurationMs = dt;
            }
            else
            {
                // Keep current state until MinPulseMs is satisfied; accumulator retains the error for next frame!
                actualHold = _isHeld;
                _currentStateDurationMs += dt;
            }
        }
        else
        {
            actualHold = _isHeld;
            _currentStateDurationMs += dt;
        }

        _accumulator = Math.Clamp(_accumulator - (actualHold ? 1.0 : 0.0), -1.2, 1.5);
        return actualHold;
    }
}

/// <summary>
/// Real-time Closed-Loop Adaptive PD+FeedForward Servo Controller (Feedback Loop 3.0) with:
/// - Axis Polarity Normalization
/// - 1D Alpha-Beta Kinematic Filtering
/// - Latency Compensation &amp; Relative-Velocity Damping
/// - Comfort Zone &amp; Kinematic Stopping-Distance Braking
/// - Frame-Safe Sigma-Delta Modulation (<c>MinPulseMs</c> enforcement)
/// - Online Auto-Calibration &amp; Overshoot/Lag Adaptation
/// </summary>
public sealed class FeedbackServoController
{
    private readonly FeedbackLoopStep _step;
    private readonly SigmaDeltaModulator _modulator = new();
    private AlphaBetaFilter1D _targetFilter = new(alpha: 0.76, beta: 0.30, maxVelNorm: 8.0);
    private AlphaBetaFilter1D _indicatorFilter = new(alpha: 0.74, beta: 0.28, maxVelNorm: 8.0);

    private double _prevTargetPos;
    private double _prevIndicatorPos;
    private double _prevIndicatorMin;
    private double _prevIndicatorMax;
    private bool _hasPrevPositions;
    private double _targetVelNorm;
    private double _indicatorVelNorm;
    private double _prevTargetVelNorm;
    private double _prevIndicatorVelNorm;
    private double _prevRawErrorPx;
    private bool _hasPrevVelocities;
    private int _bothSeenTicks;
    private double _bothMissingElapsedMs;
    private int _coastFrames;

    // Online Auto-Calibration & Adaptation state
    private double _riseAccelPxPerSec2;
    private double _fallAccelPxPerSec2;
    private double _maxSpeedPxPerSec;
    private double _effectiveHoverDuty;
    private double _latencySeconds;
    private double _brakeScale = 1.0;
    private double _kpScale = 1.0;
    private double _sameSignErrorDurationMs;
    private double _lastDutyCycle;
    private int _accelSamples;

    public FeedbackServoController(FeedbackLoopStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        InitializeProfileFromStep();
    }

    /// <summary>True once both Target and Indicator have been detected for at least 3 ticks.</summary>
    public bool HasSeenMinigameOnce { get; private set; }

    /// <summary>Smoothed normalized target velocity (in trackLengths / sec along the Hold axis).</summary>
    public double TargetVelNorm => _targetVelNorm;

    /// <summary>Smoothed normalized indicator velocity (in trackLengths / sec along the Hold axis).</summary>
    public double IndicatorVelNorm => _indicatorVelNorm;

    /// <summary>Relative velocity (indicatorVelNorm - targetVelNorm) along the Hold axis.</summary>
    public double RelativeVelNorm => _indicatorVelNorm - _targetVelNorm;

    /// <summary>Total count of ankle-break / stopping-distance braking activations during this run.</summary>
    public int AnkleBrakes { get; private set; }

    /// <summary>Number of overshoot crossings detected and adapted during this run.</summary>
    public int OvershootCount { get; private set; }

    /// <summary>Number of tracked frames where the target was inside the player bar.</summary>
    public int OnTargetFrames { get; private set; }

    /// <summary>Total number of tracked frames where both target and player bar were active.</summary>
    public int TotalTrackedFrames { get; private set; }

    /// <summary>Percentage of tracked frames (0 to 100) where the target was inside the player bar.</summary>
    public double OnTargetPercent => TotalTrackedFrames > 0 ? (100.0 * OnTargetFrames / TotalTrackedFrames) : 100.0;

    /// <summary>Current online-calibrated bar physics profile.</summary>
    public BarProfile CurrentProfile => new(
        RiseAccelPxPerSec2: Math.Round(_riseAccelPxPerSec2, 1),
        FallAccelPxPerSec2: Math.Round(_fallAccelPxPerSec2, 1),
        MaxSpeedPxPerSec: Math.Round(_maxSpeedPxPerSec, 1),
        GlidePx: Math.Round((_maxSpeedPxPerSec * _maxSpeedPxPerSec) / Math.Max(100.0, 2.0 * (_riseAccelPxPerSec2 + _fallAccelPxPerSec2)), 1),
        LatencyMs: Math.Round(_latencySeconds * 1000.0, 1),
        HoverDuty: Math.Round(_effectiveHoverDuty, 3),
        IsCalibrated: _accelSamples >= 6);

    public void Reset()
    {
        _targetFilter.Reset();
        _indicatorFilter.Reset();
        _modulator.Reset();
        _hasPrevPositions = false;
        _hasPrevVelocities = false;
        _targetVelNorm = 0;
        _indicatorVelNorm = 0;
        _prevTargetVelNorm = 0;
        _prevIndicatorVelNorm = 0;
        _prevRawErrorPx = 0;
        _bothSeenTicks = 0;
        _bothMissingElapsedMs = 0;
        _coastFrames = 0;
        HasSeenMinigameOnce = false;
        AnkleBrakes = 0;
        OvershootCount = 0;
        OnTargetFrames = 0;
        TotalTrackedFrames = 0;
        InitializeProfileFromStep();
    }

    private void InitializeProfileFromStep()
    {
        var isHorizontal = _step.Orientation == BarOrientation.Horizontal;
        var trackLength = Math.Max(100.0, isHorizontal ? _step.RegionWidth : _step.RegionHeight);
        var profile = _step.LearnedProfile;

        _riseAccelPxPerSec2 = profile is { RiseAccelPxPerSec2: > 50 } ? profile.RiseAccelPxPerSec2 : trackLength * 2.6;
        _fallAccelPxPerSec2 = profile is { FallAccelPxPerSec2: > 50 } ? profile.FallAccelPxPerSec2 : trackLength * 2.3;
        _maxSpeedPxPerSec = profile is { MaxSpeedPxPerSec: > 50 } ? profile.MaxSpeedPxPerSec : trackLength * 1.8;
        _effectiveHoverDuty = Math.Clamp(_step.NeutralHoverDutyCycle, 0.0, 1.0);
        _latencySeconds = profile is { LatencyMs: >= 5 and <= 120 } ? (profile.LatencyMs / 1000.0) : 0.026;
        _brakeScale = 1.0;
        _kpScale = 1.0;
        _sameSignErrorDurationMs = 0;
        _lastDutyCycle = _effectiveHoverDuty;
        _accelSamples = profile is { IsCalibrated: true } ? 10 : 0;
    }

    /// <summary>
    /// Advances the servo controller by one control tick using the 1D scan results for Target and Indicator.
    /// </summary>
    public FeedbackServoStepResult Step(
        BarScanResult targetScan,
        BarScanResult indicatorScan,
        double dtSeconds,
        double estimatedBarWidth = 0)
    {
        var isHorizontal = _step.Orientation == BarOrientation.Horizontal;
        var trackLength = Math.Max(1.0, isHorizontal ? _step.RegionWidth : _step.RegionHeight);
        var cycleMs = Math.Clamp(_step.PwmCycleMs, 12, 50);
        var minPulseMs = Math.Clamp(_step.MinPulseMs, 8, 50);

        // 1. Axis Polarity Normalization:
        // Horizontal default: +1 (+X Right). Vertical default: -1 (-Y Up). InvertDirection flips polarity.
        var axisSign = (_step.Orientation == BarOrientation.Vertical) ? -1.0 : 1.0;
        if (_step.InvertDirection) axisSign *= -1.0;

        // 5. Auto-Exit When Minigame Finishes:
        if (targetScan.Found && indicatorScan.Found)
        {
            _bothSeenTicks++;
            if (_bothSeenTicks >= 3)
                HasSeenMinigameOnce = true;
            _bothMissingElapsedMs = 0;
        }
        else if (!targetScan.Found && !indicatorScan.Found)
        {
            _bothMissingElapsedMs += Math.Max(0.0, dtSeconds * 1000.0);
            if (_step.AutoExitWhenBarDisappears && HasSeenMinigameOnce && _bothMissingElapsedMs >= _step.DisappearExitMs)
            {
                var exitTelemetry = CreateTelemetry(
                    ServoTelemetryState.Searching,
                    false, _prevTargetPos, 0,
                    false, _prevIndicatorPos, _prevIndicatorMin, _prevIndicatorMax, 0,
                    0, 0.0, estimatedBarWidth);
                return new FeedbackServoStepResult(
                    0.0, cycleMs, 0, cycleMs, false, true,
                    ServoTelemetryState.Searching, 0, 0, 0, 0,
                    ShouldAutoExit: true, exitTelemetry, HoldInput: false);
            }
        }
        else
        {
            _bothMissingElapsedMs = 0;
        }

        // Handle missing/coasting frames
        var isCoasting = false;
        double targetPos, indicatorPos, indMin, indMax;

        if (targetScan.Found && indicatorScan.Found)
        {
            _coastFrames = 0;
            targetPos = targetScan.CenterPos;
            indicatorPos = indicatorScan.CenterPos;
            indMin = indicatorScan.MinEdge;
            indMax = indicatorScan.MaxEdge;
        }
        else if (_hasPrevPositions && _coastFrames < 4)
        {
            isCoasting = true;
            _coastFrames++;
            var dt = Math.Clamp(dtSeconds, 0.0, 0.1);
            if (targetScan.Found)
            {
                targetPos = targetScan.CenterPos;
            }
            else
            {
                _targetFilter.Coast(dt, trackLength, axisSign);
                _targetVelNorm = _targetFilter.VelocityNorm;
                targetPos = _targetFilter.Position;
            }

            if (indicatorScan.Found)
            {
                indicatorPos = indicatorScan.CenterPos;
                indMin = indicatorScan.MinEdge;
                indMax = indicatorScan.MaxEdge;
            }
            else
            {
                _indicatorFilter.Coast(dt, trackLength, axisSign);
                _indicatorVelNorm = _indicatorFilter.VelocityNorm;
                var deltaPx = _indicatorFilter.Position - _prevIndicatorPos;
                indicatorPos = _indicatorFilter.Position;
                indMin = Math.Clamp(_prevIndicatorMin + deltaPx, 0, trackLength);
                indMax = Math.Clamp(_prevIndicatorMax + deltaPx, 0, trackLength);
            }
        }
        else
        {
            // Searching: neither seen and cannot coast
            _targetFilter.Reset();
            _indicatorFilter.Reset();
            _modulator.Reset();
            _hasPrevPositions = false;
            _hasPrevVelocities = false;
            _targetVelNorm = 0;
            _indicatorVelNorm = 0;
            var searchTelemetry = CreateTelemetry(
                ServoTelemetryState.Searching,
                targetScan.Found, targetScan.CenterPos, 0,
                indicatorScan.Found, indicatorScan.CenterPos, indicatorScan.MinEdge, indicatorScan.MaxEdge, 0,
                0, 0.0, estimatedBarWidth);
            return new FeedbackServoStepResult(
                0.0, cycleMs, 0, cycleMs, false, true,
                ServoTelemetryState.Searching, 0, 0, 0, 0,
                ShouldAutoExit: false, searchTelemetry, HoldInput: false);
        }

        // Update 1D Alpha-Beta kinematic filters
        _prevTargetVelNorm = _targetVelNorm;
        _prevIndicatorVelNorm = _indicatorVelNorm;

        if (!isCoasting)
        {
            var maxJumpRatio = Math.Clamp(_step.MaxJumpPercent / 100.0, 0.10, 1.0);
            _targetFilter.Update(targetPos, dtSeconds, trackLength, axisSign, maxJumpRatio);
            _indicatorFilter.Update(indicatorPos, dtSeconds, trackLength, axisSign, maxJumpRatio);

            targetPos = _targetFilter.Position;
            indicatorPos = _indicatorFilter.Position;
            _targetVelNorm = _targetFilter.VelocityNorm;
            _indicatorVelNorm = _indicatorFilter.VelocityNorm;
            _hasPrevVelocities = _indicatorFilter.HasVelocity;
        }

        var rawErrorPx = (targetPos - indicatorPos) * axisSign;
        var normError = Math.Clamp(rawErrorPx / trackLength, -1.0, 1.0);
        var barWidth = estimatedBarWidth > 0 ? estimatedBarWidth : Math.Max(trackLength * 0.15, indMax - indMin);

        // Track On-Target Percentage (target inside player bar span)
        TotalTrackedFrames++;
        var halfBar = Math.Max(4.0, barWidth * 0.52);
        var isInsideBar = (targetPos >= indMin - 2.0 && targetPos <= indMax + 2.0) || Math.Abs(rawErrorPx) <= halfBar;
        if (isInsideBar)
            OnTargetFrames++;

        // Online Auto-Calibration & Overshoot/Lag Adaptation
        if (_step.AutoCalibrate && !isCoasting && _hasPrevVelocities && dtSeconds > 1e-4 && dtSeconds <= 0.1)
        {
            UpdateOnlineCalibration(rawErrorPx, normError, indicatorPos, trackLength, barWidth, dtSeconds);
        }

        _prevTargetPos = targetPos;
        _prevIndicatorPos = indicatorPos;
        _prevIndicatorMin = indMin;
        _prevIndicatorMax = indMax;
        _prevRawErrorPx = rawErrorPx;
        _hasPrevPositions = true;

        // Compute duty cycle via core control law
        var (dutyCycle, state, braked, stopDistPx) = ComputeDutyCycleCore(
            targetPos, indicatorPos, rawErrorPx, normError,
            _targetVelNorm, _indicatorVelNorm, _prevTargetVelNorm,
            trackLength, barWidth, axisSign, isCoasting);

        _lastDutyCycle = dutyCycle;
        if (braked) AnkleBrakes++;

        // Frame-Safe Sigma-Delta Modulator decision
        var dtMs = dtSeconds > 1e-4 ? (dtSeconds * 1000.0) : cycleMs;
        var immediateOverride = state is ServoTelemetryState.Braking or ServoTelemetryState.WallDrop or ServoTelemetryState.WallHold;
        var holdInput = _modulator.Step(dutyCycle, dtMs, minPulseMs, immediateOverride);

        // Legacy pulse fields for backward-compatible callers/tests
        bool continuousHold = false, continuousRelease = false;
        int holdMs, releaseMs;
        if (dutyCycle >= 0.92)
        {
            continuousHold = true;
            holdMs = cycleMs;
            releaseMs = 0;
        }
        else if (dutyCycle <= 0.08)
        {
            continuousRelease = true;
            holdMs = 0;
            releaseMs = cycleMs;
        }
        else
        {
            holdMs = Math.Clamp((int)Math.Round(dutyCycle * cycleMs), 1, cycleMs - 1);
            releaseMs = cycleMs - holdMs;
        }

        // Convert normalized velocities back to screen axis px/sec for telemetry vector display
        var targetVelScreenPxPerSec = _targetVelNorm * trackLength * axisSign;
        var indicatorVelScreenPxPerSec = _indicatorVelNorm * trackLength * axisSign;

        var telemetry = CreateTelemetry(
            state,
            targetScan.Found || isCoasting, targetPos, targetVelScreenPxPerSec,
            indicatorScan.Found || isCoasting, indicatorPos, indMin, indMax, indicatorVelScreenPxPerSec,
            rawErrorPx, dutyCycle, barWidth);

        return new FeedbackServoStepResult(
            dutyCycle,
            cycleMs,
            holdMs,
            releaseMs,
            continuousHold,
            continuousRelease,
            state,
            rawErrorPx,
            normError,
            _targetVelNorm,
            _indicatorVelNorm,
            ShouldAutoExit: false,
            telemetry,
            HoldInput: holdInput,
            RelativeVelNorm: _indicatorVelNorm - _targetVelNorm,
            StoppingDistancePx: stopDistPx);
    }

    private void UpdateOnlineCalibration(
        double rawErrorPx,
        double normError,
        double indicatorPos,
        double trackLength,
        double barWidth,
        double dtSeconds)
    {
        var notNearWall = indicatorPos > trackLength * 0.08 && indicatorPos < trackLength * 0.92;
        var accelPxPerSec2 = ((_indicatorVelNorm - _prevIndicatorVelNorm) * trackLength) / dtSeconds;
        var absVelPx = Math.Abs(_indicatorVelNorm * trackLength);
        if (absVelPx > _maxSpeedPxPerSec && absVelPx < trackLength * 4.0)
            _maxSpeedPxPerSec = (0.85 * _maxSpeedPxPerSec) + (0.15 * absVelPx);

        if (notNearWall && Math.Abs(accelPxPerSec2) >= trackLength * 0.25 && Math.Abs(accelPxPerSec2) <= trackLength * 12.0)
        {
            if (_lastDutyCycle >= 0.80 && accelPxPerSec2 > 0)
            {
                _riseAccelPxPerSec2 = (0.88 * _riseAccelPxPerSec2) + (0.12 * accelPxPerSec2);
                _accelSamples++;
            }
            else if (_lastDutyCycle <= 0.20 && accelPxPerSec2 < 0)
            {
                _fallAccelPxPerSec2 = (0.88 * _fallAccelPxPerSec2) + (0.12 * (-accelPxPerSec2));
                _accelSamples++;
            }

            if (_accelSamples >= 4 && (_riseAccelPxPerSec2 + _fallAccelPxPerSec2) > 100.0)
            {
                var impliedHover = Math.Clamp(_fallAccelPxPerSec2 / (_riseAccelPxPerSec2 + _fallAccelPxPerSec2), 0.25, 0.75);
                _effectiveHoverDuty = (0.95 * _effectiveHoverDuty) + (0.05 * impliedHover);
            }
        }

        // Detect overshoot: error flipped sign across zero while moving rapidly and outside inner deadzone
        var relVelNorm = _indicatorVelNorm - _targetVelNorm;
        var crossedTarget = _hasPrevPositions &&
                            Math.Sign(rawErrorPx) != 0 &&
                            Math.Sign(_prevRawErrorPx) != 0 &&
                            Math.Sign(rawErrorPx) != Math.Sign(_prevRawErrorPx) &&
                            Math.Abs(relVelNorm) > 0.45 &&
                            Math.Abs(rawErrorPx) > barWidth * 0.18;

        if (crossedTarget)
        {
            OvershootCount++;
            _brakeScale = Math.Min(2.20, _brakeScale + 0.08);
            _kpScale = Math.Max(0.72, _kpScale - 0.025);
            _sameSignErrorDurationMs = 0;
        }
        else if (Math.Abs(rawErrorPx) > barWidth * 0.35 && Math.Sign(rawErrorPx) == Math.Sign(_prevRawErrorPx))
        {
            _sameSignErrorDurationMs += dtSeconds * 1000.0;
            // If lagging behind on the same side for > 320 ms without closing fast enough, gently boost responsiveness & hover trim
            if (_sameSignErrorDurationMs >= 320.0 && Math.Abs(relVelNorm) < 0.30)
            {
                _kpScale = Math.Min(1.45, _kpScale + 0.03);
                _effectiveHoverDuty = Math.Clamp(_effectiveHoverDuty + (0.006 * Math.Sign(normError)), 0.25, 0.75);
                _sameSignErrorDurationMs = 160.0;
            }
        }
        else
        {
            _sameSignErrorDurationMs = 0;
        }

        if (_accelSamples >= 6)
            _step.LearnedProfile = CurrentProfile;
    }

    /// <summary>
    /// Pure control-law evaluation given positions and normalized velocities (can also be called directly in unit tests).
    /// </summary>
    public (double DutyCycle, ServoTelemetryState State, bool Braked) ComputeDutyCycle(
        double targetPos,
        double indicatorPos,
        double rawErrorPx,
        double normError,
        double targetVelNorm,
        double indicatorVelNorm,
        double prevTargetVelNorm,
        double trackLength,
        double estimatedBarWidth,
        double axisSign,
        bool isCoasting = false)
    {
        var (duty, state, braked, _) = ComputeDutyCycleCore(
            targetPos, indicatorPos, rawErrorPx, normError,
            targetVelNorm, indicatorVelNorm, prevTargetVelNorm,
            trackLength, estimatedBarWidth, axisSign, isCoasting);
        return (duty, state, braked);
    }

    private (double DutyCycle, ServoTelemetryState State, bool Braked, double StoppingDistancePx) ComputeDutyCycleCore(
        double targetPos,
        double indicatorPos,
        double rawErrorPx,
        double normError,
        double targetVelNorm,
        double indicatorVelNorm,
        double prevTargetVelNorm,
        double trackLength,
        double estimatedBarWidth,
        double axisSign,
        bool isCoasting)
    {
        var safeTrack = Math.Max(1.0, trackLength);

        // 2. Boundary / Wall Lock Zones:
        var rawTargetNorm = Math.Clamp(targetPos / safeTrack, 0.0, 1.0);
        var rawIndicatorNorm = Math.Clamp(indicatorPos / safeTrack, 0.0, 1.0);
        var targetNormPos = axisSign > 0 ? rawTargetNorm : (1.0 - rawTargetNorm);
        var indicatorNormPos = axisSign > 0 ? rawIndicatorNorm : (1.0 - rawIndicatorNorm);

        var wallZone = _step.BoundaryLockThresholdPercent <= 0
            ? 0.0
            : Math.Clamp(_step.BoundaryLockThresholdPercent / 100.0, 0.02, 0.25);

        if (wallZone > 0 && targetNormPos <= wallZone)
        {
            return (0.0, ServoTelemetryState.WallDrop, false, 0.0);
        }

        if (wallZone > 0 && targetNormPos >= (1.0 - wallZone))
        {
            var wallHoldDuty = indicatorNormPos >= 0.96 ? 0.85 : 0.94;
            return (wallHoldDuty, ServoTelemetryState.WallHold, false, 0.0);
        }

        // 3. Relative-Velocity Damping + Comfort Zone + Stopping-Distance Braking:
        var hoverDuty = _step.AutoCalibrate ? _effectiveHoverDuty : _step.NeutralHoverDutyCycle;
        var relVelNorm = indicatorVelNorm - targetVelNorm;
        var relVelPxPerSec = relVelNorm * safeTrack;

        // Responsiveness scaling (0 = Calm, 50 = Balanced, 100 = Aggressive)
        var respNorm = Math.Clamp(_step.Responsiveness, 0, 100) / 100.0;
        var respGainScale = (0.78 + (0.44 * respNorm)) * _kpScale;

        // Comfort zone radius inside the player bar
        var comfortMarginRatio = Math.Clamp(_step.ComfortZonePercent / 100.0, 0.05, 0.45);
        var comfortScale = 1.15 - (0.30 * respNorm);
        var comfortRadiusPx = Math.Max(3.0, estimatedBarWidth * Math.Clamp((0.5 - comfortMarginRatio) * comfortScale, 0.10, 0.38));

        // Kinematic stopping distance when closing in on the target
        var isClosingOnTarget = Math.Sign(relVelNorm) != 0 &&
                                Math.Sign(normError) != 0 &&
                                Math.Sign(relVelNorm) == Math.Sign(normError);

        var closingSpeedPxPerSec = isClosingOnTarget ? Math.Abs(relVelPxPerSec) : 0.0;
        var availableDecelPxPerSec2 = relVelNorm >= 0
            ? Math.Max(safeTrack * 1.2, _fallAccelPxPerSec2)
            : Math.Max(safeTrack * 1.2, _riseAccelPxPerSec2);

        var brakeMult = Math.Clamp(_step.AnkleBreakBrakingMultiplier, 0.5, 5.0) * _brakeScale;
        var stoppingDistancePx = isClosingOnTarget
            ? ((closingSpeedPxPerSec * closingSpeedPxPerSec) / (2.0 * availableDecelPxPerSec2)) * (0.95 * brakeMult)
              + (closingSpeedPxPerSec * _latencySeconds)
            : 0.0;

        var rushingToTarget = Math.Sign(indicatorVelNorm) != 0 &&
                              Math.Sign(indicatorVelNorm) == Math.Sign(normError) &&
                              Math.Abs(indicatorVelNorm) > Math.Abs(normError) * 2.2;

        var stoppingBrakeTriggered = isClosingOnTarget &&
                                     closingSpeedPxPerSec > safeTrack * 0.16 &&
                                     Math.Abs(rawErrorPx) <= Math.Max(5.0, stoppingDistancePx);

        var targetReversed = prevTargetVelNorm != 0 &&
                             Math.Sign(targetVelNorm) != 0 &&
                             Math.Sign(targetVelNorm) != Math.Sign(prevTargetVelNorm) &&
                             Math.Abs(targetVelNorm) > 0.15;

        var isAnkleBreak = rushingToTarget || stoppingBrakeTriggered || targetReversed;

        // Predict normalized error ahead by latency compensation
        var predErrorPx = rawErrorPx - (relVelPxPerSec * _latencySeconds);
        var predNormError = Math.Clamp(predErrorPx / safeTrack, -1.0, 1.0);

        var kdEff = isAnkleBreak ? (_step.Kd * brakeMult) : _step.Kd;
        var kpEff = _step.Kp * respGainScale;

        // Core PD + Feed-Forward control law using RELATIVE velocity damping (relVelNorm)
        // High derivative-to-proportional ratio (~380-420ms lookahead lead time) brakes low-drag bars early before crossing!
        var u = hoverDuty
                + (kpEff * predNormError * 1.85)
                + (_step.DerivativeTargetGain * targetVelNorm * 0.35)
                - (kdEff * relVelNorm * 1.15);

        // If kinematic stopping distance says we must brake now, apply direct counter-thrust to shed closing momentum
        if (isAnkleBreak && isClosingOnTarget)
        {
            var decelNeeded = (closingSpeedPxPerSec * closingSpeedPxPerSec) / Math.Max(4.0, 2.0 * Math.Abs(rawErrorPx));
            var brakeIntensity = Math.Clamp((decelNeeded / availableDecelPxPerSec2) * (0.65 * brakeMult), 0.30, 1.0);
            var counterU = relVelNorm > 0
                ? hoverDuty * (1.0 - brakeIntensity)
                : hoverDuty + ((1.0 - hoverDuty) * brakeIntensity);
            u = relVelNorm > 0 ? Math.Min(u, counterU) : Math.Max(u, counterU);
        }

        // Inside Comfort Zone: velocity-lock regime — hover smoothly and damp relative velocity strongly so the bar stays locked on target
        ServoTelemetryState state;
        if (Math.Abs(rawErrorPx) <= comfortRadiusPx && !isAnkleBreak)
        {
            var blend = Math.Clamp(Math.Abs(rawErrorPx) / comfortRadiusPx, 0.0, 1.0);
            var hoverU = hoverDuty
                         + (kpEff * normError * 0.85)
                         + (_step.DerivativeTargetGain * targetVelNorm * 0.38)
                         - (_step.Kd * relVelNorm * 1.05);
            u = (hoverU * (1.0 - blend)) + (u * blend);
            state = ServoTelemetryState.Locked;
        }
        else if (isAnkleBreak)
        {
            state = ServoTelemetryState.Braking;
        }
        else
        {
            state = ServoTelemetryState.Tracking;
        }

        if (isCoasting)
            state = ServoTelemetryState.Coasting;

        var dutyCycle = Math.Clamp(u, 0.0, 1.0);
        return (dutyCycle, state, isAnkleBreak, stoppingDistancePx);
    }

    public static string FormatStateLabel(ServoTelemetryState state) => state switch
    {
        ServoTelemetryState.Learning => "LEARNING",
        ServoTelemetryState.Locked => "LOCKED",
        ServoTelemetryState.Tracking => "TRACKING",
        ServoTelemetryState.Braking => "BRAKING",
        ServoTelemetryState.WallDrop => "WALL_DROP",
        ServoTelemetryState.WallHold => "WALL_HOLD",
        ServoTelemetryState.Coasting => "COASTING",
        _ => "SEARCHING",
    };

    private FeedbackTelemetryData CreateTelemetry(
        ServoTelemetryState state,
        bool targetFound, double targetPos, double targetVelPxPerSec,
        bool indicatorFound, double indicatorPos, double indMin, double indMax, double indicatorVelPxPerSec,
        double errorPx, double dutyCycle, double estimatedBarWidth) =>
        new(
            _step.Region,
            _step.Orientation,
            state,
            FormatStateLabel(state),
            targetFound,
            targetPos,
            targetVelPxPerSec,
            indicatorFound,
            indicatorPos,
            indMin,
            indMax,
            indicatorVelPxPerSec,
            errorPx,
            dutyCycle,
            estimatedBarWidth,
            OnTargetPercent);
}
