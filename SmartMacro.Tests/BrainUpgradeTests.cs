using SmartMacro.Core.Help;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

internal sealed class FakeClipboardReader : IClipboardReader
{
    public string? Text { get; set; }
    public string? ReadText() => Text;
}

public class VariableStoreTests
{
    [Fact]
    public void Defaults_operations_and_case_insensitive_lookup_work_across_all_types()
    {
        var store = new VariableStore(new[]
        {
            new MacroVariableDefinition { Name = "gold", Type = MacroVariableType.Number, DefaultValue = "100" },
            new MacroVariableDefinition { Name = "status", Type = MacroVariableType.Text, DefaultValue = "Idle" },
            new MacroVariableDefinition { Name = "ready", Type = MacroVariableType.Boolean, DefaultValue = "true" },
        });

        Assert.True(store.TryGetNumber("GOLD", out var initialGold));
        Assert.Equal(100, initialGold);
        Assert.Equal("Idle", store.GetText("Status"));
        Assert.Equal("true", store.GetText("READY"));

        store.Apply("gold", MacroVariableType.Number, VariableOperation.Add, "250");
        store.Apply("gold", MacroVariableType.Number, VariableOperation.Subtract, "50");
        Assert.True(store.TryGetNumber("gold", out var updatedGold));
        Assert.Equal(300, updatedGold);

        store.Apply("status", MacroVariableType.Text, VariableOperation.AppendText, " -> Farming");
        Assert.Equal("Idle -> Farming", store.GetText("status"));

        store.Apply("ready", MacroVariableType.Boolean, VariableOperation.Set, "false");
        Assert.Equal("false", store.GetText("ready"));
    }

    [Fact]
    public void Interpolate_replaces_known_variables_and_leaves_unknown_tokens_untouched()
    {
        var store = new VariableStore(new[]
        {
            new MacroVariableDefinition { Name = "item", Type = MacroVariableType.Text, DefaultValue = "Iron Ore" },
            new MacroVariableDefinition { Name = "qty", Type = MacroVariableType.Number, DefaultValue = "42" },
        });

        var result = store.Interpolate("Sell {qty}x {ITEM} ({unknown})");
        Assert.Equal("Sell 42x Iron Ore ({unknown})", result);
    }

    [Theory]
    [InlineData("Gold: 12,450", 12450)]
    [InlineData("$1,299.50", 1299.5)]
    [InlineData("-42", -42)]
    [InlineData("1O0", 100)]
    [InlineData("HP 85 / 100", 85)]
    public void TryParseScreenNumber_handles_currency_commas_negatives_and_ocr_lookalikes(string raw, double expected)
    {
        Assert.True(VariableStore.TryParseScreenNumber(raw, out var value));
        Assert.Equal(expected, value, precision: 3);
    }

    [Fact]
    public void TryParseScreenNumber_returns_false_when_no_digits_exist()
    {
        Assert.False(VariableStore.TryParseScreenNumber("No numbers here!", out _));
    }

    [Fact]
    public void Compare_supports_all_eight_operators_across_numbers_text_and_booleans_plus_negate()
    {
        var store = new VariableStore(new[]
        {
            new MacroVariableDefinition { Name = "score", Type = MacroVariableType.Number, DefaultValue = "100" },
            new MacroVariableDefinition { Name = "title", Type = MacroVariableType.Text, DefaultValue = "Quest Complete" },
            new MacroVariableDefinition { Name = "flag", Type = MacroVariableType.Boolean, DefaultValue = "true" },
        });

        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.Equals, RightValue = "100" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.NotEquals, RightValue = "99" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.GreaterThan, RightValue = "50" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.GreaterOrEqual, RightValue = "100" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.LessThan, RightValue = "101" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "score", Operator = VariableCompareOperator.LessOrEqual, RightValue = "100" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "title", Operator = VariableCompareOperator.Contains, RightValue = "complete" }, false, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "title", Operator = VariableCompareOperator.StartsWith, RightValue = "Quest" }, false, out _));
        Assert.False(store.EvaluateComparison(new VariableComparison { VariableName = "title", Operator = VariableCompareOperator.StartsWith, RightValue = "Quest" }, negate: true, out _));
        Assert.True(store.EvaluateComparison(new VariableComparison { VariableName = "flag", Operator = VariableCompareOperator.Equals, RightValue = "true" }, false, out _));
    }
}

