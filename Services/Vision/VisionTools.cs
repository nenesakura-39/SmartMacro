using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Vision;
using SmartMacro.Services.Input;
using SmartMacro.Services.Ocr;

namespace SmartMacro.Services.Vision;

/// <summary>A picture the user dragged a box around, and where it was on screen.</summary>
public sealed record CapturedTemplate(ScreenRect Bounds, BgraImage Image);

/// <summary>A colour the user clicked on, and where.</summary>
public sealed record PickedColor(int X, int Y, RgbColor Color);

/// <summary>
/// Screen helpers for the Find image / Find color editors: picking areas, capturing the picture to find,
/// picking a colour, and testing a step without playing the macro. SmartMacro hides itself while it looks.
/// </summary>
public interface IVisionTools
{
    Task<ScreenRect?> PickRegionAsync();

    /// <summary>The user drags a box around a button / icon; returns its pixels (null if cancelled).</summary>
    Task<CapturedTemplate?> CaptureTemplateAsync();

    /// <summary>The user clicks a pixel; returns its colour (null if cancelled).</summary>
    Task<PickedColor?> PickColorAsync();

    /// <summary>Looks for the step's image once, shows a preview with the matches, and describes the result.</summary>
    Task<string> TestImageAsync(FindImageStep step);

    /// <summary>Looks for the step's colour once, shows a preview with the patches, and describes the result.</summary>
    Task<string> TestColorAsync(FindColorStep step);

    /// <summary>
    /// Follows the colour as a moving marker inside the step's area for a few seconds and reports its positions
    /// (0–100 % along the area), the frame rate reached and the time per frame.
    /// </summary>
    Task<string> TrackColorAsync(FindColorStep step, TimeSpan duration);

    /// <summary>Turns a picture into something WPF can show (frozen, so any thread may use it).</summary>
    ImageSource ToImageSource(BgraImage image);
}

public sealed class VisionTools : IVisionTools
{
    // Time for the picker overlays to disappear from the screen before we capture it.
    private const int OverlayFadeMs = 150;

    // Tracking test: at most ~30 captures per second.
    private static readonly TimeSpan TrackFrameInterval = TimeSpan.FromMilliseconds(33);

    private static readonly RgbColor MatchColor = new(0x22, 0xC5, 0x5E);
    private static readonly RgbColor OtherColor = new(0xF5, 0x9E, 0x0B);

    private readonly Window _mainWindow;
    private readonly GdiScreenCapture _capture;
    private readonly string _logsDirectory;
    private Window? _preview;

    public VisionTools(Window mainWindow, GdiScreenCapture capture, string logsDirectory)
    {
        _mainWindow = mainWindow;
        _capture = capture;
        _logsDirectory = logsDirectory;
    }

    public Task<ScreenRect?> PickRegionAsync() => WhileHiddenAsync(() => RegionPicker.PickAsync(
        "Drag a box around the part of the screen to search. Smaller areas are faster and find fewer wrong things.   Esc or right-click to cancel."));

    public Task<CapturedTemplate?> CaptureTemplateAsync() => WhileHiddenAsync(async () =>
    {
        var picked = await RegionPicker.PickAsync(
            "Drag a box tightly around the button or icon to find. Include only the part that always looks the same.   Esc or right-click to cancel.");
        if (picked is not { } rect) return null;

        await Task.Delay(OverlayFadeMs);
        var frame = _capture.Capture(rect);
        return (CapturedTemplate?)new CapturedTemplate(frame.Bounds, frame.Image);
    });

    public Task<PickedColor?> PickColorAsync() => WhileHiddenAsync(async () =>
    {
        var picked = await RegionPicker.PickAsync(
            "Click the color to look for (for example the moving marker or a button).   Esc or right-click to cancel.", pickPoint: true);
        if (picked is not { } point) return null;

        await Task.Delay(OverlayFadeMs);
        var frame = _capture.Capture(point);
        return (PickedColor?)new PickedColor(point.X, point.Y, frame.Image.GetPixel(0, 0));
    });

