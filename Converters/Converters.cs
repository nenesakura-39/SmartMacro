using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Validation;
using SmartMacro.ViewModels;

namespace SmartMacro.Converters;

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
