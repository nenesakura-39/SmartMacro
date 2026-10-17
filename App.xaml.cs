using System.Windows;
using System.Windows.Threading;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Services;
using SmartMacro.Services.Input;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;
using SmartMacro.ViewModels;

namespace SmartMacro;

/// <summary>
/// Startup: creates the data folders, services and main window by hand (a tiny "composition root").
/// Also catches unexpected errors so they get logged and shown instead of silently killing the app.
/// </summary>
public partial class App : Application
{
    private LogService? _log;
    private MainViewModel? _mainViewModel;
    private HotkeyService? _hotkeys;
    private RunModeHost? _runHost;
    private PrecisePlaybackClock? _playbackClock;

    // Only one SmartMacro may run at a time (two copies would fight over shortcuts and input hooks).
    private const string SingleInstanceName = @"Local\SmartMacro.SingleInstance";
    private const string ActivateEventName = @"Local\SmartMacro.Activate";
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            Shutdown(0);
            return;
        }

        AppPaths paths;
        try
        {
            paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SmartMacro could not create its data folder.\n\n{ex.Message}", "SmartMacro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _log = new LogService(paths.LogsDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var dialogs = new DialogService();
        var repository = new JsonMacroRepository(paths.MacrosDirectory, paths.TrashDirectory);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);

        var window = new MainWindow();

        // Recording / playback pieces. The run-mode host reads the current settings through the view model.
        MainViewModel? viewModel = null;
        _runHost = new RunModeHost(window, () => viewModel?.CurrentSettings ?? new AppSettings());
        var ocr = new WindowsOcrService();
        _log.Info(ocr.StatusText);
        _playbackClock = new PrecisePlaybackClock();
        if (!_playbackClock.HasHighResolutionTimer)
            _log.Warning("High-resolution timer isn't available on this Windows version; playback timing falls back to 1 ms steps.");
        var screenCapture = new GdiScreenCapture();
        var screenInfo = new ScreenInfoProvider(); // monitors + app windows for "where to look"
        var automation = new AutomationServices(
            new PlaybackEngine(new Win32InputSimulator(_log), _playbackClock, ocr, screenCapture, screenInfo),
            new WindowsInputRecorder(),
            new RawInputRecorder(),
            new SmartInputRecorder(),
            new EmergencyStopWatcher(),
            _runHost,
            ocr,
            new FindTextTools(window, ocr, Path.Combine(paths.LogsDirectory, "last-ocr-test.png"), screenInfo),
            new VisionTools(window, screenCapture, paths.LogsDirectory, screenInfo));

        var mainViewModel = new MainViewModel(repository, settingsStore, _log, dialogs, screenInfo, paths, Dispatcher, automation);
        viewModel = mainViewModel;
        _mainViewModel = mainViewModel;

        window.DataContext = mainViewModel;
        MainWindow = window;
        window.Show();

        _hotkeys = new HotkeyService(_log);
        _hotkeys.Pressed += (_, action) => mainViewModel.Automation.HandleHotkey(action);
        _hotkeys.Attach(window);
        _hotkeys.Apply(mainViewModel.CurrentSettings);
        mainViewModel.SettingsApplied += (_, settings) =>
        {
            var failed = _hotkeys.Apply(settings);
            if (failed.Count > 0)
                mainViewModel.SetStatus($"These shortcuts are used by another program and won't work: {string.Join(", ", failed)}. Change them in Settings.", StatusKind.Warning);
        };

        try
        {
            await _mainViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed.", ex);
            _mainViewModel.SetStatus($"Startup problem: {ex.Message}", StatusKind.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned */ }
            _singleInstance.Dispose();
        }

        _hotkeys?.Dispose();
        _runHost?.Dispose();
        _mainViewModel?.Dispose();
        _playbackClock?.Dispose();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    /// <summary>
    /// Returns false if SmartMacro is already running; in that case the running copy is brought to the front.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is still starting up; nothing to signal.
            }

            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.InvokeAsync(BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;  // nudge it in front of other apps
        window.Topmost = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unexpected error.", e.Exception);
        DarkMessageBox.Show("Unexpected error",
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe error was saved to the log. SmartMacro will keep running, but please save your work.",
            DialogKind.Error, "OK");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _log?.Error("Fatal error.", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Background task error.", e.Exception);
        e.SetObserved();
    }
}
