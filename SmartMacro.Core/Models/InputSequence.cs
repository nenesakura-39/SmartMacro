using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>What one recorded event does.</summary>
public enum InputEventKind
{
    /// <summary>
    /// Mouse movement. Dx, Dy are relative mouse counts (like a real mouse reports). Smart recordings also store
    /// where the cursor was right after the movement (HasPosition, X, Y in physical screen pixels), so playback can
    /// use either representation — but only ever one of them per event (see <see cref="MouseSegment"/>).
    /// </summary>
    MouseMove,
    MouseDown,
    MouseUp,

    /// <summary>Vertical wheel. 120 = one notch, positive = up.</summary>
    Wheel,

    /// <summary>Horizontal wheel. 120 = one notch, positive = right.</summary>
    HorizontalWheel,
    KeyDown,
    KeyUp,
}

/// <summary>
/// One event inside an <see cref="InputSequenceStep"/>. TimeUs is microseconds from the start of the step.
/// Only the fields that matter for the Kind are used:
/// MouseMove → Dx/Dy (+ optional X/Y), MouseDown/Up → Button (+ optional X/Y), Wheel → Delta (+ optional X/Y),
/// KeyDown/Up → VirtualKey, ScanCode, Extended.
/// HasPosition says whether X/Y were recorded. Game-mode recordings never have positions; smart recordings
/// have them on every mouse event. A relative delta is never turned into a position or the other way round.
/// </summary>
public readonly record struct InputEvent(
    long TimeUs,
    InputEventKind Kind,
    int Dx = 0,
    int Dy = 0,
    MacroMouseButton Button = MacroMouseButton.Left,
    int Delta = 0,
    int VirtualKey = 0,
    int ScanCode = 0,
    bool Extended = false,
    bool HasPosition = false,
    int X = 0,
    int Y = 0)
{
    public static InputEvent Move(long timeUs, int dx, int dy) => new(timeUs, InputEventKind.MouseMove, Dx: dx, Dy: dy);

    /// <summary>Relative movement plus the cursor position (physical pixels) right after it.</summary>
    public static InputEvent Move(long timeUs, int dx, int dy, int x, int y) =>
        new(timeUs, InputEventKind.MouseMove, Dx: dx, Dy: dy, HasPosition: true, X: x, Y: y);

    public static InputEvent ButtonDown(long timeUs, MacroMouseButton button) => new(timeUs, InputEventKind.MouseDown, Button: button);

    public static InputEvent ButtonDown(long timeUs, MacroMouseButton button, int x, int y) =>
        new(timeUs, InputEventKind.MouseDown, Button: button, HasPosition: true, X: x, Y: y);

    public static InputEvent ButtonUp(long timeUs, MacroMouseButton button) => new(timeUs, InputEventKind.MouseUp, Button: button);

    public static InputEvent ButtonUp(long timeUs, MacroMouseButton button, int x, int y) =>
        new(timeUs, InputEventKind.MouseUp, Button: button, HasPosition: true, X: x, Y: y);

    public static InputEvent WheelEvent(long timeUs, int delta, bool horizontal = false) =>
        new(timeUs, horizontal ? InputEventKind.HorizontalWheel : InputEventKind.Wheel, Delta: delta);

    /// <summary>Wheel event plus where the cursor was.</summary>
    public static InputEvent WheelAt(long timeUs, int delta, bool horizontal, int x, int y) =>
        new(timeUs, horizontal ? InputEventKind.HorizontalWheel : InputEventKind.Wheel, Delta: delta, HasPosition: true, X: x, Y: y);

    public static InputEvent KeyDown(long timeUs, int virtualKey, int scanCode = 0, bool extended = false) =>
        new(timeUs, InputEventKind.KeyDown, VirtualKey: virtualKey, ScanCode: scanCode, Extended: extended);

    public static InputEvent KeyUp(long timeUs, int virtualKey, int scanCode = 0, bool extended = false) =>
        new(timeUs, InputEventKind.KeyUp, VirtualKey: virtualKey, ScanCode: scanCode, Extended: extended);

    public bool IsKey => Kind is InputEventKind.KeyDown or InputEventKind.KeyUp;

    public bool IsButton => Kind is InputEventKind.MouseDown or InputEventKind.MouseUp;

    public bool IsWheel => Kind is InputEventKind.Wheel or InputEventKind.HorizontalWheel;

    /// <summary>Mouse events can carry a cursor position; key events never do.</summary>
    public bool IsMouse => Kind is InputEventKind.MouseMove or InputEventKind.MouseDown or InputEventKind.MouseUp
        or InputEventKind.Wheel or InputEventKind.HorizontalWheel;
}

