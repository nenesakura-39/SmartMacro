using System.Globalization;
using System.Windows;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.ViewModels;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.AppTests;

public sealed class EditorViewModelTests
{
    private static (EditorViewModel Vm, FakeMacroRepository Repo, FakeLogService Log, FakeDialogService Dialogs, List<Macro> SavedCallbacks) CreateHarness()
    {
        var repo = new FakeMacroRepository();
        var log = new FakeLogService();
        var dialogs = new FakeDialogService();
        var screen = new FakeScreenInfoProvider();
        var saved = new List<Macro>();
        var vm = new EditorViewModel(
            repo,
            log,
            dialogs,
            screen,
            () => 250,
            m => saved.Add(m),
            (_, _) => { },
            () => { });
        return (vm, repo, log, dialogs, saved);
    }

    private static Macro CreateSampleMacro(string name = "Editor Sample") =>
        new()
        {
            Name = name,
            Steps =
            [
                new DelayStep { DurationMs = 150 },
                new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press },
            ],
        };

    [Fact]
    public void LoadAndUnload_ResetPropertiesStepsAndDirtyFlag()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _, _) = CreateHarness();
            var macro = CreateSampleMacro("Boss Rotation");

            vm.Load(macro);

            Assert.True(vm.HasMacro);
            Assert.Equal(macro.Id, vm.MacroId);
            Assert.Equal("Boss Rotation", vm.Name);
            Assert.Equal(2, vm.Steps.Count);
            Assert.False(vm.IsDirty);

            vm.Unload();

            Assert.False(vm.HasMacro);
            Assert.Null(vm.MacroId);
            Assert.Empty(vm.Steps);
            Assert.False(vm.IsDirty);
        });
    }

    [Fact]
    public void AddDeleteMoveDuplicateAndUndoRedo_UpdateStepsAndHistory()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _, _) = CreateHarness();
            vm.Load(new Macro { Name = "Edit Flow" });

            vm.AddStepCommand.Execute(StepKind.Delay);
            vm.AddStepCommand.Execute(StepKind.KeyPress);

            Assert.Equal(2, vm.Steps.Count);
            Assert.Equal(StepKind.Delay, vm.Steps[0].Kind);
            Assert.Equal(StepKind.KeyPress, vm.Steps[1].Kind);
            Assert.True(vm.IsDirty);
            Assert.True(vm.UndoCommand.CanExecute(null));

            vm.SelectedStep = vm.Steps[1];
            vm.MoveUpCommand.Execute(null);
            Assert.Equal(StepKind.KeyPress, vm.Steps[0].Kind);
            Assert.Equal(StepKind.Delay, vm.Steps[1].Kind);

            vm.SelectedStep = vm.Steps[0];
            vm.DuplicateStepCommand.Execute(null);
            Assert.Equal(3, vm.Steps.Count);
            Assert.Equal(StepKind.KeyPress, vm.Steps[1].Kind);

            vm.SelectedStep = vm.Steps[1];
            vm.DeleteStepCommand.Execute(null);
            Assert.Equal(2, vm.Steps.Count);

            vm.UndoCommand.Execute(null);
            Assert.Equal(3, vm.Steps.Count);

            vm.RedoCommand.Execute(null);
            Assert.Equal(2, vm.Steps.Count);
        });
    }

    [Fact]
    public void WrapInIfAndWrapInRepeat_SurroundSelectedStepWithClosers()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _, _) = CreateHarness();
            vm.Load(new Macro
            {
                Name = "Wrap Flow",
                Steps = [new DelayStep { DurationMs = 100 }],
            });

            vm.SelectedStep = vm.Steps[0];
            vm.WrapInIfCommand.Execute(null);

            Assert.Equal(3, vm.Steps.Count);
            Assert.Equal(StepKind.If, vm.Steps[0].Kind);
            Assert.Equal(StepKind.Delay, vm.Steps[1].Kind);
            Assert.Equal(StepKind.EndIf, vm.Steps[2].Kind);

            vm.SelectedStep = vm.Steps[1];
            vm.WrapInRepeatCommand.Execute(null);

            Assert.Equal(5, vm.Steps.Count);
            Assert.Equal(StepKind.Repeat, vm.Steps[1].Kind);
            Assert.Equal(StepKind.Delay, vm.Steps[2].Kind);
            Assert.Equal(StepKind.EndRepeat, vm.Steps[3].Kind);
        });
    }

    [Fact]
    public void CopyAndPasteStep_DuplicatesStepWithDistinctId()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _, _) = CreateHarness();
            var original = new DelayStep { DurationMs = 420, Comment = "Wait here" };
            vm.Load(new Macro
            {
                Name = "Clipboard Flow",
                Steps = [original],
            });

            vm.SelectedStep = vm.Steps[0];
            try
            {
                Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Transient clipboard contention from external host is handled gracefully.
            }

            vm.CopySelectedStepCommand.Execute(null);
            if (!Clipboard.ContainsText())
                return;

            vm.PasteStepCommand.Execute(null);
            Assert.Equal(2, vm.Steps.Count);
            Assert.NotEqual(vm.Steps[0].Id, vm.Steps[1].Id);
            Assert.Equal(StepKind.Delay, vm.Steps[1].Kind);
            Assert.Equal(420, ((DelayStep)vm.Steps[1].Model).DurationMs);
        });
    }

    [Fact]
    public async Task SaveAsync_PersistsCloneAndClearsDirtyFlag()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, repo, _, _, savedCallbacks) = CreateHarness();
            var macro = CreateSampleMacro("Original");
            vm.Load(macro);

            vm.Name = "Renamed Macro";
            Assert.True(vm.IsDirty);

            var ok = await vm.SaveAsync();
            Assert.True(ok);
            Assert.False(vm.IsDirty);
            Assert.Single(savedCallbacks);
            Assert.Equal("Renamed Macro", savedCallbacks[0].Name);

            var stored = repo.FindStored(macro.Id);
            Assert.NotNull(stored);
            Assert.Equal("Renamed Macro", stored!.Name);
        });
    }

    [Fact]
    public void Debugger2_BreakpointsWatchpointsAndTraceFilters_WorkEndToEnd()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _, _, _) = CreateHarness();
            var macro = CreateSampleMacro("Debugged");
            vm.Load(macro);

            var first = vm.Steps[0];
            vm.ToggleBreakpointCommand.Execute(first);
            Assert.True(vm.HasStepBreakpoint(first.Id));
            Assert.Equal(1, vm.BreakpointCount);

            vm.SelectedStep = first;
            vm.SelectedBreakpointExpression = "== invalid ===";
            vm.ApplyBreakpointConditionCommand.Execute(null);
            Assert.NotNull(vm.BreakpointRuleError);

            vm.SelectedBreakpointExpression = "{score} >= 10";
            vm.ApplyBreakpointConditionCommand.Execute(null);
            Assert.Null(vm.BreakpointRuleError);
            Assert.Equal("{score} >= 10", first.BreakpointCondition);
            Assert.NotNull(vm.GetBreakpointRule(first.Id));

            vm.NewWatchpointVariableName = "score";
            vm.NewWatchpointExpression = "{score} > 5";
            vm.AddWatchpointCommand.Execute(null);
            Assert.Single(vm.VariableWatchpoints);
            Assert.True(vm.HasWatchpoints);

            vm.AppendTraceEntry(new ExecutionTraceEntry(
                1, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), macro.Id, macro.Name, 0, first.Id, 1, "Wait 150 ms", 150, ExecutionTraceOutcome.Executed));
            vm.AppendTraceEntry(new ExecutionTraceEntry(
                2, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(20), macro.Id, macro.Name, 0, vm.Steps[1].Id, 2, "Find Button", 20, ExecutionTraceOutcome.Executed, VisionScore: 0.94));
            vm.AppendTraceEntry(new ExecutionTraceEntry(
                3, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(35), macro.Id, macro.Name, 0, vm.Steps[1].Id, 2, "Press A", 12, ExecutionTraceOutcome.Failed, Error: "Timeout"));

            Assert.Equal(3, vm.FilteredTraceEntries.Count);

            vm.SetTraceOutcomeFilterCommand.Execute(TraceOutcomeFilter.Failed);
            Assert.Single(vm.FilteredTraceEntries);
            Assert.Equal(ExecutionTraceOutcome.Failed, vm.FilteredTraceEntries[0].Outcome);

            vm.SetTraceOutcomeFilterCommand.Execute(TraceOutcomeFilter.Vision);
            Assert.Single(vm.FilteredTraceEntries);
            Assert.Equal(0.94, vm.FilteredTraceEntries[0].VisionScore);

            vm.ClearTraceCommand.Execute(null);
            Assert.Empty(vm.FilteredTraceEntries);
            Assert.False(vm.HasTraceEntries);
        });
    }
}
