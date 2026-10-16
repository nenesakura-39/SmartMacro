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

    void MouseButton(MacroMouseButton button, bool down);

    /// <summary>120 = one notch. Positive = up / right.</summary>
    void Scroll(int delta, bool horizontal);

    /// <summary>Presses or releases a key by Windows virtual-key code.</summary>
    void Key(int virtualKey, bool down);

    /// <summary>
    /// Types one character (or one surrogate pair). "\n" means Enter and "\t" means Tab.
    /// </summary>
    void TypeCharacters(string text);
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
