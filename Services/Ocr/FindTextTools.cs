using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Vision;

namespace SmartMacro.Services.Ocr;

/// <summary>Screen helpers the "Find text" editor uses: picking an area, test-reading it, and inspecting all OCR words on screen.</summary>
public interface IFindTextTools
{
    bool IsOcrAvailable { get; }

    string OcrStatus { get; }

    /// <summary>Hides SmartMacro, lets the user drag a rectangle on screen, returns it (null if cancelled).</summary>
    Task<ScreenRect?> PickRegionAsync();

    /// <summary>Hides SmartMacro, reads the step's search area once, highlights all found matches, and describes what was found.</summary>
    Task<string> TestAsync(FindTextStep step);

    /// <summary>
    /// Hides SmartMacro, captures and reads the step's search area, and opens an interactive visual OCR Inspector window
    /// showing all detected words, word frequency counts, and highlighted matches.
    /// </summary>
    Task<string> InspectScreenTextAsync(FindTextStep step, Action<string>? onPickWord = null);

    /// <summary>Where the most recent test found the primary text match (null when it wasn't found).</summary>
    ScreenPoint? LastTestMatch { get; }

    /// <summary>All matched screen spots from the most recent test (in the step's selected order).</summary>
    IReadOnlyList<ScreenPoint> LastTestMatches { get; }

    /// <summary>Number of matches found in the most recent test.</summary>
    int LastTestMatchCount { get; }

    /// <summary>Total number of OCR words read on screen in the most recent test.</summary>
    int LastTestTotalWordCount { get; }

    /// <summary>Moves the mouse cursor to a spot so the user can see where a match is (nothing is clicked).</summary>
    void ShowSpot(ScreenPoint point);
}

public sealed class FindTextTools : IFindTextTools
{
    private readonly Window _mainWindow;
    private readonly IOcrService _ocr;
    private readonly string? _debugImagePath;
    private readonly IScreenLayout? _layout;
    private Window? _inspectorWindow;

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

    public IReadOnlyList<ScreenPoint> LastTestMatches { get; private set; } = Array.Empty<ScreenPoint>();

    public int LastTestMatchCount { get; private set; }

    public int LastTestTotalWordCount { get; private set; }

    public void ShowSpot(ScreenPoint point) => NativeMethods.SetCursorPos(point.X, point.Y);

    public string OcrStatus => _ocr.StatusText;

    public Task<ScreenRect?> PickRegionAsync() => WhileHiddenAsync(() => RegionPicker.PickAsync());