public class MemoryAndScreenReadingPlaybackTests
{
    [Fact]
    public async Task End_to_end_farm_until_gold_reaches_10000_via_screen_ocr_number()
    {
        var ocr = new FakeOcr()
            .Then(("Gold: 2,500", 100, 40))
            .Then(("Gold: 6,200", 100, 40))
            .Then(("Gold: 10,400", 100, 40));

        var macro = new Macro
        {
            Name = "Farm Until 10k Gold",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "gold", Type = MacroVariableType.Number, DefaultValue = "0" },
                new() { Name = "runs", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps =
            {
                new RepeatStep
                {
                    Mode = RepeatMode.Until,
                    MaxIterations = 10,
                    Condition = new Condition
                    {
                        Comparison = new VariableComparison
                        {
                            VariableName = "gold",
                            Operator = VariableCompareOperator.GreaterOrEqual,
                            RightValue = "10000",
                        },
                    },
                },
                new SetVariableStep
                {
                    VariableName = "runs",
                    Operation = VariableOperation.Add,
                    Source = VariableSourceKind.Constant,
                    Value = "1",
                },
                new SetVariableStep
                {
                    VariableName = "gold",
                    Operation = VariableOperation.Set,
                    Source = VariableSourceKind.ScreenOcrNumber,
                    RegionX = 80,
                    RegionY = 20,
                    RegionWidth = 200,
                    RegionHeight = 60,
                },
                new EndRepeatStep(),
            },
        };

        IReadOnlyList<VariableSnapshot>? lastLiveSnapshot = null;
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), ocr);
        engine.VariablesChanged += (_, snap) => lastLiveSnapshot = snap;

        var res = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Equal("10400", res.FinalVariables.Single(v => v.Name == "gold").Value);
        Assert.Equal("3", res.FinalVariables.Single(v => v.Name == "runs").Value);
        Assert.NotNull(lastLiveSnapshot);
    }

    [Fact]
    public async Task Variable_interpolation_clipboard_found_coords_and_loop_counter_work_in_playback()
    {
        var ocr = new FakeOcr().Then(("Confirm", 400, 250)); // width = 70, height = 20 -> center (435, 260)
        var input = new FakeInput();
        var clock = new FakeClock();
        var clipboard = new FakeClipboardReader { Text = "PROMO-77" };

        var macro = new Macro
        {
            Name = "Interpolate & Sources",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "btn", Type = MacroVariableType.Text, DefaultValue = "Confirm" },
                new() { Name = "code", Type = MacroVariableType.Text, DefaultValue = "" },
                new() { Name = "foundX", Type = MacroVariableType.Number, DefaultValue = "0" },
                new() { Name = "iter", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps =
            {
                new SetVariableStep
                {
                    VariableName = "code",
                    Source = VariableSourceKind.ClipboardText,
                },
                new FindTextStep
                {
                    Text = "{btn}",
                    TimeoutMs = 500,
                    Action = FoundTextAction.None,
                },
                new SetVariableStep
                {
                    VariableName = "foundX",
                    Source = VariableSourceKind.LastFoundX,
                },
                new RepeatStep { Mode = RepeatMode.Times, Count = 2 },
                new SetVariableStep
                {
                    VariableName = "iter",
                    Source = VariableSourceKind.LoopCounter,
                },
                new EndRepeatStep(),
                new TypeTextStep
                {
                    Text = "{code}@{foundX}#{iter}",
                },
            },
        };

        var engine = new PlaybackEngine(input, clock, ocr) { Clipboard = clipboard };
        var res = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        var typed = string.Concat(input.Calls.Where(c => c.StartsWith("type ", StringComparison.Ordinal)).Select(c => c["type ".Length..]));
        Assert.Equal("PROMO-77@435#2", typed);
    }
}

