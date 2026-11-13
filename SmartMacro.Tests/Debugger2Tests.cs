using System.Collections.Concurrent;
using System.Text.Json;
using SmartMacro.Core.Help;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;
using Xunit;

namespace SmartMacro.Tests;

public sealed class Debugger2Tests
{
    private static async Task WaitUntilAsync(TaskCompletionSource tcs, int timeoutMs = 5000) =>
        await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    private sealed class PauseAwaiter
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiters = new();
        private int _pendingUnawaitedPauses;

        public PauseAwaiter(PlaybackEngine engine)
        {
            engine.PausedChanged += (_, paused) =>
            {
                if (!paused) return;
                while (_waiters.TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult()) return;
                }
                Interlocked.Increment(ref _pendingUnawaitedPauses);
            };
        }

        public async Task WaitForPauseAsync(int timeoutMs = 5000)
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingUnawaitedPauses);
                if (current > 0)
                {
                    if (Interlocked.CompareExchange(ref _pendingUnawaitedPauses, current - 1, current) == current)
                        return;
                    continue;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(tcs);

                var raced = Volatile.Read(ref _pendingUnawaitedPauses);
                if (raced > 0 && Interlocked.CompareExchange(ref _pendingUnawaitedPauses, raced - 1, raced) == raced)
                {
                    tcs.TrySetResult();
                }

                await WaitUntilAsync(tcs, timeoutMs);
                return;
            }
        }
    }

    [Fact]
    public async Task SubMacroBreakpoint_PausesInsideSubroutine_ExposesCallStackAndVariables()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var pauses = new PauseAwaiter(engine);

        var subStep1 = new TypeTextStep { Text = "a", CharacterDelayMs = 0 };
        var subStep2 = new TypeTextStep { Text = "b", CharacterDelayMs = 0 };
        var subMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "SubWorker",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "subCount", Type = MacroVariableType.Number, DefaultValue = "7" },
            },
            Steps = new List<MacroStep> { subStep1, subStep2 },
        };

        var rootStep1 = new TypeTextStep { Text = "r", CharacterDelayMs = 0 };
        var callStep = new CallMacroStep { TargetMacroId = subMacro.Id, TargetMacroName = subMacro.Name };
        var rootStep3 = new TypeTextStep { Text = "z", CharacterDelayMs = 0 };
        var rootMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "RootMacro",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "rootFlag", Type = MacroVariableType.Boolean, DefaultValue = "true" },
            },
            Steps = new List<MacroStep> { rootStep1, callStep, rootStep3 },
        };

        engine.MacroResolver = (id, _) => id == subMacro.Id ? subMacro : null;
        engine.HasBreakpoint = id => id == subStep2.Id;

        var runTask = engine.RunAsync(rootMacro);
        await pauses.WaitForPauseAsync();

        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.Equal(1, engine.CurrentCallDepth);

        var stack = engine.GetCallStackSnapshot();
        Assert.Equal(2, stack.Count);
        Assert.Equal(subMacro.Name, stack[0].MacroName);
        Assert.Equal(2, stack[0].StepNumber);
        Assert.Equal(1, stack[0].Depth);
        Assert.True(stack[0].IsSubRoutine);
        Assert.Contains(stack[0].VariablesSnapshot, v => v.Name == "subCount" && v.Value == "7");

        Assert.Equal(rootMacro.Name, stack[1].MacroName);
        Assert.Equal(2, stack[1].StepNumber);
        Assert.Equal(0, stack[1].Depth);
        Assert.False(stack[1].IsSubRoutine);
        Assert.Contains(stack[1].VariablesSnapshot, v => v.Name == "rootFlag" && v.Value == "true");

        lock (input.Calls)
        {
            Assert.Equal(new[] { "type r", "type a" }, input.Calls);
        }

        Assert.True(engine.Resume());
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        lock (input.Calls)
        {
            Assert.Equal(new[] { "type r", "type a", "type b", "type z" }, input.Calls);
        }
    }

    [Fact]
    public async Task StepInto_StepOver_StepOut_NavigatesNestedSubMacroCallStack()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var pauses = new PauseAwaiter(engine);

        var subB = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "SubB",
            Steps = new List<MacroStep>
            {
                new TypeTextStep { Text = "B", CharacterDelayMs = 0 },
                new TypeTextStep { Text = "C", CharacterDelayMs = 0 },
            },
        };

        var subAStep1 = new TypeTextStep { Text = "A", CharacterDelayMs = 0 };
        var callSubB = new CallMacroStep { TargetMacroId = subB.Id, TargetMacroName = subB.Name };
        var subAStep3 = new TypeTextStep { Text = "D", CharacterDelayMs = 0 };
        var subA = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "SubA",
            Steps = new List<MacroStep> { subAStep1, callSubB, subAStep3 },
        };

        var rootStep1 = new TypeTextStep { Text = "R", CharacterDelayMs = 0 };
        var callSubA = new CallMacroStep { TargetMacroId = subA.Id, TargetMacroName = subA.Name };
        var rootStep3 = new TypeTextStep { Text = "Z", CharacterDelayMs = 0 };
        var rootMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Root",
            Steps = new List<MacroStep> { rootStep1, callSubA, rootStep3 },
        };

        engine.MacroResolver = (id, _) => id == subA.Id ? subA : id == subB.Id ? subB : null;
        engine.HasBreakpoint = id => id == callSubA.Id;

        var runTask = engine.RunAsync(rootMacro);

        // Hit breakpoint at Root step 2 (CallMacro SubA):
        await pauses.WaitForPauseAsync();
        Assert.Equal(0, engine.CurrentCallDepth);
        Assert.False(engine.StepOut()); // Cannot step out of root macro

        // F11 Step Into CallMacro(SubA) -> pauses before SubA step 1 (A):
        Assert.True(engine.StepInto());
        await pauses.WaitForPauseAsync();
        Assert.Equal(1, engine.CurrentCallDepth);
        Assert.Equal(subAStep1.Id, engine.GetCallStackSnapshot()[0].StepId);
        lock (input.Calls) Assert.Equal(new[] { "type R" }, input.Calls);

        // F10 Step Over A -> pauses before CallMacro(SubB):
        Assert.True(engine.StepOver());
        await pauses.WaitForPauseAsync();
        Assert.Equal(1, engine.CurrentCallDepth);
        Assert.Equal(callSubB.Id, engine.GetCallStackSnapshot()[0].StepId);
        lock (input.Calls) Assert.Equal(new[] { "type R", "type A" }, input.Calls);

        // F11 Step Into CallMacro(SubB) -> pauses before B (depth 2):
        Assert.True(engine.StepInto());
        await pauses.WaitForPauseAsync();
        var deepStack = engine.GetCallStackSnapshot();
        Assert.Equal(2, engine.CurrentCallDepth);
        Assert.Equal(3, deepStack.Count);
        Assert.Equal(subB.Steps[0].Id, deepStack[0].StepId);

        // Shift+F11 Step Out of SubB -> executes B & C, returns and pauses before D (depth 1):
        Assert.True(engine.StepOut());
        await pauses.WaitForPauseAsync();
        Assert.Equal(1, engine.CurrentCallDepth);
        Assert.Equal(subAStep3.Id, engine.GetCallStackSnapshot()[0].StepId);
        lock (input.Calls) Assert.Equal(new[] { "type R", "type A", "type B", "type C" }, input.Calls);

        // Shift+F11 Step Out of SubA -> executes D, returns and pauses before Z (depth 0):
        Assert.True(engine.StepOut());
        await pauses.WaitForPauseAsync();
        Assert.Equal(0, engine.CurrentCallDepth);
        Assert.Equal(rootStep3.Id, engine.GetCallStackSnapshot()[0].StepId);
        lock (input.Calls) Assert.Equal(new[] { "type R", "type A", "type B", "type C", "type D" }, input.Calls);

        // F8 Resume to completion:
        Assert.True(engine.Resume());
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        lock (input.Calls) Assert.Equal(new[] { "type R", "type A", "type B", "type C", "type D", "type Z" }, input.Calls);
    }

    [Fact]
    public async Task StepOver_OnCallMacro_RunsSubMacroWithoutStoppingInsideUnlessBreakpointed()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var pauses = new PauseAwaiter(engine);

        var sub = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "HelperSub",
            Steps = new List<MacroStep>
            {
                new TypeTextStep { Text = "H", CharacterDelayMs = 0 },
                new TypeTextStep { Text = "J", CharacterDelayMs = 0 },
            },
        };

        var callStep = new CallMacroStep { TargetMacroId = sub.Id, TargetMacroName = sub.Name };
        var afterStep = new TypeTextStep { Text = "Z", CharacterDelayMs = 0 };
        var root = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "CallerRoot",
            Steps = new List<MacroStep> { callStep, afterStep },
        };

        engine.MacroResolver = (id, _) => id == sub.Id ? sub : null;
        engine.HasBreakpoint = id => id == callStep.Id;

        var runTask = engine.RunAsync(root);
        await pauses.WaitForPauseAsync();

        // F10 Step Over CallMacro executes entire sub-macro and pauses before Z:
        Assert.True(engine.StepOver());
        await pauses.WaitForPauseAsync();
        Assert.Equal(0, engine.CurrentCallDepth);
        Assert.Equal(afterStep.Id, engine.GetCallStackSnapshot()[0].StepId);
        lock (input.Calls) Assert.Equal(new[] { "type H", "type J" }, input.Calls);

        Assert.True(engine.Resume());
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task ConditionalBreakpoint_VariableExpression_And_VisionScoreRule_WorkDeterministically()
    {
        // 1. Variable conditional expression: "{hp} >= 30" in a loop
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var pauses = new PauseAwaiter(engine);

        var incStep = new SetVariableStep
        {
            VariableName = "hp",
            VariableType = MacroVariableType.Number,
            Operation = VariableOperation.Add,
            Source = VariableSourceKind.Constant,
            Value = "10",
        };
        var markStep = new TypeTextStep { Text = "T", CharacterDelayMs = 0 };
        var loopMacro = new Macro
        {
            Name = "ConditionalLoop",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps = new List<MacroStep>
            {
                new RepeatStep { Mode = RepeatMode.Times, Count = 5 },
                incStep,
                markStep,
                new EndRepeatStep(),
            },
        };

        engine.SetBreakpointRule(markStep.Id, new BreakpointRule
        {
            StepId = markStep.Id,
            Expression = "{hp} >= 30",
        });

        var runTask = engine.RunAsync(loopMacro);
        await pauses.WaitForPauseAsync();
        // Should only break once hp reached 30 (turns 1 & 2 saw hp 10 and 20 -> already typed 2 "T"s):
        lock (input.Calls) Assert.Equal(2, input.Calls.Count);
        var varsAtBreak = engine.GetCallStackSnapshot()[0].VariablesSnapshot;
        Assert.Equal("30", varsAtBreak.Single(v => v.Name == "hp").Value);

        // Clear conditional rule so remaining iterations finish cleanly:
        engine.SetBreakpointRule(markStep.Id, null);
        Assert.True(engine.Resume());
        var res1 = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, res1.Outcome);
        lock (input.Calls) Assert.Equal(5, input.Calls.Count);

        // 2. VisionScore conditional breakpoint: "VisionScore < 0.85"
        var screen = TestImages.Blocky(120, 120, seed: 10);
        var target = TestImages.Icon(16, 16, seed: 99);

        var findStep = new FindImageStep
        {
            TemplateWidth = target.Width,
            TemplateHeight = target.Height,
            TemplateData = TemplateCodec.Encode(target),
            Scope = SearchScope.PrimaryMonitor,
            MinSimilarityPercent = 90,
            TimeoutMs = 0,
            WhenNotFound = TextNotFoundBehavior.Continue,
            Action = FoundTextAction.None,
        };
        var afterVisionStep = new TypeTextStep { Text = "V", CharacterDelayMs = 0 };
        var visionMacro = new Macro
        {
            Name = "VisionScoreRule",
            Steps = new List<MacroStep> { findStep, afterVisionStep },
        };

        var visionInput = new FakeInput();
        var visionEngine = new PlaybackEngine(visionInput, capture: new FakeCapture(screen));
        var visionPauses = new PauseAwaiter(visionEngine);
        visionEngine.SetBreakpointRule(afterVisionStep.Id, new BreakpointRule
        {
            StepId = afterVisionStep.Id,
            Expression = "VisionScore < 0.85",
        });

        var visionRun = visionEngine.RunAsync(visionMacro);
        await visionPauses.WaitForPauseAsync();
        Assert.Equal(afterVisionStep.Id, visionEngine.GetCallStackSnapshot()[0].StepId);
        Assert.True(visionEngine.Resume());
        var res2 = await visionRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, res2.Outcome);
    }

    [Fact]
    public async Task Watchpoints_And_LiveVariableMutation_WhilePaused_ChangeBranchOutcome()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var pauses = new PauseAwaiter(engine);

        var setOther = new SetVariableStep { VariableName = "noise", VariableType = MacroVariableType.Number, Operation = VariableOperation.Set, Value = "9" };
        var setHp = new SetVariableStep { VariableName = "hp", VariableType = MacroVariableType.Number, Operation = VariableOperation.Set, Value = "15" };
        var ifStep = new IfStep
        {
            Condition = new Condition
            {
                Comparison = new VariableComparison
                {
                    VariableName = "hp",
                    Operator = VariableCompareOperator.LessThan,
                    RightValue = "20",
                },
            },
        };
        var lowBranch = new TypeTextStep { Text = "L", CharacterDelayMs = 0 };
        var okBranch = new TypeTextStep { Text = "O", CharacterDelayMs = 0 };

        var macro = new Macro
        {
            Name = "WatchAndMutate",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "100" },
                new() { Name = "noise", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps = new List<MacroStep>
            {
                setOther,
                setHp,
                ifStep,
                lowBranch,
                new ElseStep(),
                okBranch,
                new EndIfStep(),
            },
        };

        // Watch hp with condition {hp} < 25 -> must ignore noise change, then pause right after setHp before ifStep runs:
        engine.SetWatchpoints(new[]
        {
            new VariableWatchpoint { VariableName = "hp", Enabled = true, Expression = "{hp} < 25" },
        });

        // Cannot mutate while idle:
        Assert.False(engine.TrySetLiveVariable("hp", "80"));

        var runTask = engine.RunAsync(macro);
        await pauses.WaitForPauseAsync();

        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.Equal(ifStep.Id, engine.GetCallStackSnapshot()[0].StepId);
        Assert.Equal("15", engine.GetCallStackSnapshot()[0].VariablesSnapshot.Single(v => v.Name == "hp").Value);

        // Invalid numeric edit rejected cleanly:
        Assert.False(engine.TrySetLiveVariable("hp", "not-a-number", out var err));
        Assert.NotNull(err);

        // Mutate live variable hp back to 80 while paused so the Else branch executes instead of If-True:
        Assert.True(engine.TrySetLiveVariable("hp", "80", out err));
        Assert.Null(err);

        Assert.True(engine.Resume());
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        lock (input.Calls) Assert.Equal(new[] { "type O" }, input.Calls);
    }

    [Fact]
    public async Task ExecutionTrace_RingBufferCapsAt200_And_SavesFailureTraceBesideSnapshot()
    {
        // 1. Ring buffer capacity N = 200 over 235 executed steps:
        var input = new FakeInput();
        var engine = new PlaybackEngine(input);
        var ringMacro = new Macro
        {
            Name = "TraceRing",
            Steps = new List<MacroStep>
            {
                new RepeatStep { Mode = RepeatMode.Times, Count = 235 },
                new DelayStep { DurationMs = 0 },
                new EndRepeatStep(),
            },
        };

        var ringRes = await engine.RunAsync(ringMacro);
        Assert.Equal(PlaybackOutcome.Completed, ringRes.Outcome);
        Assert.Equal(PlaybackEngine.ExecutionTraceCapacity, ringRes.Trace.Count);
        Assert.Equal(200, ringRes.Trace.Count);
        Assert.True(ringRes.Trace[0].SequenceNumber > 1);
        Assert.Equal(ringRes.Trace[^1].SequenceNumber, ringRes.Trace[0].SequenceNumber + 199);

        // 2. Failure inside nested sub-macro writes last-failure-trace.json + FailureCallStack:
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartMacro_TraceTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pixel = new BgraImage(20, 20, new byte[20 * 20 * 4]);
            var failSub = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "SubFail",
                Steps = new List<MacroStep>
                {
                    new FindColorStep
                    {
                        Color = "#FF00FF",
                        Tolerance = 2,
                        TimeoutMs = 0,
                        WhenNotFound = TextNotFoundBehavior.StopMacro,
                    },
                },
            };
            var root = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "RootFail",
                Steps = new List<MacroStep>
                {
                    new CallMacroStep { TargetMacroId = failSub.Id, TargetMacroName = failSub.Name },
                },
            };

            var failEngine = new PlaybackEngine(new FakeInput(), capture: new FakeCapture(pixel))
            {
                FailureSnapshotDirectory = tempDir,
                MacroResolver = (id, _) => id == failSub.Id ? failSub : null,
            };

            var failRes = await failEngine.RunAsync(root);
            Assert.Equal(PlaybackOutcome.Failed, failRes.Outcome);
            Assert.NotNull(failRes.FailureTracePath);
            Assert.Equal("last-failure-trace.json", Path.GetFileName(failRes.FailureTracePath));
            Assert.True(File.Exists(failRes.FailureTracePath!));
            Assert.Equal(2, failRes.FailureCallStack.Count);
            Assert.Equal("SubFail", failRes.FailureCallStack[0].MacroName);
            Assert.Equal("RootFail", failRes.FailureCallStack[1].MacroName);

            var json = await File.ReadAllTextAsync(failRes.FailureTracePath!);
            var doc = JsonSerializer.Deserialize<ExecutionTraceDocument>(json, MacroJson.Options);
            Assert.NotNull(doc);
            Assert.Equal("RootFail", doc!.MacroName);
            Assert.Equal(PlaybackOutcome.Failed, doc.Outcome);
            Assert.Equal(2, doc.CallStack.Count);
            Assert.Contains(doc.Entries, e => e.Outcome == ExecutionTraceOutcome.Failed && e.MacroName == "SubFail");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DebugConditionEvaluator_And_ManualCatalog_SupportDebugger2Rules()
    {
        Assert.True(DebugConditionEvaluator.TryParseExpression("{hp} < 20", out var cmp, out var neg, out _));
        Assert.False(neg);
        Assert.Equal("hp", cmp!.VariableName);
        Assert.Equal(VariableCompareOperator.LessThan, cmp.Operator);
        Assert.Equal("20", cmp.RightValue);

        Assert.True(DebugConditionEvaluator.TryParseExpression("VisionScore < 0.85", out var vsCmp, out _, out _));
        Assert.Equal("VisionScore", vsCmp!.VariableName);
        Assert.Equal(VariableCompareOperator.LessThan, vsCmp.Operator);
        Assert.Equal("0.85", vsCmp.RightValue);

        var shortcuts = ManualCatalog.Shortcuts("F9", "F6", "F8");
        Assert.Contains(ManualCatalog.FilterShortcuts(shortcuts, "step into"), s => s.Keys == "F11");
        Assert.Contains(ManualCatalog.FilterShortcuts(shortcuts, "step out"), s => s.Keys == "Shift+F11");
        Assert.Contains(ManualCatalog.Filter("watchpoint trace"), a => a.Id == "debugger-breakpoints");
    }
}
