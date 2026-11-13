using System.Runtime.CompilerServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;


public sealed class DelayStepViewModel : StepViewModel
{
    private readonly DelayStep _step;

    public DelayStepViewModel(DelayStep step) : base(step)
    {
        _step = step;
        AddDurationCommand = new RelayCommand(p =>
        {
            if (TryParseAdditiveMs(p, out var ms))
                DurationMs = ms == 0 ? 0 : Math.Max(0, Math.Max(0, DurationMs) + ms);
        });
        ResetDurationCommand = new RelayCommand(_ => DurationMs = 0);
        SetDurationCommand = AddDurationCommand;
    }

    public static IReadOnlyList<Choice<DelayMode>> Modes { get; } = new[]
    {
        new Choice<DelayMode>(DelayMode.Fixed, "⏱ Fixed time (exact milliseconds)"),
        new Choice<DelayMode>(DelayMode.UntilScreenSettles, "✨ Until screen settles (Smart — fast when ready, safe when slow)"),
        new Choice<DelayMode>(DelayMode.UntilScreenChanges, "⚡ Until screen changes (wake as soon as UI updates)"),
    };

    public RelayCommand SetDurationCommand { get; }
    public RelayCommand AddDurationCommand { get; }
    public RelayCommand ResetDurationCommand { get; }

    public DelayMode Mode
    {
        get => _step.Mode;
        set
        {
            if (_step.Mode == value) return;
            _step.Mode = value;
            if (value != DelayMode.Fixed && _step.MaxDurationMs <= 0)
            {
                var baseMs = Math.Max(200, _step.DurationMs);
                if (_step.MinDurationMs <= 0)
                    _step.MinDurationMs = value == DelayMode.UntilScreenChanges ? 20 : Math.Clamp(baseMs / 4, 60, 250);
                _step.MaxDurationMs = Math.Clamp(baseMs * 3, Math.Max(600, _step.MinDurationMs), 15_000);
            }

            NotifyChanged();
            OnPropertyChanged(nameof(IsFixed));
            OnPropertyChanged(nameof(IsAdaptive));
            OnPropertyChanged(nameof(MinDurationMs));
            OnPropertyChanged(nameof(MaxDurationMs));
            OnPropertyChanged(nameof(DurationFieldLabel));
            OnPropertyChanged(nameof(AdaptiveModeHelp));
        }
    }

    public bool IsFixed => _step.Mode == DelayMode.Fixed;

    public bool IsAdaptive => _step.Mode != DelayMode.Fixed;

    public int MinDurationMs
    {
        get => _step.MinDurationMs;
        set
        {
            if (_step.MinDurationMs == value) return;
            _step.MinDurationMs = value;
            NotifyChanged();
        }
    }

    public int MaxDurationMs
    {
        get => _step.MaxDurationMs;
        set
        {
            if (_step.MaxDurationMs == value) return;
            _step.MaxDurationMs = value;
            NotifyChanged();
        }
    }

    public string DurationFieldLabel => IsAdaptive
        ? "Recorded / fallback wait time (ms)"
        : "Wait time (milliseconds)";

    public string AdaptiveModeHelp => _step.Mode switch
    {
        DelayMode.UntilScreenSettles =>
            "Watches the area around the last action after Minimum wait: continues immediately once the screen stays calm (~120 ms), or stops waiting at Max timeout so slow pages never break playback.",
        DelayMode.UntilScreenChanges =>
            "Watches the area around the last action after Minimum wait and continues the instant any visible UI change appears (up to Max timeout).",
        _ =>
            "Waits for the exact number of milliseconds below.",
    };

    public int DurationMs
    {
        get => _step.DurationMs;
        set
        {
            if (_step.DurationMs == value) return;
            _step.DurationMs = value;
            NotifyChanged();
            OnPropertyChanged(nameof(SecondsText));
        }
    }

    public string SecondsText => $"= {_step.DurationMs / 1000.0:0.###} seconds";
}

public sealed class MouseMoveStepViewModel : StepViewModel
{
    private readonly MouseMoveStep _step;

    public MouseMoveStepViewModel(MouseMoveStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
        OffsetPicker = new OffsetPickerViewModel(SetOffset, () => ReferenceFinder?.Invoke());
    }

    /// <summary>"Pick position" / "Use cursor position" for the destination.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    /// <summary>"🎯 Pick offset from screen" for "To the last found spot" / "By an offset from the cursor".</summary>
    public OffsetPickerViewModel OffsetPicker { get; }

    /// <summary>Set by the editor: the Find step / If-Repeat condition before this step whose found spot it uses.</summary>
    public Func<ScreenSearchStep?>? ReferenceFinder { get; set; }

    public static IReadOnlyList<Choice<ClickTargetMode>> Targets { get; } = new[]
    {
        new Choice<ClickTargetMode>(ClickTargetMode.Coordinates, "To a chosen position (X / Y below)"),
        new Choice<ClickTargetMode>(ClickTargetMode.LastFoundMatch, "To what the last Find step / If condition found (+ offset)"),
        new Choice<ClickTargetMode>(ClickTargetMode.CurrentCursor, "By an offset from where the cursor is"),
    };