public class ResiliencePoliciesAndCheckpointsTests
{
    [Fact]
    public async Task StepFailurePolicy_Retry_succeeds_when_second_attempt_finds_text()
    {
        var ocr = new FakeOcr()
            .ThenNothing()
            .Then(("Claim", 200, 100));

        var find = new FindTextStep
        {
            Text = "Claim",
            TimeoutMs = 0, // single check per attempt
            Action = FoundTextAction.Click,
            FailurePolicy = new StepFailurePolicy
            {
                Action = StepFailureAction.Retry,
                RetryCount = 2,
                RetryDelayMs = 200,
            },
        };

        var input = new FakeInput();
        var clock = new FakeClock();
        var engine = new PlaybackEngine(input, clock, ocr);
        var res = await engine.RunAsync(Aaa.MacroWith(find));

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.True(clock.TotalDelayed.TotalMilliseconds >= 200);
        Assert.Contains(input.Calls, c => c.StartsWith("move ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StepFailurePolicy_RunRecoveryMacro_invokes_recovery_submacro_and_continues()
    {
        var recovery = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Dismiss Banner",
            Steps = { new TypeTextStep { Text = "RECOVERED" } },
        };

        var main = Aaa.MacroWith(
            new FindTextStep
            {
                Text = "MissingButton",
                TimeoutMs = 0,
                FailurePolicy = new StepFailurePolicy
                {
                    Action = StepFailureAction.RunRecoveryMacro,
                    RecoveryMacroId = recovery.Id,
                    RecoveryMacroName = recovery.Name,
                },
            },
            new TypeTextStep { Text = "CONTINUED" });

        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock(), new FakeOcr().ThenNothing())
        {
            MacroResolver = (id, name) => id == recovery.Id || name == recovery.Name ? recovery : null,
        };

        var res = await engine.RunAsync(main);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        var typed = string.Concat(input.Calls.Where(c => c.StartsWith("type ", StringComparison.Ordinal)).Select(c => c["type ".Length..]));
        Assert.Equal("RECOVEREDCONTINUED", typed);
    }

    [Fact]
    public async Task StepFailurePolicy_RestartFromCheckpoint_jumps_to_top_level_checkpoint_and_completes()
    {
        var ocr = new FakeOcr()
            .ThenNothing()
            .Then(("Ready", 300, 200));

        var main = new Macro
        {
            Name = "Checkpoint Recovery",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "attempts", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps =
            {
                new CheckpointStep { Name = "lobby" },
                new SetVariableStep
                {
                    VariableName = "attempts",
                    Operation = VariableOperation.Add,
                    Source = VariableSourceKind.Constant,
                    Value = "1",
                },
                new FindTextStep
                {
                    Text = "Ready",
                    TimeoutMs = 0,
                    FailurePolicy = new StepFailurePolicy
                    {
                        Action = StepFailureAction.RestartFromCheckpoint,
                        CheckpointName = "lobby",
                    },
                },
            },
        };

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), ocr);
        var res = await engine.RunAsync(main);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Equal("2", res.FinalVariables.Single(v => v.Name == "attempts").Value);
    }

    [Fact]
    public async Task Checkpoint_restarts_are_capped_at_MaxCheckpointRestarts_to_prevent_infinite_loops()
    {
        var main = Aaa.MacroWith(
            new CheckpointStep { Name = "start" },
            new FindTextStep
            {
                Text = "NeverAppears",
                TimeoutMs = 0,
                FailurePolicy = new StepFailurePolicy
                {
                    Action = StepFailureAction.RestartFromCheckpoint,
                    CheckpointName = "start",
                },
            });

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), new FakeOcr().ThenNothing());
        var res = await engine.RunAsync(main);

        Assert.Equal(PlaybackOutcome.Failed, res.Outcome);
        Assert.Contains($"{PlaybackEngine.MaxCheckpointRestarts}", res.Error ?? string.Empty);
    }

    [Fact]
    public void Validator_rejects_checkpoint_inside_nested_block_and_unknown_checkpoint_target()
    {
        var nested = Aaa.MacroWith(
            new RepeatStep { Mode = RepeatMode.Times, Count = 2 },
            new CheckpointStep { Name = "insideLoop" },
            new EndRepeatStep());

        var nestedIssues = MacroValidator.Validate(nested);
        Assert.Contains(nestedIssues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("top level", StringComparison.OrdinalIgnoreCase));

        var badJump = Aaa.MacroWith(
            new CheckpointStep { Name = "alpha" },
            new FindTextStep
            {
                Text = "Go",
                FailurePolicy = new StepFailurePolicy
                {
                    Action = StepFailureAction.RestartFromCheckpoint,
                    CheckpointName = "beta",
                },
            });

        var badJumpIssues = MacroValidator.Validate(badJump);
        Assert.Contains(badJumpIssues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("beta", StringComparison.OrdinalIgnoreCase));
    }
}

