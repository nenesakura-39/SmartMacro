using SmartMacro.Core.Input;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Core.Recording;

/// <summary>
/// Captures input for smart recording. The real version uses Windows Raw Input (one source, one clock): every
/// mouse event carries both the relative counts the mouse reported and the cursor position at that moment,
/// plus whether the cursor was hidden / confined. Event times are microseconds since recording started.
/// </summary>
public interface ISmartInputRecorder
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
    IReadOnlyList<CapturedInputEvent> Stop();
}

public sealed class SmartRecordingOptions
{
    /// <summary>Keys to strip from the end of the recording (the stop shortcut).</summary>
    public IReadOnlyCollection<int> TrimTrailingKeys { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Keep cursor movement in the editable parts (Move steps). Turning it off only drops Move steps; camera
    /// movement inside "Recorded input" steps is always kept, because without it there's no camera movement at all.
    /// </summary>
    public bool RecordMouseMoves { get; init; } = true;

    /// <summary>Editable parts: keep how long each tap was held (Click / Press HoldMs).</summary>
    public bool KeepHoldTime { get; init; } = true;

    /// <summary>
    /// Editable parts: keep only the cursor points that matter (path shape, reversals, rests, the spot before every
    /// press / release) instead of one Move + Delay pair every ~30 ms. See <see cref="MousePathSimplifier"/>.
    /// </summary>
    public bool SimplifyMousePath { get; init; } = true;

    public int StartX { get; init; }

    public int StartY { get; init; }

    public MouseContextOptions Context { get; init; } = new();

    /// <summary>
    /// A short stretch of cursor-only movement (no clicks, keys or wheel) next to a camera part stays inside that
    /// "Recorded input" step instead of becoming a few separate Move / Delay steps.
    /// </summary>
    public int AbsorbShortCursorPartUs { get; init; } = 300_000;
}

/// <summary>What a smart recording turned into, for the log and the status bar.</summary>
public sealed record SmartRecordingResult(
    List<MacroStep> Steps,
    int EditableParts,
    int RecordedInputParts,
    int CursorSegments,
    int RelativeSegments,
    int EventCount,
    double DurationMs)
{
    public static SmartRecordingResult Empty => new(new List<MacroStep>(), 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Turns one smart recording into steps, keeping one accurate timeline:
/// 1. Clean-up (like game mode): trim the stop shortcut, drop releases of keys/buttons that were down before
///    recording started, drop key auto-repeat and duplicate presses, drop empty moves, release anything still held.
/// 2. <see cref="MouseContextClassifier"/> splits the timeline into cursor-position parts and relative (camera) parts.
/// 3. The timeline is cut only at moments when no key or button is held (so a held W or right button never gets
///    split across steps). Pieces with only cursor-position movement become normal editable steps (via the desktop
///    <see cref="RecordingConverter"/>: Click, Press, Move, Delay...). Pieces that contain camera movement become one
///    "Recorded input" step that keeps every event, both mouse representations and per-part mouse segments.
/// 4. Delay steps between pieces keep the original spacing, so the whole recording lasts as long as it did.
/// Nothing is reordered: events keep their recorded order even when their timestamps are equal.
/// </summary>
public static class SmartRecordingConverter
{
    /// <summary>Recorder cap, leaving room under the validator limit for the releases added at the end.</summary>
    public const int MaxRecordedEvents = GameRecordingConverter.MaxRecordedEvents;

    public static SmartRecordingResult Convert(IReadOnlyList<CapturedInputEvent> rawEvents, SmartRecordingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawEvents);
        options ??= new SmartRecordingOptions();

        var events = Normalize(rawEvents, options);
        if (events.Count == 0) return SmartRecordingResult.Empty;

        var segments = MouseContextClassifier.Classify(events, options.Context);
        var modes = ModesPerEvent(events, segments);
        var chunks = BuildChunks(events, modes, options);

        var steps = new List<MacroStep>();
        var editable = 0;
        var recorded = 0;
        long? playedUs = null; // where playback will be on the recording's own clock

        foreach (var chunk in chunks)
        {
            List<MacroStep> chunkSteps;
            long startUs;
            long durationUs;
            if (chunk.Relative)
            {
                var step = ConvertRecordedChunk(events, chunk, segments, options);
                chunkSteps = new List<MacroStep> { step };
                startUs = events[chunk.First].Input.TimeUs;
                durationUs = step.Events[^1].TimeUs;
                recorded++;
            }
            else
            {
                if (!TryConvertCursorChunk(events, chunk, options, out chunkSteps, out startUs, out durationUs)) continue;
                editable++;
            }

            if (playedUs is long played)
            {
                // Rounded to whole milliseconds, but measured from the played timeline, so rounding never adds up.
                var gapMs = (startUs - played + 500) / 1000;
                if (gapMs > 0)
                {
                    var delayMs = (int)Math.Min(gapMs, MacroValidator.MaxDelayMs);
                    steps.Add(new DelayStep { DurationMs = delayMs });
                    played += delayMs * 1000L;
                }

                playedUs = played + durationUs;
            }
            else
            {
                playedUs = startUs + durationUs;
            }

            steps.AddRange(chunkSteps);
        }

        var cursorSegments = segments.Count(s => s.Detected == MouseMovementMode.CursorPosition);
        var relativeSegments = segments.Count - cursorSegments;
        var durationMs = (events[^1].Input.TimeUs - events[0].Input.TimeUs) / 1000.0;
        return new SmartRecordingResult(steps, editable, recorded, cursorSegments, relativeSegments, events.Count, durationMs);
    }

    // ---------------- 1. Clean-up ----------------

    internal static List<CapturedInputEvent> Normalize(IReadOnlyList<CapturedInputEvent> raw, SmartRecordingOptions options)
    {
        var count = Math.Min(raw.Count, MaxRecordedEvents);
        var inputs = new List<InputEvent>(count);
        for (var i = 0; i < count; i++) inputs.Add(raw[i].Input);
        var cut = GameRecordingConverter.TrailingCut(inputs, options.TrimTrailingKeys);

        var output = new List<CapturedInputEvent>(cut);
        var keysDown = new List<(int Vk, int Scan, bool Ext)>();
        var buttonsDown = new List<MacroMouseButton>();
        var lastTime = long.MinValue;
        var hasPosition = false;
        int lastX = options.StartX, lastY = options.StartY;

        for (var i = 0; i < cut; i++)
        {
            var captured = raw[i];
            var e = captured.Input;

            // Times must never go backwards (the recorder guarantees this; be safe with other sources).
            var time = Math.Max(e.TimeUs, lastTime);
            lastTime = time;

            InputEvent? keep = null;
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    if (e.Dx == 0 && e.Dy == 0) break;
                    keep = e with
                    {
                        TimeUs = time,
                        Dx = Math.Clamp(e.Dx, -MacroValidator.MaxRelativeMove, MacroValidator.MaxRelativeMove),
                        Dy = Math.Clamp(e.Dy, -MacroValidator.MaxRelativeMove, MacroValidator.MaxRelativeMove),
                    };
                    break;

                case InputEventKind.MouseDown:
                    if (!Enum.IsDefined(e.Button) || buttonsDown.Contains(e.Button)) break;
                    buttonsDown.Add(e.Button);
                    keep = e with { TimeUs = time };
                    break;

                case InputEventKind.MouseUp:
                    // Released but never pressed during the recording: it was down before we started.
                    if (!buttonsDown.Remove(e.Button)) break;
                    keep = e with { TimeUs = time };
                    break;

                case InputEventKind.Wheel:
                case InputEventKind.HorizontalWheel:
                    if (e.Delta == 0) break;
                    keep = e with { TimeUs = time, Delta = Math.Clamp(e.Delta, -MacroValidator.MaxScrollDelta, MacroValidator.MaxScrollDelta) };
                    break;

                case InputEventKind.KeyDown:
                {
                    if (!VirtualKeys.IsValid(e.VirtualKey) || e.ScanCode is < 0 or > 0xFF) break;
                    var id = (e.VirtualKey, e.ScanCode, e.Extended);
                    if (keysDown.Contains(id)) break; // auto-repeat
                    keysDown.Add(id);
                    keep = e with { TimeUs = time, HasPosition = false, X = 0, Y = 0 };
                    break;
                }

                case InputEventKind.KeyUp:
                {
                    var id = (e.VirtualKey, e.ScanCode, e.Extended);
                    if (!keysDown.Remove(id)) break; // was down before recording started
                    keep = e with { TimeUs = time, HasPosition = false, X = 0, Y = 0 };
                    break;
                }
            }

            if (keep is not { } k) continue;
            if (k.HasPosition)
            {
                hasPosition = true;
                lastX = k.X;
                lastY = k.Y;
            }

            output.Add(new CapturedInputEvent(k, captured.Cursor));
        }

        if (output.Count == 0) return output;

        // Release anything still held, so playback never leaves a key or button stuck down.
        var end = output[^1].Input.TimeUs;
        for (var idx = keysDown.Count - 1; idx >= 0; idx--)
            output.Add(new CapturedInputEvent(InputEvent.KeyUp(end, keysDown[idx].Vk, keysDown[idx].Scan, keysDown[idx].Ext)));
        foreach (var button in buttonsDown)
        {
            output.Add(new CapturedInputEvent(hasPosition
                ? InputEvent.ButtonUp(end, button, lastX, lastY)
                : InputEvent.ButtonUp(end, button)));
        }

        return output;
    }

    // ---------------- 2. Mouse mode per event ----------------

    private static MouseMovementMode[] ModesPerEvent(List<CapturedInputEvent> events, List<MouseSegment> segments)
    {
        var modes = new MouseMovementMode[events.Count];
        var j = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var time = events[i].Input.TimeUs;
            while (j + 1 < segments.Count && segments[j + 1].StartUs <= time) j++;
            modes[i] = segments[j].Detected;
        }

        return modes;
    }