    /// <summary>Where to move (AAA upgrade): a fixed spot, the last found spot + offset, or the cursor + offset.</summary>
    public ClickTargetMode TargetMode
    {
        get => _step.TargetMode;
        set
        {
            if (_step.TargetMode == value) return;
            _step.TargetMode = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesFixedPosition));
            OnPropertyChanged(nameof(UsesOffset));
            OnPropertyChanged(nameof(OffsetHelp));
        }
    }

    public bool UsesFixedPosition => _step.TargetMode == ClickTargetMode.Coordinates;

    public bool UsesOffset => _step.TargetMode != ClickTargetMode.Coordinates;

    /// <summary>"🎮 Relative mouse delta (3D camera)": play DeltaX / DeltaY as relative mouse movement.</summary>
    public bool UseRelativeDelta
    {
        get => _step.UseRelativeDelta;
        set
        {
            if (_step.UseRelativeDelta == value) return;
            _step.UseRelativeDelta = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsAbsolute));
        }
    }

    /// <summary>The normal (absolute position / offset) settings apply.</summary>
    public bool IsAbsolute => !_step.UseRelativeDelta;

    public int DeltaX
    {
        get => _step.DeltaX;
        set { if (_step.DeltaX == value) return; _step.DeltaX = value; NotifyChanged(); }
    }

    public int DeltaY
    {
        get => _step.DeltaY;
        set { if (_step.DeltaY == value) return; _step.DeltaY = value; NotifyChanged(); }
    }

    public int OffsetX
    {
        get => _step.OffsetX;
        set { if (_step.OffsetX == value) return; _step.OffsetX = value; NotifyChanged(); }
    }

    public int OffsetY
    {
        get => _step.OffsetY;
        set { if (_step.OffsetY == value) return; _step.OffsetY = value; NotifyChanged(); }
    }

    public string OffsetHelp => _step.TargetMode == ClickTargetMode.CurrentCursor
        ? "Moves the cursor this many pixels from wherever it is when the step runs (+X = right, +Y = down)."
        : "Moves to the center of what the last Find step / If condition found, plus this offset (+X = right, +Y = down). \"Pick offset from screen\" finds the target and lets you click the spot.";

    private void SetOffset(int x, int y)
    {
        _step.OffsetX = x;
        _step.OffsetY = y;
        OnPropertyChanged(nameof(OffsetX));
        NotifyChanged(nameof(OffsetY));
    }

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        if (_step.TargetMode != ClickTargetMode.Coordinates)
        {
            _step.TargetMode = ClickTargetMode.Coordinates; // picking a spot means "go there"
            OnPropertyChanged(nameof(TargetMode));
            OnPropertyChanged(nameof(UsesFixedPosition));
            OnPropertyChanged(nameof(UsesOffset));
        }

        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
    }

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }

    /// <summary>How the cursor travels there (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _step.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_step.MoveStyle == value) return;
            _step.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
            OnPropertyChanged(nameof(MoveHelp));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _step.MoveDurationMs;
        set { if (_step.MoveDurationMs == value) return; _step.MoveDurationMs = value; NotifyChanged(); }
    }

    public string MoveHelp => Glides
        ? "The cursor glides to this spot (about 120 updates per second), so hover effects, sliders and game buttons see it arrive. Nothing is clicked."
        : "The cursor jumps straight to this spot and nothing is clicked. Add a Mouse click step to click, or use Mouse click on its own: it can move and click in one step.";
}

public sealed class MouseClickStepViewModel : StepViewModel
{
    private readonly MouseClickStep _step;

    private System.Windows.Media.ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public MouseClickStepViewModel(MouseClickStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
        OffsetPicker = new OffsetPickerViewModel(SetOffset, () => ReferenceFinder?.Invoke());
        EndPoint = new PointEditorViewModel(
            () => _step.EndX, v => _step.EndX = v,
            () => _step.EndY, v => _step.EndY = v,
            () => NotifyChanged(nameof(EndPoint)));
        AddDurationCommand = new RelayCommand(p =>
        {
            if (TryParseAdditiveMs(p, out var ms))
                HoldMs = ms == 0 ? 0 : Math.Max(0, Math.Max(0, HoldMs) + ms);
        });
        ResetDurationCommand = new RelayCommand(_ => HoldMs = 0);
        SetDurationCommand = AddDurationCommand;
        TestSmartAimCommand = new AsyncRelayCommand(TestSmartAimAsync, ex => SmartAimTestResult = $"✗ Test failed: {ex.Message}",
            () => _visionTools is not null && !_testingSmartAim && HasTargetThumbnail);
        LocateTargetCommand = new AsyncRelayCommand(LocateTargetAsync, ex => SmartAimTestResult = $"✗ Locate Target failed: {ex.Message}",
            () => _visionTools is not null && !_testingSmartAim && HasSmartCard);
    }

    public RelayCommand SetDurationCommand { get; }
    public RelayCommand AddDurationCommand { get; }
    public RelayCommand ResetDurationCommand { get; }

    // ---------------- 🎯 Test Smart-Aim & 🧭 Locate Target ----------------

    private IVisionTools? _visionTools;
    private bool _testingSmartAim;