public class WatcherInterruptTests
{
    [Fact]
    public async Task Watcher_ClickFoundSpot_dismisses_popup_between_steps_and_respects_cooldown()
    {
        // Step 1 boundary: watcher sees "Close" at (500, 300) -> width 50, height 20 -> center (525, 310) -> clicks it!
        // Step 2 boundary (300ms later, still within 5000ms cooldown): watcher does not re-fire.
        var ocr = new FakeOcr().Then(("Close", 500, 300));
        var input = new FakeInput();
        var clock = new FakeClock();

        var macro = new Macro
        {
            Name = "Guarded Macro",
            Watchers = new List<MacroWatcher>
            {
                new()
                {
                    Name = "Ad Closer",
                    Enabled = true,
                    Action = WatcherActionKind.ClickFoundSpot,
                    CheckIntervalMs = 200,
                    CooldownMs = 5_000,
                    ClickOffsetX = 5,
                    ClickOffsetY = -5,
                    Condition = new Condition
                    {
                        Check = new FindTextStep { Text = "Close", TimeoutMs = 0 },
                    },
                },
            },
            Steps =
            {
                new DelayStep { DurationMs = 300 },
                new TypeTextStep { Text = "WORK" },
            },
        };

        var engine = new PlaybackEngine(input, clock, ocr);
        var res = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Equal(1, ocr.Reads); // Cooldown prevented re-checking before step 2
        Assert.Contains("move 530,305", input.Calls);
        var typed = string.Concat(input.Calls.Where(c => c.StartsWith("type ", StringComparison.Ordinal)).Select(c => c["type ".Length..]));
        Assert.Equal("WORK", typed);
    }

    [Fact]
    public async Task Watcher_StopMacro_halts_cleanly_when_disconnect_banner_appears()
    {
        var ocr = new FakeOcr().Then(("Disconnected", 400, 300));
        var input = new FakeInput();

        var macro = new Macro
        {
            Name = "Stop On Disconnect",
            Watchers = new List<MacroWatcher>
            {
                new()
                {
                    Name = "Disconnect Guard",
                    Enabled = true,
                    Action = WatcherActionKind.StopMacro,
                    Condition = new Condition
                    {
                        Check = new FindTextStep { Text = "Disconnected", TimeoutMs = 0 },
                    },
                },
            },
            Steps =
            {
                new TypeTextStep { Text = "SHOULD_NOT_TYPE" },
            },
        };

        var engine = new PlaybackEngine(input, new FakeClock(), ocr);
        var res = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Failed, res.Outcome);
        Assert.Empty(input.Calls);
    }
}

