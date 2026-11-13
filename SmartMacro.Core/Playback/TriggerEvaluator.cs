using SmartMacro.Core.Models;
using SmartMacro.Core.Vision;

namespace SmartMacro.Core.Playback;

public enum TriggerDecisionKind
{
    None,
    StartMacro,
    StopPlaying,
    StopAndStartMacro,
    QueueMacro,
}

public sealed record TriggerDecision(
    TriggerDecisionKind Kind,
    Macro? Macro = null,
    MacroTrigger? Trigger = null,
    bool IsHoldMode = false,
    string Reason = "");

/// <summary>
/// Deterministic state machine and Run Governor for v1.4 "Reflex" triggers:
/// evaluates per-macro hotkeys (PressOnce, Toggle, HoldToRun, RepeatWhileHeld), schedules
/// (EveryNMinutes, DailyAtTime, OnAppStartup), and window events (WindowFocused, WindowOpened),
/// enforcing <see cref="MacroTrigger.CooldownMs"/>, <see cref="MacroTrigger.OnlyWhenWindowActive"/>,
/// and <see cref="TriggerConflictPolicy"/>.
/// </summary>
public sealed class TriggerEvaluator
{
    public const int MaxQueueSize = 8;

    private readonly Dictionary<Guid, DateTimeOffset> _lastFiredAt = new();
    private readonly Dictionary<Guid, DateTimeOffset> _scheduleBaseline = new();
    private readonly Dictionary<Guid, DateOnly> _dailyFiredDate = new();
    private readonly HashSet<Guid> _startupFired = new();
    private readonly HashSet<HotkeyBinding> _heldHotkeys = new();
    private readonly Queue<TriggerDecision> _runQueue = new();

    private Guid? _activeHoldTriggerId;
    private HotkeyBinding? _activeHoldBinding;
    private Guid? _activeHoldMacroId;

    private bool _hasWindowBaseline;
    private string? _previousActiveWindow;
    private string? _previousActiveProcess;
    private List<(string Title, string ProcessName)> _previousOpenWindows = new();

    public bool IsArmed { get; set; } = true;

    public int QueuedCount => _runQueue.Count;

    public void ResetState()
    {
        _lastFiredAt.Clear();
        _scheduleBaseline.Clear();
        _dailyFiredDate.Clear();
        _startupFired.Clear();
        _heldHotkeys.Clear();
        _runQueue.Clear();
        _activeHoldTriggerId = null;
        _activeHoldBinding = null;
        _activeHoldMacroId = null;
        _hasWindowBaseline = false;
        _previousActiveWindow = null;
        _previousActiveProcess = null;
        _previousOpenWindows.Clear();
    }

    /// <summary>Handles a key-down / shortcut-pressed event for a registered per-macro hotkey.</summary>
    public TriggerDecision EvaluateHotkeyDown(
        IReadOnlyList<Macro> library,
        HotkeyBinding binding,
        string? activeWindowTitle,
        Guid? currentlyPlayingMacroId,
        DateTimeOffset now,
        string? activeProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!IsArmed) return new TriggerDecision(TriggerDecisionKind.None);

        // Ignore OS key-repeat events while a HoldToRun / RepeatWhileHeld key is already held down.
        if (_heldHotkeys.Contains(binding) && _activeHoldBinding == binding)
            return new TriggerDecision(TriggerDecisionKind.None);

        _heldHotkeys.Add(binding);

        foreach (var macro in library)
        {
            if (macro?.Triggers is not { Count: > 0 } triggers) continue;
            foreach (var trigger in triggers)
            {
                if (trigger is not { Enabled: true, Kind: TriggerKind.Hotkey } || trigger.Hotkey != binding)
                    continue;

                if (!MatchesWindowFilter(trigger, activeWindowTitle, activeProcessName))
                    continue;

                // PressOnce / Toggle while this same macro is already playing stops it (even during cooldown).
                if (currentlyPlayingMacroId == macro.Id
                    && trigger.HotkeyMode is HotkeyTriggerMode.PressOnce or HotkeyTriggerMode.Toggle)
                {
                    ClearHoldTracking();
                    return new TriggerDecision(
                        TriggerDecisionKind.StopPlaying,
                        macro,
                        trigger,
                        Reason: $"⚡ Trigger \"{trigger.Name}\" ({binding}) stopped \"{macro.Name}\".");
                }

                if (IsOnCooldown(trigger, now))
                    continue;

                var isHold = trigger.HotkeyMode is HotkeyTriggerMode.HoldToRun or HotkeyTriggerMode.RepeatWhileHeld;
                var effectiveMacro = PrepareMacroForTrigger(macro, trigger);
                var decision = ResolveGovernance(
                    effectiveMacro,
                    trigger,
                    currentlyPlayingMacroId,
                    now,
                    isHold,
                    $"⚡ Shortcut {trigger.DescribeShort()} triggered \"{macro.Name}\".");

                if (decision.Kind is TriggerDecisionKind.StartMacro or TriggerDecisionKind.StopAndStartMacro && isHold)
                {
                    _activeHoldTriggerId = trigger.Id;
                    _activeHoldBinding = binding;
                    _activeHoldMacroId = macro.Id;
                }

                return decision;
            }
        }

