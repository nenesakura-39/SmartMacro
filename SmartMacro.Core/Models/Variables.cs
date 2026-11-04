using System.Text.Json.Serialization;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Models;

/// <summary>
/// Plain-data variable types supported by SmartMacro v1.2 ("Brain").
/// Macros stay safe, declarative data — never scripts.
/// </summary>
public enum MacroVariableType
{
    Number,
    Text,
    Boolean,
}

/// <summary>
/// A named variable declared on a <see cref="Macro"/> with an initial default value.
/// </summary>
public sealed class MacroVariableDefinition
{
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 2_000;

    public string Name { get; set; } = string.Empty;

    public MacroVariableType Type { get; set; } = MacroVariableType.Number;

    public string DefaultValue { get; set; } = "0";

    public MacroVariableDefinition Clone() => new()
    {
        Name = Name,
        Type = Type,
        DefaultValue = DefaultValue,
    };

    /// <summary>True when <paramref name="name"/> is 1..64 ASCII identifier characters (<c>[A-Za-z_][A-Za-z0-9_]*</c>).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
        var first = name[0];
        if (!((first is >= 'A' and <= 'Z') || (first is >= 'a' and <= 'z') || first == '_')) return false;
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c == '_';
            if (!ok) return false;
        }

        return true;
    }
}

/// <summary>How a <see cref="SetVariableStep"/> updates its target variable.</summary>
public enum VariableOperation
{
    Set,
    Add,
    Subtract,
    AppendText,
}

/// <summary>Where a <see cref="SetVariableStep"/> reads its input value from.</summary>
public enum VariableSourceKind
{
    /// <summary>A constant value or <c>{otherVar}</c> placeholder text entered in the step.</summary>
    Constant,

    /// <summary>Reads text from a screen rectangle via Windows OCR and extracts the numeric value (e.g. "12,450" → 12450).</summary>
    ScreenOcrNumber,

    /// <summary>Reads trimmed text from a screen rectangle via Windows OCR.</summary>
    ScreenOcrText,

    /// <summary>Reads the current text on the Windows clipboard.</summary>
    ClipboardText,

    /// <summary>X coordinate of the most recent Find / condition match (0 if none).</summary>
    LastFoundX,

    /// <summary>Y coordinate of the most recent Find / condition match (0 if none).</summary>
    LastFoundY,

    /// <summary>1-based turn counter of the innermost Repeat loop (or the current macro run number when outside a loop).</summary>
    LoopCounter,

    /// <summary>Active seconds elapsed since playback started (paused time excluded).</summary>
    ElapsedSeconds,

    /// <summary>
    /// v1.6 "Eyes": reads the fill percentage (0–100) of a HUD bar (HP, Mana, Stamina, Cast meter) or a work progress bar
    /// inside <see cref="SetVariableStep.Region"/> by matching <see cref="SetVariableStep.GaugeColor"/>.
    /// </summary>
    ScreenGaugePercent,
}

/// <summary>Comparison operators for <see cref="ConditionKind.VariableCompare"/>.</summary>
public enum VariableCompareOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Contains,
    StartsWith,
}

/// <summary>
/// Structured variable comparison used inside a <see cref="Condition"/> (If, Repeat While/Until, Feedback loop stop
/// conditions, and Watchers): e.g. <c>gold &gt;= 10000</c> or <c>status Contains "Ready"</c>.
/// </summary>
public sealed class VariableComparison
{
    public string VariableName { get; set; } = "count";

    public VariableCompareOperator Operator { get; set; } = VariableCompareOperator.GreaterOrEqual;

    public string RightValue { get; set; } = "1";

    public VariableComparison Clone() => new()
    {
        VariableName = VariableName,
        Operator = Operator,
        RightValue = RightValue,
    };
}

/// <summary>
/// "Set variable" step: sets, increments, decrements, or appends to a named macro variable from a constant, OCR screen
/// area, clipboard, last-found position, loop counter, or elapsed time.
/// </summary>
public sealed class SetVariableStep : MacroStep
{
    public string VariableName { get; set; } = "count";

    public MacroVariableType VariableType { get; set; } = MacroVariableType.Number;

    public VariableOperation Operation { get; set; } = VariableOperation.Set;

    public VariableSourceKind Source { get; set; } = VariableSourceKind.Constant;

    /// <summary>Constant value (supports <c>{varName}</c> placeholders) when <see cref="Source"/> is <see cref="VariableSourceKind.Constant"/>.</summary>
    public string Value { get; set; } = "1";

    /// <summary>Screen area to read when <see cref="Source"/> is <see cref="VariableSourceKind.ScreenOcrNumber"/> or <see cref="VariableSourceKind.ScreenOcrText"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionY { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionWidth { get; set; } = 240;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionHeight { get; set; } = 80;

    [JsonIgnore]
    public ScreenRect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    /// <summary>Fill colour ("#RRGGBB") of the HUD bar / progress bar when <see cref="Source"/> is <see cref="VariableSourceKind.ScreenGaugePercent"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GaugeColor { get; set; }

    /// <summary>Allowed colour difference per RGB channel (0–255) for <see cref="VariableSourceKind.ScreenGaugePercent"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int GaugeTolerance { get; set; } = 32;

    /// <summary>Fill direction of the HUD bar / progress bar for <see cref="VariableSourceKind.ScreenGaugePercent"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GaugeDirection GaugeDirection { get; set; } = GaugeDirection.LeftToRight;

    [JsonIgnore]
    public string EffectiveGaugeColor => string.IsNullOrWhiteSpace(GaugeColor) ? "#22C55E" : GaugeColor.Trim();

    [JsonIgnore]
    public bool UsesScreenRegion =>
        Source is VariableSourceKind.ScreenOcrNumber or VariableSourceKind.ScreenOcrText or VariableSourceKind.ScreenGaugePercent;

    [JsonIgnore]
    public override StepKind Kind => StepKind.SetVariable;
}