public class SubMacroAndBundlingTests
{
    [Fact]
    public async Task CallMacroStep_passes_inputs_isolates_callee_memory_and_copies_outputs_back()
    {
        var sellSubMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Calculate Sale",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "count", Type = MacroVariableType.Number, DefaultValue = "0" },
                new() { Name = "earned", Type = MacroVariableType.Number, DefaultValue = "0" },
                new() { Name = "internalTemp", Type = MacroVariableType.Text, DefaultValue = "secret" },
            },
            Steps =
            {
                new SetVariableStep
                {
                    VariableName = "earned",
                    Operation = VariableOperation.Set,
                    Source = VariableSourceKind.Constant,
                    Value = "{count}",
                },
                new SetVariableStep
                {
                    VariableName = "earned",
                    Operation = VariableOperation.Add,
                    Source = VariableSourceKind.Constant,
                    Value = "100",
                },
            },
        };

        var caller = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Main Routine",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "items", Type = MacroVariableType.Number, DefaultValue = "25" },
                new() { Name = "profit", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps =
            {
                new CallMacroStep
                {
                    TargetMacroId = sellSubMacro.Id,
                    TargetMacroName = sellSubMacro.Name,
                    Inputs = new List<VariableBinding>
                    {
                        new() { TargetVariable = "count", SourceExpression = "{items}" },
                    },
                    Outputs = new List<VariableBinding>
                    {
                        new() { SourceExpression = "earned", TargetVariable = "profit" },
                    },
                },
            },
        };

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock())
        {
            MacroResolver = (id, name) => id == sellSubMacro.Id || name == sellSubMacro.Name ? sellSubMacro : null,
        };

        var res = await engine.RunAsync(caller);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        Assert.Equal("125", res.FinalVariables.Single(v => v.Name == "profit").Value);
        Assert.DoesNotContain(res.FinalVariables, v => v.Name == "internalTemp");
    }

    [Fact]
    public async Task Validator_and_PlaybackEngine_detect_direct_and_indirect_submacro_call_cycles()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var macroA = new Macro
        {
            Id = idA,
            Name = "Macro A",
            Steps = { new CallMacroStep { TargetMacroId = idB, TargetMacroName = "Macro B" } },
        };
        var macroB = new Macro
        {
            Id = idB,
            Name = "Macro B",
            Steps = { new CallMacroStep { TargetMacroId = idA, TargetMacroName = "Macro A" } },
        };

        var library = new[] { macroA, macroB };
        var graphIssues = MacroValidator.ValidateCallGraph(macroA, library);
        Assert.Contains(graphIssues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock())
        {
            MacroResolver = (id, _) => library.FirstOrDefault(m => m.Id == id),
        };
        var res = await engine.RunAsync(macroA);
        Assert.Equal(PlaybackOutcome.Failed, res.Outcome);
        Assert.Contains("cycle", res.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Smacro_export_bundles_transitive_submacros_and_import_remaps_ids_and_colliding_names()
    {
        var leaf = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Leaf Helper",
            Steps = { new DelayStep { DurationMs = 50 } },
        };
        var mid = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Mid Helper",
            Steps = { new CallMacroStep { TargetMacroId = leaf.Id, TargetMacroName = leaf.Name } },
        };
        var root = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Master Farm",
            Steps = { new CallMacroStep { TargetMacroId = mid.Id, TargetMacroName = mid.Name } },
        };
        var unrelated = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Unrelated Macro",
            Steps = { new DelayStep { DurationMs = 50 } },
        };

        var json = MacroFileFormat.Serialize(root, new[] { root, mid, leaf, unrelated });

        Assert.True(MacroFileFormat.TryParseBundle(json, out var parsedMain, out var parsedSubs, out var error));
        Assert.Null(error);
        Assert.NotNull(parsedMain);
        Assert.Equal(2, parsedSubs.Count);
        Assert.DoesNotContain(parsedSubs, s => s.Name == "Unrelated Macro");

        // Simulate importing into a library that ALREADY has a macro named "Mid Helper"
        var (importedMain, importedSubs) = MacroFileFormat.PrepareBundleForImport(parsedMain!, parsedSubs, new[] { "Mid Helper" });

        Assert.NotEqual(root.Id, importedMain.Id);
        var importedMid = Assert.Single(importedSubs, s => s.Name.StartsWith("Mid Helper", StringComparison.Ordinal));
        Assert.NotEqual("Mid Helper", importedMid.Name); // renamed to avoid collision!
        var rootCall = Assert.IsType<CallMacroStep>(Assert.Single(importedMain.Steps));
        Assert.Equal(importedMid.Id, rootCall.TargetMacroId);
        Assert.Equal(importedMid.Name, rootCall.TargetMacroName);
    }

    [Fact]
    public void Schema_version_16_and_legacy_failure_policy_preserve_backward_compatibility()
    {
        Assert.True(MacroDocument.CurrentSchemaVersion >= 16); // 17 since v1.4 Reflex

        // Plain macro stays Schema 1; FindTextStep with Continue / SkipNextStep / StopMacro stays Schema 7!
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(new DelayStep { DurationMs = 100 })));
        var find = new FindTextStep { Text = "OK" };
        find.EffectiveFailureAction = StepFailureAction.SkipNextStep;
        Assert.Null(find.FailurePolicy);
        Assert.Equal(TextNotFoundBehavior.SkipNextStep, find.WhenNotFound);
        Assert.Equal(7, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(find)));

        // Advanced failure policy bumps to Schema 16
        find.EffectiveFailureAction = StepFailureAction.Retry;
        Assert.NotNull(find.FailurePolicy);
        Assert.Equal(16, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(find)));

        // Switching back to Continue clears FailurePolicy so Schema drops all the way back to 1!
        find.EffectiveFailureAction = StepFailureAction.Continue;
        Assert.Null(find.FailurePolicy);
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(Aaa.MacroWith(find)));

        // StepListView & StudioCommandCatalog support all 3 new step kinds cleanly
        var brainMacro = new Macro
        {
            Name = "Brain Full",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "100" },
            },
            Steps =
            {
                new CheckpointStep { Name = "entry" },
                new SetVariableStep { VariableName = "hp", Source = VariableSourceKind.Constant, Value = "95" },
                new CallMacroStep { TargetMacroId = Guid.NewGuid(), TargetMacroName = "Heal" },
            },
        };
        Assert.Equal(16, MacroDocument.RequiredSchemaVersion(brainMacro));
        Assert.Contains(StudioCommandCatalog.All, c => c.Step == StepKind.SetVariable);
        Assert.Contains(StudioCommandCatalog.All, c => c.Step == StepKind.Checkpoint);
        Assert.Contains(StudioCommandCatalog.All, c => c.Step == StepKind.CallMacro);
        Assert.True(StepListView.MatchesFilter(brainMacro.Steps[0], StepFilter.VisionAndLogic));
        Assert.True(StepListView.MatchesFilter(brainMacro.Steps[1], StepFilter.VisionAndLogic));
        Assert.True(StepListView.MatchesFilter(brainMacro.Steps[2], StepFilter.VisionAndLogic));
    }
}

