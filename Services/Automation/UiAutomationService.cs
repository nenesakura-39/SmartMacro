using System.Runtime.InteropServices;
using System.Windows.Automation;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Targeting;
using SmartMacro.Services.Input;
using UiaCondition = System.Windows.Automation.Condition;

namespace SmartMacro.Services.Automation;

/// <summary>
/// Windows UI Automation (<c>System.Windows.Automation</c>) implementation of <see cref="IUiElementLocator"/>.
/// Runs strictly off input-hook threads and bounds every cross-process accessibility query with a tight timeout
/// (150 ms on inspect, 300 ms on search) so unresponsive target windows can never stall recording or playback.
/// </summary>
public sealed class UiAutomationService : IUiElementLocator
{
    private const uint GA_ROOT = 2;
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromMilliseconds(300);
    // Windows cross-process UIA pattern RPCs (Invoke/Value/Scroll) can wait up to ~2 s internally on busy desktops.
    private static readonly TimeSpan BackgroundActionTimeout = TimeSpan.FromSeconds(3);
    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly bool _allowOwnProcess;

    public UiAutomationService() : this(allowOwnProcess: false)
    {
    }

    public UiAutomationService(bool allowOwnProcess)
    {
        _allowOwnProcess = allowOwnProcess;
    }

    private bool IsExcludedProcess(uint pid) => !_allowOwnProcess && pid == (uint)OwnProcessId;

