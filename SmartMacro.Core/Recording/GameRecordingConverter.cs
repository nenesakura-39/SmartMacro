using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Recording;

/// <summary>Which recorder the Record button uses.</summary>
public enum RecordingMode
{
    /// <summary>
    /// Advanced / fallback. Low-level hooks, absolute cursor positions, editable steps (Click, Move to X/Y, Press...).
    /// Best for normal desktop apps and menus.
    /// </summary>
    Desktop,

    /// <summary>
    /// Advanced / fallback. Raw Input: relative mouse movement and exact key down/up timing, stored as one
    /// "Recorded input (game mode)" step. For games that use the mouse for camera control.
    /// </summary>
    Game,

    /// <summary>
    /// The default. One Raw Input recording that keeps both the cursor position and the relative mouse counts for
    /// every mouse event, on one timeline. Parts where the cursor moves freely become editable steps; parts where a
    /// game hides or locks the cursor (camera turning) become "Recorded input" steps that play relative movement.
    /// See <see cref="SmartRecordingConverter"/>.
    /// </summary>
    Smart,
}

/// <summary>
/// Captures raw relative input for game mode. The real version uses Windows Raw Input (WM_INPUT).
/// Event times are microseconds since recording started.
/// </summary>
public interface IRelativeInputRecorder
{
    bool IsRecording { get; }

    /// <summary>Number of events captured so far.</summary>
    int EventCount { get; }

    /// <summary>True when the event limit was hit and further input was ignored.</summary>
    bool LimitReached { get; }

    /// <summary>Where the cursor was when recording started (physical pixels).</summary>
    int StartX { get; }

    int StartY { get; }

    void Start();

    /// <summary>Stops recording and returns everything captured, in the order it arrived.</summary>
    IReadOnlyList<InputEvent> Stop();
}

public sealed class GameRecordingOptions
{
    /// <summary>
    /// Keys to strip from the end of the recording (the stop shortcut). Mouse movement recorded before the
    /// shortcut is kept; only movement after its first key is dropped.
    /// </summary>
    public IReadOnlyCollection<int> TrimTrailingKeys { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 0 (default) keeps every mouse sample exactly as recorded. A value above 0 merges consecutive mouse
    /// samples that arrive within this many microseconds of the first one into a single event with the summed
    /// movement — no movement is lost, only sub-window timing. Useful for very high polling-rate mice.
    /// </summary>
    public int MoveCoalesceUs { get; init; }

    /// <summary>
    /// Keep keyboard auto-repeat (extra "down" events while a key is held). Off by default: games read whether
    /// a key is held, and replaying repeats only adds noise.
    /// </summary>
    public bool KeepKeyRepeats { get; init; }

    public int StartX { get; init; }

    public int StartY { get; init; }
}

/// <summary>
/// Turns a game-mode recording into a single <see cref="InputSequenceStep"/>. Unlike the desktop converter it
/// never thins out or reorders anything: every mouse delta keeps its sign, value, time and position in the
/// stream, and every key keeps its real down and up time. It only:
/// - removes the stop shortcut from the end,
/// - drops releases of keys/buttons that were already down when recording started, and duplicate downs,
/// - drops zero-length mouse moves and zero wheel events,
/// - starts the timeline at the first event (no leading wait),
/// - releases anything still held at the end.
/// </summary>
public static class GameRecordingConverter
{
    /// <summary>Recorder cap, leaving room under the validator limit for the releases added at the end.</summary>
    public const int MaxRecordedEvents = MacroValidator.MaxSequenceEvents - 1_000;

