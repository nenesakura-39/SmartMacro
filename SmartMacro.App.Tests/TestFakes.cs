using System.Collections.Concurrent;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Recording;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.ViewModels;

namespace SmartMacro.AppTests;

internal static class TestCommandHelper
{
    public static async Task RunAsync(AsyncRelayCommand command, object? parameter = null)
    {
        command.Execute(parameter);
        while (command.IsRunning)
        {
            StaTestRunner.DoEvents();
            await Task.Delay(5);
        }
        StaTestRunner.DoEvents();
    }
}

internal sealed class FakeLogService : ILogService
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _gate = new();

    public string LogDirectory { get; set; } = Path.GetTempPath();

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public void Log(AppLogLevel level, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, message, exception?.Message);
        lock (_gate)
            _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> GetRecent() => Entries;
}

internal sealed class FakeDialogService : IDialogService
{
    public bool NextConfirmResult { get; set; } = true;
    public SaveChoice NextSaveChoice { get; set; } = SaveChoice.Save;
    public string[]? NextOpenFiles { get; set; }
    public string? NextSaveFile { get; set; }

    public List<(string Title, string Message)> Confirms { get; } = new();
    public List<(string Title, string Message)> Errors { get; } = new();
    public List<(string Title, string Message)> Warnings { get; } = new();
    public List<(string Title, string Message)> Infos { get; } = new();

    public bool Confirm(string title, string message)
    {
        Confirms.Add((title, message));
        return NextConfirmResult;
    }

    public SaveChoice AskSaveChanges(string macroName) => NextSaveChoice;

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public void ShowWarning(string title, string message) => Warnings.Add((title, message));

    public void ShowInfo(string title, string message) => Infos.Add((title, message));

    public string[]? PickFilesToOpen(string title, string filter) => NextOpenFiles;

    public string? PickSaveFile(string title, string filter, string defaultFileName) => NextSaveFile;
}

internal sealed class FakeScreenInfoProvider : IScreenInfoProvider, IScreenLayout
{
    public ScreenBounds VirtualScreen { get; set; } = new(0, 0, 1920, 1080);
    public List<MonitorInfo> Monitors { get; } =
    [
        new MonitorInfo(1, new ScreenRect(0, 0, 1920, 1080), IsPrimary: true, @"\\.\DISPLAY1"),
    ];
    public List<WindowInfo> Windows { get; } = new();
    public (string? Title, string ProcessName) ForegroundWindow { get; set; } = (null, string.Empty);

    public ScreenBounds GetVirtualScreen() => VirtualScreen;

    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors.ToList();

    public IReadOnlyList<WindowInfo> GetWindows() => Windows.ToList();

    public string? GetForegroundWindowTitle() => ForegroundWindow.Title;

    public (string? Title, string ProcessName) GetForegroundWindowInfo() => ForegroundWindow;
}

internal sealed class FakeMacroRepository : IMacroRepository
{
    private readonly Dictionary<Guid, Macro> _macros = new();
    private readonly Dictionary<string, (TrashedMacroItem Meta, Macro Data)> _trash = new(StringComparer.OrdinalIgnoreCase);

    public string MacrosDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "SmartMacroAppTests", "Macros");
    public string TrashDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "SmartMacroAppTests", "Trash");
    public List<MacroLoadProblem> Problems { get; } = new();
    public List<string> Repairs { get; } = new();

    public void Seed(params Macro[] macros)
    {
        foreach (var macro in macros)
            _macros[macro.Id] = MacroJson.Clone(macro);
    }

    public Macro? FindStored(Guid id) => _macros.TryGetValue(id, out var m) ? MacroJson.Clone(m) : null;

    public Task<MacroLoadResult> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var list = _macros.Values.Select(MacroJson.Clone).ToList();
        return Task.FromResult(new MacroLoadResult(list, Problems.ToList(), Repairs.ToList()));
    }

    public Task SaveAsync(Macro macro, CancellationToken cancellationToken = default)
    {
        var copy = MacroJson.Clone(macro);
        var now = DateTimeOffset.UtcNow;
        if (copy.CreatedAt == default)
            copy.CreatedAt = now;
        copy.ModifiedAt = now;
        macro.CreatedAt = copy.CreatedAt;
        macro.ModifiedAt = copy.ModifiedAt;
        _macros[copy.Id] = copy;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_macros.Remove(id, out var removed))
            return Task.FromResult(false);

        var fileName = $"{removed.Id:N}.trash.json";
        var meta = new TrashedMacroItem(fileName, removed.Name, removed.Steps.Count, DateTimeOffset.UtcNow, removed.ProfileName);
        _trash[fileName] = (meta, MacroJson.Clone(removed));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TrashedMacroItem>> LoadTrashAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TrashedMacroItem> items = _trash.Values
            .OrderByDescending(x => x.Meta.DeletedAt)
            .Select(x => x.Meta)
            .ToList();
        return Task.FromResult(items);
    }

    public Task<Macro?> RestoreFromTrashAsync(string trashFileName, IEnumerable<string>? existingNames = null, CancellationToken cancellationToken = default)
    {
        if (!_trash.Remove(trashFileName, out var entry))
            return Task.FromResult<Macro?>(null);

        var restored = MacroJson.Clone(entry.Data);
        if (_macros.ContainsKey(restored.Id))
            restored.Id = Guid.NewGuid();
        if (existingNames is not null)
        {
            var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
            var candidate = restored.Name;
            var n = 2;
            while (taken.Contains(candidate))
                candidate = $"{restored.Name} ({n++})";
            restored.Name = candidate;
        }

        _macros[restored.Id] = MacroJson.Clone(restored);
        return Task.FromResult<Macro?>(restored);
    }

    public Task<bool> DeleteFromTrashAsync(string trashFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_trash.Remove(trashFileName));

    public Task<int> EmptyTrashAsync(CancellationToken cancellationToken = default)
    {
        var count = _trash.Count;
        _trash.Clear();
        return Task.FromResult(count);
    }
}

