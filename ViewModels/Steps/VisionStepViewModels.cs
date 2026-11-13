using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;

namespace SmartMacro.ViewModels.Steps;


public abstract class ScreenSearchStepViewModel : StepViewModel
{
    private readonly ScreenSearchStep _search;
    private string? _testResult;
    private bool _isBusy;
    private ScreenPoint? _testMatch;
    private bool? _testFound;
    private IScreenLayout? _layout;

    protected ScreenSearchStepViewModel(ScreenSearchStep step) : base(step)
    {
        _search = step;
        FailurePolicy = new FailurePolicyViewModel(
            () => _search.WhenNotFound, v => _search.WhenNotFound = v,
            () => _search.FailurePolicy, v => _search.FailurePolicy = v,
            () => NotifyChanged(nameof(FailurePolicy)));
        PickRegionCommand = new AsyncRelayCommand(PickRegionAsync, ex => TestResult = ex.Message, () => HasTools && !_isBusy);
        ShowMatchCommand = new RelayCommand(() =>
        {
            if (_testMatch is { } spot) ShowSpot(spot);
        }, () => _testMatch is not null && !_isBusy);
        RefreshMonitorsCommand = new RelayCommand(RefreshMonitors);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        ShowSearchAreaCommand = new RelayCommand(
            () => TestResult = SmartMacro.Services.Vision.ScreenHighlightOverlay.ShowSearchArea(_search, _layout),
            () => !_isBusy);
        AddDurationCommand = new RelayCommand(p =>
        {
            if (TryParseAdditiveMs(p, out var ms))
                TimeoutMs = ms == 0 ? 0 : Math.Max(0, Math.Max(0, TimeoutMs) + ms);
        });
        ResetDurationCommand = new RelayCommand(_ => TimeoutMs = 0);
        SetDurationCommand = AddDurationCommand;
        ApplyScenarioCommand = new RelayCommand(p =>
        {
            if (p is VisionScenario s) Scenario = s;
            else if (p is string text && Enum.TryParse<VisionScenario>(text, ignoreCase: true, out var parsed)) Scenario = parsed;
        });
        RefreshMonitors();
    }

    public RelayCommand SetDurationCommand { get; }
    public RelayCommand AddDurationCommand { get; }
    public RelayCommand ResetDurationCommand { get; }
    public RelayCommand ApplyScenarioCommand { get; }
    public FailurePolicyViewModel FailurePolicy { get; }

    // ---------------- Vision 2.0 Scenario Presets & Tracking ----------------

    public static IReadOnlyList<Choice<VisionScenario>> Scenarios { get; } = new[]
    {
        new Choice<VisionScenario>(VisionScenario.Custom, "⚙ Custom (Manual Tuning)"),
        new Choice<VisionScenario>(VisionScenario.StaticUi, "🖥 Static Desktop / App UI"),
        new Choice<VisionScenario>(VisionScenario.GameHud, "🎮 Game HUD / Menu Button (Lighting & Edge Tolerant)"),
        new Choice<VisionScenario>(VisionScenario.MovingTarget, "🏃 Moving Object (Kalman Velocity Tracking + Lead Click)"),
        new Choice<VisionScenario>(VisionScenario.BusyParticles, "💥 Busy Combat / Heavy Particles (60% Partial Visibility + 2-Frame Confirm)"),
        new Choice<VisionScenario>(VisionScenario.ScrollingText, "📜 Fast / Moving Text (Find Once via OCR, Track as Image at 60 FPS)"),
        new Choice<VisionScenario>(VisionScenario.FastReaction, "⚡ Fast Reaction / QTE (16 ms Polling + Instant Click)"),
    };

    public static IReadOnlyList<Choice<VisionSearchStrategy>> Strategies { get; } = new[]
    {
        new Choice<VisionSearchStrategy>(VisionSearchStrategy.Balanced, "Balanced (Default)"),
        new Choice<VisionSearchStrategy>(VisionSearchStrategy.Fastest, "Fastest (16 ms Game-Frame Polling)"),
        new Choice<VisionSearchStrategy>(VisionSearchStrategy.MostAccurate, "Most Accurate (Full Pyramid + Multi-Cue)"),
        new Choice<VisionSearchStrategy>(VisionSearchStrategy.Deadline, "Strict Time Budget"),
    };

    public static IReadOnlyList<Choice<SearchMatchMode>> TargetSelectionOptions { get; } = new[]
    {
        new Choice<SearchMatchMode>(SearchMatchMode.FirstOrBest, "Find One (Best / First Match)"),
        new Choice<SearchMatchMode>(SearchMatchMode.AllMatches, "Find All (Count & Act on Every Match)"),
        new Choice<SearchMatchMode>(SearchMatchMode.ReadingOrderNth, "Find Specific # (N-th Match in Reading Order)"),
        new Choice<SearchMatchMode>(SearchMatchMode.RandomMatch, "Find One (Random Match)"),
        new Choice<SearchMatchMode>(SearchMatchMode.NearestToCursor, "Find One (Nearest to Mouse Cursor)"),
    };

    public static IReadOnlyList<Choice<MultiMatchOrder>> MatchOrderOptions { get; } = new[]
    {
        new Choice<MultiMatchOrder>(MultiMatchOrder.ReadingOrder, "Reading Order (Top → Bottom, Left → Right)"),
        new Choice<MultiMatchOrder>(MultiMatchOrder.BottomToTop, "Bottom → Top (Safe when clicking removes items)"),
        new Choice<MultiMatchOrder>(MultiMatchOrder.NearestNeighbor, "Shortest Path (Nearest Neighbor)"),
    };

