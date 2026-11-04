using SmartMacro.Core.Ocr;

namespace SmartMacro.Core.Targeting;

/// <summary>
/// Snapshot of the UI element and owning top-level window at a screen point, captured off the input-hook thread.
/// </summary>
public sealed record UiElementSnapshot(
    string? ElementName,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    ScreenRect ElementBounds,
    string? WindowTitle,
    string? WindowProcessName,
    ScreenRect WindowBounds);

/// <summary>
/// A live UI Automation element matched during playback or target diagnostics.
/// </summary>
public sealed record UiElementMatch(ScreenRect Bounds, double Confidence, string MatchedBy);

/// <summary>
/// Finds desktop/web UI controls via accessibility APIs. The Windows implementation uses built-in UI Automation
/// (<c>System.Windows.Automation</c>) with strict timeouts; unit tests supply a fast fake.
/// </summary>
public interface IUiElementLocator
{
    bool IsAvailable { get; }

    /// <summary>
    /// Inspects what UI control and top-level window own physical screen pixel (<paramref name="x"/>, <paramref name="y"/>).
    /// Must never be called on a low-level input hook thread. Returns null if nothing usable is there or the query times out.
    /// </summary>
    UiElementSnapshot? InspectPoint(int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for a live UI element matching <paramref name="target"/>, scoped to <paramref name="windowBounds"/> (or
    /// <see cref="TargetFingerprint.WindowTitle"/> / <see cref="TargetFingerprint.WindowProcessName"/>) when available,
    /// using <paramref name="expectedPoint"/> to break ties between identical controls.
    /// </summary>
    Task<UiElementMatch?> FindAsync(
        TargetFingerprint target,
        ScreenRect? windowBounds,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken = default);
}
