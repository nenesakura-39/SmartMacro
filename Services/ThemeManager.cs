using System.Windows;
using System.Windows.Media;
using SmartMacro.Core.Models;

namespace SmartMacro.Services;

/// <summary>
/// Live runtime theme, accent, density, and motion manager for SmartMacro Studio X.
/// Updates <see cref="Application.Resources"/> keys watched via <c>{DynamicResource}</c> so switching
/// between Obsidian Neon and Midnight Blue (or changing the primary accent / density) applies immediately without restarting.
/// </summary>
public static class ThemeManager
{
    private static bool _reduceMotionSetting;

    /// <summary>
    /// True when Windows client-area animations are enabled AND the user hasn't turned on Reduce Motion in Settings.
    /// </summary>
    public static bool AreAnimationsEnabled => SystemParameters.ClientAreaAnimation && !_reduceMotionSetting;

    public static StudioTheme CurrentTheme { get; private set; } = StudioTheme.ObsidianNeon;
    public static StudioAccent CurrentAccent { get; private set; } = StudioAccent.NeonBrand;
    public static StudioDensity CurrentDensity { get; private set; } = StudioDensity.Comfortable;

    public static void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _reduceMotionSetting = settings.ReduceMotion;
        CurrentTheme = settings.Theme;
        CurrentAccent = settings.Accent;
        CurrentDensity = settings.Density;

        var res = Application.Current?.Resources;
        if (res is null) return;

        ApplySurfacePalette(res, settings.Theme);
        ApplyAccentPalette(res, settings.Accent);
        ApplyDensityTokens(res, settings.Density);
    }

    private static void ApplySurfacePalette(ResourceDictionary res, StudioTheme theme)
    {
        if (theme == StudioTheme.MidnightBlue)
        {
            SetBrush(res, "BackgroundBrush", "#060B19");
            SetBrush(res, "WindowBackdropBrush", "#D8060B19");
            SetBrush(res, "TitleBarBrush", "#CC050914");
            SetBrush(res, "SidebarBrush", "#081024");
            SetBrush(res, "SurfaceBrush", "#0D1730");
            SetBrush(res, "SurfaceAltBrush", "#132142");
            SetBrush(res, "InputBrush", "#17284E");
            SetBrush(res, "InputBorderBrush", "#2B4270");
            SetBrush(res, "CharcoalBrush", "#111E3C");
            SetBrush(res, "BorderBrush", "#1C2E52");
            SetBrush(res, "HoverBrush", "#1A2C52");
            SetBrush(res, "PressedBrush", "#233A6B");
            SetBrush(res, "StepsCanvasBrush", "#081126");
            SetBrush(res, "StepCardBrush", "#101E3D");
            SetBrush(res, "StepCardBorderBrush", "#213663");
            SetBrush(res, "SectionCardBrush", "#0E1B36");
            SetBrush(res, "DotGridBrush", "#16274B");
            SetBrush(res, "IndentGuideBrush", "#233863");
        }
        else // ObsidianNeon (default)
        {
            SetBrush(res, "BackgroundBrush", "#0B0F17");
            SetBrush(res, "WindowBackdropBrush", "#D80B0F17");
            SetBrush(res, "TitleBarBrush", "#CC090D14");
            SetBrush(res, "SidebarBrush", "#0D121C");
            SetBrush(res, "SurfaceBrush", "#111827");
            SetBrush(res, "SurfaceAltBrush", "#172033");
            SetBrush(res, "InputBrush", "#1E293B");
            SetBrush(res, "InputBorderBrush", "#334155");
            SetBrush(res, "CharcoalBrush", "#1A2232");
            SetBrush(res, "BorderBrush", "#1F2937");
            SetBrush(res, "HoverBrush", "#1F2A3D");
            SetBrush(res, "PressedBrush", "#27354D");
            SetBrush(res, "StepsCanvasBrush", "#0D131F");
            SetBrush(res, "StepCardBrush", "#161F30");
            SetBrush(res, "StepCardBorderBrush", "#243045");
            SetBrush(res, "SectionCardBrush", "#151E2E");
            SetBrush(res, "DotGridBrush", "#1C2638");
            SetBrush(res, "IndentGuideBrush", "#2A3547");
        }
    }

    private static void ApplyAccentPalette(ResourceDictionary res, StudioAccent accent)
    {
        var (main, hover, pressed, soft, cardTint) = accent switch
        {
            StudioAccent.ElectricBlue => ("#3B82F6", "#60A5FA", "#2563EB", "#263B82F6", "#1A2B48"),
            StudioAccent.CyberViolet => ("#8B5CF6", "#A78BFA", "#7C3AED", "#268B5CF6", "#231D45"),
            StudioAccent.EmeraldPulse => ("#10B981", "#34D399", "#059669", "#2610B981", "#13322F"),
            StudioAccent.RoseQuartz => ("#F43F5E", "#FB7185", "#E11D48", "#26F43F5E", "#331829"),
            _ => ("#22D3EE", "#67E8F9", "#06B6D4", "#2622D3EE", "#142B3E"), // NeonBrand (cyan-indigo core)
        };

        SetBrush(res, "AccentBrush", main);
        SetBrush(res, "AccentHoverBrush", hover);
        SetBrush(res, "AccentPressedBrush", pressed);
        SetBrush(res, "AccentSoftBrush", soft);
        SetBrush(res, "SelectedTintBrush", soft);
        SetBrush(res, "SelectedCardBrush", cardTint);
        SetBrush(res, "PlayBrush", pressed);
        SetBrush(res, "PlayHoverBrush", main);
    }

    private static void ApplyDensityTokens(ResourceDictionary res, StudioDensity density)
    {
        if (density == StudioDensity.Compact)
        {
            res["PadCard"] = new Thickness(11, 8, 11, 8);
            res["StepRowPadding"] = new Thickness(8, 5, 10, 5);
        }
        else
        {
            res["PadCard"] = new Thickness(14, 12, 14, 12);
            res["StepRowPadding"] = new Thickness(10, 8, 12, 8);
        }
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