    public SearchMatchMode TargetSelection
    {
        get => _search.TargetSelection;
        set
        {
            if (_search.TargetSelection == value) return;
            _search.TargetSelection = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IsAllMatches));
            OnPropertyChanged(nameof(IsReadingOrderNth));
            OnPropertyChanged(nameof(TargetSelectionHelp));
        }
    }

    public bool IsAllMatches => _search.TargetSelection == SearchMatchMode.AllMatches;

    public bool IsReadingOrderNth => _search.TargetSelection == SearchMatchMode.ReadingOrderNth;

    public MultiMatchOrder MatchOrder
    {
        get => _search.MatchOrder;
        set
        {
            if (_search.MatchOrder == value) return;
            _search.MatchOrder = value;
            NotifyChanged();
        }
    }

    public int TargetMatchIndex
    {
        get => _search.EffectiveTargetMatchIndex;
        set
        {
            var clamped = Math.Clamp(value, 1, 9999);
            var stored = clamped == 1 ? 0 : clamped;
            if (_search.TargetMatchIndex == stored) return;
            _search.TargetMatchIndex = stored;
            NotifyChanged();
        }
    }

    public int MinMatchCount
    {
        get => _search.EffectiveMinMatchCount;
        set
        {
            var clamped = Math.Clamp(value, 1, 9999);
            var stored = clamped == 1 ? 0 : clamped;
            if (_search.MinMatchCount == stored) return;
            _search.MinMatchCount = stored;
            NotifyChanged();
        }
    }

    public int MaxMatchesToActOn
    {
        get => _search.MaxMatchesToActOn;
        set
        {
            var clamped = Math.Clamp(value, 0, 9999);
            if (_search.MaxMatchesToActOn == clamped) return;
            _search.MaxMatchesToActOn = clamped;
            NotifyChanged();
        }
    }

    public int BetweenMatchesDelayMs
    {
        get => _search.BetweenMatchesDelayMs;
        set
        {
            var clamped = Math.Clamp(value, 0, 60_000);
            if (_search.BetweenMatchesDelayMs == clamped && _search.BetweenMatchesDelayOverride is not null) return;
            _search.BetweenMatchesDelayMs = clamped;
            NotifyChanged();
        }
    }

    public string CountVariableName
    {
        get => _search.CountVariableName ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_search.CountVariableName == normalized) return;
            _search.CountVariableName = normalized;
            NotifyChanged();
        }
    }

    public string TargetSelectionHelp => _search.TargetSelection switch
    {
        SearchMatchMode.AllMatches => "Finds every occurrence on screen (even across 1,500+ words), counts how many times it appears, highlights all of them, and either just counts (When found = Do nothing) or acts on each match in order.",
        SearchMatchMode.ReadingOrderNth => $"Picks match #{_search.EffectiveTargetMatchIndex} in reading order (top-to-bottom, left-to-right).",
        SearchMatchMode.RandomMatch => "Counts all occurrences on screen and picks one at random.",
        SearchMatchMode.NearestToCursor => "Counts all occurrences on screen and picks the one closest to the current mouse cursor.",
        _ => "Finds the single best / first match on screen, while still counting how many times it appears.",
    };

    public VisionScenario Scenario
    {
        get => _search.Scenario;
        set
        {
            if (_search.Scenario == value && value == VisionScenario.Custom) return;
            TemplateAutoTuner.ApplyScenario(_search, value);
            OnPropertyChanged(string.Empty);
            NotifyChanged();
        }
    }

    public VisionSearchStrategy Strategy
    {
        get => _search.Strategy;
        set { if (_search.Strategy == value) return; _search.Strategy = value; NotifyChanged(); }
    }

    public int TimeBudgetMs
    {
        get => _search.TimeBudgetMs;
        set { if (_search.TimeBudgetMs == value) return; _search.TimeBudgetMs = value; NotifyChanged(); }
    }

    public bool TrackTarget
    {
        get => _search.TrackTarget;
        set
        {
            if (_search.TrackTarget == value) return;
            _search.TrackTarget = value;
            NotifyChanged();
        }
    }

    public bool PredictiveClick
    {
        get => _search.PredictiveClick;
        set
        {
            if (_search.PredictiveClick == value) return;
            _search.PredictiveClick = value;
            if (value && !_search.TrackTarget)
            {
                _search.TrackTarget = true;
                OnPropertyChanged(nameof(TrackTarget));
            }
            NotifyChanged();
        }
    }

    public int PredictiveLeadMs
    {
        get => _search.PredictiveLeadMs;
        set { if (_search.PredictiveLeadMs == value) return; _search.PredictiveLeadMs = value; NotifyChanged(); }
    }

    public int ConfirmFrames
    {
        get => _search.ConfirmFrames;
        set { if (_search.ConfirmFrames == value) return; _search.ConfirmFrames = Math.Clamp(value, 1, 10); NotifyChanged(); }
    }

    public bool OnlyWhereMoved
    {
        get => _search.OnlyWhereMoved;
        set { if (_search.OnlyWhereMoved == value) return; _search.OnlyWhereMoved = value; NotifyChanged(); }
    }

    /// <summary>"🎯 Show search area on screen": outlines where this step looks right now, for a moment.</summary>
    public RelayCommand ShowSearchAreaCommand { get; }

    // ---------------- "Where to look" ----------------

    /// <summary>The "Where to look" choices. New steps start on the main monitor.</summary>
    public static IReadOnlyList<Choice<SearchScope>> Scopes { get; } = new[]
    {
        new Choice<SearchScope>(SearchScope.PrimaryMonitor, "Main Monitor (Primary Screen)"),
        new Choice<SearchScope>(SearchScope.Monitor, "Specific Monitor…"),
        new Choice<SearchScope>(SearchScope.Window, "Specific App Window…"),
        new Choice<SearchScope>(SearchScope.CustomArea, "Custom Area on Screen"),
        new Choice<SearchScope>(SearchScope.AllMonitors, "All Monitors (Entire Desktop)"),
    };

    /// <summary>Monitors and windows from the editor (set right after creation), for the monitor / window lists.</summary>
    public IScreenLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            RefreshMonitors();
            if (EffectiveScopeIsWindow) RefreshWindows();
        }
    }

    public RelayCommand RefreshMonitorsCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }

    /// <summary>"Monitor 1 — Primary (1920×1080)", "Monitor 2 (1920×1080)", …</summary>
    public System.Collections.ObjectModel.ObservableCollection<Choice<int>> Monitors { get; } = new();

    /// <summary>Titles of the app windows open right now (front to back).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> WindowTitles { get; } = new();

    public SearchScope Scope
    {
        get => _search.EffectiveScope;
        set
        {
            if (_search.Scope == value) return;
            _search.Scope = value;
            if (value == SearchScope.Monitor) RefreshMonitors();
            if (value == SearchScope.Window) RefreshWindows();
            NotifyChanged();
            OnPropertyChanged(nameof(SearchFullScreen));
            NotifyScopeChanged();
        }
    }

    private bool EffectiveScopeIsWindow => _search.EffectiveScope == SearchScope.Window;

    public bool UsesMonitor => _search.EffectiveScope == SearchScope.Monitor;

    public bool UsesWindow => _search.EffectiveScope == SearchScope.Window;

    public int MonitorNumber
    {
        get => _search.MonitorNumber;
        set { if (_search.MonitorNumber == value || value < 1) return; _search.MonitorNumber = value; NotifyChanged(); OnPropertyChanged(nameof(ScopeHelp)); }
    }

    /// <summary>The window to search in: (part of) its title.</summary>
    public string WindowTitle
    {
        get => _search.WindowTitle ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_search.WindowTitle == normalized) return;
            _search.WindowTitle = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(SelectedWindowTitle));
            OnPropertyChanged(nameof(ScopeHelp));
        }
    }

    /// <summary>The drop-down of open windows: choosing one fills in <see cref="WindowTitle"/>.</summary>
    public string? SelectedWindowTitle
    {
        get => WindowTitles.FirstOrDefault(t => string.Equals(t, _search.WindowTitle, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) WindowTitle = value;
        }
    }

    public string ScopeHelp => _search.EffectiveScope switch
    {
        SearchScope.PrimaryMonitor => "Only your main monitor is searched. Faster than all monitors, and it can't match something on a second screen by mistake.",
        SearchScope.Monitor => "Only the chosen monitor is searched. Click \"Refresh monitors\" after plugging a monitor in or out.",
        SearchScope.Window => string.IsNullOrWhiteSpace(_search.WindowTitle)
            ? "Choose an open window (e.g. Roblox). Only the inside of that window is searched, wherever it is on screen when the step runs."
            : $"Searches inside the window whose title contains \"{_search.WindowTitle}\", wherever it is when the step runs. If it isn't open, the step keeps checking until its time is up.",
        SearchScope.CustomArea => "Only the area below is searched. The smaller the area, the faster the search and the fewer wrong matches.",
        _ => "Every monitor is searched. Slowest, and it can match things on other screens (chat apps, browsers) by mistake.",
    };

    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(UsesRegion));
        OnPropertyChanged(nameof(UsesMonitor));
        OnPropertyChanged(nameof(UsesWindow));
        OnPropertyChanged(nameof(ScopeHelp));
    }

    private void RefreshMonitors()
    {
        Monitors.Clear();
        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            monitors = _layout?.GetMonitors() ?? Array.Empty<MonitorInfo>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            monitors = Array.Empty<MonitorInfo>();
        }

        foreach (var monitor in monitors) Monitors.Add(new Choice<int>(monitor.Number, monitor.Label));
        // A monitor the macro was made for but that isn't connected now stays selectable (and the validator explains).
        if (Monitors.All(m => m.Value != _search.MonitorNumber))
            Monitors.Add(new Choice<int>(_search.MonitorNumber, $"Monitor {_search.MonitorNumber} (not connected right now)"));
        OnPropertyChanged(nameof(MonitorNumber));
    }

    private void RefreshWindows()
    {
        WindowTitles.Clear();
        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = _layout?.GetWindows() ?? Array.Empty<WindowInfo>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            windows = Array.Empty<WindowInfo>();
        }

        foreach (var title in windows.Select(w => w.Title).Distinct(StringComparer.OrdinalIgnoreCase)) WindowTitles.Add(title);
        OnPropertyChanged(nameof(SelectedWindowTitle));
    }

    // ---------------- Movement and click timing (Advanced) ----------------

    /// <summary>How the cursor gets to what was found (older steps: Instant).</summary>
    public MouseMoveStyle MoveStyle
    {
        get => _search.MoveStyle ?? MouseMoveStyle.Instant;
        set
        {
            if (_search.MoveStyle == value) return;
            _search.MoveStyle = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Glides));
        }
    }

    public bool Glides => MoveStyle != MouseMoveStyle.Instant;

    public int MoveDurationMs
    {
        get => _search.MoveDurationMs;
        set { if (_search.MoveDurationMs == value) return; _search.MoveDurationMs = value; NotifyChanged(); }
    }

    public int ClickHoldMs
    {
        get => _search.ClickHoldMs;
        set { if (_search.ClickHoldMs == value) return; _search.ClickHoldMs = value; NotifyChanged(); }
    }

    public int DoubleClickIntervalMs
    {
        get => _search.DoubleClickIntervalMs;
        set { if (_search.DoubleClickIntervalMs == value) return; _search.DoubleClickIntervalMs = value; NotifyChanged(); }
    }

    /// <summary>"Wait after click (ms)".</summary>
    public int PostActionDelayMs
    {
        get => _search.PostActionDelayMs;
        set { if (_search.PostActionDelayMs == value) return; _search.PostActionDelayMs = value; NotifyChanged(); }
    }

    /// <summary>"4. Search timing" in a Find step; "3. …" inside a condition (which has no "when found" card).</summary>
    public string TimingCardTitle => IsStandalone ? "4. Search timing" : "3. Search timing";

    /// <summary>The live plain-English summary banner (only for a standalone Find step, not inside a condition).</summary>
    public override string? PlainSummary => IsStandalone ? StepDescriber.Explain(_search) : null;

    /// <summary>
    /// "When found" choices. Click / double / right-click / hold happen at the CENTER of what was found (plus the
    /// offset), so a button that moves around the screen (a "Shake" button, a shop item) is still hit.
    /// </summary>
    public static IReadOnlyList<Choice<FoundTextAction>> Actions { get; } = new[]
    {
        new Choice<FoundTextAction>(FoundTextAction.None, "Do nothing (just continue)"),
        new Choice<FoundTextAction>(FoundTextAction.Click, "Click its center (left click)"),
        new Choice<FoundTextAction>(FoundTextAction.HoverThenClick, "Hover briefly, then left-click (for game buttons)"),
        new Choice<FoundTextAction>(FoundTextAction.DoubleClick, "Double-click its center"),
        new Choice<FoundTextAction>(FoundTextAction.RightClick, "Right-click its center"),
        new Choice<FoundTextAction>(FoundTextAction.MiddleClick, "Middle-click its center"),
        new Choice<FoundTextAction>(FoundTextAction.Move, "Move the mouse to it (no click)"),
        new Choice<FoundTextAction>(FoundTextAction.Hold, "Hold the left button on it for…"),
        new Choice<FoundTextAction>(FoundTextAction.PressKey, "Press a keyboard key (no mouse click)…"),
        new Choice<FoundTextAction>(FoundTextAction.ClickAndPressKey, "Left-click it, then press a keyboard key…"),
        new Choice<FoundTextAction>(FoundTextAction.ClickAndTypeText, "Left-click it, then type text…"),
        new Choice<FoundTextAction>(FoundTextAction.ScrollUp, "Scroll mouse wheel UP at its center…"),
        new Choice<FoundTextAction>(FoundTextAction.ScrollDown, "Scroll mouse wheel DOWN at its center…"),
    };

    public static IReadOnlyList<KeyInfo> FoundKeys => VirtualKeys.All;

    public static IReadOnlyList<Choice<TextNotFoundBehavior>> NotFoundOptions { get; } = new[]
    {
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.Continue, "Continue to the next step"),
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.SkipNextStep, "Skip the next step, then continue"),
        new Choice<TextNotFoundBehavior>(TextNotFoundBehavior.StopMacro, "Stop the macro (show an error)"),
    };

    /// <summary>Moves the cursor to where the last "Test detection now" found the match, so you can see it.</summary>
    public RelayCommand ShowMatchCommand { get; }

    /// <summary>True when the last test found something (the "Show me" button is enabled).</summary>
    public bool HasTestMatch => _testMatch is not null;

    public bool HasTestResult => !string.IsNullOrEmpty(_testResult);

    public string TestStatusBadgeText => _testFound switch
    {
        true => "✓ MATCH VERIFIED",
        false => "✕ NO MATCH",
        _ => "ℹ LIVE TEST",
    };

    /// <summary>Whether the last test found it (null before any test). Conditions turn this into "true / false right now".</summary>
    public bool? TestFound
    {
        get => _testFound;
        private set
        {
            if (SetProperty(ref _testFound, value))
                OnPropertyChanged(nameof(TestStatusBadgeText));
        }
    }

    /// <summary>Moves the cursor with this step's own screen tools.</summary>
    protected abstract void ShowSpot(ScreenPoint point);

    /// <summary>Shows a test's text and remembers where it matched (null = not found).</summary>
    protected void SetTestResult(string text, ScreenPoint? match, bool found)
    {
        TestResult = text;
        _testMatch = match;
        TestFound = found;
        OnPropertyChanged(nameof(HasTestMatch));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public static IReadOnlyList<Choice<WaitGoal>> Goals { get; } = new[]
    {
        new Choice<WaitGoal>(WaitGoal.Appear, "Wait until it appears"),
        new Choice<WaitGoal>(WaitGoal.Disappear, "Wait until it disappears"),
    };

    public AsyncRelayCommand PickRegionCommand { get; }

    /// <summary>True once the editor has given this step its screen tools.</summary>
    protected abstract bool HasTools { get; }

    protected bool IsBusy => _isBusy;

    public string? TestResult
    {
        get => _testResult;
        protected set
        {
            if (SetProperty(ref _testResult, value))
                OnPropertyChanged(nameof(HasTestResult));
        }
    }

    /// <summary>The old "whole screen" switch (all monitors on / custom area off). The editor now uses <see cref="Scope"/>.</summary>
    public bool SearchFullScreen
    {
        get => _search.SearchFullScreen;
        set
        {
            if (_search.SearchFullScreen == value) return;
            _search.SearchFullScreen = value;
            NotifyChanged();
            OnPropertyChanged(nameof(Scope));
            NotifyScopeChanged();
        }
    }

    public bool UsesRegion => _search.EffectiveScope == SearchScope.CustomArea;

    public int RegionX
    {
        get => _search.RegionX;
        set { if (_search.RegionX == value) return; _search.RegionX = value; NotifyChanged(); }
    }

    public int RegionY
    {
        get => _search.RegionY;
        set { if (_search.RegionY == value) return; _search.RegionY = value; NotifyChanged(); }
    }

    public int RegionWidth
    {
        get => _search.RegionWidth;
        set { if (_search.RegionWidth == value) return; _search.RegionWidth = value; NotifyChanged(); }
    }

    public int RegionHeight
    {
        get => _search.RegionHeight;
        set { if (_search.RegionHeight == value) return; _search.RegionHeight = value; NotifyChanged(); }
    }

    public int TimeoutMs
    {
        get => _search.TimeoutMs;
        set { if (_search.TimeoutMs == value) return; _search.TimeoutMs = value; NotifyChanged(); }
    }

    public int CheckIntervalMs
    {
        get => _search.CheckIntervalMs;
        set { if (_search.CheckIntervalMs == value) return; _search.CheckIntervalMs = value; NotifyChanged(); }
    }

    public WaitGoal Goal
    {
        get => _search.Goal;
        set
        {
            if (_search.Goal == value) return;
            _search.Goal = value;
            NotifyChanged();
            OnPropertyChanged(nameof(WaitsForAppear));
            OnPropertyChanged(nameof(HasPointerAction));
            OnPropertyChanged(nameof(IsHoldAction));
            OnPropertyChanged(nameof(NotFoundLabel));
        }
    }

    /// <summary>The "when found" options only matter while waiting for something to appear.</summary>
    public bool WaitsForAppear => _search.Goal == WaitGoal.Appear;

    public string NotFoundLabel => _search.Goal == WaitGoal.Appear ? "When not found (time is up)" : "If it's still there when the time is up";

    public FoundTextAction Action
    {
        get => _search.Action;
        set
        {
            if (_search.Action == value) return;
            _search.Action = value;
            NotifyChanged();
            OnPropertyChanged(nameof(HasPointerAction));
            OnPropertyChanged(nameof(IsHoldAction));
            OnPropertyChanged(nameof(IsFoundKeyAction));
            OnPropertyChanged(nameof(IsFoundTextAction));
            OnPropertyChanged(nameof(IsFoundScrollAction));
        }
    }

    public bool HasPointerAction => _search.Goal == WaitGoal.Appear && _search.Action is not (FoundTextAction.None or FoundTextAction.PressKey);

    /// <summary>Shows the hold time box for "hold the left button on it".</summary>
    public bool IsHoldAction => _search.Goal == WaitGoal.Appear && _search.Action == FoundTextAction.Hold;

    public bool IsFoundKeyAction => _search.Goal == WaitGoal.Appear
        && _search.Action is FoundTextAction.PressKey or FoundTextAction.ClickAndPressKey;

    public bool IsFoundTextAction => _search.Goal == WaitGoal.Appear
        && _search.Action == FoundTextAction.ClickAndTypeText;

    public bool IsFoundScrollAction => _search.Goal == WaitGoal.Appear
        && _search.Action is FoundTextAction.ScrollUp or FoundTextAction.ScrollDown;

    public int FoundVirtualKey
    {
        get => _search.FoundVirtualKey <= 0 ? 0x20 : _search.FoundVirtualKey;
        set { if (_search.FoundVirtualKey == value) return; _search.FoundVirtualKey = value; NotifyChanged(); }
    }

    public string FoundTextToType
    {
        get => _search.FoundTextToType ?? string.Empty;
        set { if (_search.FoundTextToType == value) return; _search.FoundTextToType = string.IsNullOrEmpty(value) ? null : value; NotifyChanged(); }
    }

    public int FoundScrollClicks
    {
        get => _search.FoundScrollClicks <= 0 ? 3 : _search.FoundScrollClicks;
        set { var c = Math.Clamp(value, 1, 50); if (_search.FoundScrollClicks == c) return; _search.FoundScrollClicks = c; NotifyChanged(); }
    }

    /// <summary>How long "hold the left button on it" holds, in ms.</summary>
    public int ActionHoldMs
    {
        get => _search.ActionHoldMs;
        set { if (_search.ActionHoldMs == value) return; _search.ActionHoldMs = value; NotifyChanged(); }
    }

    /// <summary>"Move the cursor away after clicking" (so its hover effect doesn't hide the next thing to find).</summary>
    public bool MoveCursorAwayAfterAction
    {
        get => _search.MoveCursorAwayAfterAction;
        set { if (_search.MoveCursorAwayAfterAction == value) return; _search.MoveCursorAwayAfterAction = value; NotifyChanged(); }
    }

    public int OffsetX
    {
        get => _search.OffsetX;
        set { if (_search.OffsetX == value) return; _search.OffsetX = value; NotifyChanged(); }
    }

    public int OffsetY
    {
        get => _search.OffsetY;
        set { if (_search.OffsetY == value) return; _search.OffsetY = value; NotifyChanged(); }
    }

    public TextNotFoundBehavior WhenNotFound
    {
        get => _search.WhenNotFound;
        set
        {
            if (_search.WhenNotFound == value) return;
            _search.WhenNotFound = value;
            NotifyChanged();
            OnPropertyChanged(nameof(NotFoundHelp));
        }
    }

    /// <summary>Plain-English note under the "when the time is up" choice, so it's clear what runs next.</summary>
    public string NotFoundHelp => _search.WhenNotFound switch
    {
        TextNotFoundBehavior.StopMacro => "Nothing below this step runs. The macro stops and the log says what was on screen.",
        TextNotFoundBehavior.SkipNextStep => "The step right below (or the whole If / Repeat block starting there) is skipped, then the macro goes on. Use this to make a Find step guard the action after it.",
        _ => "The steps below run as if it had been found. A later click on \"the last found spot\" stops with an error until something is found again.",
    };

    /// <summary>Lets the user drag the search area (each step type uses its own tool).</summary>
    protected abstract Task<ScreenRect?> PickRegionCoreAsync();

    /// <summary>Sets the search area (and turns off "whole screen"), marking the macro as changed.</summary>
    protected void SetRegion(ScreenRect rect)
    {
        _search.Scope = SearchScope.CustomArea; // picking an area means "search here"
        _search.RegionX = rect.X;
        _search.RegionY = rect.Y;
        _search.RegionWidth = rect.Width;
        _search.RegionHeight = rect.Height;
        OnPropertyChanged(string.Empty); // refresh all fields
        NotifyChanged(nameof(RegionX));  // mark the macro as changed + re-validate
    }

    /// <summary>Runs a screen tool with the buttons disabled; errors end up in the result box.</summary>
    protected async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await action();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task PickRegionAsync() => RunBusyAsync(async () =>
    {
        var picked = await PickRegionCoreAsync();
        if (picked is not { } rect)
        {
            TestResult = "Picking the area was cancelled.";
            return;
        }

        SetRegion(rect);
        TestResult = $"Search area set to {rect}.";
    });

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}