    public async Task<string> TestImageAsync(FindImageStep step)
    {
        if (!TemplateCodec.TryDecode(step.TemplateData, step.TemplateWidth, step.TemplateHeight, out var decoded, out var error))
            return error ?? "Capture the image first.";
        var template = decoded!;

        var region = step.SearchFullScreen ? _capture.GetVirtualScreen() : step.Region;
        var where = step.SearchFullScreen ? "the whole screen" : $"area {region}";
        ClosePreview();

        ScreenFrame frame;
        TimeSpan captureTime;
        try
        {
            (frame, captureTime) = await WhileHiddenAsync(() => Task.FromResult(TimedCapture(region)));
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        if (frame.Image.Width < template.Width || frame.Image.Height < template.Height)
            return $"The search area on screen ({frame.Bounds.Width}×{frame.Bounds.Height}) is smaller than the image ({template.Width}×{template.Height}). Make the area bigger.";

        var options = new TemplateMatchOptions { MinScore = step.MinSimilarityPercent / 100.0, IgnoreColor = step.IgnoreColor, MaxResults = 10 };
        var watch = Stopwatch.StartNew();
        var result = await Task.Run(() => TemplateMatcher.Find(frame.Image, template, options));
        var searchTime = watch.Elapsed;

        var picture = frame.Image.Clone();
        for (var i = result.Matches.Count - 1; i >= 0; i--)
            picture.DrawRectangle(result.Matches[i].Bounds, i == 0 ? MatchColor : OtherColor, i == 0 ? 3 : 2);
        var saved = TrySave(picture, "last-image-test.png");

        var timing = $"Capture took {captureTime.TotalMilliseconds:0} ms, search {searchTime.TotalMilliseconds:0} ms{(result.Scale > 1 ? $" (coarse search at 1/{result.Scale} size, then exact)" : "")}.";
        string text;
        if (result.Best is { } best)
        {
            var bounds = frame.ToScreen(best.Bounds);
            var more = result.Matches.Count > 1
                ? $" {result.Matches.Count} matches (green = the one a macro uses, orange = others): " +
                  string.Join(", ", result.Matches.Skip(1).Select(m => $"{m.Score:P0} at ({frame.ToScreen(m.Bounds).CenterX}, {frame.ToScreen(m.Bounds).CenterY})")) + "."
                : "";
            text = $"Found at ({bounds.CenterX}, {bounds.CenterY}), {best.Score:P0} match (needs {step.MinSimilarityPercent}%). Searched {where}.{more}";
        }
        else
        {
            text = $"Not found in {where}. The best similarity was {result.BestScore:P0} (needs {step.MinSimilarityPercent}%).";
            if (result.RejectedForColor > 0)
                text += $" {result.RejectedForColor} spot(s) had the right shape but a different color; turn on \"Ignore color\" if the color changes.";
            if (result.BestScore >= 0.6 && result.RejectedForColor == 0)
                text += " If the right thing is on screen, lower the match accuracy a little (or capture the image again).";
        }

        text += " " + timing;
        if (saved is not null) text += $"\nPicture saved to: {saved}";
        ShowPreview("Find image – test result", picture, text);
        return text;
    }

    public async Task<string> TestColorAsync(FindColorStep step)
    {
        if (!RgbColor.TryParse(step.Color, out var color)) return "Enter the color as #RRGGBB or pick it from the screen first.";

        var region = step.SearchFullScreen ? _capture.GetVirtualScreen() : step.Region;
        var where = step.SearchFullScreen ? "the whole screen" : $"area {region}";
        ClosePreview();

        ScreenFrame frame;
        TimeSpan captureTime;
        try
        {
            (frame, captureTime) = await WhileHiddenAsync(() => Task.FromResult(TimedCapture(region)));
        }
        catch (Exception ex)
        {
            return $"Test failed: {ex.Message}";
        }

        var watch = Stopwatch.StartNew();
        var result = await Task.Run(() => ColorFinder.Find(frame.Image, color, step.Tolerance, step.MinPixels, maxResults: 10));
        var searchTime = watch.Elapsed;

        var picture = frame.Image.Clone();
        for (var i = result.Blobs.Count - 1; i >= 0; i--)
            picture.DrawRectangle(Grow(result.Blobs[i].Bounds, 2), i == 0 ? MatchColor : OtherColor, i == 0 ? 3 : 2);
        var saved = TrySave(picture, "last-color-test.png");

        string text;
        if (result.Largest is { } blob)
        {
            var x = frame.Bounds.X + (int)Math.Round(blob.CenterX);
            var y = frame.Bounds.Y + (int)Math.Round(blob.CenterY);
            text = $"Found color {color} at ({x}, {y}): {blob.PixelCount:N0} pixels (green box). Searched {where}.";
            if (result.Blobs.Count > 1)
                text += $" There are {result.Blobs.Count} separate patches of this color (orange); a macro uses the biggest. If that's wrong, pick a smaller area, lower the tolerance or raise the minimum size.";
        }
        else
        {
            text = result.MatchingPixels > 0
                ? $"Not found: {result.MatchingPixels:N0} pixel(s) of that color are in {where}, but no patch has at least {step.MinPixels} pixels."
                : $"Not found: no pixels of {color} (± {step.Tolerance}) in {where}.";
        }

        text += $" Capture took {captureTime.TotalMilliseconds:0} ms, search {searchTime.TotalMilliseconds:0} ms.";
        if (saved is not null) text += $"\nPicture saved to: {saved}";
        ShowPreview("Find color – test result", picture, text);
        return text;
    }

    public async Task<string> TrackColorAsync(FindColorStep step, TimeSpan duration)
    {
        if (!RgbColor.TryParse(step.Color, out var color)) return "Enter the color as #RRGGBB or pick it from the screen first.";
        if (step.SearchFullScreen)
            return "Tracking needs a search area: turn off \"Search the whole screen\" and pick the bar or track the marker moves along.";

        var region = step.Region;
        var orientation = region.Width >= region.Height ? BarOrientation.Horizontal : BarOrientation.Vertical;
        ClosePreview();

        TrackSummary summary;
        try
        {
            summary = await WhileHiddenAsync(() => Task.Run(() => TrackAsync(region, color, step, orientation, duration)));
        }
        catch (Exception ex)
        {
            return $"Tracking failed: {ex.Message}";
        }

        var axis = orientation == BarOrientation.Horizontal ? "left (0 %) to right (100 %)" : "top (0 %) to bottom (100 %)";
        var text = $"Tracked for {summary.Elapsed.TotalSeconds:0.0} s: {summary.Frames} frames ({summary.Frames / Math.Max(0.001, summary.Elapsed.TotalSeconds):0.0} per second). " +
                   $"Marker found in {summary.Found}, unclear (two similar patches) in {summary.Ambiguous}, missing in {summary.Lost}, glitches ignored {summary.IgnoredJumps}. ";
        text += summary.Min is { } min && summary.Max is { } max
            ? $"Position along the area, {axis}: {min:0}% to {max:0}%. Samples: {string.Join(", ", summary.Samples.Select(p => p is { } v ? $"{v:0}" : "–"))}. "
            : "The marker was never found: pick its color again or raise the tolerance. ";
        text += $"Per frame: capture {summary.CaptureMs:0.0} ms, search {summary.SearchMs:0.0} ms on average.";
        text += " Real games add their own delay before the screen updates, so a feedback loop reacts a bit later than this.";

        if (summary.LastFrame is not null) ShowPreview("Find color – tracking (last frame)", summary.LastFrame, text);
        return text;
    }

    public ImageSource ToImageSource(BgraImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record TrackSummary(TimeSpan Elapsed, int Frames, int Found, int Ambiguous, int Lost, int IgnoredJumps,
        double? Min, double? Max, IReadOnlyList<double?> Samples, double CaptureMs, double SearchMs, BgraImage? LastFrame);

    private async Task<TrackSummary> TrackAsync(ScreenRect region, RgbColor color, FindColorStep step, BarOrientation orientation, TimeSpan duration)
    {
        var tracker = new MarkerTracker(color, step.Tolerance, orientation, Math.Max(1, step.MinPixels));
        var watch = Stopwatch.StartNew();
        int frames = 0, found = 0, ambiguous = 0, lost = 0, ignored = 0;
        double? min = null, max = null;
        var samples = new List<double?>();
        var nextSample = TimeSpan.Zero;
        double captureMs = 0, searchMs = 0;
        BgraImage? last = null;
        MarkerReading? lastReading = null;

        while (watch.Elapsed < duration)
        {
            var t0 = watch.Elapsed;
            var frame = _capture.Capture(region);
            var t1 = watch.Elapsed;
            var reading = tracker.Update(frame.Image);
            var t2 = watch.Elapsed;

            frames++;
            captureMs += (t1 - t0).TotalMilliseconds;
            searchMs += (t2 - t1).TotalMilliseconds;
            switch (reading.Status)
            {
                case MarkerStatus.Found: found++; break;
                case MarkerStatus.Ambiguous: ambiguous++; break;
                case MarkerStatus.Lost: lost++; break;
                case MarkerStatus.IgnoredJump: ignored++; break;
            }

            if (reading.Status == MarkerStatus.Found && reading.PositionPercent is { } position)
            {
                min = min is null ? position : Math.Min(min.Value, position);
                max = max is null ? position : Math.Max(max.Value, position);
            }

            // One sample every 0.25 s for the summary (so the user can see the movement).
            if (t0 >= nextSample && samples.Count < 24)
            {
                samples.Add(reading.Status == MarkerStatus.Found ? reading.PositionPercent : null);
                nextSample += TimeSpan.FromMilliseconds(250);
            }

            last = frame.Image;
            lastReading = reading;

            var wait = TrackFrameInterval - (watch.Elapsed - t0);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }

        if (last is not null && lastReading is { Bounds: { } box } final)
        {
            last = last.Clone();
            last.DrawRectangle(Grow(box, 2), final.Status == MarkerStatus.Found ? MatchColor : OtherColor, 2);
        }

        var count = Math.Max(1, frames);
        return new TrackSummary(watch.Elapsed, frames, found, ambiguous, lost, ignored, min, max, samples,
            captureMs / count, searchMs / count, last);
    }

    private (ScreenFrame Frame, TimeSpan Time) TimedCapture(ScreenRect region)
    {
        var watch = Stopwatch.StartNew();
        var frame = _capture.Capture(region);
        return (frame, watch.Elapsed);
    }

    private static ScreenRect Grow(ScreenRect rect, int by) =>
        new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    /// <summary>Saves a PNG in the Logs folder (overwritten each test). Returns the path, or null if it couldn't be saved.</summary>
    private string? TrySave(BgraImage image, string fileName)
    {
        try
        {
            var path = Path.Combine(_logsDirectory, fileName);
            Directory.CreateDirectory(_logsDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)ToImageSource(image)));
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void ClosePreview()
    {
        _preview?.Close();
        _preview = null;
    }

    /// <summary>A simple window with the captured picture (matches marked) and the result text.</summary>
    private void ShowPreview(string title, BgraImage picture, string text)
    {
        ClosePreview();

        var close = new Button { Content = "Close", Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var info = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEB, 0xF5)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var image = new Image { Source = ToImageSource(picture), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(info);
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);

        var panel = new DockPanel { Margin = new Thickness(14) };
        panel.Children.Add(bottom);
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x0A, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x32, 0x4A)),
            BorderThickness = new Thickness(1),
            Child = image,
        });

        var window = new Window
        {
            Title = title,
            Width = 900,
            Height = 640,
            MinWidth = 400,
            MinHeight = 300,
            Owner = _mainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)),
            ShowInTaskbar = false,
            Content = panel,
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_preview, window)) _preview = null; };
        _preview = window;
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
            NativeMethods.SetExcludedFromCapture(_mainWindow, false);
            _mainWindow.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
            _mainWindow.Activate();
        }
    }
}