public class SelfHealingAdvisorTests
{
    [Fact]
    public async Task Relocated_smart_intent_click_emits_opt_in_suggestion_without_silently_mutating_step()
    {
        var ui = new FakeUiLocator
        {
            // Button moved from (100, 100) to (420, 260) -> center (460, 280), shift > 25 px!
            MatchToReturn = new UiElementMatch(new ScreenRect(420, 260, 80, 40), 0.99, "AutomationId=\"PlayBtn\""),
        };

        var click = new MouseClickStep
        {
            X = 100,
            Y = 100,
            UseSmartIntent = true,
            Target = new TargetFingerprint
            {
                ElementName = "Play",
                AutomationId = "PlayBtn",
                ControlType = "Button",
                WindowTitle = "Launcher",
                WindowWidth = 800,
                WindowHeight = 600,
                WindowRelativeX = 100,
                WindowRelativeY = 100,
            },
        };
        var macro = Aaa.MacroWith(click);

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), uiLocator: ui);
        var res = await engine.RunAsync(macro);

        Assert.Equal(PlaybackOutcome.Completed, res.Outcome);
        // Never mutated silently!
        Assert.Equal((100, 100), (click.X, click.Y));

        var suggestion = Assert.Single(res.Suggestions);
        Assert.Equal(click.Id, suggestion.StepId);
        Assert.Equal((100, 100), (suggestion.OldX, suggestion.OldY));
        Assert.InRange(suggestion.NewX, 459, 460);
        Assert.InRange(suggestion.NewY, 279, 280);

        // User opts in to apply the suggestion
        Assert.True(suggestion.ApplyTo(macro));
        Assert.Equal((suggestion.NewX, suggestion.NewY), (click.X, click.Y));
        Assert.Equal(suggestion.NewX, click.Target!.WindowRelativeX);
        Assert.Equal(suggestion.NewY, click.Target!.WindowRelativeY);
    }

    [Fact]
    public void Small_shift_under_threshold_does_not_spam_self_heal_suggestions()
    {
        var click = new MouseClickStep
        {
            X = 200,
            Y = 200,
            UseSmartIntent = true,
            Target = new TargetFingerprint { ElementName = "OK", AutomationId = "OkBtn" },
        };
        var bounds = new ScreenRect(188, 190, 40, 30);
        var resolution = new TargetResolution(
            LocatorStrategyKind.UiAutomation,
            208,
            205,
            bounds,
            0.99,
            1.2,
            "UI Automation",
            new[] { new StrategyDiagnostic(LocatorStrategyKind.UiAutomation, true, 208, 205, bounds, 0.99, 1.2, "Matched") });

        Assert.Null(SelfHealSuggestion.TryCreate(click, 1, resolution));
    }
}