public sealed class FindTextStepViewModel : ScreenSearchStepViewModel
{
    private readonly FindTextStep _step;
    private IFindTextTools? _tools;
    private int _currentMatchPreviewIndex;

    public FindTextStepViewModel(FindTextStep step) : base(step)
    {
        _step = step;
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        InspectScreenTextCommand = new AsyncRelayCommand(InspectScreenTextAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        ShowNextMatchCommand = new RelayCommand(ShowNextMatch, () => HasMultipleTestMatches && !IsBusy);
        InsertVariableTokenCommand = new RelayCommand(p =>
        {
            var token = p as string;
            if (string.IsNullOrWhiteSpace(token)) token = "{value}";
            else if (!token.StartsWith('{')) token = $"{{{token}}}";
            Text = string.IsNullOrEmpty(Text) ? token : $"{Text}{token}";
        });
    }

    public RelayCommand InsertVariableTokenCommand { get; }

    public static IReadOnlyList<Choice<TextMatchMode>> MatchModes { get; } = new[]
    {
        new Choice<TextMatchMode>(TextMatchMode.Contains, "Contains the text (can be part of a word)"),
        new Choice<TextMatchMode>(TextMatchMode.WholeWords, "Whole word(s) only"),
    };

    public static IReadOnlyList<Choice<OcrPreprocessMode>> PreprocessModes { get; } = new[]
    {
        new Choice<OcrPreprocessMode>(OcrPreprocessMode.Auto, "Auto (Standard Desktop & Clear Text)"),
        new Choice<OcrPreprocessMode>(OcrPreprocessMode.HighContrast, "High Contrast (Faint or Low-Contrast Text)"),
        new Choice<OcrPreprocessMode>(OcrPreprocessMode.InvertLightOnDark, "Invert Light-on-Dark (Bright Game HUD Text)"),
        new Choice<OcrPreprocessMode>(OcrPreprocessMode.BinarizeOtsu, "Adaptive Binarize (Stylized / Noisy Background)"),
    };

    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand InspectScreenTextCommand { get; }
    public RelayCommand ShowNextMatchCommand { get; }

    public int LastTestMatchCount => _tools?.LastTestMatchCount ?? 0;

    public int LastTestTotalWordCount => _tools?.LastTestTotalWordCount ?? 0;

    public bool HasMultipleTestMatches => (_tools?.LastTestMatches.Count ?? 0) > 1;

    public string? MatchCountBadgeText => LastTestMatchCount > 0
        ? $"🎯 {LastTestMatchCount} match{(LastTestMatchCount == 1 ? "" : "es")} found{(LastTestTotalWordCount > 0 ? $" · {LastTestTotalWordCount} words on screen" : "")}"
        : (LastTestTotalWordCount > 0 && TestFound == false ? $"0 matches · {LastTestTotalWordCount} words read on screen" : null);

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IFindTextTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            OnPropertyChanged(nameof(OcrWarning));
        }
    }

    protected override bool HasTools => _tools is not null;

    /// <summary>Shown when OCR isn't available on this PC.</summary>
    public string? OcrWarning => _tools is { IsOcrAvailable: false } tools ? tools.OcrStatus : null;

    public string Text
    {
        get => _step.Text ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (_step.Text == value) return;
            _step.Text = value;
            NotifyChanged();
        }
    }

    public TextMatchMode MatchMode
    {
        get => _step.MatchMode;
        set
        {
            if (_step.MatchMode == value) return;
            _step.MatchMode = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MatchWholeWord));
        }
    }

    public bool CaseSensitive
    {
        get => _step.CaseSensitive;
        set
        {
            if (_step.CaseSensitive == value) return;
            _step.CaseSensitive = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MatchCase));
        }
    }

    /// <summary>"Match whole word only (e.g. 'art' won't match 'apart')" – same setting as <see cref="MatchMode"/>.</summary>
    public bool MatchWholeWord
    {
        get => _step.MatchWholeWord;
        set
        {
            if (_step.MatchWholeWord == value) return;
            _step.MatchWholeWord = value;
            NotifyChanged();
            OnPropertyChanged(nameof(MatchMode));
        }
    }

    /// <summary>"Match exact case (e.g. 'aRt' only matches 'aRt')" – same setting as <see cref="CaseSensitive"/>.</summary>
    public bool MatchCase
    {
        get => _step.MatchCase;
        set
        {
            if (_step.MatchCase == value) return;
            _step.MatchCase = value;
            NotifyChanged();
            OnPropertyChanged(nameof(CaseSensitive));
        }
    }

    public bool NormalizeLookAlikes
    {
        get => _step.NormalizeLookAlikes;
        set { if (_step.NormalizeLookAlikes == value) return; _step.NormalizeLookAlikes = value; NotifyChanged(); }
    }

    public bool StrictPunctuation
    {
        get => _step.StrictPunctuation;
        set { if (_step.StrictPunctuation == value) return; _step.StrictPunctuation = value; NotifyChanged(); }
    }

    public int MinSimilarityPercent
    {
        get => _step.MinSimilarityPercent;
        set { if (_step.MinSimilarityPercent == value) return; _step.MinSimilarityPercent = value; NotifyChanged(); }
    }

    public bool UseRegexPattern
    {
        get => _step.UseRegexPattern;
        set { if (_step.UseRegexPattern == value) return; _step.UseRegexPattern = value; NotifyChanged(); }
    }

    public OcrPreprocessMode Preprocess
    {
        get => _step.Preprocess;
        set { if (_step.Preprocess == value) return; _step.Preprocess = value; NotifyChanged(); }
    }

    public bool TrackTextAsImage
    {
        get => _step.TrackTextAsImage;
        set
        {
            if (_step.TrackTextAsImage == value) return;
            _step.TrackTextAsImage = value;
            if (value && !_step.TrackTarget)
            {
                _step.TrackTarget = true;
                OnPropertyChanged(nameof(TrackTarget));
            }
            NotifyChanged();
        }
    }

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Reading the screen…";
        _currentMatchPreviewIndex = 0;
        var text = await _tools.TestAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
        RaiseMatchCountProperties();
    });

    private Task InspectScreenTextAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Capturing and reading all words on screen…";
        _currentMatchPreviewIndex = 0;
        var text = await _tools.InspectScreenTextAsync(_step, selectedWord => Text = selectedWord);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
        RaiseMatchCountProperties();
    });

    private void ShowNextMatch()
    {
        if (_tools is not { LastTestMatches.Count: > 0 } tools) return;
        _currentMatchPreviewIndex = (_currentMatchPreviewIndex + 1) % tools.LastTestMatches.Count;
        var pt = tools.LastTestMatches[_currentMatchPreviewIndex];
        ShowSpot(pt);
    }

    private void RaiseMatchCountProperties()
    {
        OnPropertyChanged(nameof(LastTestMatchCount));
        OnPropertyChanged(nameof(LastTestTotalWordCount));
        OnPropertyChanged(nameof(HasMultipleTestMatches));
        OnPropertyChanged(nameof(MatchCountBadgeText));
    }

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);
}

