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

/// <summary>Info shown in the "Add step" toolbox.</summary>
public sealed record StepTypeInfo(StepKind Kind, string DisplayName, string Glyph, string Description);

public static class StepTypeCatalog
{
    public static IReadOnlyList<StepTypeInfo> All { get; } =
        StepCatalog.All
            .Select(d => new StepTypeInfo(d.Kind, d.DisplayName, d.Glyph, d.Description))
            .ToList();

    private static readonly Dictionary<StepKind, StepTypeInfo> ByKind =
        All.ToDictionary(t => t.Kind);

    /// <summary>Action step types offered in the editor's "Add step" toolbox (recorded input only comes from recording).</summary>
    public static IReadOnlyList<StepTypeInfo> Addable { get; } =
        StepCatalog.All
            .Where(d => d.IsAddableFromToolbox && d.Category is StepCategory.Input or StepCategory.Vision or StepCategory.Utility)
            .Select(d => ByKind[d.Kind])
            .ToList();

    /// <summary>Block steps offered in the toolbox's "Logic" section.</summary>
    public static IReadOnlyList<StepTypeInfo> Logic { get; } =
        StepCatalog.ByCategory(StepCategory.Logic)
            .Select(d => ByKind[d.Kind])
            .ToList();

    /// <summary>v1.2 Brain steps offered in the toolbox's "Memory &amp; Sub-Macros" section.</summary>
    public static IReadOnlyList<StepTypeInfo> Brain { get; } =
        StepCatalog.ByCategory(StepCategory.Brain)
            .Select(d => ByKind[d.Kind])
            .ToList();

    public static StepTypeInfo Get(StepKind kind) => ByKind[kind];
}
/// <summary>
/// Wraps one MacroStep for the editor. Edits go straight into the wrapped model;
/// the Modified event tells the editor to mark the macro dirty and re-validate.
/// </summary>
public abstract class StepViewModel : ObservableObject
{
    private static readonly System.Windows.Media.Brush[] DepthAccentBrushes =
    {
        CreateFrozenBrush(0x3B, 0x82, 0xF6), // Depth 0: Blue
        CreateFrozenBrush(0xA8, 0x55, 0xF7), // Depth 1: Purple
        CreateFrozenBrush(0x14, 0xB8, 0xA6), // Depth 2: Teal
        CreateFrozenBrush(0xF5, 0x9E, 0x0B), // Depth 3: Amber
    };

