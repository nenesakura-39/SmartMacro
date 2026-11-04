using System.Runtime.InteropServices;
using System.Windows;
using SmartMacro.Core.Playback;

namespace SmartMacro.Services;

/// <summary>
/// Thread-safe clipboard text reader for <see cref="VariableSourceKind.ClipboardText"/> during macro playback.
/// Reads on the WPF STA UI dispatcher and returns an empty string if the clipboard is empty or locked by another app.
/// </summary>
public sealed class WpfClipboardReader : IClipboardReader
{
    public string? ReadText()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return string.Empty;
            if (dispatcher.CheckAccess()) return ReadCore();
            return dispatcher.Invoke(ReadCore);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    private static string ReadCore()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return string.Empty;
        }
    }
}
