using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Vision;
using MacroCondition = SmartMacro.Core.Models.Condition;

namespace SmartMacro.ViewModels.Steps;

/// <summary>
/// Editor for a "Feedback loop" step (Stage 4): the bar area and marker colour, the target zone, the three rules,
/// which key / button to use, the safety limits and the optional done / failed conditions, plus the screen tools
/// (Pick bar area, Pick colors, Measure loop speed, Preview without input).
/// </summary>
/// <summary>The kinds of minigame offered as cards in the Feedback loop's guided setup.</summary>
public enum MinigameKind
{
    HorizontalFollower,
    VerticalFollower,
    TimingCheck,
    Custom,
}

/// <summary>One preset card: a little picture made of characters, a title and a one-line explanation.</summary>
public sealed record MinigameCard(MinigameKind Kind, string Picture, string Title, string Description);

public sealed class FeedbackLoopStepViewModel : StepViewModel
{
    private static readonly TimeSpan MeasureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(10);

    private readonly FeedbackLoopStep _step;
    private ScreenTools _tools = ScreenTools.None;
    private ConditionViewModel? _completeCondition;
    private ConditionViewModel? _failCondition;
    private string? _testResult;
    private bool _isBusy;
    private bool _showAdvanced;
    private ScreenPoint? _testMatch;