    public static IReadOnlyList<Choice<BackgroundInputBackend>> BackgroundBackends { get; } = new[]
    {
        new Choice<BackgroundInputBackend>(BackgroundInputBackend.Auto, "Auto (UI Automation → PostMessage)"),
        new Choice<BackgroundInputBackend>(BackgroundInputBackend.Win32PostMessage, "Win32 PostMessage"),
        new Choice<BackgroundInputBackend>(BackgroundInputBackend.UiAutomationInvoke, "UI Automation only"),
    };

    public string BackgroundWindowTitle
    {
        get => _step.BackgroundWindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.BackgroundWindowTitle == normalized) return;
            _step.BackgroundWindowTitle = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(HasBackgroundTarget));
        }
    }

    public bool HasBackgroundTarget => !string.IsNullOrWhiteSpace(_step.BackgroundWindowTitle);

    public BackgroundInputBackend BackgroundBackend
    {
        get => _step.BackgroundBackend;
        set
        {
            if (_step.BackgroundBackend == value) return;
            _step.BackgroundBackend = value;
            NotifyChanged();
        }
    }
    private string? _smartAimTestResult;

    /// <summary>Screen tools from the editor (set right after the step editor is created).</summary>
    public IVisionTools? VisionTools
    {
        get => _visionTools;
        set
        {
            _visionTools = value;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>"🎯 Test Smart-Aim": looks for the recorded picture right now (nothing is clicked) and shows the result.</summary>
    public AsyncRelayCommand TestSmartAimCommand { get; }

    /// <summary>"🧭 Locate Target": tests all 5 Smart Intent strategies right now and shows ✓/✗ for each tier.</summary>
    public AsyncRelayCommand LocateTargetCommand { get; }

    /// <summary>"✓ Found at (512, 340) — 98% match in 3 ms…" / "✗ Not found within 120 px…", or null before the first test.</summary>
    public string? SmartAimTestResult
    {
        get => _smartAimTestResult;
        private set => SetProperty(ref _smartAimTestResult, value);
    }

    private async Task TestSmartAimAsync()
    {
        if (_visionTools is not { } tools || _testingSmartAim) return;
        _testingSmartAim = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            SmartAimTestResult = "Looking for the picture on screen…";
            SmartAimTestResult = await tools.TestSmartAimAsync(_step);
        }
        finally
        {
            _testingSmartAim = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task LocateTargetAsync()
    {
        if (_visionTools is not { } tools || _testingSmartAim) return;
        _testingSmartAim = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            SmartAimTestResult = "Testing all 5 target strategies on screen…";
            SmartAimTestResult = await tools.LocateTargetAsync(_step);
        }
        finally
        {
            _testingSmartAim = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public static IReadOnlyList<MacroMouseButton> Buttons { get; } = Enum.GetValues<MacroMouseButton>();

    public static IReadOnlyList<Choice<MouseClickAction>> Actions { get; } = new[]
    {
        new Choice<MouseClickAction>(MouseClickAction.Click, "Single click"),
        new Choice<MouseClickAction>(MouseClickAction.DoubleClick, "Double click"),
        new Choice<MouseClickAction>(MouseClickAction.Hold, "Hold for a duration (ms)"),
        new Choice<MouseClickAction>(MouseClickAction.Drag, "Drag to a position"),
        new Choice<MouseClickAction>(MouseClickAction.Down, "Press down only (release in a later step)"),
        new Choice<MouseClickAction>(MouseClickAction.Up, "Release only"),
    };

    /// <summary>Drag: where the drag ends ("Pick position" works here too).</summary>
    public PointEditorViewModel EndPoint { get; }

    public static IReadOnlyList<Choice<ClickTarget>> Targets { get; } = new[]
    {
        new Choice<ClickTarget>(ClickTarget.ChosenPosition, "At a chosen position (X / Y below)"),
        new Choice<ClickTarget>(ClickTarget.CurrentCursor, "Where the cursor already is"),
        new Choice<ClickTarget>(ClickTarget.LastFound, "On what the last Find step / If condition found (+ offset)"),
    };

    /// <summary>"Pick position" / "Use cursor position" for the click position.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    /// <summary>Where the click happens: one choice instead of two check boxes (same two settings underneath).</summary>
    public ClickTarget Target
    {
        get => _step.TargetMode switch
        {
            ClickTargetMode.LastFoundMatch => ClickTarget.LastFound,
            ClickTargetMode.CurrentCursor => ClickTarget.CurrentCursor,
            _ => ClickTarget.ChosenPosition,
        };
        set
        {
            if (Target == value) return;
            _step.TargetMode = value switch
            {
                ClickTarget.LastFound => ClickTargetMode.LastFoundMatch,
                ClickTarget.CurrentCursor => ClickTargetMode.CurrentCursor,
                _ => ClickTargetMode.Coordinates,
            };
            NotifyChanged();
            RaiseTargetChanged();
        }
    }

    private void RaiseTargetChanged()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(UseCurrentPosition));
        OnPropertyChanged(nameof(UseLastFoundText));
        OnPropertyChanged(nameof(UsesFixedPosition));
        OnPropertyChanged(nameof(UsesLastFound));
        OnPropertyChanged(nameof(SmartAimHint));
        OnPropertyChanged(nameof(SmartIntentHint));
    }

    // ---------------- AAA: "On the last found spot" + offset ----------------

    /// <summary>"🎯 Pick offset from screen" for "On what the last Find step found".</summary>
    public OffsetPickerViewModel OffsetPicker { get; }

    /// <summary>Set by the editor: the Find step / If-Repeat condition before this step whose found spot it uses.</summary>
    public Func<ScreenSearchStep?>? ReferenceFinder { get; set; }

    public bool UsesLastFound => _step.UseLastFoundText;

    public int OffsetX
    {
        get => _step.OffsetX;
        set { if (_step.OffsetX == value) return; _step.OffsetX = value; NotifyChanged(); }
    }

    public int OffsetY
    {
        get => _step.OffsetY;
        set { if (_step.OffsetY == value) return; _step.OffsetY = value; NotifyChanged(); }
    }

    private void SetOffset(int x, int y)
    {
        _step.OffsetX = x;
        _step.OffsetY = y;
        OnPropertyChanged(nameof(OffsetX));
        NotifyChanged(nameof(OffsetY));
    }

    // ---------------- AAA + v1.1: Smart Click Snapshot, Smart-Aim & Smart Intent ----------------

    /// <summary>True when this click was recorded with a picture of what it hit.</summary>
    public bool HasTargetThumbnail => _step.HasTargetThumbnail;

    /// <summary>True when this click captured UI element, OCR text or window anchor signals.</summary>
    public bool HasTargetIntent => _step.HasTargetIntent;

    /// <summary>True when the Smart Click &amp; Intent card has either a visual thumbnail or an intent fingerprint.</summary>
    public bool HasSmartCard => HasTargetThumbnail || HasTargetIntent;

    /// <summary>Human-readable summary of captured intent signals (UI button, OCR text, window anchor).</summary>
    public string TargetSignalsSummary => _step.Target?.DescribeSignals() ?? string.Empty;

    /// <summary>"[x] 🧭 Smart Intent: relocate target automatically (UI → OCR → Image → Window → X/Y)".</summary>
    public bool UseSmartIntent
    {
        get => _step.UseSmartIntent;
        set
        {
            if (_step.UseSmartIntent == value) return;
            _step.UseSmartIntent = value;
            NotifyChanged();
            OnPropertyChanged(nameof(SmartIntentHint));
        }
    }

    public string SmartIntentHint => !_step.UseSmartIntent
        ? "Off: SmartMacro skips UI / OCR / window relocation (Smart-Aim or fixed X / Y below is used instead)."
        : _step.UseCurrentPosition || _step.UseLastFoundText
            ? "Smart Intent only applies when \"Where to click\" is \"At a chosen position\"."
            : "On: playback walks UI Automation → OCR Text → Image Snapshot (75%–150%) → Window-Relative → Coordinates, so moving, resizing or rescaling the window still clicks the right target.";

    /// <summary>The recorded 56×56 picture of what was clicked (null when none / unreadable).</summary>
    public System.Windows.Media.ImageSource? TargetThumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = LoadThumbnail(_step.TargetThumbnailBase64);
            }

            return _thumbnail;
        }
    }

    /// <summary>"56 × 56 px · clicked at the center" etc.</summary>
    public string TargetThumbnailInfo
    {
        get
        {
            if (TargetThumbnail is not System.Windows.Media.Imaging.BitmapSource bitmap) return "The recorded picture can't be shown (it may be damaged).";
            var w = bitmap.PixelWidth;
            var h = bitmap.PixelHeight;
            var (ax, ay) = Core.Workflow.SmartClickConversion.Anchor(_step, w, h);
            var where = ax == w / 2 && ay == h / 2 ? "clicked in the middle" : $"clicked at ({ax}, {ay}) inside it (near a screen edge)";
            return $"What you clicked when recording: {w} × {h} px, {where}.";
        }
    }

    /// <summary>"[x] ✨ Smart-Aim: find &amp; click this visual target if it moved".</summary>
    public bool SmartAimAtTarget
    {
        get => _step.SmartAimAtTarget;
        set
        {
            if (_step.SmartAimAtTarget == value) return;
            _step.SmartAimAtTarget = value;
            NotifyChanged();
            OnPropertyChanged(nameof(SmartAimHint));
        }
    }

    /// <summary>Smart-Aim accuracy in percent (50–100; stored as 0.5–1.0).</summary>
    public int SmartAimAccuracyPercent
    {
        get => (int)Math.Round(_step.SmartAimAccuracy * 100);
        set
        {
            var accuracy = value / 100.0;
            if (_step.SmartAimAccuracy.Equals(accuracy)) return;
            _step.SmartAimAccuracy = accuracy;
            NotifyChanged();
        }
    }

    /// <summary>How far (px) the target may have moved.</summary>
    public int SmartAimRadiusPx
    {
        get => _step.SmartAimRadiusPx;
        set
        {
            if (_step.SmartAimRadiusPx == value) return;
            _step.SmartAimRadiusPx = value;
            NotifyChanged();
            OnPropertyChanged(nameof(SmartAimLabel));
        }
    }

    public string SmartAimLabel => $"✨ Smart-Aim: find & click this visual target if it moved (within {_step.SmartAimRadiusPx} px)";

    public string SmartAimHint => !_step.SmartAimAtTarget
        ? "Off: the click always happens at X / Y, exactly as recorded."
        : _step.UseCurrentPosition || _step.UseLastFoundText
            ? "Smart-Aim only works with \"At a chosen position\" (it looks around X / Y)."
            : $"On: before clicking, SmartMacro looks for this picture within {_step.SmartAimRadiusPx} px of where it was and clicks the same spot on it. Not found = it clicks X / Y as recorded.";

    /// <summary>True when "🔍 Convert step to full 'Find image &amp; click'" can be used.</summary>
    public bool CanConvertToFindImage => Core.Workflow.SmartClickConversion.WhyNotConvertible(_step) is null;

    public string ConvertHint => Core.Workflow.SmartClickConversion.WhyNotConvertible(_step)
        ?? "Replaces this click with a Find image step that looks for this picture anywhere on screen (up to 5 s) and does the same click on it. Ctrl+Z undoes it.";

    // Cached + frozen (Converters.FrozenBitmapCache): recycled step cards, Undo / Redo and reloads don't decode it again.
    private static System.Windows.Media.ImageSource? LoadThumbnail(string? base64) =>
        SmartMacro.Converters.FrozenBitmapCache.FromPngBase64(base64);

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        Target = ClickTarget.ChosenPosition; // picking a spot means "click there"
        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
    }

    public MacroMouseButton Button
    {
        get => _step.Button;
        set { if (_step.Button == value) return; _step.Button = value; NotifyChanged(); }
    }

    public MouseClickAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            if (value == MouseClickAction.Hold && _step.HoldMs <= 0) _step.HoldMs = MouseClickStep.DefaultHoldActionMs;
            if (value == MouseClickAction.Drag && _step.EndX == 0 && _step.EndY == 0)
            {
                // Start the end point a little to the right of the start, so the drag does something visible.
                _step.EndX = _step.X + 100;
                _step.EndY = _step.Y;
                EndPoint.Refresh();
            }

            NotifyChanged();
            OnPropertyChanged(nameof(IsClick));
            OnPropertyChanged(nameof(UsesHoldTime));
            OnPropertyChanged(nameof(IsDrag));
            OnPropertyChanged(nameof(IsDoubleClick));
            OnPropertyChanged(nameof(UsesClickTiming));
            OnPropertyChanged(nameof(HoldMs));
            OnPropertyChanged(nameof(ActionHelp));
            OnPropertyChanged(nameof(CanConvertToFindImage));
            OnPropertyChanged(nameof(ConvertHint));
        }
    }

    public bool IsClick => _step.Action == MouseClickAction.Click;

    /// <summary>The hold-time box is used by Single click (recorded hold), Double click (each click) and Hold.</summary>
    public bool UsesHoldTime => _step.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;

    public bool IsDrag => _step.Action == MouseClickAction.Drag;

    /// <summary>Drag: how long the glide from start to end takes.</summary>
    public int DragDurationMs
    {
        get => _step.DragDurationMs;
        set { if (_step.DragDurationMs == value) return; _step.DragDurationMs = value; NotifyChanged(); }
    }

    public string ActionHelp => _step.Action switch
    {
        MouseClickAction.Hold => "Presses the button, keeps it down for the time below, then releases it. Good for casting meters and hold-to-buy buttons.",
        MouseClickAction.Drag => "Presses the button at the start position, glides smoothly to the end position over the drag time while holding it, then releases. Good for sliders and dragging items.",
        MouseClickAction.Down => "Only presses the button. Add a \"Release only\" step later to let go (everything held is released when the macro ends).",
        MouseClickAction.Up => "Only releases the button (pressed by an earlier \"Press down only\" step).",
        MouseClickAction.DoubleClick => "Two quick clicks, each held for the click hold time, with the double-click gap in between.",
        _ => "One click. The click hold keeps the button down that long (35 ms is reliable in games; recordings fill in the real time).",
    };

    public bool UseCurrentPosition
    {
        get => _step.UseCurrentPosition;
        set
        {
            if (_step.UseCurrentPosition == value) return;
            _step.UseCurrentPosition = value;
            if (value) _step.UseLastFoundText = false;
            NotifyChanged();
            RaiseTargetChanged();
        }
    }

    /// <summary>Click where the last Find text / image / color step found something.</summary>
    public bool UseLastFoundText
    {
        get => _step.UseLastFoundText;
        set
        {
            if (_step.UseLastFoundText == value) return;
            _step.UseLastFoundText = value;
            if (value) _step.UseCurrentPosition = false;
            NotifyChanged();
            RaiseTargetChanged();
        }
    }

    public bool UsesFixedPosition => !_step.UseCurrentPosition && !_step.UseLastFoundText;

    /// <summary>How long a Click keeps the button down (0 = instant).</summary>
    public int HoldMs
    {
        get => _step.HoldMs;
        set
        {
            if (_step.HoldMs == value) return;
            _step.HoldMs = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HoldDurationMs));
        }
    }

    /// <summary>"Hold duration (ms, 0 = instant click)" — the step's HoldDurationMs (same value as <see cref="HoldMs"/>).</summary>
    public int HoldDurationMs
    {
        get => _step.HoldDurationMs;
        set => HoldMs = value;
    }

    /// <summary>How the cursor travels to the click spot (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _step.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_step.MoveStyle == value) return;
            _step.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _step.MoveDurationMs;
        set { if (_step.MoveDurationMs == value) return; _step.MoveDurationMs = value; NotifyChanged(); }
    }

    /// <summary>Double click: gap between the two clicks.</summary>
    public int DoubleClickIntervalMs
    {
        get => _step.DoubleClickIntervalMs;
        set { if (_step.DoubleClickIntervalMs == value) return; _step.DoubleClickIntervalMs = value; NotifyChanged(); }
    }

    public bool IsDoubleClick => _step.Action == MouseClickAction.DoubleClick;

    /// <summary>"Wait after click (ms)".</summary>
    public int PostActionDelayMs
    {
        get => _step.PostActionDelayMs;
        set { if (_step.PostActionDelayMs == value) return; _step.PostActionDelayMs = value; NotifyChanged(); }
    }

    /// <summary>Hold time also applies to a timed double click.</summary>
    public bool UsesClickTiming => _step.Action is MouseClickAction.Click or MouseClickAction.DoubleClick or MouseClickAction.Hold;

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }
}

