using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.Converters;

/// <summary>
/// Zero-lag thumbnails: a bounded (LRU, <see cref="Capacity"/> entries), thread-safe cache of FROZEN WPF bitmaps, keyed by
/// the picture text stored in the macro (Base64 PNG of a click snapshot, or a Find image template). Step cards are
/// virtualized and recycled, and Undo / Redo / reloading rebuilds the step view models, so without this every scroll or
/// edit decoded the same pictures again. Frozen bitmaps can be shared by any number of Image elements and threads.
/// Unreadable pictures are remembered as "none" (null) too.
/// </summary>
public static class FrozenBitmapCache
{
    /// <summary>How many decoded thumbnails are kept.</summary>
    public const int Capacity = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource? Bitmap)>> Map = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Key, BitmapSource? Bitmap)> Lru = new();
    private static long _decodes;

    /// <summary>Decodes done so far (cache misses), for diagnostics.</summary>
    public static long Decodes => Interlocked.Read(ref _decodes);

    /// <summary>How many thumbnails are cached right now.</summary>
    public static int Count
    {
        get { lock (Gate) return Map.Count; }
    }

    /// <summary>A Base64 PNG (Smart Click Snapshot) as a frozen bitmap; null when empty or unreadable.</summary>
    public static BitmapSource? FromPngBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        return GetOrAdd("png:" + base64, () =>
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            return image;
        });
    }

    /// <summary>A stored Find image template (<see cref="Core.Vision.TemplateCodec"/> text) as a frozen bitmap; null when unreadable.</summary>
    public static BitmapSource? FromTemplate(string? data, int width, int height)
    {
        if (string.IsNullOrEmpty(data)) return null;
        return GetOrAdd($"tpl:{width}x{height}:{data}", () =>
            Core.Vision.TemplateCache.TryGetOrDecode(data, width, height, out var pixels, out _) && pixels is not null
                ? BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgr32, null, pixels.Pixels, pixels.Stride)
                : null);
    }

    /// <summary>Forgets every cached thumbnail.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Lru.Clear();
        }
    }

    private static BitmapSource? GetOrAdd(string key, Func<BitmapSource?> create)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        Interlocked.Increment(ref _decodes);
        BitmapSource? bitmap;
        try
        {
            bitmap = create();
            if (bitmap is { CanFreeze: true }) bitmap.Freeze();
            else if (bitmap is { IsFrozen: false }) bitmap = null; // can't be shared safely
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or IOException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            bitmap = null;
        }

        lock (Gate)
        {
            if (Map.TryGetValue(key, out var raced)) return raced.Value.Bitmap; // another thread was faster: use its copy
            Map[key] = Lru.AddFirst((key, bitmap));
            while (Map.Count > Capacity && Lru.Last is { } oldest)
            {
                Lru.RemoveLast();
                Map.Remove(oldest.Value.Key);
            }
        }

        return bitmap;
    }
}

/// <summary>
/// Base64 PNG text (a click's Smart Click Snapshot) → a cached, frozen <see cref="BitmapSource"/> (see
/// <see cref="FrozenBitmapCache"/>). Null / unreadable → no picture.
/// </summary>
public sealed class Base64ToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        FrozenBitmapCache.FromPngBase64(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed. Pass "Invert" as the parameter to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is not null (or "Invert": visible when null).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string s || s.Length > 0);
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Used by the sidebar radio buttons: IsChecked = (CurrentPage == parameter).</summary>
public sealed class PageToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppPage page && parameter is string name && Enum.TryParse<AppPage>(name, out var target) && page == target;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Enum.TryParse<AppPage>(name, out var target)
            ? target
            : Binding.DoNothing;
}

/// <summary>Maps status/log/validation levels to theme brushes.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusKind.Success or AppLogLevel.Success => "SuccessBrush",
            StatusKind.Warning or AppLogLevel.Warning or IssueSeverity.Warning => "WarningBrush",
            StatusKind.Error or AppLogLevel.Error or IssueSeverity.Error => "ErrorBrush",
            AppLogLevel.Debug => "MutedTextBrush",
            _ => "AccentBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Icon glyph (Segoe Fluent Icons / MDL2) for a level.</summary>
public sealed class LevelToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusKind.Success or AppLogLevel.Success => "",
        StatusKind.Warning or AppLogLevel.Warning or IssueSeverity.Warning => "",
        StatusKind.Error or AppLogLevel.Error or IssueSeverity.Error => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// ============ SmartMacro Studio (editor redesign) ============

/// <summary>The colour family a step belongs to in the editor (toolbox tiles, step cards, inspector header).</summary>
public static class StepCategories
{
    public const string Input = "Input";
    public const string Timing = "Timing";
    public const string Vision = "Vision";
    public const string Logic = "Logic";
    public const string Feedback = "Feedback";
    public const string Recorded = "Recorded";

    public static string Of(StepKind kind) => kind switch
    {
        StepKind.Delay => Timing,
        StepKind.MouseMove or StepKind.MouseClick or StepKind.MouseScroll or StepKind.KeyPress or StepKind.TypeText => Input,
        StepKind.FindText or StepKind.FindImage or StepKind.FindColor => Vision,
        StepKind.If or StepKind.Else or StepKind.EndIf or StepKind.Repeat or StepKind.EndRepeat => Logic,
        StepKind.FeedbackLoop => Feedback,
        _ => Recorded,
    };

    /// <summary>Accepts a StepKind, a StepTypeInfo or a StepViewModel.</summary>
    public static StepKind? KindOf(object? value) => value switch
    {
        StepKind k => k,
        StepTypeInfo t => t.Kind,
        StepViewModel s => s.Kind,
        _ => null,
    };
}