    public FeedbackLoopStepViewModel(FeedbackLoopStep step) : base(step)
    {
        _step = step;
        PickAreaCommand = new AsyncRelayCommand(PickAreaAsync, ex => TestResult = ex.Message, CanUseTools);
        ShowSearchAreaCommand = new RelayCommand(() =>
        {
            var area = new ScreenRect(_step.RegionX, _step.RegionY, _step.RegionWidth, _step.RegionHeight);
            SmartMacro.Services.Vision.ScreenHighlightOverlay.ShowResult(area, null, found: true, $"Bar area · {area.Width}×{area.Height}",
                TimeSpan.FromMilliseconds(1600));
        });
        PickMarkerColorCommand = new AsyncRelayCommand(() => PickColorAsync(zone: false), ex => TestResult = ex.Message, CanUseTools);
        PickZoneColorCommand = new AsyncRelayCommand(() => PickColorAsync(zone: true), ex => TestResult = ex.Message, CanUseTools);
        MeasureCommand = new AsyncRelayCommand(MeasureAsync, ex => TestResult = ex.Message, CanUseTools);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, ex => TestResult = ex.Message, CanUseTools);
        TestDetectionCommand = new AsyncRelayCommand(TestDetectionAsync, ex => TestResult = ex.Message, CanUseTools);
        ShowMatchCommand = new RelayCommand(() =>
        {
            if (_testMatch is { } spot) _tools.Vision?.ShowSpot(spot);
        }, () => _testMatch is not null && !_isBusy);
        _showAdvanced = step.Preset == FeedbackPreset.Custom;
        RebuildConditions();
    }

    // ---------------- Step 1 of the guided setup: which kind of minigame ----------------

    /// <summary>The visual preset cards at the top of the guided setup.</summary>
    public static IReadOnlyList<MinigameCard> MinigameCards { get; } = new[]
    {
        new MinigameCard(MinigameKind.HorizontalFollower, "◀ ── [ 🐟 ] ── ▶", "Horizontal Bar Follower (Left / Right)",
            "For fishing / reeling bars (like Fisch). Holds click when the target is to the Right of your bar; releases when it's to the Left."),
        new MinigameCard(MinigameKind.VerticalFollower, "▲ │ [ 🐟 ] │ ▼", "Vertical Bar Follower (Up / Down)",
            "Holds click to push your bar Up toward the target; releases to let it fall Down."),
        new MinigameCard(MinigameKind.TimingCheck, "🎯", "Timing / Skill Check (Click When Marker Hits Zone)",
            "Watches a moving marker and clicks the instant it enters the target zone."),
        new MinigameCard(MinigameKind.Custom, "⚙", "Custom / Advanced",
            "Every setting by hand: rules for before / inside / after the zone, key or mouse button, limits and stop conditions."),
    };

    /// <summary>Which card matches the step's current settings.</summary>
    public MinigameKind Minigame => _step.Preset switch
    {
        FeedbackPreset.FollowTargetBar => _step.Orientation == BarOrientation.Vertical ? MinigameKind.VerticalFollower : MinigameKind.HorizontalFollower,
        FeedbackPreset.ClickWhenInZone => MinigameKind.TimingCheck,
        _ => MinigameKind.Custom,
    };

    /// <summary>The selected card; choosing one sets up the loop for that kind of minigame (area and colours are kept).</summary>
    public MinigameCard? SelectedMinigame
    {
        get => MinigameCards.FirstOrDefault(c => c.Kind == Minigame);
        set
        {
            if (value is null || value.Kind == Minigame) return;
            switch (value.Kind)
            {
                case MinigameKind.HorizontalFollower:
                    _step.Orientation = BarOrientation.Horizontal;
                    FeedbackPresets.Apply(_step, FeedbackPreset.FollowTargetBar);
                    break;
                case MinigameKind.VerticalFollower:
                    _step.Orientation = BarOrientation.Vertical;
                    FeedbackPresets.Apply(_step, FeedbackPreset.FollowTargetBar);
                    break;
                case MinigameKind.TimingCheck:
                    FeedbackPresets.Apply(_step, FeedbackPreset.ClickWhenInZone);
                    break;
                default:
                    FeedbackPresets.Apply(_step, FeedbackPreset.Custom);
                    break;
            }

            _showAdvanced = value.Kind == MinigameKind.Custom;
            OnPropertyChanged(string.Empty); // many settings changed: refresh every field
            NotifyChanged();
        }
    }

    /// <summary>The plain-English rule preview card: what the loop will do when it holds / releases.</summary>
    public override string? PlainSummary => string.Join(Environment.NewLine, StepDescriber.ExplainFeedbackRules(_step).Select(line => "• " + line));

    /// <summary>Step 2's title depends on the minigame.</summary>
    public string AreaStepTitle => _step.Orientation == BarOrientation.Vertical
        ? "Step 2: Pick the minigame bar area (drag tightly around the up / down bar)"
        : "Step 2: Pick the minigame bar area (drag tightly around the left / right bar)";

    public string MarkerStepTitle => _step.Preset == FeedbackPreset.FollowTargetBar
        ? "Step 3: Pick the moving target color (e.g. the fish / indicator)"
        : "Step 3: Pick the moving marker color";

    public string ZoneStepTitle => _step.Preset == FeedbackPreset.FollowTargetBar
        ? "Step 4: Pick YOUR control bar color"
        : "Step 4: Pick the target zone color";

    // ---------------- Preset (QoL: guided setup) ----------------

    public static IReadOnlyList<Choice<FeedbackPreset>> Presets { get; } = new[]
    {
        new Choice<FeedbackPreset>(FeedbackPreset.FollowTargetBar, "Follow target bar (fishing / reeling minigame)"),
        new Choice<FeedbackPreset>(FeedbackPreset.ClickWhenInZone, "Click when marker enters zone (timing / skill check / casting meter)"),
        new Choice<FeedbackPreset>(FeedbackPreset.Custom, "Custom / advanced (all settings)"),
    };

    public static IReadOnlyList<Choice<bool>> ZoneEntryActions { get; } = new[]
    {
        new Choice<bool>(false, "Click once (the input stays released while waiting)"),
        new Choice<bool>(true, "Release the held input (hold while waiting, e.g. a casting meter)"),
    };

    /// <summary>Choosing a preset fills in the rules and limits for that kind of minigame (area and colours are kept).</summary>
    public FeedbackPreset Preset
    {
        get => _step.Preset;
        set
        {
            if (_step.Preset == value) return;
            FeedbackPresets.Apply(_step, value);
            _showAdvanced = value == FeedbackPreset.Custom; // presets start with the short guided view
            OnPropertyChanged(string.Empty); // the preset changed many settings: refresh every field
            NotifyChanged();
        }
    }

    public bool IsGuided => _step.Preset != FeedbackPreset.Custom;

    public bool IsFollowPreset => _step.Preset == FeedbackPreset.FollowTargetBar;

    public bool IsZoneClickPreset => _step.Preset == FeedbackPreset.ClickWhenInZone;

    /// <summary>Show every raw setting (always for Custom; on request for a preset).</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set => SetProperty(ref _showAdvanced, value);
    }

    /// <summary>"Click when it enters the zone": click once (false) or release the held input (true).</summary>
    public bool ZoneEntryReleases
    {
        get => _step.WhenInsideZone == FeedbackAction.Release;
        set
        {
            if (ZoneEntryReleases == value) return;
            FeedbackPresets.SetZoneEntryAction(_step, value);
            OnPropertyChanged(string.Empty);
            NotifyChanged();
        }
    }

    public string PresetHelp => _step.Preset switch
    {
        FeedbackPreset.FollowTargetBar => "Your bar (the zone) has to stay on the moving target (the marker). 1. Pick the minigame area tightly around the track. 2. Pick the target's color (e.g. the fish). 3. Pick YOUR bar's color. While the target is ahead of your bar (right / above) the left button is held, behind it it's released, and inside your bar it pulses proportionally so the bar doesn't overshoot. The loop ends by itself when the minigame disappears.",
        FeedbackPreset.ClickWhenInZone => "Waits for the moving marker to reach the target zone, then clicks once (or lets go of the held button) and finishes. 1. Pick the minigame area. 2. Pick the moving marker's color. 3. Pick the target zone's color (or switch the zone to a fixed % range under all settings).",
        _ => "Every setting is shown below. Pick a preset above for a guided setup.",
    };

    public string MarkerStepLabel => _step.Preset == FeedbackPreset.FollowTargetBar ? "2. Pick target color (what you follow)" : "2. Pick moving marker color";

    public string ZoneStepLabel => _step.Preset == FeedbackPreset.FollowTargetBar ? "3. Pick your bar's color" : "3. Pick target zone color";

    public RelayCommand ShowMatchCommand { get; }

    public AsyncRelayCommand TestDetectionCommand { get; }

    public bool UsesPulse => _step.WhenInsideZone == FeedbackAction.Pulse;

    public int PulsePeriodMs
    {
        get => _step.PulsePeriodMs;
        set { if (_step.PulsePeriodMs == value) return; _step.PulsePeriodMs = value; NotifyChanged(); }
    }

    public bool FinishWhenInZone
    {
        get => _step.FinishWhenInZone;
        set { if (_step.FinishWhenInZone == value) return; _step.FinishWhenInZone = value; NotifyChanged(); }
    }

    // ---------------- Choices for the drop-downs ----------------

    public static IReadOnlyList<Choice<BarOrientation>> Orientations { get; } = new[]
    {
        new Choice<BarOrientation>(BarOrientation.Horizontal, "Left ↔ right (horizontal bar)"),
        new Choice<BarOrientation>(BarOrientation.Vertical, "Up ↕ down (vertical bar)"),
    };

    public static IReadOnlyList<Choice<FeedbackZoneMode>> ZoneModes { get; } = new[]
    {
        new Choice<FeedbackZoneMode>(FeedbackZoneMode.Fixed, "A fixed part of the bar (%)"),
        new Choice<FeedbackZoneMode>(FeedbackZoneMode.DetectedByColor, "Found by its color (zone that moves)"),
    };

    public static IReadOnlyList<Choice<FeedbackAction>> Actions { get; } = new[]
    {
        new Choice<FeedbackAction>(FeedbackAction.Hold, "Hold down"),
        new Choice<FeedbackAction>(FeedbackAction.Release, "Release"),
        new Choice<FeedbackAction>(FeedbackAction.Keep, "Keep as it is"),
        new Choice<FeedbackAction>(FeedbackAction.Tap, "Tap once when it gets there"),
        new Choice<FeedbackAction>(FeedbackAction.Pulse, "Pulse proportionally (inside the zone only)"),
    };

    public static IReadOnlyList<Choice<FeedbackInputKind>> InputKinds { get; } = new[]
    {
        new Choice<FeedbackInputKind>(FeedbackInputKind.MouseButton, "A mouse button"),
        new Choice<FeedbackInputKind>(FeedbackInputKind.Key, "A key"),
    };

    public static IReadOnlyList<MacroMouseButton> MouseButtons { get; } = Enum.GetValues<MacroMouseButton>();

    public static IReadOnlyList<KeyInfo> Keys => VirtualKeys.All;

    public static IReadOnlyList<Choice<FeedbackLostAction>> LostActions { get; } = new[]
    {
        new Choice<FeedbackLostAction>(FeedbackLostAction.ReleaseAndWait, "Release and keep looking"),
        new Choice<FeedbackLostAction>(FeedbackLostAction.ExitLoop, "Release and continue with the next step"),
        new Choice<FeedbackLostAction>(FeedbackLostAction.StopMacro, "Release and stop the macro (error)"),
    };

    public static IReadOnlyList<Choice<LoopLimitBehavior>> EndOptions { get; } = new[]
    {
        new Choice<LoopLimitBehavior>(LoopLimitBehavior.ExitLoop, "Continue with the next step"),
        new Choice<LoopLimitBehavior>(LoopLimitBehavior.StopMacro, "Stop the macro (show an error)"),
    };

    // ---------------- Tools ----------------

    public AsyncRelayCommand PickAreaCommand { get; }

    /// <summary>"🎯 Show on screen": outlines the bar area this loop watches, for a moment.</summary>
    public RelayCommand ShowSearchAreaCommand { get; }
    public AsyncRelayCommand PickMarkerColorCommand { get; }
    public AsyncRelayCommand PickZoneColorCommand { get; }
    public AsyncRelayCommand MeasureCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }

    /// <summary>Screen tools from the editor (set right after creation). The conditions get them too.</summary>
    public ScreenTools Tools
    {
        get => _tools;
        set
        {
            _tools = value ?? ScreenTools.None;
            if (_completeCondition is not null) _completeCondition.Tools = _tools;
            if (_failCondition is not null) _failCondition.Tools = _tools;
        }
    }

    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    // ---------------- Observe ----------------

    public int RegionX
    {
        get => _step.RegionX;
        set { if (_step.RegionX == value) return; _step.RegionX = value; NotifyChanged(); }
    }

    public int RegionY
    {
        get => _step.RegionY;
        set { if (_step.RegionY == value) return; _step.RegionY = value; NotifyChanged(); }
    }

    public int RegionWidth
    {
        get => _step.RegionWidth;
        set { if (_step.RegionWidth == value) return; _step.RegionWidth = value; NotifyChanged(); }
    }

    public int RegionHeight
    {
        get => _step.RegionHeight;
        set { if (_step.RegionHeight == value) return; _step.RegionHeight = value; NotifyChanged(); }
    }

    public BarOrientation Orientation
    {
        get => _step.Orientation;
        set
        {
            if (_step.Orientation == value) return;
            _step.Orientation = value;
            if (_step.Preset == FeedbackPreset.FollowTargetBar)
            {
                // "Ahead" is right on a horizontal bar but above on a vertical one: keep the rules right.
                FeedbackPresets.ApplyFollowRules(_step);
                OnPropertyChanged(nameof(WhenBeforeZone));
                OnPropertyChanged(nameof(WhenAfterZone));
                OnPropertyChanged(nameof(WhenInsideZone));
            }

            NotifyChanged();
            OnPropertyChanged(nameof(BeforeLabel));
            OnPropertyChanged(nameof(AfterLabel));
            OnPropertyChanged(nameof(SelectedMinigame));
            OnPropertyChanged(nameof(AreaStepTitle));
        }
    }

    public string MarkerColor
    {
        get => _step.MarkerColor ?? string.Empty;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (_step.MarkerColor == value) return;
            _step.MarkerColor = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MarkerBrush));
        }
    }

    public System.Windows.Media.Brush MarkerBrush => ToBrush(_step.MarkerColor);

    public int MarkerTolerance
    {
        get => _step.MarkerTolerance;
        set { if (_step.MarkerTolerance == value) return; _step.MarkerTolerance = value; NotifyChanged(); }
    }

    public int MarkerMinPixels
    {
        get => _step.MarkerMinPixels;
        set { if (_step.MarkerMinPixels == value) return; _step.MarkerMinPixels = value; NotifyChanged(); }
    }

    public int MaxJumpPercent
    {
        get => _step.MaxJumpPercent;
        set { if (_step.MaxJumpPercent == value) return; _step.MaxJumpPercent = value; NotifyChanged(); }
    }

    // ---------------- Zone ----------------

    public FeedbackZoneMode ZoneMode
    {
        get => _step.ZoneMode;
        set
        {
            if (_step.ZoneMode == value) return;
            _step.ZoneMode = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsFixedZone));
            OnPropertyChanged(nameof(IsDetectedZone));
        }
    }

    public bool IsFixedZone => _step.ZoneMode == FeedbackZoneMode.Fixed;

    public bool IsDetectedZone => _step.ZoneMode == FeedbackZoneMode.DetectedByColor;

    public int ZoneStartPercent
    {
        get => _step.ZoneStartPercent;
        set { if (_step.ZoneStartPercent == value) return; _step.ZoneStartPercent = value; NotifyChanged(); }
    }

    public int ZoneEndPercent
    {
        get => _step.ZoneEndPercent;
        set { if (_step.ZoneEndPercent == value) return; _step.ZoneEndPercent = value; NotifyChanged(); }
    }

    public string ZoneColor
    {
        get => _step.ZoneColor ?? string.Empty;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (_step.ZoneColor == value) return;
            _step.ZoneColor = value;
            NotifyChanged();
            OnPropertyChanged(nameof(ZoneBrush));
        }
    }

    public System.Windows.Media.Brush ZoneBrush => ToBrush(_step.ZoneColor);

    public int ZoneTolerance
    {
        get => _step.ZoneTolerance;
        set { if (_step.ZoneTolerance == value) return; _step.ZoneTolerance = value; NotifyChanged(); }
    }

    public int ZoneMinPixels
    {
        get => _step.ZoneMinPixels;
        set { if (_step.ZoneMinPixels == value) return; _step.ZoneMinPixels = value; NotifyChanged(); }
    }

    // ---------------- Rules ----------------

    public string BeforeLabel => _step.Orientation == BarOrientation.Horizontal
        ? "When the marker is LEFT of the zone"
        : "When the marker is ABOVE the zone";

    public string AfterLabel => _step.Orientation == BarOrientation.Horizontal
        ? "When the marker is RIGHT of the zone"
        : "When the marker is BELOW the zone";

    public FeedbackAction WhenBeforeZone
    {
        get => _step.WhenBeforeZone;
        set { if (_step.WhenBeforeZone == value) return; _step.WhenBeforeZone = value; NotifyChanged(); OnPropertyChanged(nameof(UsesTap)); }
    }

    public FeedbackAction WhenInsideZone
    {
        get => _step.WhenInsideZone;
        set
        {
            if (_step.WhenInsideZone == value) return;
            _step.WhenInsideZone = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesTap));
            OnPropertyChanged(nameof(UsesPulse));
            OnPropertyChanged(nameof(ZoneEntryReleases));
        }
    }

    public FeedbackAction WhenAfterZone
    {
        get => _step.WhenAfterZone;
        set { if (_step.WhenAfterZone == value) return; _step.WhenAfterZone = value; NotifyChanged(); OnPropertyChanged(nameof(UsesTap)); }
    }

    public bool UsesTap => _step.UsesTap;

    public int DeadBandPercent
    {
        get => _step.DeadBandPercent;
        set { if (_step.DeadBandPercent == value) return; _step.DeadBandPercent = value; NotifyChanged(); }
    }

    // ---------------- Act ----------------

    public FeedbackInputKind InputKind
    {
        get => _step.InputKind;
        set
        {
            if (_step.InputKind == value) return;
            _step.InputKind = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesMouseButton));
            OnPropertyChanged(nameof(UsesKey));
        }
    }

    public bool UsesMouseButton => _step.InputKind == FeedbackInputKind.MouseButton;

    public bool UsesKey => _step.InputKind == FeedbackInputKind.Key;

    public MacroMouseButton MouseButton
    {
        get => _step.MouseButton;
        set { if (_step.MouseButton == value) return; _step.MouseButton = value; NotifyChanged(); }
    }

    public int VirtualKey
    {
        get => _step.VirtualKey;
        set { if (_step.VirtualKey == value) return; _step.VirtualKey = value; NotifyChanged(); }
    }

    public int TapMs
    {
        get => _step.TapMs;
        set { if (_step.TapMs == value) return; _step.TapMs = value; NotifyChanged(); }
    }

    // ---------------- Limits ----------------

    public int MaxCapturesPerSecond
    {
        get => _step.MaxCapturesPerSecond;
        set { if (_step.MaxCapturesPerSecond == value) return; _step.MaxCapturesPerSecond = value; NotifyChanged(); }
    }

    public int MinInputChangeMs
    {
        get => _step.MinInputChangeMs;
        set { if (_step.MinInputChangeMs == value) return; _step.MinInputChangeMs = value; NotifyChanged(); }
    }

    public int MaxRuntimeMs
    {
        get => _step.MaxRuntimeMs;
        set
        {
            if (_step.MaxRuntimeMs == value) return;
            _step.MaxRuntimeMs = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MaxRuntimeText));
        }
    }

    public string MaxRuntimeText => $"= {_step.MaxRuntimeMs / 1000.0:0.###} seconds (required; paused time doesn't count)";

    public LoopLimitBehavior WhenTimeUp
    {
        get => _step.WhenTimeUp;
        set { if (_step.WhenTimeUp == value) return; _step.WhenTimeUp = value; NotifyChanged(); }
    }

    public int LostTimeoutMs
    {
        get => _step.LostTimeoutMs;
        set { if (_step.LostTimeoutMs == value) return; _step.LostTimeoutMs = value; NotifyChanged(); }
    }

    public FeedbackLostAction WhenTargetLost
    {
        get => _step.WhenTargetLost;
        set { if (_step.WhenTargetLost == value) return; _step.WhenTargetLost = value; NotifyChanged(); }
    }

    // ---------------- Stop when ----------------

    /// <summary>Turns the "done when" condition on (a new colour condition) or off.</summary>
    public bool HasCompleteCondition
    {
        get => _step.CompleteWhen is not null;
        set
        {
            if (HasCompleteCondition == value) return;
            _step.CompleteWhen = value ? MacroFactory.NewCondition(ConditionKind.ColorVisible) : null;
            RebuildConditions();
            NotifyChanged();
        }
    }

    /// <summary>Turns the "failed when" condition on (a new colour condition) or off.</summary>
    public bool HasFailCondition
    {
        get => _step.FailWhen is not null;
        set
        {
            if (HasFailCondition == value) return;
            _step.FailWhen = value ? MacroFactory.NewCondition(ConditionKind.ColorVisible) : null;
            RebuildConditions();
            NotifyChanged();
        }
    }

    public ConditionViewModel? CompleteCondition
    {
        get => _completeCondition;
        private set => SetProperty(ref _completeCondition, value);
    }

    public ConditionViewModel? FailCondition
    {
        get => _failCondition;
        private set => SetProperty(ref _failCondition, value);
    }

    public LoopLimitBehavior WhenFailed
    {
        get => _step.WhenFailed;
        set { if (_step.WhenFailed == value) return; _step.WhenFailed = value; NotifyChanged(); }
    }

    // ---------------- Helpers ----------------

    private bool CanUseTools() => _tools.Vision is not null && !_isBusy;

    private void RebuildConditions()
    {
        CompleteCondition = _step.CompleteWhen is { } complete
            ? new ConditionViewModel(complete, () => NotifyChanged(nameof(CompleteCondition))) { Tools = _tools }
            : null;
        FailCondition = _step.FailWhen is { } fail
            ? new ConditionViewModel(fail, () => NotifyChanged(nameof(FailCondition))) { Tools = _tools }
            : null;
    }

    private static System.Windows.Media.Brush ToBrush(string? text)
    {
        if (!RgbColor.TryParse(text, out var c)) return System.Windows.Media.Brushes.Transparent;
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Runs a screen tool with the buttons disabled; errors end up in the result box.</summary>
    private async Task RunBusyAsync(Func<IVisionTools, Task> action)
    {
        if (_isBusy || _tools.Vision is not { } vision) return;
        _isBusy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            await action(vision);
        }
        finally
        {
            _isBusy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private Task PickAreaAsync() => RunBusyAsync(async vision =>
    {
        var picked = await vision.PickRegionAsync();
        if (picked is not { } rect)
        {
            TestResult = "Picking the bar area was cancelled.";
            return;
        }

        _step.RegionX = rect.X;
        _step.RegionY = rect.Y;
        _step.RegionWidth = rect.Width;
        _step.RegionHeight = rect.Height;
        // A wide area is a left/right bar, a tall one an up/down bar.
        _step.Orientation = rect.Width >= rect.Height ? BarOrientation.Horizontal : BarOrientation.Vertical;
        if (_step.Preset == FeedbackPreset.FollowTargetBar) FeedbackPresets.ApplyFollowRules(_step);
        OnPropertyChanged(string.Empty); // refresh all fields
        NotifyChanged(nameof(RegionX));  // mark the macro as changed + re-validate
        TestResult = $"Bar area set to {rect}; direction: {(_step.Orientation == BarOrientation.Horizontal ? "left ↔ right" : "up ↕ down")}. Next: pick the marker color.";
    });

    private Task PickColorAsync(bool zone) => RunBusyAsync(async vision =>
    {
        var picked = await vision.PickColorAsync();
        if (picked is null)
        {
            TestResult = "Picking the color was cancelled.";
            return;
        }

        if (zone)
        {
            ZoneColor = picked.Color.ToString();
            TestResult = $"Zone color {picked.Color} picked at ({picked.X}, {picked.Y}). Use \"Preview\" to see the zone it finds.";
        }
        else
        {
            MarkerColor = picked.Color.ToString();
            TestResult = $"Marker color {picked.Color} picked at ({picked.X}, {picked.Y}). Use \"Measure loop speed\" or \"Preview\" to check it.";
        }
    });

    private Task MeasureAsync() => RunBusyAsync(async vision =>
    {
        if (!CheckBasics()) return;
        TestResult = $"Measuring for {MeasureDuration.TotalSeconds:0} seconds (no input is sent)…";
        TestResult = await vision.MeasureFeedbackAsync(_step, MeasureDuration);
    });

    private Task PreviewAsync() => RunBusyAsync(async vision =>
    {
        if (!CheckBasics()) return;
        TestResult = $"Previewing for {PreviewDuration.TotalSeconds:0} seconds (no input is sent)…";
        TestResult = await vision.PreviewFeedbackAsync(_step, PreviewDuration);
    });

    /// <summary>"Test detection now": one capture, where the marker / zone are and what the loop would do (no input).</summary>
    private Task TestDetectionAsync() => RunBusyAsync(async vision =>
    {
        if (!CheckBasics()) return;
        TestResult = "Looking at the bar once (no input is sent)…";
        TestResult = await vision.TestFeedbackOnceAsync(_step);
        _testMatch = vision.LastTestMatch;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    });

    /// <summary>The tools need a usable area and colours; say what's missing instead of failing later.</summary>
    private bool CheckBasics()
    {
        var problems = MacroValidator.ValidateStep(_step)
            .Where(i => i.Severity == IssueSeverity.Error && i.Property is "Region"
                or nameof(FeedbackLoopStep.MarkerColor) or nameof(FeedbackLoopStep.ZoneColor)
                or nameof(FeedbackLoopStep.MarkerTolerance) or nameof(FeedbackLoopStep.ZoneTolerance)
                or nameof(FeedbackLoopStep.MarkerMinPixels) or nameof(FeedbackLoopStep.ZoneMinPixels)
                or nameof(FeedbackLoopStep.ZoneStartPercent))
            .Select(i => i.Message)
            .ToList();
        if (problems.Count == 0) return true;

        TestResult = "Fix these first: " + string.Join(" ", problems);
        return false;
    }
}