/// <summary>
/// An extra X / Y pair with its own "Pick position" buttons (e.g. where a Drag ends). Works with the shared
/// PositionPickerTemplate, which binds X, Y and PositionPicker.
/// </summary>
public sealed class PointEditorViewModel : Mvvm.ObservableObject
{
    private readonly Func<int> _getX;
    private readonly Action<int> _setX;
    private readonly Func<int> _getY;
    private readonly Action<int> _setY;
    private readonly Action _changed;

    public PointEditorViewModel(Func<int> getX, Action<int> setX, Func<int> getY, Action<int> setY, Action changed)
    {
        _getX = getX;
        _setX = setX;
        _getY = getY;
        _setY = setY;
        _changed = changed;
        PositionPicker = new PositionPickerViewModel((x, y) =>
        {
            _setX(x);
            _setY(y);
            Refresh();
            _changed();
        });
    }

    public PositionPickerViewModel PositionPicker { get; }

    public int X
    {
        get => _getX();
        set { if (_getX() == value) return; _setX(value); OnPropertyChanged(); _changed(); }
    }

    public int Y
    {
        get => _getY();
        set { if (_getY() == value) return; _setY(value); OnPropertyChanged(); _changed(); }
    }

    /// <summary>Re-reads X / Y after the step was changed elsewhere.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }
}