/// <summary>
/// Step kind → category brush. Parameter: "Accent" (default, the bright colour), "Badge" (solid dark badge
/// background) or "Soft" (15 % tint). Looks up "{Category}{Parameter}Brush" from Themes/Colors.xaml.
/// </summary>
public sealed class StepCategoryBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = StepCategories.KindOf(value);
        var category = kind is null ? StepCategories.Recorded : StepCategories.Of(kind.Value);
        var part = (parameter as string) is { Length: > 0 } p ? p : "Accent";
        return Application.Current?.TryFindResource($"{category}{part}Brush") as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Filters a list of toolbox entries (StepTypeInfo) by category. Parameter: "Input" (input + Delay),
/// "Vision" (Find steps + Feedback loop) or "Logic". Anything else returns the list unchanged.
/// </summary>
public sealed class StepTypeFilterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<StepTypeInfo> types) return value ?? Binding.DoNothing;
        Func<string, bool>? keep = (parameter as string) switch
        {
            "Input" => c => c is StepCategories.Input or StepCategories.Timing,
            "Vision" => c => c is StepCategories.Vision or StepCategories.Feedback,
            "Logic" => c => c is StepCategories.Logic,
            _ => null,
        };
        return keep is null ? value : types.Where(t => keep(StepCategories.Of(t.Kind))).ToList();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Nesting level (StepViewModel.IndentLevel) → left indentation. Returns a Thickness for Margin targets and a
/// double for Width targets. Parameter = pixels per level (default 24).
/// </summary>
public sealed class IndentLevelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? Math.Max(0, i) : 0;
        var perLevel = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 24;
        var pixels = level * perLevel;
        return targetType == typeof(Thickness) ? new Thickness(pixels, 0, 0, 0) : pixels;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → first width, false → second width. Parameter "175|60" (the sidebar's expanded / rail widths).</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "175|60").Split('|');
        var text = value is true ? parts[0] : parts.Length > 1 ? parts[1] : parts[0];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            ? new GridLength(px)
            : GridLength.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Text → UPPER CASE (for section headers whose text comes from a view model).</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.ToUpper(culture) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A number → Visible when it is greater than 0 ("Invert": visible when 0).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value is int i && i > 0;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Picks a template by the step's type, for small type-specific extras (the inline preview on a step card, the
/// pinned "Test detection now" bar in the inspector). Order: the exact kind's template, then
/// <see cref="ScreenSearchTemplate"/> for Find text / image / color, then <see cref="DefaultTemplate"/>
/// (an empty template, so nothing – not even the object's name – is shown).
/// </summary>
public sealed class StepKindTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? FindImageTemplate { get; set; }
    public DataTemplate? FindColorTemplate { get; set; }
    public DataTemplate? FindTextTemplate { get; set; }
    public DataTemplate? FeedbackLoopTemplate { get; set; }
    public DataTemplate? ScreenSearchTemplate { get; set; }

    /// <summary>Mouse click steps recorded with a Smart Click Snapshot (shows the little "what was clicked" picture).</summary>
    public DataTemplate? ClickSnapshotTemplate { get; set; }

    public DataTemplate? DefaultTemplate { get; set; }

    public override DataTemplate SelectTemplate(object? item, DependencyObject container)
    {
        var specific = StepCategories.KindOf(item) switch
        {
            StepKind.FindImage => FindImageTemplate,
            StepKind.FindColor => FindColorTemplate,
            StepKind.FindText => FindTextTemplate,
            StepKind.FeedbackLoop => FeedbackLoopTemplate,
            StepKind.MouseClick when item is SmartMacro.ViewModels.Steps.MouseClickStepViewModel { HasTargetThumbnail: true } => ClickSnapshotTemplate,
            _ => null,
        };
        if (specific is not null) return specific;
        if (item is SmartMacro.ViewModels.Steps.ScreenSearchStepViewModel && ScreenSearchTemplate is not null) return ScreenSearchTemplate;
        return DefaultTemplate ?? EmptyTemplate;
    }

    private static readonly DataTemplate EmptyTemplate = CreateEmpty();

    private static DataTemplate CreateEmpty()
    {
        var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.Border)) };
        template.Seal();
        return template;
    }
}

// ============ Logs page: colour-coded level pills ============

/// <summary>
/// A log entry → its pill. Parameter "Label" (default) gives the text (INFO / MATCH / DONE / WARN / ERROR), "Brush" the
/// bright colour and "Soft" the tinted background (Info blue, Match violet, Done green, Warn amber, Error red).
/// </summary>
public sealed class LogPillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LogEntry entry) return Binding.DoNothing;
        var category = LogViewModel.CategoryOf(entry);
        var part = parameter as string ?? "Label";
        if (part == "Label")
        {
            return category switch
            {
                LogCategory.Match => "MATCH",
                LogCategory.Done => "DONE",
                LogCategory.Warn => "WARN",
                LogCategory.Error => "ERROR",
                _ => "INFO",
            };
        }

        var soft = part == "Soft";
        var key = category switch
        {
            LogCategory.Match => soft ? "VisionSoftBrush" : "VisionAccentBrush",
            LogCategory.Done => soft ? "SuccessSoftBrush" : "SuccessBrush",
            LogCategory.Warn => soft ? "WarningSoftBrush" : "WarningBrush",
            LogCategory.Error => soft ? "ErrorSoftBrush" : "ErrorBrush",
            _ => soft ? "AccentSoftBrush" : "AccentBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Two-way "this radio button = this string value" (the Logs page level filter pills).</summary>
public sealed class StringMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string text ? text : Binding.DoNothing;
}
