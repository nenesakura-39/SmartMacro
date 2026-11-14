using System.Runtime.InteropServices;
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
    private WindowsGraphicsCaptureService? _screenCapture;

    // Only one SmartMacro may run at a time (two copies would fight over shortcuts and input hooks).
    private const string SingleInstanceName = @"Local\SmartMacro.SingleInstance";
    private const int AttachParentProcess = -1;
    private Mutex? _singleInstance;
    private CliIpcServer? _ipcServer;
    private TaskCompletionSource<bool>? _startupReadyTcs;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var cliRequest = CliCommandLine.Parse(e.Args);
        if (cliRequest.Action == CliActionKind.Help)
        {
            var help = CliCommandLine.BuildHelpText();
            if (!TryWriteCliOutput(help))
                MessageBox.Show(help, "SmartMacro CLI Options", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(CliExitCodes.Success);
            return;
        }

        if (!string.IsNullOrEmpty(cliRequest.ParseError))
        {
            if (!TryWriteCliOutput(cliRequest.ParseError) && !cliRequest.NoFocus)
                MessageBox.Show(cliRequest.ParseError, "SmartMacro CLI", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(CliExitCodes.InvalidOrNotFound);
            return;
        }

        if (!ClaimSingleInstanceMutex())
        {
            var responseTimeout = cliRequest.Action == CliActionKind.RunMacro && cliRequest.EffectiveWait
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(15);

            var response = await CliIpcClient.SendAsync(
                CliIpcNames.GetDefaultPipeName(),
                cliRequest,
                connectTimeout: TimeSpan.FromMilliseconds(2500),
                responseTimeout: responseTimeout);

            if (response is null)
            {
                const string unreachable = "SmartMacro is already running, but did not respond to CLI IPC.";
                TryWriteCliOutput(unreachable);
                Shutdown(CliExitCodes.BusyOrIpcError);
                return;
            }

            if (!string.IsNullOrWhiteSpace(response.Message))
                TryWriteCliOutput(response.Message);

            Shutdown(response.ExitCode);
            return;
        }

        if (cliRequest.Action is CliActionKind.Stop or CliActionKind.DisarmTriggers && !cliRequest.StartInTray)
        {
            TryWriteCliOutput("SmartMacro is not running.");
            Shutdown(CliExitCodes.Success);
            return;
        }

        _startupReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ipcServer = new CliIpcServer(
            CliIpcNames.GetDefaultPipeName(),
            HandleIpcRequestAsync,
            AfterIpcResponseSentAsync,
            (msg, ex) => _log?.Warning(msg, ex));

        AppPaths paths;
        try
        {
            paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            _startupReadyTcs.TrySetResult(false);
            MessageBox.Show($"SmartMacro could not create its data folder.\n\n{ex.Message}", "SmartMacro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _log = new LogService(paths.LogsDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var startHidden = !cliRequest.ShouldShowMainWindowOnStartup;
        Views.SplashLoadingWindow? splash = null;
        if (!startHidden)
        {
            splash = new Views.SplashLoadingWindow();
            splash.Show();
            splash.ReportStage(0.18, "MOUNTING STORAGE & TELEMETRY");
            await Task.Delay(75);
        }

        var dialogs = new DialogService();
        var repository = new JsonMacroRepository(paths.MacrosDirectory, paths.TrashDirectory);
        var settingsStore = new WindowsSettingsPersistence(new JsonSettingsStore(paths.SettingsFile));

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
        Services.Vision.GpuAccelerator.Initialize();
        _playbackClock = new PrecisePlaybackClock();
        if (!_playbackClock.HasHighResolutionTimer)
            _log.Warning("High-resolution timer isn't available on this Windows version; playback timing falls back to 1 ms steps.");
        var screenCapture = new WindowsGraphicsCaptureService(
            logInfo: msg => _log?.Info(msg),
            logWarning: (msg, ex) => _log?.Warning(ex is null ? msg : $"{msg} ({ex.Message})"));
        _screenCapture = screenCapture;
        _runHost.Exited += (_, _) => _screenCapture?.ReleaseIdleSessions(forceAll: false);
        _log.Info(Core.Vision.HardwareAccelerationInfo.StatusText);
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

        var windowMonitor = new WindowEventMonitor();
        var mainViewModel = new MainViewModel(repository, settingsStore, _log, dialogs, screenInfo, paths, Dispatcher, automation, windowMonitor);
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

        if (startHidden)
        {
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Hide();
            _log.Info(cliRequest.StartInTray
                ? "Started in system tray (--tray)."
                : "Started hidden in CLI --no-focus mode.");
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
            _hotkeys.ApplyMacroHotkeys(mainViewModel.GetActiveTriggerHotkeyEntries());
            _tray.UpdateTooltip($"SmartMacro Studio — {mainViewModel.TriggersArmedStatusText}");
        };
        _hotkeys.ApplyMacroHotkeys(mainViewModel.GetActiveTriggerHotkeyEntries());
        _tray.UpdateTooltip($"SmartMacro Studio — {mainViewModel.TriggersArmedStatusText}");

        // Pause / resume (F8 by default) is only a global shortcut while a macro plays, so F8 works normally otherwise.
        mainViewModel.Automation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AutomationViewModel.IsPlaying) or "")
            {
                _hotkeys?.SetPauseActive(mainViewModel.Automation.IsPlaying);
                if (!mainViewModel.Automation.IsPlaying)
                    _screenCapture?.ReleaseIdleSessions(forceAll: true);
            }
        };
        mainViewModel.SettingsApplied += (_, settings) =>
        {
            settingsStore.ApplyAppearance(settings);
            // "🎥 Show overlays in OBS / Discord": re-applies the capture rule to every open overlay right away.
            Services.Vision.OverlayCaptureService.AllowObsCapture = settings.ShowOverlaysInObsAndRecording;
            settingsStore.ApplyStartup(settings.StartWithWindows, _log);
            var failed = _hotkeys.Apply(settings);
            if (failed.Count > 0)
                mainViewModel.SetStatus($"These shortcuts are used by another program and won't work: {string.Join(", ", failed)}. Change them in Settings.", StatusKind.Warning);
        };

        _startupReadyTcs.TrySetResult(true);

        if (cliRequest.Action != CliActionKind.Activate)
        {
            var cliResponse = await ExecuteCliRequestOnUiThreadAsync(cliRequest, mainViewModel, window);
            if (cliRequest.Action == CliActionKind.RunMacro && cliRequest.ExitAfterRun)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.ForceClose();
                Shutdown(cliResponse.ExitCode);
            }
        }
    }

    private async Task<CliCommandResponse> HandleIpcRequestAsync(CliCommandRequest request, CancellationToken ct)
    {
        if (_startupReadyTcs is null)
            return new CliCommandResponse(CliExitCodes.BusyOrIpcError, "NotReady", "SmartMacro is still initializing.");

        bool ready;
        try
        {
            ready = await _startupReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch
        {
            ready = false;
        }

        if (!ready || _mainViewModel is not { } vm || MainWindow is not MainWindow window)
            return new CliCommandResponse(CliExitCodes.BusyOrIpcError, "NotReady", "SmartMacro is unavailable.");

        return await await Dispatcher.InvokeAsync(() => ExecuteCliRequestOnUiThreadAsync(request, vm, window));
    }

    private async Task AfterIpcResponseSentAsync(CliCommandRequest request, CliCommandResponse response)
    {
        if (request.Action == CliActionKind.RunMacro
            && request.ExitAfterRun
            && response.Status is "Completed" or "Stopped" or "Failed")
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                (MainWindow as MainWindow)?.ForceClose();
                Shutdown(response.ExitCode);
            });
        }
    }

    private async Task<CliCommandResponse> ExecuteCliRequestOnUiThreadAsync(
        CliCommandRequest request,
        MainViewModel mainViewModel,
        MainWindow window)
    {
        switch (request.Action)
        {
            case CliActionKind.Activate:
                BringToFront();
                return new CliCommandResponse(CliExitCodes.Success, "Activated", "SmartMacro Studio brought to front.");

            case CliActionKind.ArmTriggers:
                if (request.ActivateWindow && !request.NoFocus) BringToFront();
                mainViewModel.AreTriggersArmed = true;
                _log?.Info("CLI --arm: macro triggers armed.");
                return new CliCommandResponse(CliExitCodes.Success, "Armed", "Macro triggers armed.");

            case CliActionKind.DisarmTriggers:
                if (request.ActivateWindow && !request.NoFocus) BringToFront();
                mainViewModel.AreTriggersArmed = false;
                _log?.Info("CLI --disarm: macro triggers disarmed.");
                return new CliCommandResponse(CliExitCodes.Success, "Disarmed", "Macro triggers disarmed.");

            case CliActionKind.Stop:
                if (request.ActivateWindow && !request.NoFocus) BringToFront();
                await mainViewModel.Automation.StopAllAsync();
                _log?.Info("CLI --stop: automation stopped.");
                return new CliCommandResponse(CliExitCodes.Success, "Stopped", "Automation stopped.");

            case CliActionKind.RunMacro:
                return await ExecuteCliRunMacroAsync(request, mainViewModel, window);

            default:
                return new CliCommandResponse(CliExitCodes.InvalidOrNotFound, "Unsupported", $"Unsupported CLI action: {request.Action}.");
        }
    }

    private async Task<CliCommandResponse> ExecuteCliRunMacroAsync(
        CliCommandRequest request,
        MainViewModel mainViewModel,
        MainWindow window)
    {
        if (request.ActivateWindow && !request.NoFocus)
            BringToFront();

        if (!mainViewModel.Automation.IsIdle)
            return new CliCommandResponse(CliExitCodes.BusyOrIpcError, "Busy", "SmartMacro is already recording or playing another macro.");

        Macro? macroToRun;
        try
        {
            macroToRun = await ResolveCliMacroAsync(mainViewModel, request.MacroTarget);
        }
        catch (Exception ex)
        {
            _log?.Error($"CLI --run failed while loading \"{request.MacroTarget}\".", ex);
            return new CliCommandResponse(CliExitCodes.InvalidOrNotFound, "LoadError", ex.Message);
        }

        if (macroToRun is null)
        {
            var msg = $"CLI --run target not found: \"{request.MacroTarget}\".";
            _log?.Warning(msg);
            mainViewModel.SetStatus(msg, StatusKind.Warning);
            return new CliCommandResponse(CliExitCodes.InvalidOrNotFound, "NotFound", msg);
        }

        var suppressDialogsAndFocus = request.NoFocus || request.StartInTray || !window.IsVisible;
        if (!mainViewModel.Automation.TryStartMacroFromCli(
                macroToRun,
                suppressDialogsAndFocus,
                out var runTask,
                out var errorMessage,
                out var wasBusy))
        {
            return wasBusy
                ? new CliCommandResponse(CliExitCodes.BusyOrIpcError, "Busy", errorMessage)
                : new CliCommandResponse(CliExitCodes.InvalidOrNotFound, "ValidationFailed", errorMessage);
        }

        _log?.Info($"CLI --run started \"{macroToRun.Name}\" (wait={request.EffectiveWait}, exitAfterRun={request.ExitAfterRun}, noFocus={suppressDialogsAndFocus}).");
        _ = runTask.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                _log?.Error($"CLI --run failed for \"{macroToRun.Name}\".", ex);
        }, TaskScheduler.Default);

        if (!request.EffectiveWait)
            return new CliCommandResponse(CliExitCodes.Success, "Started", $"Started macro \"{macroToRun.Name}\".");

        var result = await runTask;
        return result?.Outcome switch
        {
            PlaybackOutcome.Completed => new CliCommandResponse(
                CliExitCodes.Success,
                "Completed",
                $"Completed macro \"{macroToRun.Name}\".",
                result.Outcome),
            PlaybackOutcome.Stopped => new CliCommandResponse(
                CliExitCodes.PlaybackFailedOrStopped,
                "Stopped",
                $"Stopped macro \"{macroToRun.Name}\".",
                result.Outcome),
            _ => new CliCommandResponse(
                CliExitCodes.PlaybackFailedOrStopped,
                "Failed",
                result?.Error ?? $"Macro \"{macroToRun.Name}\" failed.",
                result?.Outcome ?? PlaybackOutcome.Failed),
        };
    }

    private async Task<Macro?> ResolveCliMacroAsync(MainViewModel mainViewModel, string cliTarget)
    {
        if (string.IsNullOrWhiteSpace(cliTarget)) return null;

        var trimmed = cliTarget.Trim();
        var libraryMatch = mainViewModel.Library.Items.FirstOrDefault(item =>
            string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)
            || (Guid.TryParse(trimmed, out var parsedId) && item.Id == parsedId));

        if (libraryMatch is not null)
            return MacroJson.Clone(libraryMatch.Macro);

        if (File.Exists(trimmed))
        {
            var rawJson = await File.ReadAllTextAsync(trimmed);
            if (MacroFileFormat.TryParse(rawJson, out var parsed, out var parseError) && parsed is not null)
                return parsed;

            _log?.Warning($"CLI --run could not parse '{trimmed}': {parseError}");
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupReadyTcs?.TrySetResult(false);
        _ipcServer?.Dispose();
        _ipcServer = null;
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
        _screenCapture?.Dispose();
        _screenCapture = null;
        ScreenCapture.ReleaseAllResources(); // the two shared, lock-protected GDI capture buffers
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    /// <summary>
    /// Acquires the per-session single-instance mutex. Returns false when SmartMacro is already running.
    /// </summary>
    private bool ClaimSingleInstanceMutex()
    {
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }

        return true;
    }

    private static bool TryWriteCliOutput(string? message)
    {
        var attached = AttachConsole(AttachParentProcess);
        try
        {
            if (attached && !string.IsNullOrWhiteSpace(message))
                Console.WriteLine(message);
            return attached;
        }
        finally
        {
            if (attached)
                FreeConsole();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

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