/// <summary>Where a Mouse click happens.</summary>
public enum ClickTarget
{
    ChosenPosition,
    CurrentCursor,
    LastFound,
}

/// <summary>Which way the wheel turns (a Scroll wheel step's sign + horizontal setting, as one choice).</summary>
public enum ScrollDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Scroll wheel editor. The step stores Windows' wheel amount (120 = one notch, positive = up / right) and a
/// "horizontal" flag; the editor shows that as Direction + Number of notches. Stored values are only changed when
/// the user edits them, so old macros keep exactly what they had (including amounts that aren't whole notches).
/// </summary>
public sealed class MouseScrollStepViewModel : StepViewModel
{
    /// <summary>Windows' wheel amount for one notch (WHEEL_DELTA).</summary>
    public const int NotchDelta = 120;

    private readonly MouseScrollStep _step;

    public MouseScrollStepViewModel(MouseScrollStep step) : base(step)
    {
        _step = step;
        PositionPicker = new PositionPickerViewModel(SetPosition);
    }

    public static IReadOnlyList<Choice<ScrollDirection>> Directions { get; } = new[]
    {
        new Choice<ScrollDirection>(ScrollDirection.Up, "Up"),
        new Choice<ScrollDirection>(ScrollDirection.Down, "Down"),
        new Choice<ScrollDirection>(ScrollDirection.Left, "Left (sideways)"),
        new Choice<ScrollDirection>(ScrollDirection.Right, "Right (sideways)"),
    };