    // ---------------- 3. Pieces ----------------

    private readonly record struct Chunk(int First, int Last, bool Relative);

    /// <summary>
    /// Cuts the timeline into pieces at moments where nothing is held, labels each piece "relative" if any of its
    /// events falls in a relative part, then joins neighbouring pieces with the same label.
    /// </summary>
    private static List<Chunk> BuildChunks(List<CapturedInputEvent> events, MouseMovementMode[] modes, SmartRecordingOptions options)
    {
        var atoms = new List<Chunk>();
        var keys = new HashSet<(int, int, bool)>();
        var buttons = new HashSet<MacroMouseButton>();
        var atomStart = 0;
        var atomRelative = false;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i].Input;
            if (modes[i] == MouseMovementMode.Relative) atomRelative = true;
            switch (e.Kind)
            {
                case InputEventKind.KeyDown: keys.Add((e.VirtualKey, e.ScanCode, e.Extended)); break;
                case InputEventKind.KeyUp: keys.Remove((e.VirtualKey, e.ScanCode, e.Extended)); break;
                case InputEventKind.MouseDown: buttons.Add(e.Button); break;
                case InputEventKind.MouseUp: buttons.Remove(e.Button); break;
            }

            if (keys.Count == 0 && buttons.Count == 0)
            {
                atoms.Add(new Chunk(atomStart, i, atomRelative));
                atomStart = i + 1;
                atomRelative = false;
            }
        }

        // Normalize() releases everything at the end, so this is only a safety net.
        if (atomStart < events.Count) atoms.Add(new Chunk(atomStart, events.Count - 1, atomRelative));

        var chunks = Join(atoms);

        // Short cursor-only movement next to a camera part stays inside the recorded-input step.
        var changed = false;
        for (var c = 0; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            if (chunk.Relative) continue;
            var nextToRelative = (c > 0 && chunks[c - 1].Relative) || (c + 1 < chunks.Count && chunks[c + 1].Relative);
            if (!nextToRelative || !OnlyMoves(events, chunk)) continue;
            if (events[chunk.Last].Input.TimeUs - events[chunk.First].Input.TimeUs >= options.AbsorbShortCursorPartUs) continue;
            chunks[c] = chunk with { Relative = true };
            changed = true;
        }

        return changed ? Join(chunks) : chunks;
    }

    private static List<Chunk> Join(List<Chunk> pieces)
    {
        var joined = new List<Chunk>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (joined.Count > 0 && joined[^1].Relative == piece.Relative)
                joined[^1] = joined[^1] with { Last = piece.Last };
            else
                joined.Add(piece);
        }

        return joined;
    }

    private static bool OnlyMoves(List<CapturedInputEvent> events, Chunk chunk)
    {
        for (var i = chunk.First; i <= chunk.Last; i++)
        {
            if (events[i].Input.Kind != InputEventKind.MouseMove) return false;
        }

        return true;
    }

    // ---------------- 4. Steps ----------------

    /// <summary>Editable steps for a cursor-only piece. False if nothing in it becomes a step.</summary>
    private static bool TryConvertCursorChunk(List<CapturedInputEvent> events, Chunk chunk, SmartRecordingOptions options,
        out List<MacroStep> steps, out long startUs, out long durationUs)
    {
        steps = new List<MacroStep>();
        startUs = 0;
        durationUs = 0;

        var raw = new List<RawInputEvent>(chunk.Last - chunk.First + 1);
        int x = options.StartX, y = options.StartY;
        for (var i = chunk.First; i <= chunk.Last; i++)
        {
            var e = events[i].Input;
            if (e.HasPosition)
            {
                x = e.X;
                y = e.Y;
            }

            var ms = e.TimeUs / 1000;
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    if (options.RecordMouseMoves && e.HasPosition) raw.Add(new RawInputEvent(ms, RawInputKind.MouseMove, X: x, Y: y));
                    break;
                case InputEventKind.MouseDown:
                    raw.Add(new RawInputEvent(ms, RawInputKind.MouseDown, X: x, Y: y, Button: e.Button));
                    break;
                case InputEventKind.MouseUp:
                    raw.Add(new RawInputEvent(ms, RawInputKind.MouseUp, X: x, Y: y, Button: e.Button));
                    break;
                case InputEventKind.Wheel:
                    raw.Add(new RawInputEvent(ms, RawInputKind.Wheel, X: x, Y: y, Delta: e.Delta));
                    break;
                case InputEventKind.HorizontalWheel:
                    raw.Add(new RawInputEvent(ms, RawInputKind.HorizontalWheel, X: x, Y: y, Delta: e.Delta));
                    break;
                case InputEventKind.KeyDown:
                    raw.Add(new RawInputEvent(ms, RawInputKind.KeyDown, VirtualKey: SpecificVirtualKey(e)));
                    break;
                case InputEventKind.KeyUp:
                    raw.Add(new RawInputEvent(ms, RawInputKind.KeyUp, VirtualKey: SpecificVirtualKey(e)));
                    break;
            }
        }

        if (raw.Count == 0) return false;

        steps = RecordingConverter.Convert(raw, new RecordingOptions
        {
            RecordMouseMoves = options.RecordMouseMoves,
            KeepHoldTime = options.KeepHoldTime,
            SimplifyMousePath = options.SimplifyMousePath,
        });
        if (steps.Count == 0) return false;

        startUs = raw[0].TimeMs * 1000;
        durationUs = PlayedDurationMs(steps) * 1000;
        return true;
    }

    /// <summary>
    /// Raw Input reports Shift / Ctrl / Alt as the generic keys; the desktop recorder (and the editor) use the
    /// left / right versions. Uses the recorded scan code / E0 flag to pick the right one, so editable steps from a
    /// smart recording match desktop-mode steps.
    /// </summary>
    internal static int SpecificVirtualKey(InputEvent e) => e.VirtualKey switch
    {
        0x10 => e.ScanCode == 0x36 ? 0xA1 : 0xA0, // Shift: right Shift has scan code 0x36
        0x11 => e.Extended ? 0xA3 : 0xA2,         // Ctrl: right Ctrl has the E0 prefix
        0x12 => e.Extended ? 0xA5 : 0xA4,         // Alt: right Alt has the E0 prefix
        _ => e.VirtualKey,
    };

    /// <summary>How long the playback engine spends on these steps (delays + hold times), in ms.</summary>
    internal static long PlayedDurationMs(IEnumerable<MacroStep> steps)
    {
        long total = 0;
        foreach (var step in steps)
        {
            total += step switch
            {
                DelayStep d => d.DurationMs,
                MouseClickStep { Action: MouseClickAction.Click } c => c.HoldMs,
                KeyPressStep { Action: KeyAction.Press } k => k.HoldMs,
                _ => 0,
            };
        }

        return total;
    }

    /// <summary>One recorded-input step for a piece with camera movement (times relative to the piece).</summary>
    private static InputSequenceStep ConvertRecordedChunk(List<CapturedInputEvent> events, Chunk chunk, List<MouseSegment> segments,
        SmartRecordingOptions options)
    {
        var origin = events[chunk.First].Input.TimeUs;
        var last = events[chunk.Last].Input.TimeUs;

        var list = new List<InputEvent>(chunk.Last - chunk.First + 1);
        int? startX = null, startY = null;
        for (var i = chunk.First; i <= chunk.Last; i++)
        {
            var e = events[i].Input;
            if (startX is null && e.HasPosition)
            {
                startX = e.X;
                startY = e.Y;
            }

            list.Add(e with { TimeUs = e.TimeUs - origin });
        }

        // The mouse segments that overlap this piece, moved to the piece's own clock.
        var parts = new List<MouseSegment>();
        for (var s = 0; s < segments.Count; s++)
        {
            var segmentStart = segments[s].StartUs;
            var segmentEnd = s + 1 < segments.Count ? segments[s + 1].StartUs : long.MaxValue;
            if (segmentEnd <= origin || segmentStart > last) continue;

            var start = Math.Max(0, segmentStart - origin);
            if (parts.Count > 0 && parts[^1].Detected == segments[s].Detected) continue;
            if (parts.Count > 0 && start <= parts[^1].StartUs)
            {
                parts[^1].Detected = segments[s].Detected;
                parts[^1].Evidence = segments[s].Evidence;
                continue;
            }

            parts.Add(new MouseSegment { StartUs = start, Detected = segments[s].Detected, Evidence = segments[s].Evidence });
        }

        if (parts.Count == 0)
            parts.Add(new MouseSegment { StartUs = 0, Detected = MouseMovementMode.Relative, Evidence = MouseContextEvidence.Unclear });
        parts[0].StartUs = 0;

        return new InputSequenceStep
        {
            Events = list,
            MouseSegments = parts,
            StartX = startX ?? options.StartX,
            StartY = startY ?? options.StartY,
            MoveCursorToStart = false, // cursor-position parts already carry their positions
        };
    }
}
