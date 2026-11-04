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

    protected StepViewModel(MacroStep model)
    {
        Model = model;
    }

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
        set => SetProperty(ref _hasBreakpoint, value);
    }

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
        }
    }

    public bool HasComment => !string.IsNullOrEmpty(Model.Comment);

    /// <summary>Compact inline metadata badge for Step Card v2 (Intent, Smart-Aim, Multi-Scale, Retry/Recovery, Brain).</summary>
    public string? SmartChipText => this switch
    {
        MouseClickStepViewModel when Model is MouseClickStep { UseSmartIntent: true, HasTargetIntent: true } => "🧭 Intent",
        MouseClickStepViewModel when Model is MouseClickStep { SmartAimAtTarget: true, HasTargetThumbnail: true } => "✨ Smart-Aim",
        FindImageStepViewModel img => FormatPolicyOrChip(img.FailurePolicy, img.AllowMultiScale ? "🔍 Multi-Scale" : null),
        ScreenSearchStepViewModel search => FormatPolicyOrChip(search.FailurePolicy, null),
        WaitForChangeStepViewModel wait => FormatPolicyOrChip(wait.FailurePolicy, null),
        FocusWindowStepViewModel win => FormatPolicyOrChip(win.FailurePolicy, null),
        DelayStepViewModel { IsAdaptive: true } => "⚡ Adaptive",
        SetVariableStepViewModel { UsesScreenRegion: true } => "👁 OCR Read",
        SetVariableStepViewModel v when !string.IsNullOrWhiteSpace(v.VariableName) => $"{{{v.VariableName}}}",
        CheckpointStepViewModel cp when !string.IsNullOrWhiteSpace(cp.CheckpointName) => $"📍 {cp.CheckpointName}",
        CallMacroStepViewModel => "↗ Sub-Macro",
        _ => null,
    };

    public bool HasSmartChip => !string.IsNullOrEmpty(SmartChipText);

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
        _ => throw new NotSupportedException($"No editor for step type {step.GetType().Name}."),
    };
}