    private static readonly HashSet<string> InteractiveControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button",
        "MenuItem",
        "TabItem",
        "CheckBox",
        "RadioButton",
        "Hyperlink",
        "ComboBox",
        "ListItem",
        "TreeItem",
        "Edit",
        "SplitButton",
        "Slider",
        "Spinner",
        "DataItem",
    };

    private int _inspectInFlight;

    public bool IsAvailable => true;

    public UiElementSnapshot? InspectPoint(int x, int y, CancellationToken cancellationToken = default)
    {
        // Guard against ThreadPool starvation if a previous UIA COM call is still hung inside an unresponsive target window.
        if (Interlocked.CompareExchange(ref _inspectInFlight, 1, 0) != 0)
        {
            return TryInspectWindowOnly(x, y);
        }

        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    return InspectPointCore(x, y, cancellationToken);
                }
                finally
                {
                    Interlocked.Exchange(ref _inspectInFlight, 0);
                }
            }, cancellationToken);

            if (!task.Wait(InspectTimeout, cancellationToken))
                return TryInspectWindowOnly(x, y);

            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private UiElementSnapshot? TryInspectWindowOnly(int x, int y)
    {
        try
        {
            using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
            var rootHwnd = FindRootWindowAt(x, y);
            if (rootHwnd == IntPtr.Zero) return null;

            NativeMethods.GetWindowThreadProcessId(rootHwnd, out var pid);
            if (IsExcludedProcess(pid)) return null;

            var windowProcess = CleanField(WindowApi.ReadProcessName(pid));
            var windowBounds = ReadVisibleBounds(rootHwnd);
            if (string.IsNullOrWhiteSpace(windowProcess)) return null;

            return new UiElementSnapshot(
                ElementName: null,
                AutomationId: null,
                ControlType: null,
                ClassName: null,
                ElementBounds: default,
                WindowTitle: null,
                WindowProcessName: windowProcess,
                WindowBounds: windowBounds);
        }
        catch
        {
            return null;
        }
    }

    public async Task<UiElementMatch?> FindAsync(
        TargetFingerprint target,
        ScreenRect? windowBounds,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.HasUiElement) return null;

        try
        {
            var searchTask = Task.Run(() => FindCore(target, windowBounds, expectedPoint, cancellationToken), cancellationToken);
            return await searchTask.WaitAsync(FindTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public Task<UiBackgroundActionResult> TryBackgroundClickAsync(
        string windowTitle,
        TargetFingerprint? target,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken = default) =>
        RunBackgroundUiaAsync(() => BackgroundClickCore(windowTitle, target, expectedPoint, cancellationToken), cancellationToken);

    public Task<UiBackgroundActionResult> TryBackgroundSetValueAsync(
        string windowTitle,
        string text,
        CancellationToken cancellationToken = default) =>
        RunBackgroundUiaAsync(() => BackgroundSetValueCore(windowTitle, text, cancellationToken), cancellationToken);

    public Task<UiBackgroundActionResult> TryBackgroundScrollAsync(
        string windowTitle,
        int delta,
        bool horizontal,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken = default) =>
        RunBackgroundUiaAsync(() => BackgroundScrollCore(windowTitle, delta, horizontal, expectedPoint, cancellationToken), cancellationToken);

    private static async Task<UiBackgroundActionResult> RunBackgroundUiaAsync(
        Func<UiBackgroundActionResult> work,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(work, cancellationToken)
                .WaitAsync(BackgroundActionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new UiBackgroundActionResult(Succeeded: false, Error: "Background UI Automation timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UiBackgroundActionResult(Succeeded: false, Error: ex.Message);
        }
    }

    private AutomationElement? ResolveBackgroundRoot(string windowTitle)
    {
        var hwnd = WindowApi.FindTopLevelWindow(windowTitle, _allowOwnProcess);
        if (hwnd == IntPtr.Zero || WindowApi.IsIconic(hwnd)) return null;
        try { return AutomationElement.FromHandle(hwnd); }
        catch { return null; }
    }

    private UiBackgroundActionResult BackgroundClickCore(
        string windowTitle,
        TargetFingerprint? target,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        var root = ResolveBackgroundRoot(windowTitle);
        if (root is null) return UiBackgroundActionResult.NotSupported;

        AutomationElement? candidate = null;
        if (target is { HasUiElement: true })
        {
            var matched = FindBestElementByFingerprint(root, target, expectedPoint, requireEnabled: true, cancellationToken);
            candidate = matched?.Element;
        }

        if (candidate is null && expectedPoint is { } pt)
        {
            candidate = FindInteractiveElementAtPoint(root, pt, cancellationToken);
        }

        return candidate is null ? UiBackgroundActionResult.NotSupported : TryExecuteClickPattern(candidate);
    }

    private UiBackgroundActionResult BackgroundSetValueCore(string windowTitle, string text, CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        var root = ResolveBackgroundRoot(windowTitle);
        if (root is null) return UiBackgroundActionResult.NotSupported;

        AutomationElementCollection? found;
        try
        {
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            found = root.FindAll(TreeScope.Descendants, cond);
        }
        catch
        {
            return UiBackgroundActionResult.NotSupported;
        }

        if (found is null || found.Count == 0) return UiBackgroundActionResult.NotSupported;

        ValuePattern? bestPattern = null;
        var bestScore = int.MaxValue;
        for (var i = 0; i < found.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var el = found[i];
                var current = el.Current;
                if (!current.IsEnabled || current.IsOffscreen) continue;
                var rect = ToScreenRect(current.BoundingRectangle);
                if (rect.IsEmpty) continue;
                if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var patObj) || patObj is not ValuePattern vp) continue;
                if (vp.Current.IsReadOnly) continue;

                var typeScore = current.ControlType == ControlType.Edit ? 0
                    : current.ControlType == ControlType.Document ? 1
                    : 2;
                if (typeScore < bestScore)
                {
                    bestScore = typeScore;
                    bestPattern = vp;
                    if (typeScore == 0) break;
                }
            }
            catch
            {
                // Skip volatile elements.
            }
        }

        if (bestPattern is null) return UiBackgroundActionResult.NotSupported;

        bestPattern.SetValue(text ?? string.Empty);
        return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "ValuePattern");
    }

    private UiBackgroundActionResult BackgroundScrollCore(
        string windowTitle,
        int delta,
        bool horizontal,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        var root = ResolveBackgroundRoot(windowTitle);
        if (root is null) return UiBackgroundActionResult.NotSupported;

        var candidates = new List<AutomationElement>(8);
        candidates.Add(root);
        try
        {
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            var found = root.FindAll(TreeScope.Descendants, cond);
            if (found is not null)
            {
                for (var i = 0; i < found.Count; i++) candidates.Add(found[i]);
            }
        }
        catch
        {
            // Keep root fallback if tree search failed.
        }

        ScrollPattern? best = null;
        var bestContains = false;
        var bestMetric = long.MaxValue;

        foreach (var el in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!el.TryGetCurrentPattern(ScrollPattern.Pattern, out var patObj) || patObj is not ScrollPattern sp) continue;
                var sc = sp.Current;
                if (horizontal ? !sc.HorizontallyScrollable : !sc.VerticallyScrollable) continue;

                var rect = ToScreenRect(el.Current.BoundingRectangle);
                if (rect.IsEmpty) continue;

                if (expectedPoint is { } pt)
                {
                    var contains = ContainsPoint(rect, pt);
                    var area = (long)rect.Width * rect.Height;
                    long dx = rect.CenterX - pt.X;
                    long dy = rect.CenterY - pt.Y;
                    var dist = dx * dx + dy * dy;

                    if (best is null
                        || (contains && !bestContains)
                        || (contains == bestContains && (contains ? area < bestMetric : dist < bestMetric)))
                    {
                        best = sp;
                        bestContains = contains;
                        bestMetric = contains ? area : dist;
                    }
                }
                else
                {
                    best = sp;
                    break;
                }
            }
            catch
            {
                // Skip unavailable elements.
            }
        }

        if (best is null) return UiBackgroundActionResult.NotSupported;

        var notches = Math.Clamp(Math.Max(1, Math.Abs(delta) / 120), 1, 20);
        if (notches > 1)
        {
            var sc = best.Current;
            var currentPct = horizontal ? sc.HorizontalScrollPercent : sc.VerticalScrollPercent;
            var viewSize = horizontal ? sc.HorizontalViewSize : sc.VerticalViewSize;
            if (currentPct is >= 0.0 and <= 100.0)
            {
                var sign = horizontal ? (delta >= 0 ? 1.0 : -1.0) : (delta >= 0 ? -1.0 : 1.0);
                var perNotchPct = Math.Clamp(viewSize is > 0.0 and < 100.0 ? viewSize * 0.25 : 5.0, 2.5, 25.0);
                var targetPct = Math.Clamp(currentPct + sign * notches * perNotchPct, 0.0, 100.0);
                if (Math.Abs(targetPct - currentPct) < 0.01)
                    return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "ScrollPattern");

                try
                {
                    if (horizontal) best.SetScrollPercent(targetPct, ScrollPattern.NoScroll);
                    else best.SetScrollPercent(ScrollPattern.NoScroll, targetPct);
                    return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "ScrollPattern");
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // Target control only supports discrete SmallIncrement/SmallDecrement steps.
                }
            }
        }

        // Win32 vertical wheel delta > 0 scrolls up (SmallDecrement); horizontal delta > 0 scrolls right (SmallIncrement).
        var amount = horizontal
            ? (delta >= 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement)
            : (delta >= 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement);

        for (var i = 0; i < notches; i++)
        {
            if (horizontal) best.ScrollHorizontal(amount);
            else best.ScrollVertical(amount);
        }

        return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "ScrollPattern");
    }

    private static AutomationElement? FindInteractiveElementAtPoint(
        AutomationElement root,
        (int X, int Y) pt,
        CancellationToken cancellationToken)
    {
        AutomationElementCollection? found;
        try
        {
            found = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsEnabledProperty, true));
        }
        catch
        {
            return null;
        }

        if (found is null || found.Count == 0) return null;

        AutomationElement? best = null;
        var bestArea = long.MaxValue;
        for (var i = 0; i < found.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var el = found[i];
                var current = el.Current;
                if (current.IsOffscreen || !current.IsEnabled) continue;
                var rect = ToScreenRect(current.BoundingRectangle);
                if (rect.IsEmpty || !ContainsPoint(rect, pt)) continue;
                if (!SupportsAnyClickPattern(el)) continue;

                var area = (long)rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = el;
                }
            }
            catch
            {
                // Skip disappearing elements.
            }
        }

        return best;
    }

    private static bool ContainsPoint(ScreenRect rect, (int X, int Y) pt) =>
        pt.X >= rect.X && pt.X < rect.X + rect.Width && pt.Y >= rect.Y && pt.Y < rect.Y + rect.Height;

    private static bool SupportsAnyClickPattern(AutomationElement element) =>
        element.TryGetCurrentPattern(InvokePattern.Pattern, out _)
        || element.TryGetCurrentPattern(TogglePattern.Pattern, out _)
        || element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _);

    private static UiBackgroundActionResult TryExecuteClickPattern(AutomationElement element)
    {
        if (!element.Current.IsEnabled)
            return new UiBackgroundActionResult(Succeeded: false, Error: "Target UI control is disabled.");

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj) && invokeObj is InvokePattern invoke)
        {
            invoke.Invoke();
            return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "InvokePattern");
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObj) && toggleObj is TogglePattern toggle)
        {
            toggle.Toggle();
            return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "TogglePattern");
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectObj) && selectObj is SelectionItemPattern select)
        {
            select.Select();
            return new UiBackgroundActionResult(Succeeded: true, PatternUsed: "SelectionItemPattern");
        }

        return UiBackgroundActionResult.NotSupported;
    }

    private UiElementSnapshot? InspectPointCore(int x, int y, CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Identify owning top-level window first (works even for DirectX / OpenGL games without UIA trees).
        var rootHwnd = FindRootWindowAt(x, y);
        string? windowTitle = null;
        string? windowProcess = null;
        ScreenRect windowBounds = default;

        if (rootHwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(rootHwnd, out var pid);
            if (IsExcludedProcess(pid)) return null; // Never inspect SmartMacro's own windows.

            windowTitle = CleanField(WindowApi.ReadWindowTitle(rootHwnd, TargetFingerprint.MaxFieldLength));
            windowProcess = CleanField(WindowApi.ReadProcessName(pid));
            windowBounds = ReadVisibleBounds(rootHwnd);
        }

        // 2. Query UI Automation at (x, y).
        string? elementName = null;
        string? automationId = null;
        string? controlType = null;
        string? className = null;
        ScreenRect elementBounds = default;

        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element is not null)
            {
                var info = element.Current;
                if (!_allowOwnProcess && info.ProcessId == OwnProcessId) return null;

                // If the leaf element is an unnamed icon/text glyph inside a button/tab/menu item, walk up 1–2 levels.
                if (string.IsNullOrWhiteSpace(info.Name) && string.IsNullOrWhiteSpace(info.AutomationId))
                {
                    element = PromoteToNamedParent(element) ?? element;
                    info = element.Current;
                }

                var rawControlType = ShortControlTypeName(info.ControlType);
                var rawBounds = ToScreenRect(info.BoundingRectangle);
                var isRootContainer = string.Equals(rawControlType, "Window", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(rawControlType, "TitleBar", StringComparison.OrdinalIgnoreCase);
                var coversMostOfWindow = !windowBounds.IsEmpty
                                         && !rawBounds.IsEmpty
                                         && (long)rawBounds.Width * rawBounds.Height >= (long)windowBounds.Width * windowBounds.Height * 60 / 100;

                // Keep UIA element metadata only when it represents a specific control (not an entire game canvas / window).
                if (!isRootContainer && (!coversMostOfWindow || InteractiveControlTypes.Contains(rawControlType)))
                {
                    elementName = CleanField(info.Name);
                    automationId = CleanField(info.AutomationId);
                    controlType = CleanField(rawControlType);
                    className = CleanField(info.ClassName);
                    elementBounds = rawBounds;
                }

                if (string.IsNullOrEmpty(windowProcess) && info.ProcessId > 0)
                {
                    windowProcess = CleanField(WindowApi.ReadProcessName((uint)info.ProcessId));
                }
            }
        }
        catch
        {
            // UIA failed or isn't supported by this window; keep any Win32 window anchor we already read.
        }

        if (string.IsNullOrWhiteSpace(elementName)
            && string.IsNullOrWhiteSpace(automationId)
            && string.IsNullOrWhiteSpace(windowTitle)
            && string.IsNullOrWhiteSpace(windowProcess))
        {
            return null;
        }

        return new UiElementSnapshot(
            elementName,
            automationId,
            controlType,
            className,
            elementBounds,
            windowTitle,
            windowProcess,
            windowBounds);
    }

    private static AutomationElement? PromoteToNamedParent(AutomationElement leaf)
    {
        var current = leaf;
        for (var depth = 0; depth < 2; depth++)
        {
            AutomationElement? parent;
            try
            {
                parent = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch
            {
                return null;
            }

            if (parent is null || parent == AutomationElement.RootElement) return null;
            var info = parent.Current;
            var typeName = ShortControlTypeName(info.ControlType);
            if (string.Equals(typeName, "Window", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Pane", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(info.Name) || !string.IsNullOrWhiteSpace(info.AutomationId))
            {
                return parent;
            }

            current = parent;
        }

        return null;
    }

    private UiElementMatch? FindCore(
        TargetFingerprint target,
        ScreenRect? windowBounds,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        var roots = FindCandidateWindowElements(target, windowBounds, _allowOwnProcess);
        foreach (var root in roots)
        {
            var hit = FindBestElementByFingerprint(root, target, expectedPoint, requireEnabled: false, cancellationToken);
            if (hit is { } m)
                return new UiElementMatch(m.Bounds, m.Confidence, m.MatchedBy);
        }

        return null;
    }

    private static (AutomationElement Element, ScreenRect Bounds, double Confidence, string MatchedBy)? FindBestElementByFingerprint(
        AutomationElement root,
        TargetFingerprint target,
        (int X, int Y)? expectedPoint,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        var wantedId = target.AutomationId?.Trim();
        var wantedName = target.ElementName?.Trim();
        var wantedType = LookupControlType(target.ControlType);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Try AutomationId first (most stable across localizations and dynamic labels).
        if (!string.IsNullOrEmpty(wantedId))
        {
            var idCond = BuildCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, wantedId, PropertyConditionFlags.IgnoreCase),
                wantedType);
            if (PickBestElement(root, idCond, expectedPoint, requireEnabled, $"#{wantedId}", confidence: 0.98) is { } byId)
                return byId;
        }

        // 2. Try ElementName (+ ControlType when known, then ElementName alone if control wrapper changed).
        if (!string.IsNullOrEmpty(wantedName))
        {
            var nameProp = new PropertyCondition(AutomationElement.NameProperty, wantedName, PropertyConditionFlags.IgnoreCase);
            if (wantedType is not null)
            {
                var typedNameCond = BuildCondition(nameProp, wantedType);
                if (PickBestElement(root, typedNameCond, expectedPoint, requireEnabled, $"{target.ControlType} \"{wantedName}\"", confidence: 0.96) is { } byTypedName)
                    return byTypedName;
            }

            if (PickBestElement(root, nameProp, expectedPoint, requireEnabled, $"\"{wantedName}\"", confidence: 0.92) is { } byName)
                return byName;
        }

        return null;
    }

    private static UiaCondition BuildCondition(UiaCondition primary, ControlType? controlType) =>
        controlType is null
            ? primary
            : new AndCondition(primary, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));

    private static (AutomationElement Element, ScreenRect Bounds, double Confidence, string MatchedBy)? PickBestElement(
        AutomationElement root,
        UiaCondition condition,
        (int X, int Y)? expectedPoint,
        bool requireEnabled,
        string matchedBy,
        double confidence)
    {
        AutomationElementCollection? found;
        try
        {
            found = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return null;
        }

        if (found is null || found.Count == 0) return null;

        AutomationElement? bestElement = null;
        ScreenRect bestBounds = default;
        var bestDistance = long.MaxValue;

        for (var i = 0; i < found.Count; i++)
        {
            try
            {
                var candidate = found[i];
                var current = candidate.Current;
                if (current.IsOffscreen) continue;
                if (requireEnabled && !current.IsEnabled) continue;
                var rect = ToScreenRect(current.BoundingRectangle);
                if (rect.IsEmpty) continue;

                if (expectedPoint is not { } pt)
                {
                    return (candidate, rect, confidence, matchedBy);
                }

                long dx = rect.CenterX - pt.X;
                long dy = rect.CenterY - pt.Y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestElement = candidate;
                    bestBounds = rect;
                }
            }
            catch
            {
                // Element disappeared mid-enumeration: skip.
            }
        }

        return bestElement is null || bestBounds.IsEmpty
            ? null
            : (bestElement, bestBounds, confidence, matchedBy);
    }

    private static List<AutomationElement> FindCandidateWindowElements(TargetFingerprint target, ScreenRect? windowBounds, bool allowOwnProcess)
    {
        var hwnds = new List<(IntPtr Hwnd, int Rank)>();
        var wantedTitle = target.WindowTitle?.Trim() ?? string.Empty;
        var wantedProc = WindowApi.NormalizeProcessName(target.WindowProcessName);

        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (!WindowApi.IsWindowVisible(hwnd) || WindowApi.IsIconic(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!allowOwnProcess && pid == (uint)OwnProcessId) return true;

            var bounds = ReadVisibleBounds(hwnd);
            if (bounds.IsEmpty) return true;

            if (windowBounds is { IsEmpty: false } wb && bounds == wb)
            {
                hwnds.Add((hwnd, 0));
                return true;
            }

            var proc = WindowApi.NormalizeProcessName(WindowApi.ReadProcessName(pid));
            var title = WindowApi.ReadWindowTitle(hwnd, TargetFingerprint.MaxFieldLength) ?? string.Empty;

            var exactProc = wantedProc.Length > 0 && string.Equals(proc, wantedProc, StringComparison.OrdinalIgnoreCase);
            var exactTitle = wantedTitle.Length > 0 && string.Equals(title, wantedTitle, StringComparison.OrdinalIgnoreCase);
            var partialTitle = wantedTitle.Length > 0 && title.Length > 0
                && (title.Contains(wantedTitle, StringComparison.OrdinalIgnoreCase)
                    || wantedTitle.Contains(title, StringComparison.OrdinalIgnoreCase));

            var rank = (exactProc, exactTitle, partialTitle) switch
            {
                (true, true, _) => 1,
                (true, false, true) => 2,
                (false, true, _) when wantedProc.Length == 0 => 3,
                (true, false, false) => 4,
                (false, true, _) => 5,
                _ => int.MaxValue,
            };

            if (rank < int.MaxValue) hwnds.Add((hwnd, rank));
            return true;
        }, IntPtr.Zero);

        var elements = new List<AutomationElement>(Math.Min(3, hwnds.Count));
        foreach (var (hwnd, _) in hwnds.OrderBy(h => h.Rank).Take(3))
        {
            try
            {
                elements.Add(AutomationElement.FromHandle(hwnd));
            }
            catch
            {
                // Window closed: skip.
            }
        }

        // If no window anchor matched (e.g. popup window without title), try foreground window once.
        if (elements.Count == 0)
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out var fgPid);
                if (allowOwnProcess || fgPid != (uint)OwnProcessId)
                {
                    try { elements.Add(AutomationElement.FromHandle(fg)); } catch { }
                }
            }
        }

        return elements;
    }

    private static IntPtr FindRootWindowAt(int x, int y)
    {
        var child = WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (child == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(child, GA_ROOT);
        return root != IntPtr.Zero ? root : child;
    }

    private static ScreenRect ReadVisibleBounds(IntPtr hwnd)
    {
        if (WindowApi.DwmGetWindowAttribute(
                hwnd,
                WindowApi.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            return new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        return WindowApi.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }

    private static ScreenRect ToScreenRect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
            || double.IsNaN(rect.X) || double.IsNaN(rect.Y) || rect.Width <= 0 || rect.Height <= 0)
        {
            return default;
        }

        return new ScreenRect(
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            Math.Max(1, (int)Math.Round(rect.Width)),
            Math.Max(1, (int)Math.Round(rect.Height)));
    }

    private static string ShortControlTypeName(ControlType? controlType)
    {
        var progName = controlType?.ProgrammaticName;
        if (string.IsNullOrWhiteSpace(progName)) return string.Empty;
        const string prefix = "ControlType.";
        return progName.StartsWith(prefix, StringComparison.Ordinal) ? progName[prefix.Length..] : progName;
    }

    private static ControlType? LookupControlType(string? shortName) => shortName?.Trim() switch
    {
        "Button" => ControlType.Button,
        "MenuItem" => ControlType.MenuItem,
        "TabItem" => ControlType.TabItem,
        "CheckBox" => ControlType.CheckBox,
        "RadioButton" => ControlType.RadioButton,
        "Hyperlink" => ControlType.Hyperlink,
        "ComboBox" => ControlType.ComboBox,
        "ListItem" => ControlType.ListItem,
        "TreeItem" => ControlType.TreeItem,
        "Edit" => ControlType.Edit,
        "SplitButton" => ControlType.SplitButton,
        "Slider" => ControlType.Slider,
        "Spinner" => ControlType.Spinner,
        "DataItem" => ControlType.DataItem,
        "Text" => ControlType.Text,
        "Image" => ControlType.Image,
        _ => null,
    };

    private static string? CleanField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= TargetFingerprint.MaxFieldLength
            ? trimmed
            : trimmed[..TargetFingerprint.MaxFieldLength];
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
}
