using System.Text.Json.Serialization;

namespace SmartMacro.Core.Models;

/// <summary>
/// Maps an application window title substring to a named Profile (v1.5 "Context").
/// When a matching window is in the foreground, macros tagged with <see cref="ProfileName"/> are active.
/// </summary>
public sealed class MacroProfileRule
{
    public const int MaxProfileNameLength = 60;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Profile name (e.g. "Work", "Excel", "Elden Ring").</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Window title substring that activates this profile (e.g. "Excel", "ELDEN RING").</summary>
    public string WindowTitleMatch { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public MacroProfileRule Clone() => new()
    {
        Id = Id,
        ProfileName = ProfileName,
        WindowTitleMatch = WindowTitleMatch,
        Enabled = Enabled,
    };
}

/// <summary>
/// Evaluates which macros and triggers are active for the current foreground window and profile filter (v1.5 "Context").
/// </summary>
public static class ProfileRouter
{
    public const string AllProfilesLabel = "All profiles";

    /// <summary>
    /// Determines the active profile name given explicit <paramref name="rules"/>, the library's profile names,
    /// and the current <paramref name="foregroundWindowTitle"/>. Returns null when all profiles should be active.
    /// </summary>
    public static string? ResolveActiveProfile(
        IReadOnlyList<MacroProfileRule>? rules,
        IReadOnlyList<Macro> library,
        string? foregroundWindowTitle,
        string? manualProfileFilter,
        bool autoSwitchByWindow)
    {
        // 1. If the user explicitly selected a profile in the UI (not "All profiles"), honor it.
        if (!string.IsNullOrWhiteSpace(manualProfileFilter)
            && !string.Equals(manualProfileFilter.Trim(), AllProfilesLabel, StringComparison.OrdinalIgnoreCase))
        {
            return manualProfileFilter.Trim();
        }

        if (!autoSwitchByWindow || string.IsNullOrWhiteSpace(foregroundWindowTitle))
            return null;

        // 2. Check explicit window-title -> profile rules first.
        if (rules is { Count: > 0 })
        {
            foreach (var rule in rules)
            {
                if (rule is not { Enabled: true }) continue;
                if (string.IsNullOrWhiteSpace(rule.ProfileName) || string.IsNullOrWhiteSpace(rule.WindowTitleMatch))
                    continue;
                if (foregroundWindowTitle.Contains(rule.WindowTitleMatch.Trim(), StringComparison.OrdinalIgnoreCase))
                    return rule.ProfileName.Trim();
            }
        }

        // 3. Otherwise, if any macro in the library has a ProfileName that appears in the foreground window title
        //    (e.g. ProfileName = "Excel" and foreground window = "Book1 - Excel"), activate that profile.
        if (library is { Count: > 0 })
        {
            foreach (var macro in library)
            {
                var p = macro?.ProfileName?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                if (foregroundWindowTitle.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="macro"/> should have its triggers armed given the current <paramref name="activeProfile"/>
    /// and <paramref name="foregroundWindowTitle"/>.
    /// - Global macros (no <see cref="Macro.ProfileName"/>) are always armed unless a manual profile filter is forced.
    /// - Profiled macros (with <see cref="Macro.ProfileName"/>) are armed when:
    ///   (a) <paramref name="activeProfile"/> matches their <see cref="Macro.ProfileName"/>, OR
    ///   (b) <paramref name="autoSwitchByWindow"/> is off and no manual profile filter is set, OR
    ///   (c) <paramref name="foregroundWindowTitle"/> contains their <see cref="Macro.ProfileName"/> or <see cref="Macro.TargetWindowTitle"/>.
    /// </summary>
    public static bool IsMacroArmedInContext(
        Macro macro,
        string? activeProfile,
        string? foregroundWindowTitle,
        bool autoSwitchByWindow,
        bool isManualProfileSelected = false)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var macroProfile = macro.ProfileName?.Trim();

        // If the user manually picked a specific profile filter, only macros in that profile (or global ones if not strict) match.
        if (isManualProfileSelected && !string.IsNullOrWhiteSpace(activeProfile))
        {
            return string.IsNullOrEmpty(macroProfile)
                || string.Equals(macroProfile, activeProfile, StringComparison.OrdinalIgnoreCase);
        }

        // Global macro (no ProfileName): always armed.
        if (string.IsNullOrEmpty(macroProfile))
            return true;

        // If auto-switch is disabled, all profiled macros stay armed too.
        if (!autoSwitchByWindow)
            return true;

        // If activeProfile was resolved and matches this macro's profile, it's armed!
        if (!string.IsNullOrWhiteSpace(activeProfile)
            && string.Equals(macroProfile, activeProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also check if the foreground window matches the macro's TargetWindowTitle or ProfileName directly.
        if (!string.IsNullOrWhiteSpace(foregroundWindowTitle))
        {
            if (foregroundWindowTitle.Contains(macroProfile, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(macro.TargetWindowTitle)
                && foregroundWindowTitle.Contains(macro.TargetWindowTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Filters <paramref name="library"/> to only the macros armed in the current context.</summary>
    public static IReadOnlyList<Macro> FilterArmedMacros(
        IReadOnlyList<Macro> library,
        string? activeProfile,
        string? foregroundWindowTitle,
        bool autoSwitchByWindow,
        bool isManualProfileSelected = false)
    {
        ArgumentNullException.ThrowIfNull(library);
        var list = new List<Macro>(library.Count);
        foreach (var macro in library)
        {
            if (macro is not null && IsMacroArmedInContext(macro, activeProfile, foregroundWindowTitle, autoSwitchByWindow, isManualProfileSelected))
                list.Add(macro);
        }
        return list;
    }
}