/// <summary>A reusable captured image found elsewhere in the current macro, offered in the Find Image template picker.</summary>
public sealed class SharedTemplateChoice
{
    public SharedTemplateChoice(
        string label,
        string sourceSummary,
        string templateData,
        int width,
        int height,
        System.Windows.Media.ImageSource? preview)
    {
        Label = label;
        SourceSummary = sourceSummary;
        TemplateData = templateData;
        TemplateWidth = width;
        TemplateHeight = height;
        Preview = preview;
    }

    public string Label { get; }
    public string SourceSummary { get; }
    public string TemplateData { get; }
    public int TemplateWidth { get; }
    public int TemplateHeight { get; }
    public System.Windows.Media.ImageSource? Preview { get; }
    public string DimensionsText => $"{TemplateWidth} × {TemplateHeight} px";
}

/// <summary>Editor for "Find image": capture the picture, choose where and how strictly to search, test it.</summary>
public sealed class FindImageStepViewModel : ScreenSearchStepViewModel
{
    private readonly FindImageStep _step;
    private readonly ObservableCollection<SharedTemplateChoice> _sharedTemplates = new();
    private IVisionTools? _tools;
    private System.Windows.Media.ImageSource? _preview;
    private Func<Guid, IReadOnlyList<SharedTemplateCandidate>>? _sharedTemplatesProvider;
    private SharedTemplateChoice? _selectedSharedTemplate;