/// <summary>
/// Compact text format for recorded events, so a few minutes of 1000 Hz mouse data stays a reasonable file size
/// (one JSON object per event would be 5-10x bigger). Two versions:
/// <list type="bullet">
/// <item><c>v1</c> (game mode, unchanged): <c>v1 dt,code[,args] ...</c> where dt is microseconds since the
/// previous event. Codes: m dx,dy | bd button | bu button | w delta | hw delta | kd vk,scan,ext | ku vk,scan,ext.</item>
/// <item><c>u1</c> (smart recording): the same codes, and mouse events may add the cursor position at the end:
/// m dx,dy,x,y | bd button,x,y | bu button,x,y | w delta,x,y | hw delta,x,y.</item>
/// </list>
/// A list is written as v1 whenever no event has a position, so game-mode recordings are byte-for-byte unchanged.
/// Only numbers and these codes are accepted, so a damaged or hand-edited file can't do anything unexpected.
/// </summary>
public static class InputEventCodec
{
    public const string Version1 = "v1";

    /// <summary>Unified (smart) recording format: like v1, plus optional cursor positions on mouse events.</summary>
    public const string UnifiedVersion1 = "u1";

    public static string Encode(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var unified = false;
        foreach (var e in events)
        {
            if (e.HasPosition && e.IsMouse)
            {
                unified = true;
                break;
            }
        }

        var sb = new StringBuilder(4 + events.Count * (unified ? 20 : 12));
        sb.Append(unified ? UnifiedVersion1 : Version1);
        long previous = 0;
        foreach (var e in events)
        {
            sb.Append(' ');
            // Signed, so even an out-of-order (invalid) list round-trips exactly; the validator reports it.
            sb.Append((e.TimeUs - previous).ToString(CultureInfo.InvariantCulture));
            previous = e.TimeUs;
            sb.Append(',');
            switch (e.Kind)
            {
                case InputEventKind.MouseMove:
                    sb.Append("m,").Append(Num(e.Dx)).Append(',').Append(Num(e.Dy));
                    break;
                case InputEventKind.MouseDown:
                    sb.Append("bd,").Append(Num((int)e.Button));
                    break;
                case InputEventKind.MouseUp:
                    sb.Append("bu,").Append(Num((int)e.Button));
                    break;
                case InputEventKind.Wheel:
                    sb.Append("w,").Append(Num(e.Delta));
                    break;
                case InputEventKind.HorizontalWheel:
                    sb.Append("hw,").Append(Num(e.Delta));
                    break;
                case InputEventKind.KeyDown:
                case InputEventKind.KeyUp:
                    sb.Append(e.Kind == InputEventKind.KeyDown ? "kd," : "ku,")
                        .Append(Num(e.VirtualKey)).Append(',')
                        .Append(Num(e.ScanCode)).Append(',')
                        .Append(e.Extended ? '1' : '0');
                    break;
                default:
                    throw new InvalidOperationException($"Unknown input event kind {(int)e.Kind}.");
            }

            if (unified && e.HasPosition && e.IsMouse)
                sb.Append(',').Append(Num(e.X)).Append(',').Append(Num(e.Y));
        }

        return sb.ToString();
    }