    public static InputSequenceStep? Convert(IReadOnlyList<InputEvent> rawEvents, GameRecordingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        options ??= new GameRecordingOptions();

        var events = rawEvents.Take(MaxRecordedEvents).ToList();
        var count = TrailingCut(events, options.TrimTrailingKeys);

        var output = new List<InputEvent>(count);
        var keysDown = new List<(int Vk, int Scan, bool Ext)>();
        var buttonsDown = new List<MacroMouseButton>();
        long? origin = null;
        long lastTime = 0;
        long moveGroupStart = long.MinValue;

        for (var i = 0; i < count; i++)
        {
            var e = events[i];

            // Times must never go backwards (the recorder guarantees this; be safe with other sources).
            var time = Math.Max(e.TimeUs, lastTime);
            lastTime = time;

            InputEvent? keep = null;
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                {
                    if (e.Dx == 0 && e.Dy == 0) break;

                    // Optional lossless merge with the previous move (sum, never drop).
                    if (options.MoveCoalesceUs > 0 && output.Count > 0 && output[^1].Kind == InputEventKind.MouseMove
                        && time - moveGroupStart <= options.MoveCoalesceUs)
                    {
                        var prev = output[^1];
                        long sx = (long)prev.Dx + e.Dx, sy = (long)prev.Dy + e.Dy;
                        if (Math.Abs(sx) <= MacroValidator.MaxRelativeMove && Math.Abs(sy) <= MacroValidator.MaxRelativeMove)
                        {
                            output[^1] = prev with { Dx = (int)sx, Dy = (int)sy };
                            break;
                        }
                    }

                    var dx = Math.Clamp(e.Dx, -MacroValidator.MaxRelativeMove, MacroValidator.MaxRelativeMove);
                    var dy = Math.Clamp(e.Dy, -MacroValidator.MaxRelativeMove, MacroValidator.MaxRelativeMove);
                    keep = InputEvent.Move(time, dx, dy);
                    moveGroupStart = time;
                    break;
                }

                case InputEventKind.MouseDown:
                    if (!Enum.IsDefined(e.Button) || buttonsDown.Contains(e.Button)) break;
                    buttonsDown.Add(e.Button);
                    keep = InputEvent.ButtonDown(time, e.Button);
                    break;

                case InputEventKind.MouseUp:
                    // Released but never pressed during the recording: it was down before we started.
                    if (!buttonsDown.Remove(e.Button)) break;
                    keep = InputEvent.ButtonUp(time, e.Button);
                    break;

                case InputEventKind.Wheel:
                case InputEventKind.HorizontalWheel:
                    if (e.Delta == 0) break;
                    keep = InputEvent.WheelEvent(time,
                        Math.Clamp(e.Delta, -MacroValidator.MaxScrollDelta, MacroValidator.MaxScrollDelta),
                        e.Kind == InputEventKind.HorizontalWheel);
                    break;

                case InputEventKind.KeyDown:
                {
                    if (!VirtualKeys.IsValid(e.VirtualKey) || e.ScanCode is < 0 or > 0xFF) break;
                    var id = (e.VirtualKey, e.ScanCode, e.Extended);
                    if (keysDown.Contains(id))
                    {
                        if (!options.KeepKeyRepeats) break; // auto-repeat
                    }
                    else
                    {
                        keysDown.Add(id);
                    }

                    keep = InputEvent.KeyDown(time, e.VirtualKey, e.ScanCode, e.Extended);
                    break;
                }

                case InputEventKind.KeyUp:
                {
                    var id = (e.VirtualKey, e.ScanCode, e.Extended);
                    if (!keysDown.Remove(id)) break; // was down before recording started
                    keep = InputEvent.KeyUp(time, e.VirtualKey, e.ScanCode, e.Extended);
                    break;
                }
            }

            if (keep is { } k)
            {
                origin ??= k.TimeUs;
                output.Add(k with { TimeUs = k.TimeUs - origin.Value });
                if (k.Kind == InputEventKind.MouseMove) moveGroupStart = k.TimeUs;
            }
        }

        if (output.Count == 0) return null;

        // Release anything still held, so playback never leaves a key or button stuck down.
        var end = output[^1].TimeUs;
        for (var idx = keysDown.Count - 1; idx >= 0; idx--)
            output.Add(InputEvent.KeyUp(end, keysDown[idx].Vk, keysDown[idx].Scan, keysDown[idx].Ext));
        foreach (var button in buttonsDown)
            output.Add(InputEvent.ButtonUp(end, button));

        return new InputSequenceStep
        {
            Events = output,
            StartX = options.StartX,
            StartY = options.StartY,
        };
    }

    /// <summary>
    /// Finds where the stop shortcut starts at the end of the recording: the earliest of the trailing key
    /// events that belong to it (mouse moves between them are ignored). Everything from there on is cut;
    /// movement before the shortcut is kept.
    /// </summary>
    internal static int TrailingCut(IReadOnlyList<InputEvent> events, IReadOnlyCollection<int> trimKeys)
    {
        var cut = events.Count;
        if (trimKeys.Count == 0) return cut;

        for (var j = events.Count - 1; j >= 0; j--)
        {
            var e = events[j];
            if (e.IsKey && trimKeys.Contains(e.VirtualKey))
            {
                cut = j;
                continue;
            }

            if (e.Kind == InputEventKind.MouseMove) continue;
            break;
        }

        return cut;
    }
}
