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
    private SystemTrayService? _tray;
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

        var startInTray = WindowsStartupService.IsStartInTrayCommandLine(e.Args);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Views.SplashLoadingWindow? splash = null;
        if (!startInTray)
        {
            splash = new Views.SplashLoadingWindow();
            splash.Show();
            splash.ReportStage(0.18, "MOUNTING STORAGE & TELEMETRY");
            await Task.Delay(75);
        }

        var dialogs = new DialogService();
        var repository = new JsonMacroRepository(paths.MacrosDirectory, paths.TrashDirectory);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);

        var window = new MainWindow();

        // Recording / playback pieces. The run-mode host reads the current settings through the view model.
        if (splash is not null)
        {
            splash.ReportStage(0.45, "CALIBRATING HIGH-RES TIMELINE & VISION");
            await Task.Delay(75);
        }
        MainViewModel? viewModel = null;
        _runHost = new RunModeHost(window, () => viewModel?.CurrentSettings ?? new AppSettings());
        var ocr = new WindowsOcrService();
        _log.Info(ocr.StatusText);
        _log.Info(Core.Vision.HardwareAccelerationInfo.StatusText);
        _playbackClock = new PrecisePlaybackClock();
        if (!_playbackClock.HasHighResolutionTimer)
            _log.Warning("High-resolution timer isn't available on this Windows version; playback timing falls back to 1 ms steps.");
        var screenCapture = new GdiScreenCapture();
        var screenInfo = new ScreenInfoProvider(); // monitors + app windows for "where to look"
        var uiAutomation = new Services.Automation.UiAutomationService();
        var automation = new AutomationServices(
            // Playback captures hide the target-lock reticle first where Windows can't leave it out of captures.
            new PlaybackEngine(new Win32InputSimulator(_log), _playbackClock, ocr, new OverlayAwareScreenCapture(screenCapture), screenInfo, uiAutomation)
            {
                Clipboard = new WpfClipboardReader(),
                AudioPeakReader = Services.Audio.WasapiAudioPeakReader.ReadMasterPeakPercent,
                SoundCuePlayer = Services.Audio.StudioSoundCuePlayer.Play,
                FailureSnapshotDirectory = paths.LogsDirectory,
            },
            new WindowsInputRecorder { UiLocator = uiAutomation, OcrService = ocr },
            new RawInputRecorder(),
            new SmartInputRecorder { UiLocator = uiAutomation, OcrService = ocr },
            new EmergencyStopWatcher(),
            _runHost,
            ocr,
            new FindTextTools(window, ocr, Path.Combine(paths.LogsDirectory, "last-ocr-test.png"), screenInfo),
            new VisionTools(window, screenCapture, paths.LogsDirectory, screenInfo, ocr, uiAutomation));

        if (splash is not null)
        {
            splash.ReportStage(0.74, "BINDING INPUT HOOKS & WORKSPACE");
            await Task.Delay(65);
        }

        var mainViewModel = new MainViewModel(repository, settingsStore, _log, dialogs, screenInfo, paths, Dispatcher, automation);
        viewModel = mainViewModel;
        _mainViewModel = mainViewModel;

        ThemeManager.Apply(mainViewModel.CurrentSettings);
        window.DataContext = mainViewModel;
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        try
        {
            splash?.ReportStage(0.92, "LOADING MACRO LIBRARY");
            await _mainViewModel.InitializeAsync();
            ThemeManager.Apply(mainViewModel.CurrentSettings);
            Services.Vision.OverlayCaptureService.AllowObsCapture = mainViewModel.CurrentSettings.ShowOverlaysInObsAndRecording;
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed.", ex);
            _mainViewModel.SetStatus($"Startup problem: {ex.Message}", StatusKind.Error);
        }

        if (startInTray)
        {
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Hide();
            _log.Info("Started in system tray (--tray).");
        }
        else
        {
            window.Opacity = 0;
            window.Show();
            if (splash is not null)
                await splash.CompleteAndCloseAsync();

            var mainFadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            window.BeginAnimation(UIElement.OpacityProperty, mainFadeIn);
            window.Activate();
        }

        _hotkeys = new HotkeyService(_log);
        _hotkeys.Pressed += (_, action) => mainViewModel.Automation.HandleHotkey(action);
        _hotkeys.MacroHotkeyPressed += (_, binding) => mainViewModel.HandleMacroHotkeyDown(binding);
        _hotkeys.MacroHotkeyReleased += (_, binding) => mainViewModel.HandleMacroHotkeyUp(binding);
        _hotkeys.Attach(window);
        _hotkeys.Apply(mainViewModel.CurrentSettings);

        _tray = new SystemTrayService(
            openStudio: BringToFront,
            areTriggersArmed: () => mainViewModel.AreTriggersArmed,
            toggleTriggers: () => mainViewModel.AreTriggersArmed = !mainViewModel.AreTriggersArmed,
            stopPlayback: () =>
            {
                if (mainViewModel.Automation.StopCommand.CanExecute(null))
                    mainViewModel.Automation.StopCommand.Execute(null);
            },
            exitApp: () => window.ForceClose());
        _tray.Attach(window);

        mainViewModel.MacroHotkeysChanged += (_, _) =>
        {
            _hotkeys.ApplyMacroHotkeys(mainViewModel.GetActiveTriggerHotkeys());
            _tray.UpdateTooltip($"SmartMacro Studio — {mainViewModel.TriggersArmedStatusText}");
        };

        // Pause / resume (F8 by default) is only a global shortcut while a macro plays, so F8 works normally otherwise.
        mainViewModel.Automation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutomationViewModel.IsPlaying) or "")
                _hotkeys?.SetPauseActive(mainViewModel.Automation.IsPlaying);
        };
        mainViewModel.SettingsApplied += (_, settings) =>
        {
            ThemeManager.Apply(settings);
            // "🎥 Show overlays in OBS / Discord": re-applies the capture rule to every open overlay right away.
            Services.Vision.OverlayCaptureService.AllowObsCapture = settings.ShowOverlaysInObsAndRecording;
            WindowsStartupService.Apply(settings.StartWithWindows, _log);
            var failed = _hotkeys.Apply(settings);
            if (failed.Count > 0)
                mainViewModel.SetStatus($"These shortcuts are used by another program and won't work: {string.Join(", ", failed)}. Change them in Settings.", StatusKind.Warning);
        };

        if (WindowsStartupService.TryParseCliRunRequest(e.Args, out var cliTarget, out var exitAfterRun))
        {
            await RunCliMacroAsync(mainViewModel, window, cliTarget, exitAfterRun);
        }
    }

    private async Task RunCliMacroAsync(MainViewModel mainViewModel, MainWindow window, string cliTarget, bool exitAfterRun)
    {
        try
        {
            Macro? macroToRun = null;
            var libraryMatch = mainViewModel.Library.Items.FirstOrDefault(item =>
                string.Equals(item.Name, cliTarget, StringComparison.OrdinalIgnoreCase)
                || (Guid.TryParse(cliTarget, out var parsedId) && item.Id == parsedId));

            if (libraryMatch is not null)
            {
                macroToRun = libraryMatch.Macro.CopyWithSteps(libraryMatch.Macro.Steps);
            }
            else if (File.Exists(cliTarget))
            {
                var rawJson = await File.ReadAllTextAsync(cliTarget);
                if (MacroFileFormat.TryParse(rawJson, out var parsed, out var parseError) && parsed is not null)
                    macroToRun = parsed;
                else
                    _log?.Warning($"CLI --run could not parse '{cliTarget}': {parseError}");
            }

            if (macroToRun is null)
            {
                _log?.Warning($"CLI --run target not found: \"{cliTarget}\".");
                mainViewModel.SetStatus($"CLI --run macro not found: \"{cliTarget}\".", StatusKind.Warning);
            }
            else
            {
                _log?.Info($"CLI --run starting macro \"{macroToRun.Name}\" (exitAfterRun={exitAfterRun}).");
                await mainViewModel.Automation.PlayMacroAsync(macroToRun);
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"CLI --run failed for \"{cliTarget}\".", ex);
        }

        if (exitAfterRun)
        {
            window.ForceClose();
            Shutdown(0);
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

        _tray?.Dispose();
        _hotkeys?.Dispose();
        _runHost?.Dispose();
        _mainViewModel?.Dispose();
        _playbackClock?.Dispose();
        ScreenCapture.ReleaseAllResources(); // the two shared, lock-protected GDI capture buffers
        DispatcherUnhandledException -=OnDispatcherUnhandledException;
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
        CrashReporter.WriteReport(_log?.LogDirectory, "DispatcherUnhandledException", e.Exception, AppInfo.Version, _log?.GetRecent());
        DarkMessageBox.Show("Unexpected error",
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe error was saved to the log. SmartMacro will keep running, but please save your work.",
            DialogKind.Error, "OK");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _log?.Error("Fatal error.", ex);
            CrashReporter.WriteReport(_log?.LogDirectory, "AppDomain.UnhandledException", ex, AppInfo.Version, _log?.GetRecent());
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Background task error.", e.Exception);
        CrashReporter.WriteReport(_log?.LogDirectory, "TaskScheduler.UnobservedTaskException", e.Exception, AppInfo.Version, _log?.GetRecent());
        e.SetObserved();
    }
}