    /// <summary>Parses the text format. Throws FormatException with a short reason if it's damaged.</summary>
    public static List<InputEvent> Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || (tokens[0] != Version1 && tokens[0] != UnifiedVersion1))
            throw new FormatException("unknown recorded-input format");

        // Positions are only allowed in the unified format, so a v1 (game-mode) list can never gain them.
        var unified = tokens[0] == UnifiedVersion1;
        var events = new List<InputEvent>(tokens.Length - 1);
        long time = 0;
        for (var i = 1; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split(',');
            if (parts.Length < 2) throw Bad(i);

            time = checked(time + ParseLong(parts[0], i));
            var args = parts.Length - 2;
            InputEvent e = parts[1] switch
            {
                "m" when args == 2 => InputEvent.Move(time, ParseInt(parts[2], i), ParseInt(parts[3], i)),
                "m" when unified && args == 4 => InputEvent.Move(time, ParseInt(parts[2], i), ParseInt(parts[3], i),
                    ParseInt(parts[4], i), ParseInt(parts[5], i)),
                "bd" when args == 1 => InputEvent.ButtonDown(time, (MacroMouseButton)ParseInt(parts[2], i)),
                "bd" when unified && args == 3 => InputEvent.ButtonDown(time, (MacroMouseButton)ParseInt(parts[2], i),
                    ParseInt(parts[3], i), ParseInt(parts[4], i)),
                "bu" when args == 1 => InputEvent.ButtonUp(time, (MacroMouseButton)ParseInt(parts[2], i)),
                "bu" when unified && args == 3 => InputEvent.ButtonUp(time, (MacroMouseButton)ParseInt(parts[2], i),
                    ParseInt(parts[3], i), ParseInt(parts[4], i)),
                "w" when args == 1 => InputEvent.WheelEvent(time, ParseInt(parts[2], i)),
                "w" when unified && args == 3 => InputEvent.WheelAt(time, ParseInt(parts[2], i), false,
                    ParseInt(parts[3], i), ParseInt(parts[4], i)),
                "hw" when args == 1 => InputEvent.WheelEvent(time, ParseInt(parts[2], i), horizontal: true),
                "hw" when unified && args == 3 => InputEvent.WheelAt(time, ParseInt(parts[2], i), true,
                    ParseInt(parts[3], i), ParseInt(parts[4], i)),
                "kd" when args == 3 => InputEvent.KeyDown(time, ParseInt(parts[2], i), ParseInt(parts[3], i), ParseFlag(parts[4], i)),
                "ku" when args == 3 => InputEvent.KeyUp(time, ParseInt(parts[2], i), ParseInt(parts[3], i), ParseFlag(parts[4], i)),
                _ => throw Bad(i),
            };
            events.Add(e);
        }

        return events;
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static long ParseLong(string s, int index) =>
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : throw Bad(index);

    private static int ParseInt(string s, int index) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : throw Bad(index);

    private static bool ParseFlag(string s, int index) => s switch
    {
        "0" => false,
        "1" => true,
        _ => throw Bad(index),
    };

    private static FormatException Bad(int index) => new($"event {index} can't be read");
}

/// <summary>Stores the event list as one compact string (see <see cref="InputEventCodec"/>).</summary>
public sealed class InputEventListJsonConverter : JsonConverter<List<InputEvent>>
{
    // Lets a "null" in a hand-edited file become an empty list (the validator then reports it).
    public override bool HandleNull => true;

    public override List<InputEvent> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new List<InputEvent>();
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Recorded input must be stored as text.");

        try
        {
            return InputEventCodec.Decode(reader.GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new JsonException($"The recorded input is damaged ({ex.Message}).", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, List<InputEvent> value, JsonSerializerOptions options) =>
        writer.WriteStringValue(InputEventCodec.Encode(value ?? new List<InputEvent>()));
}