public class BrainBlueprintsAndManualTests
{
    [Fact]
    public void Brain_blueprints_build_valid_steps_and_seed_default_variables()
    {
        var goalSteps = MacroBlueprints.Build(MacroBlueprint.ScreenCounterGoalLoop);
        Assert.Equal(
            new[] { StepKind.Repeat, StepKind.SetVariable, StepKind.MouseClick, StepKind.Delay, StepKind.SetVariable, StepKind.EndRepeat },
            goalSteps.Select(s => s.Kind));
        var goalVars = MacroBlueprints.BuildVariables(MacroBlueprint.ScreenCounterGoalLoop);
        Assert.Equal(new[] { "gold", "runs" }, goalVars.Select(v => v.Name));

        var recoverySteps = MacroBlueprints.Build(MacroBlueprint.CheckpointRecoveryFlow);
        Assert.True(MacroBlueprints.Info(MacroBlueprint.CheckpointRecoveryFlow).RequiresTopLevel);
        Assert.Equal(new[] { StepKind.Checkpoint, StepKind.FindText, StepKind.Delay, StepKind.FindImage }, recoverySteps.Select(s => s.Kind));
        Assert.Equal(StepFailureAction.Retry, ((FindTextStep)recoverySteps[1]).EffectiveFailureAction);
        Assert.Equal(StepFailureAction.RestartFromCheckpoint, ((FindImageStep)recoverySteps[3]).EffectiveFailureAction);

        var modularSteps = MacroBlueprints.Build(MacroBlueprint.ModularSubMacroPipeline);
        Assert.Equal(
            new[] { StepKind.SetVariable, StepKind.CallMacro, StepKind.If, StepKind.Delay, StepKind.EndIf },
            modularSteps.Select(s => s.Kind));
        var modularVars = MacroBlueprints.BuildVariables(MacroBlueprint.ModularSubMacroPipeline);
        Assert.Equal(new[] { "batchSize", "earned" }, modularVars.Select(v => v.Name));
    }

    [Fact]
    public void Inserting_checkpoint_inside_nested_blocks_advances_to_top_level()
    {
        var steps = new List<MacroStep>
        {
            new RepeatStep { Mode = RepeatMode.Times, Count = 5 }, // 0
            new IfStep(),                                           // 1
            new DelayStep { DurationMs = 100 },                     // 2 (selected!)
            new EndIfStep(),                                        // 3
            new EndRepeatStep(),                                    // 4
            new DelayStep { DurationMs = 200 },                     // 5 (depth 0!)
        };

        var idx = StepListEditing.InsertIndex(steps, selectedIndex: 2, InsertPosition.AfterSelected, StepKind.Checkpoint);
        Assert.Equal(5, idx); // Right after EndRepeatStep, at block depth 0!
    }

    [Fact]
    public void Manual_catalog_includes_intent_and_brain_guides_plus_recipe_cards()
    {
        Assert.Contains(ManualCatalog.Filter("smart intent locate target"), a => a.Id == "smart-intent");
        Assert.Contains(ManualCatalog.Filter("until screen settles multi-scale"), a => a.Id == "adaptive-timing");
        Assert.Contains(ManualCatalog.Filter("variables placeholders"), a => a.Id == "variables-memory");
        Assert.Contains(ManualCatalog.Filter("checkpoints watchers"), a => a.Id == "checkpoints-watchers");
        Assert.Contains(ManualCatalog.Filter("sub-macros smacro"), a => a.Id == "submacros-bundles");

        Assert.Equal(ManualCategory.MacroRecipes, ManualCatalog.RecipeFor(MacroBlueprint.ScreenCounterGoalLoop).Category);
        Assert.Equal(ManualCategory.MacroRecipes, ManualCatalog.RecipeFor(MacroBlueprint.CheckpointRecoveryFlow).Category);
        Assert.Equal(ManualCategory.MacroRecipes, ManualCatalog.RecipeFor(MacroBlueprint.ModularSubMacroPipeline).Category);
    }
}