    public static IReadOnlyList<Choice<bool>> PositionModes { get; } = new[]
    {
        new Choice<bool>(true, "Where the cursor already is"),
        new Choice<bool>(false, "At a chosen position (X / Y below)"),
    };

    /// <summary>"Pick position" / "Use cursor position" for where to scroll.</summary>
    public PositionPickerViewModel PositionPicker { get; }

    /// <summary>Raw Windows amount (120 = one notch). Shown under "exact amount" for recorded / odd values.</summary>
    public int Delta
    {
        get => _step.Delta;
        set
        {
            if (_step.Delta == value) return;
            _step.Delta = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Direction));
            OnPropertyChanged(nameof(Notches));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    public ScrollDirection Direction
    {
        get => _step.Horizontal
            ? (_step.Delta >= 0 ? ScrollDirection.Right : ScrollDirection.Left)
            : (_step.Delta >= 0 ? ScrollDirection.Up : ScrollDirection.Down);
        set
        {
            if (Direction == value) return;
            var horizontal = value is ScrollDirection.Left or ScrollDirection.Right;
            var positive = value is ScrollDirection.Up or ScrollDirection.Right;
            var size = Math.Abs((long)_step.Delta);
            if (size == 0) size = NotchDelta;
            _step.Horizontal = horizontal;
            _step.Delta = (int)(positive ? size : -size);
            NotifyChanged();
            OnPropertyChanged(nameof(Horizontal));
            OnPropertyChanged(nameof(Delta));
            OnPropertyChanged(nameof(Notches));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    /// <summary>Whole notches. Typing a number sets the amount to that many notches in the current direction.</summary>
    public int Notches
    {
        get => (int)(Math.Abs((long)_step.Delta) / NotchDelta);
        set
        {
            if (Notches == value && Math.Abs((long)_step.Delta) % NotchDelta == 0) return;
            var positive = Direction is ScrollDirection.Up or ScrollDirection.Right;
            var size = Math.Clamp((long)value, 0, int.MaxValue / NotchDelta) * NotchDelta;
            _step.Delta = (int)(positive ? size : -size);
            NotifyChanged();
            OnPropertyChanged(nameof(Delta));
            OnPropertyChanged(nameof(AmountText));
        }
    }

    /// <summary>Explains the amount in words, including amounts that aren't whole notches (some recordings / touchpads).</summary>
    public string AmountText
    {
        get
        {
            var size = Math.Abs((long)_step.Delta);
            var notches = size / (double)NotchDelta;
            var exact = size % NotchDelta == 0 ? "" : $" Exact amount: {size} (= {notches:0.##} notches; Windows counts 120 per notch).";
            return $"1 notch = one click of a normal mouse wheel (often about 3 lines of text). Several notches are sent one by one, 15 ms apart, so games and browsers count every one.{exact}";
        }
    }

    public bool Horizontal
    {
        get => _step.Horizontal;
        set
        {
            if (_step.Horizontal == value) return;
            _step.Horizontal = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Direction));
        }
    }