    private static readonly System.Windows.Media.Brush[] DepthBadgeBrushes =
    {
        CreateFrozenBrush(0x2E, 0x3B, 0x82, 0xF6), // Depth 0: Soft Blue
        CreateFrozenBrush(0x2E, 0xA8, 0x55, 0xF7), // Depth 1: Soft Purple
        CreateFrozenBrush(0x2E, 0x14, 0xB8, 0xA6), // Depth 2: Soft Teal
        CreateFrozenBrush(0x2E, 0xF5, 0x9E, 0x0B), // Depth 3: Soft Amber
    };

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    internal static bool TryParseAdditiveMs(object? parameter, out int ms)
    {
        ms = 0;
        if (parameter is int i)
        {
            ms = i;
            return true;
        }
        if (parameter is null) return false;
        var s = parameter.ToString()!.Trim().TrimStart('+');
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            s = s[..^2].Trim();
        else if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(s[..^1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                ms = (int)Math.Round(sec * 1000);
                return true;
            }
        }
        return int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out ms);
    }

    private int _number;
    private int _indentLevel;
    private int? _blockPairId;
    private int _blockGroupKey;
    private int _blockDepth;
    private string? _blockBadgeText;
    private string? _blockFullBadgeText;
    private bool _isBlockPartnerHighlighted;
    private string? _issueText;
    private bool _hasError;
    private bool _hasWarning;
    private bool _isRunning;
    private bool _hasBreakpoint;
    private string? _breakpointCondition;
    private int _inspectorTab;
    private bool _isQuickEditOpen;
    private bool _isInsertMenuOpen;

    private static readonly System.Windows.Media.Brush InputCategoryBrush = CreateFrozenBrush(0x22, 0xD3, 0xEE);
    private static readonly System.Windows.Media.Brush VisionCategoryBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly System.Windows.Media.Brush LogicCategoryBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly System.Windows.Media.Brush BrainCategoryBrush = CreateFrozenBrush(0xA8, 0x55, 0xF7);
    private static readonly System.Windows.Media.Brush UtilityCategoryBrush = CreateFrozenBrush(0x10, 0xB9, 0x81);

    private static readonly System.Windows.Media.Brush InputGlowBrush = CreateFrozenBrush(0x24, 0x22, 0xD3, 0xEE);
    private static readonly System.Windows.Media.Brush VisionGlowBrush = CreateFrozenBrush(0x24, 0x38, 0xBD, 0xF8);
    private static readonly System.Windows.Media.Brush LogicGlowBrush = CreateFrozenBrush(0x24, 0xF5, 0x9E, 0x0B);
    private static readonly System.Windows.Media.Brush BrainGlowBrush = CreateFrozenBrush(0x24, 0xA8, 0x55, 0xF7);
    private static readonly System.Windows.Media.Brush UtilityGlowBrush = CreateFrozenBrush(0x24, 0x10, 0xB9, 0x81);

    protected StepViewModel(MacroStep model)
    {
        Model = model;
        SelectInspectorTabCommand = new RelayCommand(p =>
        {
            if (p is int idx) InspectorTab = idx;
            else if (p is string s && int.TryParse(s, out var parsed)) InspectorTab = parsed;
        });
        ToggleQuickEditCommand = new RelayCommand(() => IsQuickEditOpen = !IsQuickEditOpen);
    }

    public RelayCommand SelectInspectorTabCommand { get; }
    public RelayCommand ToggleQuickEditCommand { get; }

    public event EventHandler? Modified;

    public MacroStep Model { get; }

    public Guid Id => Model.Id;

    public StepKind Kind => Model.Kind;

    public StepTypeInfo TypeInfo => StepTypeCatalog.Get(Kind);

    public string Title => TypeInfo.DisplayName;

    public string Glyph => TypeInfo.Glyph;

    public string Summary => StepDescriber.Describe(Model);

    /// <summary>
    /// The plain-English banner at the top of the step's properties ("Searches Main Monitor every 250 ms…"), or null
    /// for steps that don't need one. Refreshed whenever the step changes.
    /// </summary>
    public virtual string? PlainSummary => null;

    /// <summary>True while playback is running this step (the list shows a "▶ Running" badge and scrolls to it).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    /// <summary>v1.8 Studio X Debugger: true when a breakpoint (F9) is set on this step.</summary>
    public bool HasBreakpoint
    {
        get => _hasBreakpoint;
        set
        {
            if (SetProperty(ref _hasBreakpoint, value))
            {
                OnPropertyChanged(nameof(HasBreakpointCondition));
                OnPropertyChanged(nameof(BreakpointBadgeText));
                OnPropertyChanged(nameof(BreakpointTooltip));
            }
        }
    }

    /// <summary>Debugger 2.0: optional condition expression attached to this step's breakpoint.</summary>
    public string? BreakpointCondition
    {
        get => _breakpointCondition;
        set
        {
            if (SetProperty(ref _breakpointCondition, value))
            {
                OnPropertyChanged(nameof(HasBreakpointCondition));
                OnPropertyChanged(nameof(BreakpointBadgeText));
                OnPropertyChanged(nameof(BreakpointTooltip));
            }
        }
    }

    public bool HasBreakpointCondition => HasBreakpoint && !string.IsNullOrWhiteSpace(_breakpointCondition);

    public string BreakpointBadgeText => HasBreakpointCondition
        ? $"🔴 BREAK · {_breakpointCondition}"
        : "🔴 BREAK";

    public string BreakpointTooltip => HasBreakpointCondition
        ? $"Conditional breakpoint active ({_breakpointCondition}) — F9 toggles, F10 Over, F11 Into, Shift+F11 Out"
        : "Breakpoint active (F9 toggles, F10 Over, F11 Into, Shift+F11 Out while paused)";

    private string? _telemetryBadgeText;

    /// <summary>v1.8 Evergreen Profiler: per-step execution count and average duration from the latest run (e.g. "×4 · 12 ms").</summary>
    public string? TelemetryBadgeText
    {
        get => _telemetryBadgeText;
        set
        {
            if (SetProperty(ref _telemetryBadgeText, value))
                OnPropertyChanged(nameof(HasTelemetryBadge));
        }
    }

    public bool HasTelemetryBadge => !string.IsNullOrEmpty(_telemetryBadgeText);

    // ---------------- Studio UI: folding, search and filter (view state only; never saved, never played) ----------------

    private bool _isCollapsed;
    private int _hiddenStepCount;
    private bool _isHiddenByCollapse;
    private bool _isFilteredOut;
    private bool _isSearchMatch;
    private bool _isSearchDimmed;
    private bool _isCurrentSearchMatch;

    /// <summary>True for If / Repeat rows, which have a ▼ / ▶ chevron to fold their block.</summary>
    public bool CanCollapse => Core.Workflow.StepListView.CanCollapse(Model);

    /// <summary>
    /// If / Repeat only: the block is folded (its inner steps and its End are hidden in the list, but stay in the macro and
    /// play normally). Set by the editor (ToggleCollapseCommand).
    /// </summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        internal set
        {
            if (!SetProperty(ref _isCollapsed, value)) return;
            OnPropertyChanged(nameof(CollapseGlyph));
            OnPropertyChanged(nameof(CollapseTooltip));
        }
    }

    /// <summary>"▾" (open) or "▸" (folded). The step row draws its own vector chevron; this text is for menus / tooltips.</summary>
    public string CollapseGlyph => _isCollapsed ? "▸" : "▾";

    public string CollapseTooltip => _isCollapsed ? "Show the steps inside this block" : "Fold this block (hide the steps inside; they still play)";

    /// <summary>How many steps a folded If / Repeat hides (0 when open).</summary>
    public int HiddenStepCount
    {
        get => _hiddenStepCount;
        internal set
        {
            if (!SetProperty(ref _hiddenStepCount, value)) return;
            OnPropertyChanged(nameof(HiddenBadgeText));
        }
    }

    /// <summary>"[4 steps hidden]" on a folded If / Repeat row.</summary>
    public string HiddenBadgeText => Core.Workflow.StepListView.HiddenBadge(_hiddenStepCount);

    /// <summary>True when a folded If / Repeat above hides this step.</summary>
    public bool IsHiddenByCollapse
    {
        get => _isHiddenByCollapse;
        internal set
        {
            if (SetProperty(ref _isHiddenByCollapse, value)) OnPropertyChanged(nameof(IsVisibleInList));
        }
    }

    /// <summary>True when a filter pill (Mouse, Keyboard…) hides this step.</summary>
    public bool IsFilteredOut
    {
        get => _isFilteredOut;
        internal set
        {
            if (SetProperty(ref _isFilteredOut, value)) OnPropertyChanged(nameof(IsVisibleInList));
        }
    }

    /// <summary>Shown in the step list (not folded away, not filtered out). Hidden rows keep their index and indentation.</summary>
    public bool IsVisibleInList => !_isHiddenByCollapse && !_isFilteredOut;

    /// <summary>The search text matches this step (highlighted).</summary>
    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        internal set => SetProperty(ref _isSearchMatch, value);
    }

    /// <summary>A search is active and this step doesn't match it (shown dimmed).</summary>
    public bool IsSearchDimmed
    {
        get => _isSearchDimmed;
        internal set => SetProperty(ref _isSearchDimmed, value);
    }

    /// <summary>The match the "↑ ↓" navigator is on.</summary>
    public bool IsCurrentSearchMatch
    {
        get => _isCurrentSearchMatch;
        internal set => SetProperty(ref _isCurrentSearchMatch, value);
    }

    /// <summary>1-based position shown in the list.</summary>
    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    /// <summary>How many If / Repeat blocks this step is inside (set by the editor; used for indentation).</summary>
    public int IndentLevel
    {
        get => _indentLevel;
        set
        {
            if (!SetProperty(ref _indentLevel, value)) return;
            OnPropertyChanged(nameof(IndentMargin));
            OnPropertyChanged(nameof(IsInsideBlock));
            OnPropertyChanged(nameof(BlockBracketGlyph));
            OnPropertyChanged(nameof(HasBlockBracket));
        }
    }

    /// <summary>Pixels of indentation per block level (the step list's indent gutter uses the same value).</summary>
    public const double IndentPerLevel = 18;

    /// <summary>Left indentation of the row in the step list.</summary>
    public System.Windows.Thickness IndentMargin => new(_indentLevel * IndentPerLevel, 0, 0, 0);

    // ---------------- AAA Studio UI: block guide rails + live step progress ----------------

    private static readonly System.Windows.Media.Brush IfRailBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);     // amber
    private static readonly System.Windows.Media.Brush RepeatRailBrush = CreateFrozenBrush(0x8B, 0x5C, 0xF6); // violet

    private Core.Workflow.BlockRailKind _railKind;
    private double _executionProgress;
    private bool _showExecutionProgress;

    /// <summary>True when the step sits inside an If / Repeat block (its card gets the coloured vertical guide rail).</summary>
    public bool IsInsideBlock => _indentLevel > 0;

    /// <summary>The innermost block the step is inside (set by the editor from <see cref="Core.Workflow.StepListView.EnclosingBlockKinds"/>).</summary>
    public Core.Workflow.BlockRailKind BlockRailKind
    {
        get => _railKind;
        internal set
        {
            if (!SetProperty(ref _railKind, value)) return;
            OnPropertyChanged(nameof(BlockRailColorHex));
            OnPropertyChanged(nameof(BlockRailBrush));
        }
    }

    /// <summary>"#F59E0B" (amber) inside an If / Else, "#8B5CF6" (violet) inside a Repeat, empty at the top level.</summary>
    public string BlockRailColorHex => Core.Workflow.StepListView.RailColorHex(_railKind);

    /// <summary>The rail colour as a frozen brush (what the 2.5 px guide rail draws with).</summary>
    public System.Windows.Media.Brush? BlockRailBrush => _railKind switch
    {
        Core.Workflow.BlockRailKind.If => IfRailBrush,
        Core.Workflow.BlockRailKind.Repeat => RepeatRailBrush,
        _ => null,
    };

    /// <summary>0..1: how far the step that is playing right now is (Delay, key hold, Find wait, feedback loop…).</summary>
    public double ExecutionProgress
    {
        get => _executionProgress;
        internal set => SetProperty(ref _executionProgress, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1));
    }

    /// <summary>True while this step plays and takes long enough (over 200 ms) for its 2 px progress bar to be shown.</summary>
    public bool ShowExecutionProgress
    {
        get => _showExecutionProgress;
        internal set => SetProperty(ref _showExecutionProgress, value);
    }

    /// <summary>True for If / Else / End If / Repeat / End Repeat.</summary>
    public bool IsBlockStep => WorkflowSteps.IsBlockStep(Model);

    /// <summary>1-based block pair number for If/Else/EndIf or Repeat/EndRepeat (null for non-block steps).</summary>
    public int? BlockPairId
    {
        get => _blockPairId;
        internal set => SetProperty(ref _blockPairId, value);
    }

    /// <summary>Unique identifier shared by all steps in the same If/Else/EndIf or Repeat/EndRepeat pair (0 if none).</summary>
    public int BlockGroupKey
    {
        get => _blockGroupKey;
        internal set => SetProperty(ref _blockGroupKey, value);
    }

    /// <summary>0-based nesting depth of this block (used to select the depth accent color).</summary>
    public int BlockDepth
    {
        get => _blockDepth;
        internal set
        {
            if (SetProperty(ref _blockDepth, value))
            {
                OnPropertyChanged(nameof(BlockAccentBrush));
                OnPropertyChanged(nameof(BlockBadgeBackgroundBrush));
            }
        }
    }

    /// <summary>Short pill badge text shown on block steps, e.g. "#1", "#2".</summary>
    public string? BlockBadgeText
    {
        get => _blockBadgeText;
        internal set
        {
            if (SetProperty(ref _blockBadgeText, value))
                OnPropertyChanged(nameof(HasBlockBadge));
        }
    }

    /// <summary>Full block badge text, e.g. "If #1", "Else #1", "End If #1", "Repeat #1", "End Repeat #1".</summary>
    public string? BlockFullBadgeText
    {
        get => _blockFullBadgeText;
        internal set => SetProperty(ref _blockFullBadgeText, value);
    }

    public bool HasBlockBadge => !string.IsNullOrEmpty(_blockBadgeText);

    /// <summary>True when a partner block step (e.g. matching If / Else / End If or Repeat / End Repeat) is selected.</summary>
    public bool IsBlockPartnerHighlighted
    {
        get => _isBlockPartnerHighlighted;
        internal set => SetProperty(ref _isBlockPartnerHighlighted, value);
    }

    public System.Windows.Media.Brush BlockAccentBrush => DepthAccentBrushes[Math.Abs(_blockDepth) % DepthAccentBrushes.Length];

    public System.Windows.Media.Brush BlockBadgeBackgroundBrush => DepthBadgeBrushes[Math.Abs(_blockDepth) % DepthBadgeBrushes.Length];

    internal void SetBlockInfo(int? pairId, int groupKey, int depth, string? badgeText, string? fullBadgeText)
    {
        BlockPairId = pairId;
        BlockGroupKey = groupKey;
        BlockDepth = depth;
        BlockBadgeText = badgeText;
        BlockFullBadgeText = fullBadgeText;
    }

    /// <summary>
    /// False when this editor is shown inside an If / Repeat condition. Then the parts that conditions don't use
    /// (title, enabled switch, "when found" action, comment) are hidden.
    /// </summary>
    public bool IsStandalone { get; internal set; } = true;

    // ---------------- Studio X Ultra: Inspector Deck 2.0 Sub-Tabs & Category Ambient Identity ----------------

    /// <summary>Active Inspector sub-tab: 0 = Setup, 1 = Tune &amp; Motion, 2 = Recovery &amp; Notes.</summary>
    public int InspectorTab
    {
        get => _inspectorTab;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (clamped == 1 && !HasTuneTab) clamped = 0;
            if (!SetProperty(ref _inspectorTab, clamped)) return;
            OnPropertyChanged(nameof(IsSetupTab));
            OnPropertyChanged(nameof(IsTuneTab));
            OnPropertyChanged(nameof(IsRecoveryTab));
            OnPropertyChanged(nameof(ShowSetupSection));
            OnPropertyChanged(nameof(ShowTuneSection));
            OnPropertyChanged(nameof(ShowRecoverySection));
        }
    }

    public bool IsSetupTab
    {
        get => _inspectorTab == 0;
        set { if (value) InspectorTab = 0; }
    }

    public bool IsTuneTab
    {
        get => _inspectorTab == 1;
        set { if (value) InspectorTab = 1; }
    }

    public bool IsRecoveryTab
    {
        get => _inspectorTab == 2;
        set { if (value) InspectorTab = 2; }
    }

    /// <summary>True when the Setup section should be visible (always true when embedded inside an If/Repeat condition).</summary>
    public bool ShowSetupSection => _inspectorTab == 0 || !IsStandalone;

    /// <summary>True when the Tune &amp; Motion section should be visible.</summary>
    public bool ShowTuneSection => _inspectorTab == 1 || (!IsStandalone && HasTuneTab);

    /// <summary>True when the Recovery &amp; Notes section should be visible.</summary>
    public bool ShowRecoverySection => _inspectorTab == 2;

    /// <summary>True when this step has a dedicated Tune &amp; Motion sub-tab.</summary>
    public virtual bool HasTuneTab => Model is MouseMoveStep or MouseClickStep or KeyPressStep or TypeTextStep or ScreenSearchStep or FeedbackLoopStep or WaitForChangeStep;

    /// <summary>True when this step has a dedicated Recovery &amp; Notes sub-tab.</summary>
    public virtual bool HasRecoveryTab => IsStandalone;

    /// <summary>True when the segmented 3-tab switcher is shown in the Inspector header.</summary>
    public bool HasInspectorTabs => IsStandalone && !(Model is ElseStep or EndIfStep or EndRepeatStep);

    /// <summary>Applies the editor's sticky preferred tab (falling back to Setup when this step has no Tune tab).</summary>
    internal void ApplyPreferredInspectorTab(int preferredTab)
    {
        var target = Math.Clamp(preferredTab, 0, 2);
        if (target == 1 && !HasTuneTab) target = 0;
        if (_inspectorTab == target) return;
        _inspectorTab = target;
        OnPropertyChanged(nameof(IsSetupTab));
        OnPropertyChanged(nameof(IsTuneTab));
        OnPropertyChanged(nameof(IsRecoveryTab));
        OnPropertyChanged(nameof(ShowSetupSection));
        OnPropertyChanged(nameof(ShowTuneSection));
        OnPropertyChanged(nameof(ShowRecoverySection));
    }

    public string TuneTabTitle => Model switch
    {
        FeedbackLoopStep => "🎛 Servo & Physics",
        ScreenSearchStep => "🎛 Match & Motion",
        MouseMoveStep or MouseClickStep => "🎛 Motion & Aim",
        _ => "🎛 Tune & Options",
    };

    public string RecoveryTabTitle
    {
        get
        {
            var baseTitle = Model is ScreenSearchStep or WaitForChangeStep or FocusWindowStep
                ? "🛡 Timing & Retry"
                : "📝 Notes & Meta";
            return HasComment ? $"{baseTitle} · 💬" : baseTitle;
        }
    }

    public StepCategory Category => StepCatalog.Get(Kind).Category;

    public string CategoryLabel => Category switch
    {
        StepCategory.Input => "INPUT & MOTION",
        StepCategory.Vision => "VISION & AI",
        StepCategory.Logic => "FLOW & LOGIC",
        StepCategory.Brain => "BRAIN & MEMORY",
        StepCategory.Utility => "WINDOW & SYSTEM",
        _ => "STUDIO STEP",
    };

    public System.Windows.Media.Brush CategoryAccentBrush => Category switch
    {
        StepCategory.Input => InputCategoryBrush,
        StepCategory.Vision => VisionCategoryBrush,
        StepCategory.Logic => LogicCategoryBrush,
        StepCategory.Brain => BrainCategoryBrush,
        StepCategory.Utility => UtilityCategoryBrush,
        _ => InputCategoryBrush,
    };

    public System.Windows.Media.Brush CategoryGlowBrush => Category switch
    {
        StepCategory.Input => InputGlowBrush,
        StepCategory.Vision => VisionGlowBrush,
        StepCategory.Logic => LogicGlowBrush,
        StepCategory.Brain => BrainGlowBrush,
        StepCategory.Utility => UtilityGlowBrush,
        _ => InputGlowBrush,
    };

    // ---------------- Connected Logic Block Brackets & Inline Card Quick-Edit ----------------

    public bool IsBlockStart => Model is IfStep or RepeatStep;
    public bool IsBlockMid => Model is ElseStep;
    public bool IsBlockEnd => Model is EndIfStep or EndRepeatStep;

    /// <summary>Connected bracket glyph drawn on the block rail for visual flow clarity.</summary>
    public string BlockBracketGlyph => Model switch
    {
        IfStep or RepeatStep => "╭─",
        ElseStep => "├─",
        EndIfStep or EndRepeatStep => "╰─",
        _ => IsInsideBlock ? "│" : string.Empty,
    };

    public bool HasBlockBracket => !string.IsNullOrEmpty(BlockBracketGlyph);

    /// <summary>True when clicking PrimaryBadge on the step card opens an inline quick-edit popover.</summary>
    public bool CanQuickEditBadge => Model is DelayStep
        or MouseClickStep { UseCurrentPosition: false, UseLastFoundText: false }
        or MouseMoveStep { UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates }
        or MouseScrollStep
        or FindImageStep
        or RepeatStep { Mode: RepeatMode.Times };

    public bool IsQuickEditOpen
    {
        get => _isQuickEditOpen;
        set => SetProperty(ref _isQuickEditOpen, value);
    }

    public bool IsInsertMenuOpen
    {
        get => _isInsertMenuOpen;
        set => SetProperty(ref _isInsertMenuOpen, value);
    }

    public string QuickEditTitle => Model switch
    {
        DelayStep => "Wait Duration",
        MouseClickStep => "Click Target (X, Y)",
        MouseMoveStep => "Move Target (X, Y)",
        MouseScrollStep => "Scroll Wheel Delta",
        FindImageStep => "Match Confidence (%)",
        RepeatStep => "Loop Count",
        _ => "Quick Parameter",
    };

    public string QuickEditUnit => Model switch
    {
        DelayStep => "ms",
        FindImageStep => "%",
        RepeatStep => "×",
        _ => "px",
    };

    public int QuickEditStep => Model switch
    {
        DelayStep => 50,
        MouseScrollStep => 120,
        FindImageStep => 1,
        RepeatStep => 1,
        _ => 10,
    };

    public int QuickEditMin => Model switch
    {
        DelayStep => 0,
        FindImageStep => 50,
        RepeatStep => 1,
        _ => -32000,
    };

    public int QuickEditMax => Model switch
    {
        DelayStep => 3_600_000,
        FindImageStep => 100,
        RepeatStep => 100_000,
        _ => 32000,
    };

    public string QuickEditPresets => Model switch
    {
        DelayStep => "-50,+50,+100,+500,+1000",
        FindImageStep => "75,85,90,95",
        RepeatStep => "2,3,5,10,50",
        MouseScrollStep => "-360,-120,120,360",
        _ => string.Empty,
    };

    public bool HasQuickEditSecondaryInt => Model is MouseClickStep { UseCurrentPosition: false, UseLastFoundText: false }
        or MouseMoveStep { UseRelativeDelta: false, TargetMode: ClickTargetMode.Coordinates };

    public int QuickEditPrimaryInt
    {
        get => Model switch
        {
            DelayStep d => d.DurationMs,
            MouseClickStep c => c.X,
            MouseMoveStep m => m.X,
            MouseScrollStep s => s.Delta,
            FindImageStep img => img.MinSimilarityPercent,
            RepeatStep r => r.Count,
            _ => 0,
        };
        set
        {
            switch (Model)
            {
                case DelayStep d when d.DurationMs != Math.Max(0, value):
                    d.DurationMs = Math.Max(0, value);
                    OnPropertyChanged(nameof(DelayStepViewModel.DurationMs));
                    NotifyChanged();
                    break;
                case MouseClickStep c when c.X != value:
                    c.X = value;
                    OnPropertyChanged(nameof(MouseClickStepViewModel.X));
                    NotifyChanged();
                    break;
                case MouseMoveStep m when m.X != value:
                    m.X = value;
                    OnPropertyChanged(nameof(MouseMoveStepViewModel.X));
                    NotifyChanged();
                    break;
                case MouseScrollStep s when s.Delta != value:
                    s.Delta = value;
                    OnPropertyChanged(nameof(MouseScrollStepViewModel.Delta));
                    NotifyChanged();
                    break;
                case FindImageStep img when img.MinSimilarityPercent != Math.Clamp(value, 50, 100):
                    img.MinSimilarityPercent = Math.Clamp(value, 50, 100);
                    OnPropertyChanged(nameof(FindImageStepViewModel.MinSimilarityPercent));
                    NotifyChanged();
                    break;
                case RepeatStep r when r.Count != Math.Max(1, value):
                    r.Count = Math.Max(1, value);
                    OnPropertyChanged(nameof(RepeatStepViewModel.Count));
                    NotifyChanged();
                    break;
            }
        }
    }

    public int QuickEditSecondaryInt
    {
        get => Model switch
        {
            MouseClickStep c => c.Y,
            MouseMoveStep m => m.Y,
            _ => 0,
        };
        set
        {
            switch (Model)
            {
                case MouseClickStep c when c.Y != value:
                    c.Y = value;
                    OnPropertyChanged(nameof(MouseClickStepViewModel.Y));
                    NotifyChanged();
                    break;
                case MouseMoveStep m when m.Y != value:
                    m.Y = value;
                    OnPropertyChanged(nameof(MouseMoveStepViewModel.Y));
                    NotifyChanged();
                    break;
            }
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            NotifyChanged();
        }
    }

    public string? Comment
    {
        get => Model.Comment;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (Model.Comment == normalized) return;
            Model.Comment = normalized;
            NotifyChanged();
            OnPropertyChanged(nameof(HasComment));
            OnPropertyChanged(nameof(RecoveryTabTitle));
        }
    }

    public bool HasComment => !string.IsNullOrEmpty(Model.Comment);

    /// <summary>Compact inline metadata badge for Step Card v2 (Intent, Smart-Aim, Multi-Scale, Retry/Recovery, Brain).</summary>
    public string? SmartChipText => this switch
    {
        MouseClickStepViewModel when Model is MouseClickStep { SendInBackground: true } => "👻 Background",
        MouseClickStepViewModel when Model is MouseClickStep { UseSmartIntent: true, HasTargetIntent: true } => "🧭 Intent",
        MouseClickStepViewModel when Model is MouseClickStep { SmartAimAtTarget: true, HasTargetThumbnail: true } => "✨ Smart-Aim",
        KeyPressStepViewModel when Model is KeyPressStep { SendInBackground: true } => "👻 Background",
        TypeTextStepViewModel when Model is TypeTextStep { SendInBackground: true } => "👻 Background",
        FindImageStepViewModel img => FormatPolicyOrChip(img.FailurePolicy, img.AllowMultiScale ? "🔍 Multi-Scale" : null),
        ScreenSearchStepViewModel search => FormatPolicyOrChip(search.FailurePolicy, null),
        WaitForChangeStepViewModel wait => FormatPolicyOrChip(wait.FailurePolicy, null),
        FocusWindowStepViewModel win => FormatPolicyOrChip(win.FailurePolicy, null),
        DelayStepViewModel { IsAdaptive: true } => "⚡ Adaptive",
        SetVariableStepViewModel { UsesScreenRegion: true } => "👁 OCR Read",
        SetVariableStepViewModel { Source: VariableSourceKind.Formula } => "🧮 Formula",
        SetVariableStepViewModel v when !string.IsNullOrWhiteSpace(v.VariableName) => $"{{{v.VariableName}}}",
        CheckpointStepViewModel cp when !string.IsNullOrWhiteSpace(cp.CheckpointName) => $"📍 {cp.CheckpointName}",
        CallMacroStepViewModel => "↗ Sub-Macro",
        FlowControlStepViewModel fc => fc.Action switch
        {
            FlowControlAction.ExitLoop => "⏹ Break",
            FlowControlAction.ContinueLoop => "⏭ Continue",
            _ => "🛑 Stop",
        },
        _ => null,
    };

    public bool HasSmartChip => !string.IsNullOrEmpty(SmartChipText);

    // ---------------- AAA Studio v3: Structured Parameter Pills & Cumulative Timeline Gutter ----------------

    private string? _timelineOffsetText;

    /// <summary>Cumulative virtual timeline offset shown in the left gutter (e.g. "+0.42s"), computed by EditorViewModel.</summary>
    public string? TimelineOffsetText
    {
        get => _timelineOffsetText;
        internal set
        {
            if (SetProperty(ref _timelineOffsetText, value))
                OnPropertyChanged(nameof(HasTimelineOffset));
        }
    }

    public bool HasTimelineOffset => !string.IsNullOrEmpty(_timelineOffsetText);

    /// <summary>3D hardware keycap badge text for KeyPress steps (e.g. "Ctrl + Shift + S" or "W").</summary>
    public string? KeycapBadge => Model switch
    {
        KeyPressStep k => FormatKeycap(k),
        _ => null,
    };

    public bool HasKeycapBadge => !string.IsNullOrEmpty(KeycapBadge);

    /// <summary>Primary structured parameter pill (coordinates, duration, match confidence, event count).</summary>
    public string? PrimaryBadge => Model switch
    {
        DelayStep d => FormatMsShort(d.DurationMs),
        MouseClickStep c => c.UseLastFoundText
            ? $"🎯 +({c.OffsetX}, {c.OffsetY})"
            : c.UseCurrentPosition
                ? "📍 Cursor"
                : $"({c.X}, {c.Y})",
        MouseMoveStep m => m.UseRelativeDelta
            ? $"Δ({m.DeltaX:+#;-#;0}, {m.DeltaY:+#;-#;0})"
            : m.TargetMode == ClickTargetMode.Coordinates
                ? $"({m.X}, {m.Y})"
                : $"Δ({m.OffsetX:+#;-#;0}, {m.OffsetY:+#;-#;0})",
        MouseScrollStep s => $"{(s.Horizontal ? "↔ " : "↕ ")}{s.Delta:+#;-#;0}",
        InputSequenceStep q => $"{q.Events.Count:N0} ev · {(q.Events.Count == 0 ? 0.0 : Math.Max(0L, q.Events[^1].TimeUs) / 1_000_000.0):0.##}s",
        FindImageStep img => $"{img.MinSimilarityPercent}% match",
        FindColorStep col => col.Color,
        RepeatStep { Mode: RepeatMode.Times } r => $"×{r.Count}",
        _ => null,
    };

    public bool HasPrimaryBadge => !string.IsNullOrEmpty(PrimaryBadge);

    /// <summary>Secondary structured parameter pill (hold duration, hardware scan code, motion curve).</summary>
    public string? SecondaryBadge => Model switch
    {
        MouseClickStep { Action: MouseClickAction.Drag } c => $"Drag {c.DragDurationMs} ms",
        MouseClickStep { HoldMs: > 0 } c => $"{c.HoldMs} ms hold",
        MouseMoveStep { UseRelativeDelta: false, MoveStyle: MouseMoveStyle.SmoothGlide or MouseMoveStyle.HumanCurve } m =>
            $"{(m.MoveStyle == MouseMoveStyle.HumanCurve ? "Curve" : "Glide")} {m.MoveDurationMs} ms",
        KeyPressStep { Action: KeyAction.Press, HoldMs: > 0 } k => $"{k.HoldMs} ms hold",
        KeyPressStep { ScanCode: > 0 } k => $"SC 0x{k.ScanCode:X2}{(k.Extended ? "·E0" : "")}",
        TypeTextStep t when !string.IsNullOrEmpty(t.Text) => $"{t.Text.Length} chars",
        _ => null,
    };

    public bool HasSecondaryBadge => !string.IsNullOrEmpty(SecondaryBadge);

    private static string FormatMsShort(int ms) =>
        ms >= 1000 ? $"{ms / 1000.0:0.##} s" : $"{ms} ms";

    private static string FormatKeycap(KeyPressStep k)
    {
        var keyName = VirtualKeys.GetName(k.VirtualKey);
        if (k.Action != KeyAction.Press || k.Modifiers == KeyModifiers.None)
            return keyName;

        var parts = new List<string>(5);
        if (k.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (k.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (k.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (k.Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        parts.Add(keyName);
        return string.Join(" + ", parts);
    }

    private static string? FormatPolicyOrChip(FailurePolicyViewModel policy, string? fallback) => policy.Action switch
    {
        StepFailureAction.Retry => $"🔁 Retry ×{policy.RetryCount}",
        StepFailureAction.RunRecoveryMacro => "🛡 Recovery",
        StepFailureAction.RestartFromCheckpoint => string.IsNullOrWhiteSpace(policy.CheckpointName) ? "📍 Restart" : $"📍 →{policy.CheckpointName}",
        _ => fallback,
    };

    public string? IssueText
    {
        get => _issueText;
        private set => SetProperty(ref _issueText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public void SetIssues(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToList();
        HasError = list.Any(i => i.Severity == IssueSeverity.Error);
        HasWarning = !HasError && list.Any(i => i.Severity == IssueSeverity.Warning);
        IssueText = list.Count == 0 ? null : string.Join(Environment.NewLine, list.Select(i => i.Message));
    }

    /// <summary>Call after changing the model: updates the UI and tells the editor.</summary>
    protected void NotifyChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PlainSummary));
        OnPropertyChanged(nameof(SmartChipText));
        OnPropertyChanged(nameof(HasSmartChip));
        OnPropertyChanged(nameof(KeycapBadge));
        OnPropertyChanged(nameof(HasKeycapBadge));
        OnPropertyChanged(nameof(PrimaryBadge));
        OnPropertyChanged(nameof(HasPrimaryBadge));
        OnPropertyChanged(nameof(SecondaryBadge));
        OnPropertyChanged(nameof(HasSecondaryBadge));
        OnPropertyChanged(nameof(CanQuickEditBadge));
        OnPropertyChanged(nameof(HasQuickEditSecondaryInt));
        OnPropertyChanged(nameof(QuickEditPrimaryInt));
        OnPropertyChanged(nameof(QuickEditSecondaryInt));
        Modified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Movement style" choices shared by Move mouse, Mouse click and the Find steps.</summary>
    public static IReadOnlyList<Choice<MouseMoveStyle>> MoveStyles { get; } = new[]
    {
        new Choice<MouseMoveStyle>(MouseMoveStyle.SmoothGlide, "Smooth Glide (straight line, recommended)"),
        new Choice<MouseMoveStyle>(MouseMoveStyle.HumanCurve, "Human Curve (natural arc, eases in and out)"),
        new Choice<MouseMoveStyle>(MouseMoveStyle.Instant, "Instant (jump straight there)"),
    };

    public static StepViewModel Create(MacroStep step) => step switch
    {
        DelayStep d => new DelayStepViewModel(d),
        MouseMoveStep m => new MouseMoveStepViewModel(m),
        MouseClickStep c => new MouseClickStepViewModel(c),
        MouseScrollStep s => new MouseScrollStepViewModel(s),
        KeyPressStep k => new KeyPressStepViewModel(k),
        TypeTextStep t => new TypeTextStepViewModel(t),
        FindTextStep f => new FindTextStepViewModel(f),
        FindImageStep i => new FindImageStepViewModel(i),
        FindColorStep c => new FindColorStepViewModel(c),
        InputSequenceStep q => new InputSequenceStepViewModel(q),
        IfStep i => new IfStepViewModel(i),
        RepeatStep r => new RepeatStepViewModel(r),
        ElseStep or EndIfStep or EndRepeatStep => new BlockMarkerStepViewModel(step),
        FeedbackLoopStep f => new FeedbackLoopStepViewModel(f),
        WaitForChangeStep w => new WaitForChangeStepViewModel(w),
        FocusWindowStep w => new FocusWindowStepViewModel(w),
        ScreenshotStep s => new ScreenshotStepViewModel(s),
        SetVariableStep v => new SetVariableStepViewModel(v),
        CheckpointStep cp => new CheckpointStepViewModel(cp),
        CallMacroStep call => new CallMacroStepViewModel(call),
        FlowControlStep fc => new FlowControlStepViewModel(fc),
        _ => throw new NotSupportedException($"No editor for step type {step.GetType().Name}."),
    };
}

