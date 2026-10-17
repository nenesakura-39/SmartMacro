using SmartMacro.Mvvm;

namespace SmartMacro.Services.Vision;

/// <summary>A choice in the "Freeze:" drop-down next to the Capture / Pick buttons.</summary>
public sealed record FreezeChoice(int Seconds, string Label);

/// <summary>
/// When the screen pickers (Capture image, Pick area, Pick color, Pick position, the feedback loop picks) freeze the
/// screen: right away, or after a 3 / 5 second countdown so you can switch to a game and open the minigame / menu
/// first. One setting shared by every picker (it's how you like to work, not part of a macro), kept while SmartMacro runs.
/// </summary>
public sealed class ScreenFreezeOptions : ObservableObject
{
    private int _delaySeconds;

    public static ScreenFreezeOptions Instance { get; } = new();

    public static IReadOnlyList<FreezeChoice> Choices { get; } = new[]
    {
        new FreezeChoice(0, "Freeze: Immediately"),
        new FreezeChoice(3, "Freeze: After 3s countdown"),
        new FreezeChoice(5, "Freeze: After 5s countdown"),
    };

    /// <summary>Seconds to wait (with a countdown on screen) before the screen is frozen for picking. 0 = right away.</summary>
    public int DelaySeconds
    {
        get => _delaySeconds;
        set => SetProperty(ref _delaySeconds, Math.Clamp(value, 0, 10));
    }
}
