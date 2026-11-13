using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Services;
using SmartMacro.Services.Input;
using SmartMacro.ViewModels;

namespace SmartMacro.AppTests;

public sealed class AutomationViewModelTests
{
    private static (
        AutomationViewModel Vm,
        FakeInputSimulator Input,
        InstantPlaybackClock Clock,
        FakeRunModeHost Host,
        FakeLogService Log,
        FakeDialogService Dialogs,
        List<(string Text, StatusKind Kind)> StatusUpdates) CreateHarness(Macro? targetMacro = null)
    {
        var input = new FakeInputSimulator();
        var clock = new InstantPlaybackClock();
        var ocr = new FakeOcrService();
        var capture = new FakeScreenCapture();
        var screen = new FakeScreenInfoProvider();
        var engine = new PlaybackEngine(input, clock, ocr, capture, screen);

        var recorder = new FakeInputRecorder();
        var gameRecorder = new FakeRelativeInputRecorder();
        var smartRecorder = new FakeSmartInputRecorder();
        var emergency = new EmergencyStopWatcher();
        var host = new FakeRunModeHost();
        var log = new FakeLogService();
        var dialogs = new FakeDialogService();
        var statusUpdates = new List<(string, StatusKind)>();

        var hooks = new AutomationHooks
        {
            GetPlayTarget = () => targetMacro is null ? null : MacroJson.Clone(targetMacro),
            GetPlayTargetInfo = () => targetMacro is null
                ? null
                : new PlayTargetInfo(targetMacro.Name, targetMacro.RepeatCount, targetMacro.Speed),
            BeforeRecording = () => Task.FromResult(true),
            RecordingFinished = _ => Task.CompletedTask,
            Settings = () => new AppSettings { ShowRunOverlay = false, MinimizeDuringRun = false },
            SetStatus = (text, kind) => statusUpdates.Add((text, kind)),
        };

        var vm = new AutomationViewModel(
            engine,
            recorder,
            gameRecorder,
            smartRecorder,
            emergency,
            host,
            ocr,
            log,
            dialogs,
            StaTestRunner.CurrentDispatcher,
            hooks);

        return (vm, input, clock, host, log, dialogs, statusUpdates);
    }

    [Fact]
    public async Task PlayMacroAsync_CompletesAndRecordsSimulatedInput()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var macro = new Macro
            {
                Name = "Quick Key",
                Steps =
                [
                    new DelayStep { DurationMs = 10 },
                    new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press, HoldMs = 15 },
                ],
            };

            var (vm, input, _, host, _, _, _) = CreateHarness(macro);
            try
            {
                await vm.PlayMacroAsync(macro);

                Assert.Equal(AutomationMode.Idle, vm.Mode);
                Assert.NotNull(vm.LastResult);
                Assert.Equal(PlaybackOutcome.Completed, vm.LastResult!.Outcome);
                Assert.True(vm.HasLastRunSummary);
                Assert.Equal(1, host.EnterCount);
                Assert.Equal(1, host.ExitCount);
                Assert.Contains("Key(65,True)", input.Operations);
                Assert.Contains("Key(65,False)", input.Operations);
                Assert.Null(vm.CurrentPlayingStepId);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public async Task PauseResumeAndStop_TransitionModesAndClearRunningStepGuard()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var step = new DelayStep { DurationMs = 5000 };
            var macro = new Macro
            {
                Name = "Pausable Macro",
                Steps = [step],
            };

            var (vm, _, clock, host, _, _, _) = CreateHarness(macro);
            clock.DelayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var started = vm.TryStartMacroFromCli(
                    macro,
                    suppressDialogsAndFocus: true,
                    out var runTask,
                    out var error,
                    out var wasBusy);

                Assert.True(started, error);
                Assert.False(wasBusy);
                Assert.Equal(AutomationMode.Playing, vm.Mode);

                // Wait until playback reaches the gated Delay step.
                await Task.Delay(120);
                StaTestRunner.DoEvents();

                Assert.True(vm.PauseResumeCommand.CanExecute(null));
                vm.PauseResumeCommand.Execute(null);
                await Task.Delay(50);
                StaTestRunner.DoEvents();
                Assert.Equal(AutomationMode.Paused, vm.Mode);

                vm.PauseResumeCommand.Execute(null);
                await Task.Delay(50);
                StaTestRunner.DoEvents();
                Assert.Equal(AutomationMode.Playing, vm.Mode);

                vm.StopCommand.Execute(null);
                Assert.Null(vm.CurrentPlayingStepId);
                clock.DelayGate.TrySetCanceled();

                var result = await runTask;
                StaTestRunner.DoEvents();

                Assert.NotNull(result);
                Assert.Equal(PlaybackOutcome.Stopped, result!.Outcome);
                Assert.Equal(AutomationMode.Idle, vm.Mode);
                Assert.Null(vm.CurrentPlayingStepId);
                Assert.Equal(1, host.ExitCount);
            }
            finally
            {
                clock.DelayGate.TrySetCanceled();
                vm.Dispose();
            }
        });
    }
}