    public FindImageStepViewModel(FindImageStep step) : base(step)
    {
        _step = step;
        CaptureTemplateCommand = new AsyncRelayCommand(CaptureTemplateAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        AutoTuneCommand = new RelayCommand(AutoTuneFromTemplate, () => HasTemplate && !IsBusy);
        ApplySharedTemplateCommand = new RelayCommand(
            p => ApplySharedTemplate(p as SharedTemplateChoice ?? SelectedSharedTemplate),
            p => (p is SharedTemplateChoice || SelectedSharedTemplate is not null) && !IsBusy);
        RefreshSharedTemplatesCommand = new RelayCommand(_ => RefreshSharedTemplates(), _ => _sharedTemplatesProvider is not null);
    }

    public AsyncRelayCommand CaptureTemplateCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand AutoTuneCommand { get; }
    public RelayCommand ApplySharedTemplateCommand { get; }
    public RelayCommand RefreshSharedTemplatesCommand { get; }

    /// <summary>Provides reusable captured templates from other steps in the active macro.</summary>
    public Func<Guid, IReadOnlyList<SharedTemplateCandidate>>? SharedTemplatesProvider
    {
        get => _sharedTemplatesProvider;
        set
        {
            _sharedTemplatesProvider = value;
            RefreshSharedTemplates();
        }
    }

    /// <summary>Reusable templates available in the active macro (Plan 10 Shared Macro Template Gallery).</summary>
    public ObservableCollection<SharedTemplateChoice> SharedTemplates => _sharedTemplates;

    public bool HasSharedTemplates => _sharedTemplates.Count > 0;

    public SharedTemplateChoice? SelectedSharedTemplate
    {
        get => _selectedSharedTemplate;
        set
        {
            if (SetProperty(ref _selectedSharedTemplate, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public override void RefreshSharedTemplates()
    {
        var previousData = _selectedSharedTemplate?.TemplateData;
        _sharedTemplates.Clear();
        if (_sharedTemplatesProvider is not null)
        {
            foreach (var candidate in _sharedTemplatesProvider(_step.Id))
            {
                var preview = SmartMacro.Converters.FrozenBitmapCache.FromTemplate(
                    candidate.TemplateData, candidate.Width, candidate.Height);
                _sharedTemplates.Add(new SharedTemplateChoice(
                    candidate.Label,
                    candidate.SourceDetail,
                    candidate.TemplateData,
                    candidate.Width,
                    candidate.Height,
                    preview));
            }
        }

        SelectedSharedTemplate = (!string.IsNullOrEmpty(previousData)
                ? _sharedTemplates.FirstOrDefault(t => string.Equals(t.TemplateData, previousData, StringComparison.Ordinal))
                : null)
            ?? _sharedTemplates.FirstOrDefault();

        OnPropertyChanged(nameof(HasSharedTemplates));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void ApplySharedTemplate(SharedTemplateChoice? choice)
    {
        if (choice is null || string.IsNullOrEmpty(choice.TemplateData)) return;
        _step.TemplateData = choice.TemplateData;
        _step.TemplateWidth = choice.TemplateWidth;
        _step.TemplateHeight = choice.TemplateHeight;
        _step.TemplateAssetId = null;
        RefreshPreview();
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateInfo));
        NotifyChanged(nameof(TemplatePreview));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        TestResult = $"Reused image ({choice.TemplateWidth} × {choice.TemplateHeight}) from {choice.SourceSummary}. Click \"Test now\" to check it.";
    }

    public static IReadOnlyList<Choice<ImageDetectorKind>> Detectors { get; } = new[]
    {
        new Choice<ImageDetectorKind>(ImageDetectorKind.Template, "Template ZNCC (Standard + Partial Visibility)"),
        new Choice<ImageDetectorKind>(ImageDetectorKind.EdgeShape, "Edge / Shape Contours (Severe Lighting & Tint Invariant)"),
        new Choice<ImageDetectorKind>(ImageDetectorKind.Combined, "Combined Ensemble (Template + Edge Shape)"),
    };

    public ImageDetectorKind Detector
    {
        get => _step.Detector;
        set { if (_step.Detector == value) return; _step.Detector = value; NotifyChanged(); }
    }

    public int MinVisiblePercent
    {
        get => _step.MinVisiblePercent;
        set { if (_step.MinVisiblePercent == value) return; _step.MinVisiblePercent = value; NotifyChanged(); }
    }

    public int RotationRangeDeg
    {
        get => _step.RotationRangeDeg;
        set { if (_step.RotationRangeDeg == value) return; _step.RotationRangeDeg = Math.Clamp(value, 0, 45); NotifyChanged(); }
    }

    private void AutoTuneFromTemplate()
    {
        if (!TemplateCache.TryGetOrDecode(_step.TemplateData, _step.TemplateWidth, _step.TemplateHeight, out var image, out var err) || image is null)
        {
            TestResult = err ?? "Capture an image first before running Auto-Tune.";
            return;
        }

        var samples = new List<BgraImage> { image };
        if (_step.AdditionalTemplates is { Count: > 0 } extras)
        {
            foreach (var extra in extras)
            {
                if (TemplateCache.TryGetOrDecode(extra.Data, extra.Width, extra.Height, out var decoded, out _) && decoded is not null)
                    samples.Add(decoded);
            }
        }

        var rec = TemplateAutoTuner.AnalyzeSamples(samples);
        _step.MinSimilarityPercent = rec.MinSimilarityPercent;
        _step.MinVisiblePercent = rec.MinVisiblePercent;
        _step.IgnoreEdgesPercent = rec.IgnoreEdgesPercent;
        _step.MatchForeground = rec.MatchForeground;
        _step.AdaptToLighting = true;
        _step.Detector = rec.Detector;
        OnPropertyChanged(string.Empty);
        NotifyChanged();
        TestResult = $"✨ Auto-Tune: {rec.Summary}";
    }

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set
        {
            _tools = value;
            RefreshPreview();
        }
    }

    protected override bool HasTools => _tools is not null;

    /// <summary>The stored picture, for the editor to show. Null if none (or damaged).</summary>
    public System.Windows.Media.ImageSource? TemplatePreview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public bool HasTemplate => !string.IsNullOrEmpty(_step.TemplateData);

    public string TemplateInfo => HasTemplate
        ? $"{_step.TemplateWidth} × {_step.TemplateHeight} pixels"
        : "No image yet. Click \"Capture image\" and drag a box around the button or icon to find.";

    public int MinSimilarityPercent
    {
        get => _step.MinSimilarityPercent;
        set { if (_step.MinSimilarityPercent == value) return; _step.MinSimilarityPercent = value; NotifyChanged(); }
    }

    public bool IgnoreColor
    {
        get => _step.IgnoreColor;
        set { if (_step.IgnoreColor == value) return; _step.IgnoreColor = value; NotifyChanged(); }
    }

    /// <summary>Adapt to Day/Night lighting changes (Zero-Mean Normalized Cross-Correlation + normalized chroma).</summary>
    public bool AdaptToLighting
    {
        get => _step.AdaptToLighting;
        set { if (_step.AdaptToLighting == value) return; _step.AdaptToLighting = value; NotifyChanged(); }
    }

    /// <summary>"Match across display scales &amp; sizes (75%–150%)": check resized copies when 1:1 doesn't match.</summary>
    public bool AllowMultiScale
    {
        get => _step.AllowMultiScale;
        set { if (_step.AllowMultiScale == value) return; _step.AllowMultiScale = value; NotifyChanged(); }
    }

    /// <summary>"Ignore background / match bright text and edges" (see-through game UI).</summary>
    public bool MatchForeground
    {
        get => _step.MatchForeground;
        set { if (_step.MatchForeground == value) return; _step.MatchForeground = value; NotifyChanged(); }
    }

    /// <summary>"Ignore the edges (%)": 0 = compare the whole picture.</summary>
    public int IgnoreEdgesPercent
    {
        get => _step.IgnoreEdgesPercent;
        set
        {
            if (_step.IgnoreEdgesPercent == value) return;
            _step.IgnoreEdgesPercent = value;
            NotifyChanged();
            OnPropertyChanged(nameof(IgnoreEdges));
        }
    }

    /// <summary>Quick on/off for the recommended 15 % edge margin (round buttons, game UI).</summary>
    public bool IgnoreEdges
    {
        get => _step.IgnoreEdgesPercent > 0;
        set
        {
            if (IgnoreEdges == value) return;
            IgnoreEdgesPercent = value ? FindImageStep.RecommendedIgnoreEdgesPercent : 0;
            OnPropertyChanged(nameof(IgnoreEdgesPercent));
        }
    }

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task CaptureTemplateAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        var captured = await _tools.CaptureTemplateAsync();
        if (captured is null)
        {
            TestResult = "Capturing the image was cancelled.";
            return;
        }

        var image = captured.Image;
        if (image.Width < MacroValidator.MinTemplateSize || image.Height < MacroValidator.MinTemplateSize)
        {
            TestResult = $"The box was {image.Width} × {image.Height} pixels. The image must be at least {MacroValidator.MinTemplateSize} × {MacroValidator.MinTemplateSize} pixels; please drag again.";
            return;
        }

        if (TemplateMatcher.IsFlat(image))
        {
            TestResult = "That area is one flat color, so its shape can't be matched. Capture an area with some detail (text, an icon edge), or use a \"Find color\" step instead.";
            return;
        }

        _step.TemplateWidth = image.Width;
        _step.TemplateHeight = image.Height;
        _step.TemplateData = TemplateCodec.Encode(image);
        _step.TemplateAssetId = null;
        RefreshPreview();
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(TemplateInfo));
        NotifyChanged(nameof(TemplatePreview));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        var b = captured.Bounds;
        TestResult = $"Image captured ({image.Width} × {image.Height}) at ({b.X}, {b.Y}). Tip: a search area around where it appears makes the search faster. Click \"Test now\" to check it.";
    });

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Looking at the screen…";
        var text = await _tools.TestImageAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
    });

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);

    private void RefreshPreview()
    {
        // Cached + frozen per picture (Converters.FrozenBitmapCache), so recycled step cards and Undo / Redo reuse it.
        TemplatePreview = SmartMacro.Converters.FrozenBitmapCache.FromTemplate(_step.TemplateData, _step.TemplateWidth, _step.TemplateHeight);
    }
}

