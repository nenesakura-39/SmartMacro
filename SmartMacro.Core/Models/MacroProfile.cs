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

    /// <summary>Optional process name substring without ".exe" (e.g. "Code", "chrome", "EXCEL").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessNameMatch { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Matches(string? foregroundWindowTitle, string? foregroundProcessName = null) =>
        ProfileRouter.MatchesWindowAndProcess(
            foregroundWindowTitle,
            foregroundProcessName,
            WindowTitleMatch,
            ProcessNameMatch);

    public MacroProfileRule Clone() => new()
    {
        Id = Id,
        ProfileName = ProfileName,
        WindowTitleMatch = WindowTitleMatch,
        ProcessNameMatch = ProcessNameMatch,
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
        bool autoSwitchByWindow,
        string? foregroundProcessName = null)
    {
        // 1. If the user explicitly selected a profile in the UI (not "All profiles"), honor it.
        if (!string.IsNullOrWhiteSpace(manualProfileFilter)
            && !string.Equals(manualProfileFilter.Trim(), AllProfilesLabel, StringComparison.OrdinalIgnoreCase))
        {
            return manualProfileFilter.Trim();
        }

        if (!autoSwitchByWindow
            || (string.IsNullOrWhiteSpace(foregroundWindowTitle) && string.IsNullOrWhiteSpace(foregroundProcessName)))
        {
            return null;
        }

        // 2. Check explicit window-title / process-name -> profile rules first.
        if (rules is { Count: > 0 })
        {
            foreach (var rule in rules)
            {
                if (rule is not { Enabled: true } || string.IsNullOrWhiteSpace(rule.ProfileName))
                    continue;
                if (rule.Matches(foregroundWindowTitle, foregroundProcessName))
                    return rule.ProfileName.Trim();
            }
        }

        // 3. Otherwise, if any macro in the library has a ProfileName matching the foreground window title or process,
        //    (e.g. ProfileName = "Excel" and foreground = "Book1 - Excel" / "EXCEL"), activate that profile.
        if (library is { Count: > 0 })
        {
            foreach (var macro in library)
            {
                var p = macro?.ProfileName?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                if ((!string.IsNullOrWhiteSpace(foregroundWindowTitle) && foregroundWindowTitle.Contains(p, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(foregroundProcessName) && foregroundProcessName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return p;
                }
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
        bool isManualProfileSelected = false,
        string? foregroundProcessName = null)
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

        // Also check if the foreground window title or process matches the macro's TargetWindowTitle or ProfileName directly.
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

        if (!string.IsNullOrWhiteSpace(foregroundProcessName)
            && foregroundProcessName.Contains(macroProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>Filters <paramref name="library"/> to only the macros armed in the current context.</summary>
    public static IReadOnlyList<Macro> FilterArmedMacros(
        IReadOnlyList<Macro> library,
        string? activeProfile,
        string? foregroundWindowTitle,
        bool autoSwitchByWindow,
        bool isManualProfileSelected = false,
        string? foregroundProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        var list = new List<Macro>(library.Count);
        foreach (var macro in library)
        {
            if (macro is not null && IsMacroArmedInContext(
                    macro, activeProfile, foregroundWindowTitle, autoSwitchByWindow, isManualProfileSelected, foregroundProcessName))
            {
                list.Add(macro);
            }
        }
        return list;
    }

    public static bool HasWindowOrProcessFilter(string? titleFilter, string? processFilter) =>
        !string.IsNullOrWhiteSpace(titleFilter) || !string.IsNullOrWhiteSpace(processFilter);

    public static string NormalizeProcessFilter(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4].Trim()
            : trimmed;
    }

    /// <summary>
    /// Evaluates window-title and optional process-name substring filters against a candidate window. When both
    /// filters are specified, both must match; returns false when both filters are empty.
    /// </summary>
    public static bool MatchesWindowAndProcess(
        string? candidateTitle,
        string? candidateProcessName,
        string? titleFilter,
        string? processFilter)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(titleFilter);
        var hasProcess = !string.IsNullOrWhiteSpace(processFilter);
        if (!hasTitle && !hasProcess)
            return false;

        if (hasTitle && (string.IsNullOrWhiteSpace(candidateTitle)
            || !candidateTitle.Contains(titleFilter!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (hasProcess)
        {
            var normalizedFilter = NormalizeProcessFilter(processFilter!);
            if (string.IsNullOrEmpty(normalizedFilter)
                || string.IsNullOrWhiteSpace(candidateProcessName)
                || !NormalizeProcessFilter(candidateProcessName).Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Evaluates whether <paramref name="trigger"/>'s window/process filter allows firing right now.</summary>
    public static bool MatchesTriggerFilter(MacroTrigger trigger, string? activeWindowTitle, string? activeProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!trigger.OnlyWhenWindowActive)
            return true;
        if (!HasWindowOrProcessFilter(trigger.WindowTitle, trigger.ProcessNameMatch))
            return true;
        return MatchesWindowAndProcess(activeWindowTitle, activeProcessName, trigger.WindowTitle, trigger.ProcessNameMatch);
    }

    /// <summary>
    /// Computes the deduplicated set of active trigger hotkey registrations for <paramref name="armedLibrary"/> under
    /// the current foreground window title and process name. Triggers filtered out by
    /// <see cref="MacroTrigger.OnlyWhenWindowActive"/> are excluded so their shortcuts pass through cleanly to other apps.
    /// </summary>
    public static IReadOnlyList<ScopedHotkeyBinding> ResolveActiveHotkeyEntries(
        IReadOnlyList<Macro> armedLibrary,
        string? foregroundWindowTitle,
        string? foregroundProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(armedLibrary);
        var passThroughByBinding = new Dictionary<HotkeyBinding, bool>();
        var order = new List<HotkeyBinding>();

        foreach (var macro in armedLibrary)
        {
            if (macro?.Triggers is not { Count: > 0 } triggers)
                continue;

            foreach (var trigger in triggers)
            {
                if (!trigger.Enabled || trigger.Kind != TriggerKind.Hotkey || trigger.Hotkey is not { VirtualKey: > 0 } hk)
                    continue;
                if (!MatchesTriggerFilter(trigger, foregroundWindowTitle, foregroundProcessName))
                    continue;

                if (passThroughByBinding.TryGetValue(hk, out var existingPass))
                {
                    // If two active triggers share one hotkey in the same context and one requires blocking, prefer RegisterHotKey.
                    passThroughByBinding[hk] = existingPass && trigger.PassThroughKey;
                }
                else
                {
                    passThroughByBinding[hk] = trigger.PassThroughKey;
                    order.Add(hk);
                }
            }
        }

        var result = new List<ScopedHotkeyBinding>(order.Count);
        foreach (var hk in order)
            result.Add(new ScopedHotkeyBinding(hk, passThroughByBinding[hk]));
        return result;
    }

    /// <summary>Convenience projection returning only the distinct <see cref="HotkeyBinding"/> values active in context.</summary>
    public static IReadOnlyList<HotkeyBinding> ResolveActiveHotkeys(
        IReadOnlyList<Macro> armedLibrary,
        string? foregroundWindowTitle,
        string? foregroundProcessName = null)
    {
        var entries = ResolveActiveHotkeyEntries(armedLibrary, foregroundWindowTitle, foregroundProcessName);
        if (entries.Count == 0)
            return Array.Empty<HotkeyBinding>();
        var list = new List<HotkeyBinding>(entries.Count);
        foreach (var entry in entries)
            list.Add(entry.Binding);
        return list;
    }
}