        return new TriggerDecision(TriggerDecisionKind.None);
    }

    /// <summary>Handles releasing a per-macro hotkey: stops playback immediately if started in HoldToRun / RepeatWhileHeld mode.</summary>
    public TriggerDecision EvaluateHotkeyUp(HotkeyBinding binding, Guid? currentlyPlayingMacroId)
    {
        _heldHotkeys.Remove(binding);

        if (_activeHoldBinding == binding)
        {
            var holdMacroId = _activeHoldMacroId;
            ClearHoldTracking();
            if (currentlyPlayingMacroId is not null && currentlyPlayingMacroId == holdMacroId)
            {
                return new TriggerDecision(
                    TriggerDecisionKind.StopPlaying,
                    Reason: $"⚡ Released {binding} — stopping held macro.");
            }
        }

        return new TriggerDecision(TriggerDecisionKind.None);
    }

    /// <summary>
    /// Periodic background tick (e.g. every 500–1000 ms): checks Startup, EveryNMinutes, DailyAtTime,
    /// WindowFocused, and WindowOpened triggers across all macros in the library.
    /// </summary>
    public IReadOnlyList<TriggerDecision> EvaluateTick(
        IReadOnlyList<Macro> library,
        DateTimeOffset now,
        IReadOnlyList<string>? openWindowTitles,
        string? activeWindowTitle,
        Guid? currentlyPlayingMacroId)
    {
        IReadOnlyList<WindowInfo>? infos = null;
        if (openWindowTitles is { Count: > 0 })
        {
            var list = new List<WindowInfo>(openWindowTitles.Count);
            foreach (var title in openWindowTitles)
            {
                if (!string.IsNullOrWhiteSpace(title))
                    list.Add(new WindowInfo(0, title, default));
            }
            infos = list;
        }

        return EvaluateTick(library, now, infos, activeWindowTitle, activeProcessName: null, currentlyPlayingMacroId);
    }

    /// <summary>
    /// Full window-aware trigger evaluation supporting both window titles and process names for window focus/open
    /// events as well as schedule <see cref="MacroTrigger.OnlyWhenWindowActive"/> filters.
    /// </summary>
    public IReadOnlyList<TriggerDecision> EvaluateTick(
        IReadOnlyList<Macro> library,
        DateTimeOffset now,
        IReadOnlyList<WindowInfo>? openWindows,
        string? activeWindowTitle,
        string? activeProcessName,
        Guid? currentlyPlayingMacroId)
    {
        ArgumentNullException.ThrowIfNull(library);
        var results = new List<TriggerDecision>();
        var currentOpen = BuildOpenWindowSnapshot(openWindows);

        if (!IsArmed)
        {
            _previousActiveWindow = activeWindowTitle;
            _previousActiveProcess = activeProcessName;
            _previousOpenWindows = currentOpen;
            _hasWindowBaseline = true;
            return results;
        }

        var playingId = currentlyPlayingMacroId;

        foreach (var macro in library)
        {
            if (macro?.Triggers is not { Count: > 0 } triggers) continue;
            foreach (var trigger in triggers)
            {
                if (trigger is not { Enabled: true }) continue;

                switch (trigger.Kind)
                {
                    case TriggerKind.Schedule:
                    {
                        if (!MatchesWindowFilter(trigger, activeWindowTitle, activeProcessName))
                            break;

                        if (ShouldFireSchedule(trigger, now, out var reason))
                        {
                            var decision = ResolveGovernance(
                                PrepareMacroForTrigger(macro, trigger),
                                trigger,
                                playingId,
                                now,
                                isHold: false,
                                $"⏰ Schedule \"{trigger.Name}\" ({reason}) triggered \"{macro.Name}\".");
                            if (decision.Kind != TriggerDecisionKind.None)
                            {
                                results.Add(decision);
                                if (decision.Kind is TriggerDecisionKind.StartMacro or TriggerDecisionKind.StopAndStartMacro)
                                    playingId = macro.Id;
                            }
                        }
                        break;
                    }

                    case TriggerKind.WindowEvent:
                    {
                        if (ShouldFireWindowEvent(trigger, currentOpen, activeWindowTitle, activeProcessName, out var reason))
                        {
                            if (IsOnCooldown(trigger, now))
                                break;

                            var decision = ResolveGovernance(
                                PrepareMacroForTrigger(macro, trigger),
                                trigger,
                                playingId,
                                now,
                                isHold: false,
                                $"🖥 Window trigger \"{trigger.Name}\" ({reason}) triggered \"{macro.Name}\".");
                            if (decision.Kind != TriggerDecisionKind.None)
                            {
                                results.Add(decision);
                                if (decision.Kind is TriggerDecisionKind.StartMacro or TriggerDecisionKind.StopAndStartMacro)
                                    playingId = macro.Id;
                            }
                        }
                        break;
                    }
                }
            }
        }

        _previousActiveWindow = activeWindowTitle;
        _previousActiveProcess = activeProcessName;
        _previousOpenWindows = currentOpen;
        _hasWindowBaseline = true;
        return results;
    }

    public static bool RequiresOpenWindowsSnapshot(IReadOnlyList<Macro> library)
    {
        if (library is null || library.Count == 0) return false;
        foreach (var macro in library)
        {
            if (macro?.Triggers is not { Count: > 0 } triggers) continue;
            foreach (var trigger in triggers)
            {
                if (trigger is { Enabled: true, Kind: TriggerKind.WindowEvent, WindowEvent: WindowTriggerEvent.WindowOpened })
                    return true;
            }
        }

        return false;
    }

    public static bool HasScheduleTriggers(IReadOnlyList<Macro> library)
    {
        if (library is null || library.Count == 0) return false;
        foreach (var macro in library)
        {
            if (macro?.Triggers is not { Count: > 0 } triggers) continue;
            foreach (var trigger in triggers)
            {
                if (trigger is { Enabled: true, Kind: TriggerKind.Schedule })
                    return true;
            }
        }

        return false;
    }

    private static List<(string Title, string ProcessName)> BuildOpenWindowSnapshot(IReadOnlyList<WindowInfo>? openWindows)
    {
        if (openWindows is null || openWindows.Count == 0)
            return new List<(string Title, string ProcessName)>();

        var list = new List<(string Title, string ProcessName)>(openWindows.Count);
        foreach (var w in openWindows)
        {
            if (!string.IsNullOrWhiteSpace(w.Title) || !string.IsNullOrWhiteSpace(w.ProcessName))
                list.Add((w.Title ?? string.Empty, w.ProcessName ?? string.Empty));
        }

        return list;
    }

    /// <summary>Pops the next queued trigger decision when playback becomes idle.</summary>
    public TriggerDecision DequeueNext()
    {
        while (_runQueue.Count > 0)
        {
            var next = _runQueue.Dequeue();
            if (next.Macro is not null)
                return next with { Kind = TriggerDecisionKind.StartMacro };
        }

        return new TriggerDecision(TriggerDecisionKind.None);
    }

    public void ClearHoldTracking()
    {
        _activeHoldTriggerId = null;
        _activeHoldBinding = null;
        _activeHoldMacroId = null;
    }

    private bool ShouldFireSchedule(MacroTrigger trigger, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (IsOnCooldown(trigger, now)) return false;

        switch (trigger.Recurrence)
        {
            case ScheduleRecurrence.OnAppStartup:
                if (_startupFired.Add(trigger.Id))
                {
                    reason = "On startup";
                    return true;
                }
                return false;

            case ScheduleRecurrence.EveryNMinutes:
            {
                var interval = Math.Clamp(trigger.IntervalMinutes, MacroTrigger.MinIntervalMinutes, MacroTrigger.MaxIntervalMinutes);
                if (!_scheduleBaseline.TryGetValue(trigger.Id, out var baseline))
                {
                    _scheduleBaseline[trigger.Id] = now;
                    return false;
                }

                if ((now - baseline).TotalMinutes >= interval)
                {
                    _scheduleBaseline[trigger.Id] = now;
                    reason = $"Every {interval} min";
                    return true;
                }
                return false;
            }

            case ScheduleRecurrence.DailyAtTime:
            {
                if (!MacroTrigger.TryParseTimeOfDay(trigger.TimeOfDay, out var hour, out var minute))
                    return false;

                var local = now.ToLocalTime();
                var today = DateOnly.FromDateTime(local.DateTime);
                if (local.Hour == hour && local.Minute == minute)
                {
                    if (!_dailyFiredDate.TryGetValue(trigger.Id, out var lastDate) || lastDate != today)
                    {
                        _dailyFiredDate[trigger.Id] = today;
                        reason = $"Daily at {hour:D2}:{minute:D2}";
                        return true;
                    }
                }
                return false;
            }

            default:
                return false;
        }
    }

    private bool ShouldFireWindowEvent(
        MacroTrigger trigger,
        IReadOnlyList<(string Title, string ProcessName)> currentOpen,
        string? activeWindowTitle,
        string? activeProcessName,
        out string reason)
    {
        reason = string.Empty;
        if (!ProfileRouter.HasWindowOrProcessFilter(trigger.WindowTitle, trigger.ProcessNameMatch))
            return false;

        // Establish baseline on first tick so already-open windows don't all fire at startup unless focused later.
        if (!_hasWindowBaseline) return false;

        var targetLabel = trigger.DescribeWindowTarget();
        switch (trigger.WindowEvent)
        {
            case WindowTriggerEvent.WindowFocused:
            {
                var wasMatching = ProfileRouter.MatchesWindowAndProcess(
                    _previousActiveWindow, _previousActiveProcess, trigger.WindowTitle, trigger.ProcessNameMatch);
                var isMatching = ProfileRouter.MatchesWindowAndProcess(
                    activeWindowTitle, activeProcessName, trigger.WindowTitle, trigger.ProcessNameMatch);
                if (!wasMatching && isMatching)
                {
                    reason = $"\"{targetLabel}\" focused";
                    return true;
                }
                return false;
            }

            case WindowTriggerEvent.WindowOpened:
            {
                var wasOpen = _previousOpenWindows.Any(w =>
                    ProfileRouter.MatchesWindowAndProcess(w.Title, w.ProcessName, trigger.WindowTitle, trigger.ProcessNameMatch));
                var isOpen = currentOpen.Any(w =>
                    ProfileRouter.MatchesWindowAndProcess(w.Title, w.ProcessName, trigger.WindowTitle, trigger.ProcessNameMatch));
                if (!wasOpen && isOpen)
                {
                    reason = $"\"{targetLabel}\" opened";
                    return true;
                }
                return false;
            }

            default:
                return false;
        }
    }

    private TriggerDecision ResolveGovernance(
        Macro macro,
        MacroTrigger trigger,
        Guid? currentlyPlayingMacroId,
        DateTimeOffset now,
        bool isHold,
        string reason)
    {
        if (currentlyPlayingMacroId is null)
        {
            _lastFiredAt[trigger.Id] = now;
            return new TriggerDecision(TriggerDecisionKind.StartMacro, macro, trigger, isHold, reason);
        }

        switch (trigger.ConflictPolicy)
        {
            case TriggerConflictPolicy.StopCurrentAndRun:
                _lastFiredAt[trigger.Id] = now;
                return new TriggerDecision(TriggerDecisionKind.StopAndStartMacro, macro, trigger, isHold, reason);

            case TriggerConflictPolicy.QueueNext:
                if (_runQueue.Count < MaxQueueSize && !_runQueue.Any(q => q.Macro?.Id == macro.Id))
                {
                    _lastFiredAt[trigger.Id] = now;
                    var queued = new TriggerDecision(TriggerDecisionKind.QueueMacro, macro, trigger, isHold, reason);
                    _runQueue.Enqueue(queued);
                    return queued;
                }
                return new TriggerDecision(TriggerDecisionKind.None);

            case TriggerConflictPolicy.IgnoreIfBusy:
            default:
                return new TriggerDecision(TriggerDecisionKind.None);
        }
    }

    private bool IsOnCooldown(MacroTrigger trigger, DateTimeOffset now)
    {
        if (trigger.CooldownMs <= 0) return false;
        if (!_lastFiredAt.TryGetValue(trigger.Id, out var last)) return false;
        return (now - last).TotalMilliseconds < trigger.CooldownMs;
    }

    public static bool MatchesWindowFilter(MacroTrigger trigger, string? activeWindowTitle, string? activeProcessName = null) =>
        ProfileRouter.MatchesTriggerFilter(trigger, activeWindowTitle, activeProcessName);

    private static Macro PrepareMacroForTrigger(Macro macro, MacroTrigger trigger)
    {
        if (trigger is { Kind: TriggerKind.Hotkey, HotkeyMode: HotkeyTriggerMode.RepeatWhileHeld })
        {
            var copy = macro.CopyWithSteps(macro.Steps);
            copy.RepeatCount = 0; // repeat until key is released
            return copy;
        }

        return macro;
    }
}