    public async Task<string> TestAsync(FindTextStep step)
    {
        LastTestMatch = null;
        LastTestMatches = Array.Empty<ScreenPoint>();
        LastTestMatchCount = 0;
        LastTestTotalWordCount = 0;

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
        var watch = Stopwatch.StartNew();
        try
        {
            // Hide SmartMacro so it doesn't read its own window (which shows the search text).
            read = await WhileHiddenAsync(async () =>
            {
                OcrReadResult result;
                if (_ocr is WindowsOcrService winOcr)
                {
                    result = await winOcr.ReadMultiPassAsync(
                        region,
                        step.Preprocess,
                        r => ScreenProbes.ProbeAllText(r, step).Count >= step.EffectiveMinMatchCount,
                        CancellationToken.None);
                    if (_debugImagePath is not null)
                        savedImage = winOcr.TrySaveCapture(region, _debugImagePath);
                }
                else
                {
                    result = await _ocr.ReadAsync(region, step.Preprocess, CancellationToken.None);
                }
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }
        var elapsedMs = watch.Elapsed.TotalMilliseconds;

        var where = step.EffectiveScope == SearchScope.CustomArea ? $"area {region}" : SearchArea.ShortName(step);
        var picture = savedImage is null ? "" : $"\nA picture of what was captured was saved to: {savedImage}";

        var matches = ScreenProbes.ProbeAllText(read, step);
        var totalWords = read.TotalWordCount;
        LastTestMatchCount = matches.Count;
        LastTestTotalWordCount = totalWords;

        if (matches.Count > 0)
        {
            var rawRects = matches.Select(m => m.Bounds).ToList();
            var orderedRects = step.TargetSelection == SearchMatchMode.AllMatches
                ? ScreenProbes.OrderRects(rawRects, step.MatchOrder)
                : step.TargetSelection == SearchMatchMode.ReadingOrderNth
                    ? ScreenProbes.SortByReadingOrder(rawRects, r => r)
                    : rawRects;

            var primaryMatch = ScreenProbes.ProbeText(read, step) ?? matches[0];
            LastTestMatches = orderedRects.Select(r => new ScreenPoint(r.CenterX, r.CenterY)).ToList();

            var metMinGate = matches.Count >= step.EffectiveMinMatchCount;
            var metNthGate = step.TargetSelection != SearchMatchMode.ReadingOrderNth || matches.Count >= step.EffectiveTargetMatchIndex;

            if (!metMinGate || !metNthGate)
            {
                ScreenHighlightOverlay.ShowMatches(region, orderedRects, found: false,
                    !metMinGate
                        ? $"✗ Found {matches.Count}× (needs ≥{step.EffectiveMinMatchCount})"
                        : $"✗ Found {matches.Count}× (needs match #{step.EffectiveTargetMatchIndex})");

                return !metMinGate
                    ? $"Found {matches.Count} occurrence(s) of '{step.Text}' across {totalWords:N0} screen words ({elapsedMs:0} ms), which is below the required minimum of {step.EffectiveMinMatchCount}. Searched {where}.{picture}"
                    : $"Found {matches.Count} occurrence(s) of '{step.Text}' across {totalWords:N0} screen words ({elapsedMs:0} ms), but this step is set to pick match #{step.EffectiveTargetMatchIndex}. Searched {where}.{picture}";
            }

            LastTestMatch = new ScreenPoint(primaryMatch.Bounds.CenterX, primaryMatch.Bounds.CenterY);

            // Put the selected primary match first in the highlight list so it gets the crosshair, while still drawing all #1..#N boxes!
            var highlightList = step.TargetSelection == SearchMatchMode.ReadingOrderNth
                ? new[] { primaryMatch.Bounds }.Concat(orderedRects.Where(r => r != primaryMatch.Bounds)).ToList()
                : orderedRects;

            var overlayLabel = matches.Count > 1
                ? $"✓ Found {matches.Count}× \"{primaryMatch.Text}\" ({totalWords:N0} words)"
                : $"✓ {primaryMatch.Similarity * 100:0}% Match";
            ScreenHighlightOverlay.ShowMatches(region, highlightList, found: true, overlayLabel);

            var modeSummary = matches.Count > 1
                ? step.TargetSelection switch
                {
                    SearchMatchMode.AllMatches => $" — Found all {matches.Count} occurrences across {totalWords:N0} screen words ({elapsedMs:0} ms)!",
                    SearchMatchMode.ReadingOrderNth => $" — Picked match #{step.EffectiveTargetMatchIndex} of {matches.Count} occurrences across {totalWords:N0} screen words ({elapsedMs:0} ms).",
                    SearchMatchMode.RandomMatch => $" — Found {matches.Count} occurrences across {totalWords:N0} screen words (picks 1 at random, {elapsedMs:0} ms).",
                    SearchMatchMode.NearestToCursor => $" — Found {matches.Count} occurrences across {totalWords:N0} screen words (picks nearest to cursor, {elapsedMs:0} ms).",
                    _ => $" — Found {matches.Count} occurrences across {totalWords:N0} screen words (the first/best one is used; switch to \"Find All\" to act on or count all {matches.Count}, {elapsedMs:0} ms).",
                }
                : $" — 1 match across {totalWords:N0} screen words ({elapsedMs:0} ms).";

            return $"Matched! Read '{primaryMatch.Text}' at ({primaryMatch.Bounds.CenterX}, {primaryMatch.Bounds.CenterY}), {primaryMatch.Similarity:P0} match{modeSummary} Searched {where}.{picture}";
        }

        ScreenHighlightOverlay.ShowResult(region, null, found: false, "✗ Text not found");
        var text = read.FullText.Replace(Environment.NewLine, "  |  ");
        if (text.Length > 400) text = text[..400] + "…";
        return string.IsNullOrWhiteSpace(text)
            ? $"Not found. No text could be read in {where}.{picture}"
            : $"Not found (0 matches across {totalWords:N0} screen words). Text read in area: '{text}' (searched {where}).{picture}";
    }

    public async Task<string> InspectScreenTextAsync(FindTextStep step, Action<string>? onPickWord = null)
    {
        if (!_ocr.IsAvailable) return _ocr.StatusText;

        ScreenRect region;
        try
        {
            region = SearchArea.Resolve(step, _ocr.GetVirtualScreen(), _layout);
        }
        catch (SearchAreaUnavailableException ex)
        {
            return $"Can't inspect screen text right now: {ex.Message}";
        }

        var screen = _ocr.GetVirtualScreen();
        var clamped = region.Intersect(screen);
        if (clamped.IsEmpty) return "The search area is outside the screen.";

        byte[] rawPixels;
        OcrReadResult read;
        var watch = Stopwatch.StartNew();
        try
        {
            (rawPixels, read) = await WhileHiddenAsync(async () =>
            {
                var px = ScreenCapture.CaptureBgra(clamped);
                var r = await _ocr.ReadAsync(clamped, step.Preprocess, CancellationToken.None);
                return (px, r);
            });
        }
        catch (Exception ex)
        {
            return $"OCR inspection failed: {ex.Message}";
        }
        var elapsedMs = watch.Elapsed.TotalMilliseconds;

        var matches = string.IsNullOrWhiteSpace(step.Text)
            ? Array.Empty<TextMatch>()
            : ScreenProbes.ProbeAllText(read, step);

        LastTestMatchCount = matches.Count;
        LastTestTotalWordCount = read.TotalWordCount;
        if (matches.Count > 0)
        {
            LastTestMatches = matches.Select(m => new ScreenPoint(m.Bounds.CenterX, m.Bounds.CenterY)).ToList();
            LastTestMatch = LastTestMatches[0];
        }

        ShowOcrInspectorWindow(clamped, rawPixels, read, matches, step, elapsedMs, onPickWord);

        return matches.Count > 0
            ? $"OCR Inspector opened: read {read.TotalWordCount:N0} words in {elapsedMs:0} ms — found {matches.Count} occurrence(s) of \"{step.Text}\"."
            : $"OCR Inspector opened: read {read.TotalWordCount:N0} words across {read.Lines.Count} lines in {elapsedMs:0} ms.";
    }

    private void ShowOcrInspectorWindow(
        ScreenRect region,
        byte[] rawPixels,
        OcrReadResult read,
        IReadOnlyList<TextMatch> matches,
        FindTextStep step,
        double elapsedMs,
        Action<string>? onPickWord)
    {
        _inspectorWindow?.Close();
        _inspectorWindow = null;

        // Draw word boxes and matched boxes on a copy of the captured image
        var annotated = new BgraImage(region.Width, region.Height, (byte[])rawPixels.Clone());
        var wordColor = new RgbColor(0x22, 0xD3, 0xEE);  // Cyan for all OCR words
        var matchColor = new RgbColor(0x22, 0xFF, 0x7A); // Bright Neon Green for matched target words

        foreach (var line in read.Lines)
        {
            foreach (var word in line.Words)
            {
                var localRect = new ScreenRect(word.Bounds.X - region.X, word.Bounds.Y - region.Y, word.Bounds.Width, word.Bounds.Height);
                annotated.DrawRectangle(localRect, wordColor, thickness: 1);
            }
        }

        foreach (var m in matches)
        {
            var localRect = new ScreenRect(
                Math.Max(0, m.Bounds.X - region.X - 2),
                Math.Max(0, m.Bounds.Y - region.Y - 2),
                m.Bounds.Width + 4,
                m.Bounds.Height + 4);
            annotated.DrawRectangle(localRect, matchColor, thickness: 3);
        }

        var bmp = BitmapSource.Create(annotated.Width, annotated.Height, 96, 96, PixelFormats.Bgr32, null, annotated.Pixels, annotated.Width * 4);
        bmp.Freeze();

        // Compute word frequencies (top words on screen with their exact occurrence counts)
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in read.Lines)
        {
            foreach (var w in line.Words)
            {
                var clean = OcrTextMatcher.TrimPunctuation(w.Text.Trim());
                if (clean.Length == 0) continue;
                freq[clean] = freq.TryGetValue(clean, out var c) ? c + 1 : 1;
            }
        }

        var topWords = freq
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. Header stats bar
        var summaryText = string.IsNullOrWhiteSpace(step.Text)
            ? $"📊 Read {read.TotalWordCount:N0} words ({freq.Count:N0} unique) across {read.Lines.Count} lines in {elapsedMs:0} ms."
            : $"📊 Read {read.TotalWordCount:N0} words in {elapsedMs:0} ms  ·  🎯 Found {matches.Count}× \"{step.Text}\" (green boxes = matches, cyan = all OCR words)";

        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x1D, 0x31)),
            BorderBrush = new SolidColorBrush(matches.Count > 0 ? Color.FromRgb(0x22, 0xFF, 0x7A) : Color.FromRgb(0x22, 0xD3, 0xEE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = summaryText,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Grid.SetRow(headerBorder, 0);
        root.Children.Add(headerBorder);

        // 2. Word Frequency Chips bar (click any word to set it as the search text!)
        if (topWords.Count > 0)
        {
            var chipsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            chipsPanel.Children.Add(new TextBlock
            {
                Text = "Word counts on screen (click to use): ",
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 4),
            });

            foreach (var (word, count) in topWords)
            {
                var isCurrentTarget = !string.IsNullOrWhiteSpace(step.Text)
                                      && string.Equals(word, OcrTextMatcher.TrimPunctuation(step.Text.Trim()), StringComparison.OrdinalIgnoreCase);
                var chipBtn = new Button
                {
                    Content = $"{word} ({count}×)",
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 5, 4),
                    FontSize = 11.5,
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(isCurrentTarget ? Color.FromRgb(0x14, 0x53, 0x2D) : Color.FromRgb(0x1E, 0x29, 0x3B)),
                    Foreground = new SolidColorBrush(isCurrentTarget ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0xE2, 0xE8, 0xF0)),
                    BorderBrush = new SolidColorBrush(isCurrentTarget ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0x33, 0x41, 0x55)),
                    BorderThickness = new Thickness(1),
                    ToolTip = $"Click to search for \"{word}\" (appears {count} time(s) on screen)",
                };
                var capturedWord = word;
                chipBtn.Click += (_, _) =>
                {
                    onPickWord?.Invoke(capturedWord);
                    _inspectorWindow?.Close();
                };
                chipsPanel.Children.Add(chipBtn);
            }

            Grid.SetRow(chipsPanel, 1);
            root.Children.Add(chipsPanel);
        }

        // 3. Annotated Screenshot Preview
        var imgControl = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
        };
        RenderOptions.SetBitmapScalingMode(imgControl, BitmapScalingMode.HighQuality);

        var imgBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x0A, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = imgControl,
        };
        Grid.SetRow(imgBorder, 2);
        root.Children.Add(imgBorder);

        // 4. Bottom copyable OCR text + Close button
        var footerGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = read.FullText,
            IsReadOnly = true,
            MaxHeight = 68,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x09, 0x0E, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(textBox, 0);
        footerGrid.Children.Add(textBox);

        var closeBtn = new Button
        {
            Content = "Close",
            Padding = new Thickness(20, 8, 20, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsCancel = true,
        };
        Grid.SetColumn(closeBtn, 1);
        footerGrid.Children.Add(closeBtn);

        Grid.SetRow(footerGrid, 3);
        root.Children.Add(footerGrid);

        var window = new Window
        {
            Title = $"OCR Screen Inspector — {read.TotalWordCount:N0} words read, {matches.Count} match(es)",
            Width = 960,
            Height = 700,
            MinWidth = 520,
            MinHeight = 400,
            Owner = _mainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)),
            ShowInTaskbar = false,
            Content = root,
        };
        closeBtn.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_inspectorWindow, window)) _inspectorWindow = null; };
        _inspectorWindow = window;
        window.Show();
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
            try
            {
                if (_mainWindow.IsLoaded && PresentationSource.FromVisual(_mainWindow) is { IsDisposed: false })
                {
                    NativeMethods.SetExcludedFromCapture(_mainWindow, false);
                    _mainWindow.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
                    _mainWindow.Activate();
                }
            }
            catch
            {
                // Main window closed while awaiting the region picker.
            }
        }
    }
}
