using System.Text.Json.Serialization;

namespace SmartMacro.Core.Targeting;

/// <summary>
/// Ranked target-finding tiers used by <see cref="TargetResolver"/> when a <see cref="Models.MouseClickStep"/> has
/// Intent / Smart Target resolution enabled. Evaluated top-to-bottom until one finds the target.
/// </summary>
public enum LocatorStrategyKind
{
    /// <summary>1. Windows UI Automation element (button, menu item, tab, input field) by Name / AutomationId / ControlType.</summary>
    UiAutomation,

    /// <summary>2. Visible text read near the click via Windows OCR + recorded offset from the text center.</summary>
    OcrText,

    /// <summary>3. Visual thumbnail around the target (with multi-scale fallback across 75%–150% DPI).</summary>
    ImageSnapshot,

    /// <summary>4. Position inside the target app window, shifted/scaled to where that window is now.</summary>
    WindowRelative,

    /// <summary>5. Recorded physical screen coordinates (X, Y).</summary>
    Coordinates,
}

/// <summary>
/// Plain-data "Intent" fingerprint captured off-thread when a mouse click is recorded. Stores what UI control, nearby
/// text, and app window were clicked so playback can still find the target when a window moves, resizes, or changes DPI.
/// All fields are optional and omitted from JSON when empty.
/// </summary>
public sealed class TargetFingerprint
{
    /// <summary>Maximum allowed character length for any single text field in a saved fingerprint.</summary>
    public const int MaxFieldLength = 500;

    // ---- Tier 1: UI Automation element ----

    /// <summary>Accessible name of the clicked UI control (e.g. "Save", "Sign in", "Play").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ElementName { get; set; }

    /// <summary>Developer-assigned AutomationId of the control (e.g. "btnSave", "okButton").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutomationId { get; set; }

    /// <summary>Short UIA control type name without "ControlType." prefix (e.g. "Button", "MenuItem", "TabItem").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ControlType { get; set; }

    /// <summary>Win32 / framework class name of the control (e.g. "Button", "Chrome_RenderWidgetHostHWND").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    /// <summary>Where inside the UI element's bounding box the click landed horizontally (0..1, 0.5 = center).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElementNormalizedX { get; set; }

    /// <summary>Where inside the UI element's bounding box the click landed vertically (0..1, 0.5 = center).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElementNormalizedY { get; set; }

    // ---- Tier 2: Nearby OCR text ----

    /// <summary>Visible label read at or right next to the clicked spot when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NearbyText { get; set; }

    /// <summary>Horizontal pixel offset from the center of <see cref="NearbyText"/> to the clicked point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NearbyTextOffsetX { get; set; }

    /// <summary>Vertical pixel offset from the center of <see cref="NearbyText"/> to the clicked point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NearbyTextOffsetY { get; set; }

    // ---- Tier 4: App window anchor ----

    /// <summary>Title of the top-level window that owned the clicked point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    /// <summary>Executable / process name of the window without ".exe" (e.g. "notepad", "chrome", "RobloxPlayerBeta").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowProcessName { get; set; }

    /// <summary>Horizontal pixel offset of the click from the window's left edge when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowRelativeX { get; set; }

    /// <summary>Vertical pixel offset of the click from the window's top edge when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowRelativeY { get; set; }

    /// <summary>Width of the target window (physical pixels) when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WindowWidth { get; set; }

    /// <summary>Height of the target window (physical pixels) when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WindowHeight { get; set; }

    // ---- Computed helpers ----

    /// <summary>True when a usable UI Automation identifier (name or AutomationId) was captured.</summary>
    [JsonIgnore]
    public bool HasUiElement =>
        !string.IsNullOrWhiteSpace(ElementName) || !string.IsNullOrWhiteSpace(AutomationId);

    /// <summary>True when nearby visible text was captured for OCR fallback.</summary>
    [JsonIgnore]
    public bool HasOcrText => !string.IsNullOrWhiteSpace(NearbyText);

    /// <summary>True when a top-level window and relative click offset were captured.</summary>
    [JsonIgnore]
    public bool HasWindowAnchor =>
        (!string.IsNullOrWhiteSpace(WindowTitle) || !string.IsNullOrWhiteSpace(WindowProcessName))
        && WindowRelativeX is not null
        && WindowRelativeY is not null
        && WindowWidth > 0
        && WindowHeight > 0;

    /// <summary>True when at least one non-coordinate locator signal is present.</summary>
    [JsonIgnore]
    public bool HasAnySignal => HasUiElement || HasOcrText || HasWindowAnchor;

    /// <summary>Short human-readable name for step summaries (e.g. "Save", "#btnSave", or the window name).</summary>
    [JsonIgnore]
    public string? ShortLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ElementName)) return Truncate(ElementName.Trim(), 36);
            if (!string.IsNullOrWhiteSpace(NearbyText)) return Truncate(NearbyText.Trim(), 36);
            if (!string.IsNullOrWhiteSpace(AutomationId)) return $"#{Truncate(AutomationId.Trim(), 32)}";
            if (!string.IsNullOrWhiteSpace(WindowTitle)) return $"in \"{Truncate(WindowTitle.Trim(), 30)}\"";
            if (!string.IsNullOrWhiteSpace(WindowProcessName)) return $"in {Truncate(WindowProcessName.Trim(), 28)}";
            return null;
        }
    }

    /// <summary>Concise one-line breakdown of captured signals for the step editor.</summary>
    public string DescribeSignals()
    {
        var parts = new List<string>(3);
        if (HasUiElement)
        {
            var type = string.IsNullOrWhiteSpace(ControlType) ? "Element" : ControlType.Trim();
            var label = !string.IsNullOrWhiteSpace(ElementName)
                ? $"\"{Truncate(ElementName.Trim(), 40)}\""
                : $"#{Truncate(AutomationId!.Trim(), 40)}";
            parts.Add($"UI {type}: {label}");
        }

        if (HasOcrText)
        {
            parts.Add($"Text: \"{Truncate(NearbyText!.Trim(), 36)}\"");
        }

        if (HasWindowAnchor)
        {
            var win = !string.IsNullOrWhiteSpace(WindowTitle)
                ? $"\"{Truncate(WindowTitle.Trim(), 34)}\""
                : WindowProcessName!.Trim();
            parts.Add($"Window: {win} ({WindowWidth}×{WindowHeight})");
        }

        return parts.Count == 0 ? "Coordinates only" : string.Join("  •  ", parts);
    }

    public TargetFingerprint Clone() => new()
    {
        ElementName = ElementName,
        AutomationId = AutomationId,
        ControlType = ControlType,
        ClassName = ClassName,
        ElementNormalizedX = ElementNormalizedX,
        ElementNormalizedY = ElementNormalizedY,
        NearbyText = NearbyText,
        NearbyTextOffsetX = NearbyTextOffsetX,
        NearbyTextOffsetY = NearbyTextOffsetY,
        WindowTitle = WindowTitle,
        WindowProcessName = WindowProcessName,
        WindowRelativeX = WindowRelativeX,
        WindowRelativeY = WindowRelativeY,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