    public bool UseCurrentPosition
    {
        get => _step.UseCurrentPosition;
        set
        {
            if (_step.UseCurrentPosition == value) return;
            _step.UseCurrentPosition = value;
            NotifyChanged();
            OnPropertyChanged(nameof(UsesFixedPosition));
        }
    }

    public bool UsesFixedPosition => !_step.UseCurrentPosition;

    public int X
    {
        get => _step.X;
        set { if (_step.X == value) return; _step.X = value; NotifyChanged(); }
    }

    public int Y
    {
        get => _step.Y;
        set { if (_step.Y == value) return; _step.Y = value; NotifyChanged(); }
    }

    private void SetPosition(int x, int y)
    {
        _step.X = x;
        _step.Y = y;
        UseCurrentPosition = false; // picking a spot means "scroll there"
        OnPropertyChanged(nameof(X));
        NotifyChanged(nameof(Y));
    }
}

public sealed class KeyPressStepViewModel : StepViewModel
{
    private readonly KeyPressStep _step;

    public KeyPressStepViewModel(KeyPressStep step) : base(step)
    {
        _step = step;
        AddDurationCommand = new RelayCommand(p =>
        {
            if (TryParseAdditiveMs(p, out var ms))
                HoldMs = ms == 0 ? 0 : Math.Max(0, Math.Max(0, HoldMs) + ms);
        });
        ResetDurationCommand = new RelayCommand(_ => HoldMs = 0);
        SetDurationCommand = AddDurationCommand;
    }

    public RelayCommand SetDurationCommand { get; }
    public RelayCommand AddDurationCommand { get; }
    public RelayCommand ResetDurationCommand { get; }

    public static IReadOnlyList<KeyInfo> Keys => VirtualKeys.All;

    public static IReadOnlyList<KeyAction> Actions { get; } = Enum.GetValues<KeyAction>();

    private bool _isRecordingKey;
    private RelayCommand? _toggleKeyRecordCommand;

    public RelayCommand ToggleKeyRecordCommand => _toggleKeyRecordCommand ??= new RelayCommand(_ => IsRecordingKey = !IsRecordingKey);

    public bool IsRecordingKey
    {
        get => _isRecordingKey;
        set
        {
            if (SetProperty(ref _isRecordingKey, value))
                OnPropertyChanged(nameof(KeyRecorderButtonText));
        }
    }

    public string KeyRecorderButtonText => _isRecordingKey
        ? "⌨ Listening… Press any key (Esc to cancel)"
        : "🎹 Press Key to Bind…";

    public string KeycapDisplayText => KeycapBadge ?? VirtualKeys.GetName(_step.VirtualKey);

    public string HardwareKeyInfoText => _step.ScanCode > 0
        ? $"VK 0x{_step.VirtualKey:X2} · ScanCode 0x{_step.ScanCode:X2}{(_step.Extended ? " (Extended E0)" : "")}"
        : $"VK 0x{_step.VirtualKey:X2} · Standard Virtual-Key";