/// <summary>Editor for "Find color": pick the color, tolerance and area, test it, or watch it move.</summary>
public sealed class FindColorStepViewModel : ScreenSearchStepViewModel
{
    private static readonly TimeSpan TrackDuration = TimeSpan.FromSeconds(3);

    private readonly FindColorStep _step;
    private IVisionTools? _tools;

    public FindColorStepViewModel(FindColorStep step) : base(step)
    {
        _step = step;
        PickColorCommand = new AsyncRelayCommand(PickColorAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        PickSecondaryColorCommand = new AsyncRelayCommand(PickSecondaryColorAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        TrackCommand = new AsyncRelayCommand(TrackAsync, ex => TestResult = ex.Message, () => _tools is not null && !IsBusy);
        ApplyColorPresetCommand = new RelayCommand(p => ApplyColorPreset(p as string));
    }

    /// <summary>One-click tuning: "exact", "game" or "glow" (see <see cref="FindColorStep.ExactUiPreset"/> and friends).</summary>
    public RelayCommand ApplyColorPresetCommand { get; }

    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand PickSecondaryColorCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand TrackCommand { get; }

    /// <summary>Screen tools from the editor (set right after creation).</summary>
    public IVisionTools? Tools
    {
        get => _tools;
        set => _tools = value;
    }

    protected override bool HasTools => _tools is not null;

    public string Color
    {
        get => _step.Color ?? string.Empty;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (_step.Color == value) return;
            _step.Color = value;
            NotifyChanged();
            OnPropertyChanged(nameof(ColorBrush));
        }
    }

    /// <summary>A swatch of the colour (transparent while the text isn't a valid #RRGGBB).</summary>
    public System.Windows.Media.Brush ColorBrush
    {
        get
        {
            if (!RgbColor.TryParse(_step.Color, out var c)) return System.Windows.Media.Brushes.Transparent;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    public bool UseSecondaryColor
    {
        get => _step.UseSecondaryColor;
        set
        {
            if (_step.UseSecondaryColor == value) return;
            _step.UseSecondaryColor = value;
            if (value && string.IsNullOrWhiteSpace(_step.SecondaryColor))
                _step.SecondaryColor = "#FFFFFF";
            NotifyChanged();
            OnPropertyChanged(nameof(SecondaryColor));
            OnPropertyChanged(nameof(SecondaryColorBrush));
        }
    }

    public string SecondaryColor
    {
        get => _step.SecondaryColor ?? string.Empty;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (_step.SecondaryColor == value) return;
            _step.SecondaryColor = string.IsNullOrEmpty(value) ? null : value;
            if (!string.IsNullOrEmpty(value) && !_step.UseSecondaryColor)
            {
                _step.UseSecondaryColor = true;
                OnPropertyChanged(nameof(UseSecondaryColor));
            }
            NotifyChanged();
            OnPropertyChanged(nameof(SecondaryColorBrush));
        }
    }

    public System.Windows.Media.Brush SecondaryColorBrush
    {
        get
        {
            if (!RgbColor.TryParse(_step.SecondaryColor, out var c)) return System.Windows.Media.Brushes.Transparent;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    public int Tolerance
    {
        get => _step.Tolerance;
        set
        {
            if (_step.Tolerance == value) return;
            _step.Tolerance = value;
            NotifyChanged();
            NotifyPresetChanged();
        }
    }

    /// <summary>"Shade variation": also match darker / lighter shades of the colour (3D balls, glowing targets).</summary>
    public bool MatchShades
    {
        get => _step.MatchShades;
        set
        {
            if (_step.MatchShades == value) return;
            _step.MatchShades = value;
            NotifyChanged();
            NotifyPresetChanged();
        }
    }

    /// <summary>True while the settings equal the 🎯 Exact UI Color (Strict) preset.</summary>
    public bool IsExactPreset => IsPreset(FindColorStep.ExactUiPreset);

    /// <summary>True while the settings equal the 🔴 Game Target / Solid Object (Recommended) preset.</summary>
    public bool IsGamePreset => IsPreset(FindColorStep.GameTargetPreset);

    /// <summary>True while the settings equal the 🌅 Lighting / Glowing Object (Broad) preset.</summary>
    public bool IsGlowPreset => IsPreset(FindColorStep.GlowingObjectPreset);

    private bool IsPreset((string Name, int Tolerance, bool MatchShades) preset) =>
        _step.Tolerance == preset.Tolerance && _step.MatchShades == preset.MatchShades;

    private void NotifyPresetChanged()
    {
        OnPropertyChanged(nameof(IsExactPreset));
        OnPropertyChanged(nameof(IsGamePreset));
        OnPropertyChanged(nameof(IsGlowPreset));
    }

    private void ApplyColorPreset(string? key)
    {
        (string Name, int Tolerance, bool MatchShades)? preset = key switch
        {
            "exact" => FindColorStep.ExactUiPreset,
            "game" => FindColorStep.GameTargetPreset,
            "glow" => FindColorStep.GlowingObjectPreset,
            _ => null,
        };
        if (preset is not { } p) return;

        Tolerance = p.Tolerance;
        MatchShades = p.MatchShades;
        TestResult = $"Preset \"{p.Name}\": tolerance {p.Tolerance}, shade variation {(p.MatchShades ? "on" : "off")}.";
    }

    public int MinPixels
    {
        get => _step.MinPixels;
        set { if (_step.MinPixels == value) return; _step.MinPixels = value; NotifyChanged(); }
    }

    public static IReadOnlyList<Choice<ColorSpaceKind>> ColorSpaces { get; } = new[]
    {
        new Choice<ColorSpaceKind>(ColorSpaceKind.Rgb, "RGB Box + Shade Variation (Default)"),
        new Choice<ColorSpaceKind>(ColorSpaceKind.Hsv, "HSV Hue / Saturation / Value (3D Lit Game Objects)"),
        new Choice<ColorSpaceKind>(ColorSpaceKind.LabDeltaE, "CIE Lab Perceptual ΔE (Human-Eye Color Distance)"),
    };

    public static IReadOnlyList<Choice<ColorSelectRule>> SelectRules { get; } = new[]
    {
        new Choice<ColorSelectRule>(ColorSelectRule.Largest, "Largest Connected Patch (Default)"),
        new Choice<ColorSelectRule>(ColorSelectRule.NearestToCenter, "Closest to Search Area Center"),
        new Choice<ColorSelectRule>(ColorSelectRule.NearestToLastSpot, "Closest to Last Found / Tracked Spot"),
        new Choice<ColorSelectRule>(ColorSelectRule.TopMost, "Top-Most on Screen"),
    };

    public ColorSpaceKind ColorSpace
    {
        get => _step.ColorSpace;
        set { if (_step.ColorSpace == value) return; _step.ColorSpace = value; NotifyChanged(); }
    }

    public ColorSelectRule SelectRule
    {
        get => _step.SelectRule;
        set { if (_step.SelectRule == value) return; _step.SelectRule = value; NotifyChanged(); }
    }

    public int MaxPixels
    {
        get => _step.MaxPixels;
        set { if (_step.MaxPixels == value) return; _step.MaxPixels = Math.Max(0, value); NotifyChanged(); }
    }

    public int MinCircularityPercent
    {
        get => _step.MinCircularityPercent;
        set { if (_step.MinCircularityPercent == value) return; _step.MinCircularityPercent = Math.Clamp(value, 0, 100); NotifyChanged(); }
    }

    public int MinFillPercent
    {
        get => _step.MinFillPercent;
        set { if (_step.MinFillPercent == value) return; _step.MinFillPercent = Math.Clamp(value, 0, 100); NotifyChanged(); }
    }

    protected override Task<ScreenRect?> PickRegionCoreAsync() =>
        _tools?.PickRegionAsync() ?? Task.FromResult<ScreenRect?>(null);

    private Task PickColorAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        var picked = await _tools.PickColorAsync();
        if (picked is null)
        {
            TestResult = "Picking the color was cancelled.";
            return;
        }

        Color = picked.Color.ToString();
        TestResult = $"Color {picked.Color} picked at ({picked.X}, {picked.Y}). Click \"Test now\" to see what matches.";
    });

    private Task PickSecondaryColorAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        var picked = await _tools.PickColorAsync();
        if (picked is null)
        {
            TestResult = "Picking the 2nd color was cancelled.";
            return;
        }

        _step.UseSecondaryColor = true;
        OnPropertyChanged(nameof(UseSecondaryColor));
        SecondaryColor = picked.Color.ToString();
        TestResult = $"2nd color {picked.Color} picked at ({picked.X}, {picked.Y}). Click \"Test now\" to see what matches.";
    });

    private Task TestAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = "Looking at the screen…";
        var text = await _tools.TestColorAsync(_step);
        SetTestResult(text, _tools.LastTestMatch, _tools.LastTestMatch is not null);
    });

    protected override void ShowSpot(ScreenPoint point) => _tools?.ShowSpot(point);

    private Task TrackAsync() => RunBusyAsync(async () =>
    {
        if (_tools is null) return;
        TestResult = $"Tracking for {TrackDuration.TotalSeconds:0} seconds…";
        TestResult = await _tools.TrackColorAsync(_step, TrackDuration);
    });
}

/// <summary>
/// Read-only overview of a recording (thousands of events aren't edited one by one), the
/// "start from the recorded cursor position" option, and — for smart recordings — how each part plays the mouse.
/// </summary>