internal sealed class FakeSettingsPersistence : ISettingsPersistence
{
    public AppSettings Stored { get; private set; } = new();
    public string? LoadWarning { get; set; }
    public int SaveCount { get; private set; }
    public int AppearanceAppliedCount { get; private set; }
    public List<bool> StartupCalls { get; } = new();
    public bool StartupResult { get; set; } = true;
    public Exception? ThrowOnSave { get; set; }

    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SettingsLoadResult(Stored.Clone(), LoadWarning));

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSave is not null)
            throw ThrowOnSave;

        Stored = settings.Clone();
        SaveCount++;
        return Task.CompletedTask;
    }

    public void ApplyAppearance(AppSettings settings) => AppearanceAppliedCount++;

    public bool ApplyStartup(bool enabled, ILogService? log = null)
    {
        StartupCalls.Add(enabled);
        return StartupResult;
    }
}

internal sealed class FakeInputSimulator : IInputSimulator
{
    public ConcurrentQueue<string> Operations { get; } = new();
    public int CursorX { get; set; }
    public int CursorY { get; set; }

    public void MoveMouse(int x, int y)
    {
        CursorX = x;
        CursorY = y;
        Operations.Enqueue($"Move({x},{y})");
    }

    public void MoveMouseRelative(int dx, int dy) => Operations.Enqueue($"MoveRel({dx},{dy})");

    public void MouseButton(MacroMouseButton button, bool down) => Operations.Enqueue($"Btn({button},{down})");

    public void Scroll(int delta, bool horizontal) => Operations.Enqueue($"Scroll({delta},{horizontal})");

    public void Key(int virtualKey, bool down) => Operations.Enqueue($"Key({virtualKey},{down})");

    public void KeyWithScanCode(int virtualKey, int scanCode, bool extended, bool down) =>
        Operations.Enqueue($"ScanKey({virtualKey},{scanCode},{extended},{down})");

    public void TypeCharacters(string text) => Operations.Enqueue($"Type({text})");

    public bool TryGetCursorPosition(out int x, out int y)
    {
        x = CursorX;
        y = CursorY;
        return true;
    }
}

internal sealed class InstantPlaybackClock : IPlaybackClock
{
    private long _elapsedTicks;

    public TaskCompletionSource<bool>? DelayGate { get; set; }

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Add(ref _elapsedTicks, Math.Max(0, delay.Ticks));
        if (DelayGate is { } gate)
        {
            using var reg = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            await gate.Task.ConfigureAwait(false);
        }
    }
}

internal sealed class FakeOcrService : IOcrService
{
    public bool IsAvailable { get; set; } = true;
    public string StatusText { get; set; } = "Fake OCR ready";
    public ScreenRect VirtualScreen { get; set; } = new(0, 0, 1920, 1080);

    public ScreenRect GetVirtualScreen() => VirtualScreen;

    public Task<OcrReadResult> ReadAsync(ScreenRect region, CancellationToken cancellationToken) =>
        Task.FromResult(new OcrReadResult(region, Array.Empty<OcrLineBox>()));
}

internal sealed class FakeScreenCapture : IScreenCapture
{
    public ScreenRect VirtualScreen { get; set; } = new(0, 0, 1920, 1080);

    public ScreenRect GetVirtualScreen() => VirtualScreen;

