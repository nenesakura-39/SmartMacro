using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Ocr;

/// <summary>Screen helpers the "Find text" editor uses: picking an area and test-reading it.</summary>
public interface IFindTextTools
{
    bool IsOcrAvailable { get; }

    string OcrStatus { get; }

    /// <summary>Hides SmartMacro, lets the user drag a rectangle on screen, returns it (null if cancelled).</summary>
    Task<ScreenRect?> PickRegionAsync();

    /// <summary>Hides SmartMacro, reads the step's search area once, and describes what was found.</summary>
    Task<string> TestAsync(FindTextStep step);

    /// <summary>Where the most recent test found the text (null when it wasn't found).</summary>
    ScreenPoint? LastTestMatch { get; }

    /// <summary>Moves the mouse cursor to a spot so the user can see where a match is (nothing is clicked).</summary>
    void ShowSpot(ScreenPoint point);
}

public sealed class FindTextTools : IFindTextTools
{
    private readonly Window _mainWindow;
    private readonly IOcrService _ocr;
    private readonly string? _debugImagePath;
    private readonly IScreenLayout? _layout;

    /// <param name="debugImagePath">Where "Test now" saves a picture of what was captured (helps diagnose misses).</param>
    /// <param name="layout">Monitors and windows, so "Test detection now" reads the same place the step will.</param>
    public FindTextTools(Window mainWindow, IOcrService ocr, string? debugImagePath = null, IScreenLayout? layout = null)
    {
        _mainWindow = mainWindow;
        _ocr = ocr;
        _debugImagePath = debugImagePath;
        _layout = layout;
    }

    public bool IsOcrAvailable => _ocr.IsAvailable;

    public ScreenPoint? LastTestMatch { get; private set; }

    public void ShowSpot(ScreenPoint point) => NativeMethods.SetCursorPos(point.X, point.Y);

    public string OcrStatus => _ocr.StatusText;

    public Task<ScreenRect?> PickRegionAsync() => WhileHiddenAsync(() => RegionPicker.PickAsync());

    public async Task<string> TestAsync(FindTextStep step)
    {
        LastTestMatch = null;
        if (!_ocr.IsAvailable) return _ocr.StatusText;
        if (string.IsNullOrWhiteSpace(step.Text)) return "Enter the text to look for first.";

        ScreenRect region;
        try
        {
            region = SearchArea.Resolve(step, _ocr.GetVirtualScreen(), _layout);
        }
        catch (SearchAreaUnavailableException ex)
        {
            return $"Can't test right now: {ex.Message}";
        }

        OcrReadResult read;
        string? savedImage = null;
        try
        {
            // Hide SmartMacro so it doesn't read its own window (which shows the search text).
            read = await WhileHiddenAsync(async () =>
            {
                var result = await _ocr.ReadAsync(region, CancellationToken.None);
                if (_debugImagePath is not null && _ocr is WindowsOcrService windowsOcr)
                    savedImage = windowsOcr.TrySaveCapture(region, _debugImagePath);
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        var where = step.EffectiveScope == SearchScope.CustomArea ? $"area {region}" : SearchArea.ShortName(step);
        var picture = savedImage is null ? "" : $"\nA picture of what was captured was saved to: {savedImage}";

        var matches = OcrTextMatcher.Find(read, step.Text, step.MatchMode, step.CaseSensitive, step.MinSimilarityPercent / 100.0,
                step.NormalizeLookAlikes);
        if (matches.Count > 0)
        {
            var best = matches[0];
            var more = matches.Count > 1 ? $" ({matches.Count} matches; the first one is used)" : "";
            LastTestMatch = new ScreenPoint(best.Bounds.CenterX, best.Bounds.CenterY);
            return $"Matched! Read text in area: '{best.Text}' at ({best.Bounds.CenterX}, {best.Bounds.CenterY}) — {best.Similarity:P0} match{more}. Searched {where}.{picture}";
        }

        var text = read.FullText.Replace(Environment.NewLine, "  |  ");
        if (text.Length > 400) text = text[..400] + "…";
        return string.IsNullOrWhiteSpace(text)
            ? $"Not found. No text could be read in {where}.{picture}"
            : $"Not found. Text read in area: '{text}' (searched {where}).{picture}";
    }

    private async Task<T> WhileHiddenAsync<T>(Func<Task<T>> action)
    {
        var previousState = _mainWindow.WindowState;
        NativeMethods.SetExcludedFromCapture(_mainWindow, true);
        _mainWindow.WindowState = WindowState.Minimized;
        try
        {
            await Task.Delay(350); // let the minimize animation finish
            return await action();
        }
        finally
        {
            NativeMethods.SetExcludedFromCapture(_mainWindow, false);
            _mainWindow.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            _mainWindow.Activate();
        }
    }
}
