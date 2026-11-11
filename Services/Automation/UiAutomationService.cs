using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly int OwnProcessId = Environment.ProcessId;

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

    private static UiElementSnapshot? TryInspectWindowOnly(int x, int y)
    {
        try
        {
            using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
            var rootHwnd = FindRootWindowAt(x, y);
            if (rootHwnd == IntPtr.Zero) return null;

            NativeMethods.GetWindowThreadProcessId(rootHwnd, out var pid);
            if (pid == (uint)OwnProcessId) return null;

            var windowProcess = ReadProcessName(pid);
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

    private static UiElementSnapshot? InspectPointCore(int x, int y, CancellationToken cancellationToken)
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
            if (pid == (uint)OwnProcessId) return null; // Never inspect SmartMacro's own windows.

            windowTitle = ReadWindowTitle(rootHwnd);
            windowProcess = ReadProcessName(pid);
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
                if (info.ProcessId == OwnProcessId) return null;

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
                    windowProcess = ReadProcessName((uint)info.ProcessId);
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

    private static UiElementMatch? FindCore(
        TargetFingerprint target,
        ScreenRect? windowBounds,
        (int X, int Y)? expectedPoint,
        CancellationToken cancellationToken)
    {
        using var dpi = NativeMethods.PhysicalPixelsScope.Enter();
        cancellationToken.ThrowIfCancellationRequested();

        var roots = FindCandidateWindowElements(target, windowBounds);
        if (roots.Count == 0) return null;

        var wantedId = target.AutomationId?.Trim();
        var wantedName = target.ElementName?.Trim();
        var wantedType = LookupControlType(target.ControlType);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Try AutomationId first (most stable across localizations and dynamic labels).
            if (!string.IsNullOrEmpty(wantedId))
            {
                var idCond = BuildCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, wantedId, PropertyConditionFlags.IgnoreCase),
                    wantedType);
                if (PickBestMatch(root, idCond, expectedPoint, $"#{wantedId}", confidence: 0.98) is { } byId)
                    return byId;
            }

            // 2. Try ElementName (+ ControlType when known, then ElementName alone if control wrapper changed).
            if (!string.IsNullOrEmpty(wantedName))
            {
                var nameProp = new PropertyCondition(AutomationElement.NameProperty, wantedName, PropertyConditionFlags.IgnoreCase);
                if (wantedType is not null)
                {
                    var typedNameCond = BuildCondition(nameProp, wantedType);
                    if (PickBestMatch(root, typedNameCond, expectedPoint, $"{target.ControlType} \"{wantedName}\"", confidence: 0.96) is { } byTypedName)
                        return byTypedName;
                }

                if (PickBestMatch(root, nameProp, expectedPoint, $"\"{wantedName}\"", confidence: 0.92) is { } byName)
                    return byName;
            }
        }

        return null;
    }

    private static UiaCondition BuildCondition(UiaCondition primary, ControlType? controlType) =>
        controlType is null
            ? primary
            : new AndCondition(primary, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));

    private static UiElementMatch? PickBestMatch(
        AutomationElement root,
        UiaCondition condition,
        (int X, int Y)? expectedPoint,
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

        ScreenRect bestBounds = default;
        var bestDistance = long.MaxValue;

        for (var i = 0; i < found.Count; i++)
        {
            try
            {
                var candidate = found[i];
                var current = candidate.Current;
                if (current.IsOffscreen) continue;
                var rect = ToScreenRect(current.BoundingRectangle);
                if (rect.IsEmpty) continue;

                if (expectedPoint is not { } pt)
                {
                    return new UiElementMatch(rect, confidence, matchedBy);
                }

                long dx = rect.CenterX - pt.X;
                long dy = rect.CenterY - pt.Y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestBounds = rect;
                }
            }
            catch
            {
                // Element disappeared mid-enumeration: skip.
            }
        }

        return bestBounds.IsEmpty ? null : new UiElementMatch(bestBounds, confidence, matchedBy);
    }

    private static List<AutomationElement> FindCandidateWindowElements(TargetFingerprint target, ScreenRect? windowBounds)
    {
        var hwnds = new List<(IntPtr Hwnd, int Rank)>();
        var wantedTitle = target.WindowTitle?.Trim() ?? string.Empty;
        var wantedProc = NormalizeProcessName(target.WindowProcessName);

        WindowApi.EnumWindows((hwnd, _) =>
        {
            if (!WindowApi.IsWindowVisible(hwnd) || WindowApi.IsIconic(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)OwnProcessId) return true;

            var bounds = ReadVisibleBounds(hwnd);
            if (bounds.IsEmpty) return true;

            if (windowBounds is { IsEmpty: false } wb && bounds == wb)
            {
                hwnds.Add((hwnd, 0));
                return true;
            }

            var proc = NormalizeProcessName(ReadProcessName(pid));
            var title = ReadWindowTitle(hwnd) ?? string.Empty;

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
                if (fgPid != (uint)OwnProcessId)
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

    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        var length = WindowApi.GetWindowTextLength(hwnd);
        if (length <= 0) return null;
        var sb = new StringBuilder(Math.Min(length + 1, TargetFingerprint.MaxFieldLength + 1));
        WindowApi.GetWindowText(hwnd, sb, sb.Capacity);
        return CleanField(sb.ToString());
    }

    private static string? ReadProcessName(uint pid)
    {
        if (pid == 0) return null;
        var handle = WindowApi.OpenProcess(WindowApi.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!WindowApi.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
            return CleanField(Path.GetFileNameWithoutExtension(buffer.ToString(0, size)));
        }
        finally
        {
            WindowApi.CloseHandle(handle);
        }
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

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
}
