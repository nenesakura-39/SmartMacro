using System.Diagnostics;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Sends fake input to Windows. The real version uses SendInput (WPF project);
/// tests use a fake that just records the calls.
/// </summary>
public interface IInputSimulator
{
    /// <summary>Moves the cursor to absolute screen pixels (virtual-screen coordinates).</summary>
    void MoveMouse(int x, int y);

    /// <summary>
    /// Sends relative mouse movement, like a physical mouse does (used by game-mode recordings).
    /// Games that read raw mouse input see these counts; moving the cursor with <see cref="MoveMouse"/> is
    /// invisible to them. Whether a particular game accepts simulated input at all is up to the game.
    /// </summary>
    void MoveMouseRelative(int dx, int dy);

    void MouseButton(MacroMouseButton button, bool down);

    /// <summary>120 = one notch. Positive = up / right.</summary>
    void Scroll(int delta, bool horizontal);

    /// <summary>Presses or releases a key by Windows virtual-key code.</summary>
    void Key(int virtualKey, bool down);

    /// <summary>
    /// Presses or releases a key with the exact hardware scan code (and extended-key flag) that was recorded.
    /// Games that read scan codes (raw input / DirectInput) then see the same key as during recording.
    /// A scan code of 0 means "unknown", and the simulator looks one up from the virtual-key code.
    /// </summary>
    void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down);

    /// <summary>
    /// Types one character (or one surrogate pair). "\n" means Enter and "\t" means Tab.
    /// </summary>
    void TypeCharacters(string text);

    /// <summary>
    /// Where the cursor is right now (physical pixels), if the simulator can tell. Used by a Drag that starts "where
    /// the cursor already is" to glide from there. The default says "unknown" (the drag then jumps to its end).
    /// </summary>
    bool TryGetCursorPosition(out int x, out int y)
    {
        x = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// Failsafe: sends a release (key up / button up) for every key and mouse button this simulator pressed and hasn't
    /// released yet. The playback engine calls it whenever a run ends (finished, stopped with Esc / the hotkey, timed out,
    /// or crashed), on top of its own bookkeeping, so nothing can ever stay stuck down in Windows or a game.
    /// Returns an error message when Windows refused one of the releases (the rest are still sent), otherwise null.
    /// The default does nothing (simulators that don't track what they pressed, e.g. test fakes).
    /// </summary>
    string? ReleaseAllHeldInputs() => null;
}

/// <summary>Thrown by an input simulator when Windows refuses the input.</summary>
public sealed class InputSimulationException : Exception
{
    public InputSimulationException(string message) : base(message)
    {
    }
}

/// <summary>Time source for playback, so tests can run delays instantly.</summary>
public interface IPlaybackClock
{
    TimeSpan Elapsed { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPlaybackClock : IPlaybackClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