    public string BackgroundWindowTitle
    {
        get => _step.BackgroundWindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.BackgroundWindowTitle == normalized) return;
            _step.BackgroundWindowTitle = normalized;
            NotifyChanged();
        }
    }

    public int ScanCode
    {
        get => _step.ScanCode;
        set
        {
            if (_step.ScanCode == value) return;
            _step.ScanCode = Math.Clamp(value, 0, 0xFF);
            NotifyChanged();
            OnPropertyChanged(nameof(HardwareKeyInfoText));
        }
    }

    public bool Extended
    {
        get => _step.Extended;
        set
        {
            if (_step.Extended == value) return;
            _step.Extended = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HardwareKeyInfoText));
        }
    }

    public void ApplyRecordedKey(int virtualKey, int scanCode, bool extended, KeyModifiers modifiers)
    {
        if (!VirtualKeys.IsValid(virtualKey)) return;
        _step.VirtualKey = virtualKey;
        _step.ScanCode = Math.Clamp(scanCode, 0, 0xFF);
        _step.Extended = extended;
        if (_step.Action == KeyAction.Press)
            _step.Modifiers = modifiers;

        IsRecordingKey = false;
        OnPropertyChanged(nameof(VirtualKey));
        OnPropertyChanged(nameof(ScanCode));
        OnPropertyChanged(nameof(Extended));
        OnPropertyChanged(nameof(Ctrl));
        OnPropertyChanged(nameof(Shift));
        OnPropertyChanged(nameof(Alt));
        OnPropertyChanged(nameof(Win));
        OnPropertyChanged(nameof(KeycapDisplayText));
        OnPropertyChanged(nameof(HardwareKeyInfoText));
        NotifyChanged(nameof(VirtualKey));
    }

    public int VirtualKey
    {
        get => _step.VirtualKey;
        set
        {
            if (_step.VirtualKey == value) return;
            _step.VirtualKey = value;
            _step.ScanCode = 0;
            _step.Extended = false;
            NotifyChanged();
            OnPropertyChanged(nameof(KeycapDisplayText));
            OnPropertyChanged(nameof(HardwareKeyInfoText));
        }
    }

    public KeyAction Action
    {
        get => _step.Action;
        set
        {
            if (_step.Action == value) return;
            _step.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsPress));
        }
    }

    public bool IsPress => _step.Action == KeyAction.Press;

    /// <summary>How long a Press keeps the key down (0 = instant).</summary>
    public int HoldMs
    {
        get => _step.HoldMs;
        set { if (_step.HoldMs == value) return; _step.HoldMs = value; NotifyChanged(); }
    }

    public bool Ctrl
    {
        get => Has(KeyModifiers.Control);
        set => SetModifier(KeyModifiers.Control, value);
    }

    public bool Shift
    {
        get => Has(KeyModifiers.Shift);
        set => SetModifier(KeyModifiers.Shift, value);
    }

    public bool Alt
    {
        get => Has(KeyModifiers.Alt);
        set => SetModifier(KeyModifiers.Alt, value);
    }

    public bool Win
    {
        get => Has(KeyModifiers.Windows);
        set => SetModifier(KeyModifiers.Windows, value);
    }

    private bool Has(KeyModifiers modifier) => (_step.Modifiers & modifier) == modifier;

    private void SetModifier(KeyModifiers modifier, bool on, [CallerMemberName] string? propertyName = null)
    {
        var updated = on ? _step.Modifiers | modifier : _step.Modifiers & ~modifier;
        if (updated == _step.Modifiers) return;
        _step.Modifiers = updated;
        OnPropertyChanged(nameof(KeycapDisplayText));
        NotifyChanged(propertyName);
    }
}

public sealed class TypeTextStepViewModel : StepViewModel
{
    private readonly TypeTextStep _step;

    public TypeTextStepViewModel(TypeTextStep step) : base(step)
    {
        _step = step;
        InsertVariableTokenCommand = new RelayCommand(p =>
        {
            var token = p as string;
            if (string.IsNullOrWhiteSpace(token)) token = "{value}";
            else if (!token.StartsWith('{')) token = $"{{{token}}}";
            Text = string.IsNullOrEmpty(Text) ? token : $"{Text}{token}";
        });
    }

    public static IReadOnlyList<Choice<BackgroundInputBackend>> BackgroundBackends => MouseClickStepViewModel.BackgroundBackends;

    public string BackgroundWindowTitle
    {
        get => _step.BackgroundWindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_step.BackgroundWindowTitle == normalized) return;
            _step.BackgroundWindowTitle = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(HasBackgroundTarget));
        }
    }

    public bool HasBackgroundTarget => !string.IsNullOrWhiteSpace(_step.BackgroundWindowTitle);

    public BackgroundInputBackend BackgroundBackend
    {
        get => _step.BackgroundBackend;
        set
        {
            if (_step.BackgroundBackend == value) return;
            _step.BackgroundBackend = value;
            NotifyChanged();
        }
    }

    public RelayCommand InsertVariableTokenCommand { get; }

    public string Text
    {
        get => _step.Text ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (_step.Text == value) return;
            _step.Text = value;
            NotifyChanged();
            OnPropertyChanged(nameof(CharacterCountText));
        }
    }

    public string CharacterCountText => $"{(_step.Text ?? string.Empty).Length:N0} / {MacroValidator.MaxTextLength:N0} characters";

    public int CharacterDelayMs
    {
        get => _step.CharacterDelayMs;
        set { if (_step.CharacterDelayMs == value) return; _step.CharacterDelayMs = value; NotifyChanged(); }
    }
}

/// <summary>A choice in a drop-down: the stored value plus a friendly label.</summary>
public sealed record Choice<T>(T Value, string Label);

/// <summary>
/// Shared editor part of Find text / Find image / Find color: search area, timing, what to wait for, what to do
/// with the result, plus "busy" handling for the screen tools (only one tool runs at a time).
/// </summary>