    public Task<ScreenFrame> CaptureAsync(ScreenRect region, CancellationToken cancellationToken) =>
        Task.FromResult(new ScreenFrame(region, new BgraImage(region.Width, region.Height, new byte[region.Width * region.Height * 4])));
}

internal sealed class FakeInputRecorder : IInputRecorder
{
    public bool IsRecording { get; private set; }
    public int EventCount => Events.Count;
    public bool LimitReached { get; set; }
    public List<RawInputEvent> Events { get; } = new();

    public void Start() => IsRecording = true;

    public IReadOnlyList<RawInputEvent> Stop()
    {
        IsRecording = false;
        return Events.ToList();
    }
}

internal sealed class FakeRelativeInputRecorder : IRelativeInputRecorder
{
    public bool IsRecording { get; private set; }
    public int EventCount => Events.Count;
    public bool LimitReached { get; set; }
    public int StartX { get; set; }
    public int StartY { get; set; }
    public List<InputEvent> Events { get; } = new();

    public void Start() => IsRecording = true;

    public IReadOnlyList<InputEvent> Stop()
    {
        IsRecording = false;
        return Events.ToList();
    }
}

internal sealed class FakeSmartInputRecorder : ISmartInputRecorder
{
    public bool IsRecording { get; private set; }
    public int EventCount => Events.Count;
    public bool LimitReached { get; set; }
    public int StartX { get; set; }
    public int StartY { get; set; }
    public List<CapturedInputEvent> Events { get; } = new();

    public void Start() => IsRecording = true;

    public IReadOnlyList<CapturedInputEvent> Stop()
    {
        IsRecording = false;
        return Events.ToList();
    }
}

internal sealed class FakeRunModeHost : IRunModeHost
{
    public int EnterCount { get; private set; }
    public int ExitCount { get; private set; }
    public List<RunHud> HudUpdates { get; } = new();

    public event EventHandler? PauseRequested;
    public event EventHandler? StopRequested;

    public void Enter(OverlayKind kind, string text, string? counter = null) => EnterCount++;

    public void Update(OverlayKind kind, string text, string? counter = null) { }

    public void UpdateHud(RunHud hud) => HudUpdates.Add(hud);

    public void Exit() => ExitCount++;

    public void RaisePause() => PauseRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseStop() => StopRequested?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeSystemHotkeyApi : ISystemHotkeyApi
{
    public Dictionary<int, (IntPtr Hwnd, uint Modifiers, uint Vk)> Registered { get; } = new();
    public HashSet<(uint Modifiers, uint Vk)> RejectBindings { get; } = new();
    public HashSet<int> DownVirtualKeys { get; } = new();

    public bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)
    {
        if (RejectBindings.Contains((fsModifiers, vk)))
            return false;

        Registered[id] = (hWnd, fsModifiers, vk);
        return true;
    }

    public bool UnregisterHotKey(IntPtr hWnd, int id) => Registered.Remove(id);

    public short GetAsyncKeyState(int vKey) =>
        DownVirtualKeys.Contains(vKey) ? unchecked((short)0x8000) : (short)0;
}

internal sealed class FakeWindowApi : IWindowApi
{
    public sealed record FakeWindowEntry(
        IntPtr Handle,
        string? Title,
        uint ProcessId,
        string ProcessName,
        ScreenRect Bounds,
        bool Visible = true,
        bool ToolWindow = false,
        bool HasOwner = false,
        bool Cloaked = false,
        bool Minimized = false);

    private readonly Dictionary<IntPtr, FakeWindowEntry> _windows = new();

    public IntPtr ForegroundWindow { get; set; }

    public void AddWindow(FakeWindowEntry entry) => _windows[entry.Handle] = entry;

    public void EnumWindows(Func<IntPtr, bool> callback)
    {
        foreach (var hwnd in _windows.Keys)
        {
            if (!callback(hwnd))
                break;
        }
    }

    public IntPtr GetForegroundWindow() => ForegroundWindow;

    public uint GetWindowThreadProcessId(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) ? entry.ProcessId : 0;

    public string? GetWindowText(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) ? entry.Title : null;

    public bool IsWindowVisible(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) && entry.Visible;

    public bool IsToolWindow(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) && entry.ToolWindow;

    public bool HasOwner(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) && entry.HasOwner;

    public bool IsCloaked(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) && entry.Cloaked;

    public bool IsMinimized(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) && entry.Minimized;

    public ScreenRect? GetWindowBounds(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var entry) ? entry.Bounds : null;

    public string QueryProcessName(uint processId) =>
        _windows.Values.FirstOrDefault(w => w.ProcessId == processId)?.ProcessName ?? string.Empty;
}
