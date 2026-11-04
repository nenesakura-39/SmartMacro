using System.Globalization;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- v1.2 "Brain": Variables, Failure Policies, Checkpoints, Watchers & Sub-Macros ----------------

    /// <summary>Maximum number of checkpoint restarts allowed in one macro run before stopping safely.</summary>
    public const int MaxCheckpointRestarts = 25;

    /// <summary>Optional clipboard text reader used by <see cref="VariableSourceKind.ClipboardText"/>.</summary>
    public IClipboardReader? Clipboard { get; set; }

    /// <summary>
    /// Resolves a sub-macro from the library by ID or case-insensitive name (used by <see cref="CallMacroStep"/>,
    /// <see cref="StepFailureAction.RunRecoveryMacro"/>, and <see cref="WatcherActionKind.RunSubMacro"/>).
    /// </summary>
    public Func<Guid, string?, Macro?>? MacroResolver { get; set; }

    /// <summary>Raised on the playback thread whenever a variable changes in the root macro's variable store.</summary>
    public event EventHandler<IReadOnlyList<VariableSnapshot>>? VariablesChanged;

    // Active run context on the playback thread (swapped during sub-macro calls and restored in finally).
    private RunContext? _activeRun;

    // Self-healing suggestions gathered during the current playback session (keyed by step ID so loops don't duplicate).
    private readonly Dictionary<Guid, SelfHealSuggestion> _sessionSuggestions = new();

    /// <summary>Internal control-flow signal thrown to jump cleanly to a top-level <see cref="CheckpointStep"/>.</summary>
    private sealed class CheckpointJumpSignal : Exception
    {
        public CheckpointJumpSignal(int targetIndex, string checkpointName)
        {
            TargetIndex = targetIndex;
            CheckpointName = checkpointName;
        }

        public int TargetIndex { get; }
        public string CheckpointName { get; }
    }

    private string InterpolateActive(string? template) =>
        _activeRun?.Variables is { } store ? store.Interpolate(template) : (template ?? string.Empty);

    private void NotifyVariablesChanged(RunContext run)
    {
        if (run.IsSubRoutine) return;
        if (VariablesChanged is not { } handler) return;
        try
        {
            handler(this, run.Variables.Snapshot());
        }
        catch
        {
            // UI observer errors never interrupt playback.
        }
    }

    private void RecordSelfHealCandidate(MouseClickStep step, TargetResolution resolution)
    {
        var stepNumber = _activeRun?.CurrentStepNumber ?? 1;
        if (SelfHealSuggestion.TryCreate(step, stepNumber, resolution) is { } suggestion)
        {
            _sessionSuggestions[step.Id] = suggestion;
        }
    }

    private async Task ExecuteSetVariableAsync(RunContext run, List<LoopFrame> loops, SetVariableStep step, CancellationToken token)
    {
        string rawValue;
        MacroVariableType? inferredType = step.VariableType;

        switch (step.Source)
        {
            case VariableSourceKind.Constant:
                rawValue = run.Variables.Interpolate(step.Value);
                break;

            case VariableSourceKind.ScreenOcrNumber:
            {
                var ocr = _ocr;
                if (ocr is null || !ocr.IsAvailable)
                    throw new OcrUnavailableException(ocr?.StatusText ?? "Text recognition (OCR) isn't available to read a screen number.");
                var read = await ocr.ReadAsync(step.Region, token).ConfigureAwait(false);
                if (!VariableStore.TryParseScreenNumber(read.FullText, out var parsed))
                {
                    Message?.Invoke(this, $"Variable {{{step.VariableName}}}: no number found in OCR text \"{ TruncateLog(read.FullText) }\"; keeping 0.");
                    parsed = 0;
                }
                rawValue = parsed.ToString("G15", CultureInfo.InvariantCulture);
                inferredType = MacroVariableType.Number;
                break;
            }

            case VariableSourceKind.ScreenOcrText:
            {
                var ocr = _ocr;
                if (ocr is null || !ocr.IsAvailable)
                    throw new OcrUnavailableException(ocr?.StatusText ?? "Text recognition (OCR) isn't available to read screen text.");
                var read = await ocr.ReadAsync(step.Region, token).ConfigureAwait(false);
                rawValue = (read.FullText ?? string.Empty).Trim();
                inferredType = MacroVariableType.Text;
                break;
            }

            case VariableSourceKind.ClipboardText:
                rawValue = Clipboard?.ReadText() ?? string.Empty;
                inferredType = MacroVariableType.Text;
                break;

            case VariableSourceKind.LastFoundX:
                rawValue = (_lastFound?.X ?? 0).ToString(CultureInfo.InvariantCulture);
                inferredType = MacroVariableType.Number;
                break;

            case VariableSourceKind.LastFoundY:
                rawValue = (_lastFound?.Y ?? 0).ToString(CultureInfo.InvariantCulture);
                inferredType = MacroVariableType.Number;
                break;

            case VariableSourceKind.LoopCounter:
            {
                var turn = loops.Count > 0 ? loops[^1].Turns + 1 : Math.Max(1, run.Iteration);
                rawValue = turn.ToString(CultureInfo.InvariantCulture);
                inferredType = MacroVariableType.Number;
                break;
            }

            case VariableSourceKind.ElapsedSeconds:
            {
                var elapsed = Math.Max(0, (ActiveTime - run.RunStartedAt).TotalSeconds);
                rawValue = Math.Round(elapsed, 2).ToString("0.##", CultureInfo.InvariantCulture);
                inferredType = MacroVariableType.Number;
                break;
            }

            default:
                rawValue = string.Empty;
                break;
        }

        var updated = run.Variables.Apply(step.VariableName, inferredType ?? MacroVariableType.Text, step.Operation, rawValue);
        Message?.Invoke(this, $"🧠 Variable {{{step.VariableName}}} = \"{TruncateLog(updated)}\"");
    }

    private CheckpointJumpSignal CreateCheckpointJump(RunContext run, string? checkpointName)
    {
        var wanted = checkpointName?.Trim() ?? string.Empty;
        var targetIndex = -1;
        for (var i = run.Start; i < run.End; i++)
        {
            if (run.Steps[i] is CheckpointStep { Enabled: true } cp
                && string.Equals(cp.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            // Also match disabled CheckpointSteps as passive anchors if present in range.
            for (var i = run.Start; i < run.End; i++)
            {
                if (run.Steps[i] is CheckpointStep cp
                    && string.Equals(cp.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex < 0)
            throw new DetectionTimeoutException($"Checkpoint \"{wanted}\" wasn't found in the active step range.");

        run.CheckpointRestarts++;
        if (run.CheckpointRestarts > MaxCheckpointRestarts)
        {
            throw new LoopLimitException(
                $"Restarted from checkpoint \"{wanted}\" more than {MaxCheckpointRestarts} times without finishing; stopping the macro to prevent an infinite recovery loop.");
        }

        return new CheckpointJumpSignal(targetIndex, wanted);
    }

    private async Task ApplyFinalFailurePolicyAsync(
        StepFailureAction action,
        StepFailurePolicy? policy,
        string message,
        bool forgetFound,
        CancellationToken token)
    {
        switch (action)
        {
            case StepFailureAction.Continue:
            case StepFailureAction.SkipNextStep:
            {
                if (forgetFound) ForgetFound();
                var skip = action == StepFailureAction.SkipNextStep;
                _skipNextStep = skip;
                Message?.Invoke(this, message + (skip ? " Skipping the next step." : " Continuing."));
                return;
            }

            case StepFailureAction.RunRecoveryMacro:
            {
                if (forgetFound) ForgetFound();
                var recName = policy?.RecoveryMacroName ?? string.Empty;
                var recId = policy?.RecoveryMacroId ?? Guid.Empty;
                Message?.Invoke(this, $"{message} Running recovery macro \"{recName}\"…");
                await RunSubMacroInternalAsync(recId, recName, inputs: null, outputs: null, token).ConfigureAwait(false);
                return;
            }

            case StepFailureAction.RestartFromCheckpoint:
            {
                if (forgetFound) ForgetFound();
                var run = _activeRun ?? throw new InvalidOperationException("Playback isn't running.");
                var cpName = policy?.CheckpointName ?? string.Empty;
                Message?.Invoke(this, $"{message} Restarting from checkpoint \"{cpName}\"…");
                throw CreateCheckpointJump(run, cpName);
            }

            default:
                throw new DetectionTimeoutException(message);
        }
    }

    private async Task CheckWatchersAsync(RunContext run, CancellationToken token)
    {
        if (run.IsSubRoutine || run.Watchers is not { Count: > 0 } watchers) return;

        foreach (var watcher in watchers)
        {
            if (watcher is null || !watcher.Enabled || watcher.Condition is null) continue;

            var now = ActiveTime;
            var minInterval = Math.Max(MacroWatcher.MinCheckIntervalMs, watcher.CheckIntervalMs);
            if (run.WatcherLastCheck.TryGetValue(watcher.Id, out var lastCheck)
                && (now - lastCheck).TotalMilliseconds < minInterval)
            {
                continue;
            }

            if (watcher.CooldownMs > 0
                && run.WatcherLastFired.TryGetValue(watcher.Id, out var lastFired)
                && (now - lastFired).TotalMilliseconds < watcher.CooldownMs)
            {
                continue;
            }

            run.WatcherLastCheck[watcher.Id] = now;
            var check = await run.Conditions.CheckAsync(watcher.Condition, null, token).ConfigureAwait(false);
            if (!check.IsTrue) continue;

            run.WatcherLastFired[watcher.Id] = ActiveTime;
            if (check.Seen)
            {
                RememberFound(check.X, check.Y, check.Match);
                ReportTarget(check.X, check.Y, check.Match, check.Confidence, $"Watcher: {watcher.Name}", check.ElapsedMs);
            }

            Message?.Invoke(this, $"🛡 Watcher \"{watcher.Name}\" triggered. {check.Details}");

            switch (watcher.Action)
            {
                case WatcherActionKind.ClickFoundSpot:
                    if (check.Seen)
                    {
                        await PerformFoundActionAsync(
                            FoundTextAction.Click,
                            check.X + watcher.ClickOffsetX,
                            check.Y + watcher.ClickOffsetY,
                            FoundClickHoldMs,
                            FoundActionTiming.Default,
                            run.Held,
                            token,
                            check.Match).ConfigureAwait(false);
                    }
                    SyncTimelineToNow();
                    break;

                case WatcherActionKind.RunSubMacro:
                    await RunSubMacroInternalAsync(
                        watcher.HandlerMacroId ?? Guid.Empty,
                        watcher.HandlerMacroName,
                        inputs: null,
                        outputs: null,
                        token).ConfigureAwait(false);
                    SyncTimelineToNow();
                    break;

                case WatcherActionKind.RestartFromCheckpoint:
                    throw CreateCheckpointJump(run, watcher.CheckpointName);

                case WatcherActionKind.StopMacro:
                    throw new DetectionTimeoutException($"Macro stopped by watcher \"{watcher.Name}\".");
            }

            // Handle at most one watcher interrupt per step boundary so the screen can settle.
            break;
        }
    }

    private Task ExecuteCallMacroAsync(CallMacroStep step, CancellationToken token) =>
        RunSubMacroInternalAsync(step.TargetMacroId, step.TargetMacroName, step.Inputs, step.Outputs, token);

    private async Task RunSubMacroInternalAsync(
        Guid targetId,
        string? targetName,
        IReadOnlyList<VariableBinding>? inputs,
        IReadOnlyList<VariableBinding>? outputs,
        CancellationToken token)
    {
        var callerRun = _activeRun ?? throw new InvalidOperationException("Playback isn't running.");
        var resolver = MacroResolver ?? throw new InvalidOperationException("No macro library is connected, so sub-macros cannot run.");
        var displayName = !string.IsNullOrWhiteSpace(targetName) ? targetName.Trim() : targetId.ToString();

        var target = resolver(targetId, targetName)
                     ?? throw new InvalidOperationException($"Sub-macro \"{displayName}\" was not found in the library.");

        if (callerRun.ActiveMacroIds.Contains(target.Id))
            throw new InvalidOperationException($"Sub-macro call cycle detected while calling \"{target.Name}\".");

        if (callerRun.CallDepth + 1 > CallMacroStep.MaxCallDepth)
            throw new InvalidOperationException($"Sub-macro nesting exceeded the maximum depth of {CallMacroStep.MaxCallDepth}.");

        var subIssues = MacroValidator.ValidateForExecution(target);
        if (subIssues.HasErrors())
            throw new MacroValidationException(subIssues);

        var calleeVars = new VariableStore(target.Variables);
        if (inputs is { Count: > 0 })
        {
            foreach (var binding in inputs)
            {
                if (binding is null || !MacroVariableDefinition.IsValidName(binding.TargetVariable)) continue;
                var evaluated = callerRun.Variables.Interpolate(binding.SourceExpression);
                var targetType = calleeVars.HasVariable(binding.TargetVariable)
                    ? calleeVars.GetTypeOrDefault(binding.TargetVariable)
                    : (double.TryParse(evaluated, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? MacroVariableType.Number
                        : bool.TryParse(evaluated, out _)
                            ? MacroVariableType.Boolean
                            : MacroVariableType.Text);
                calleeVars.Apply(binding.TargetVariable, targetType, VariableOperation.Set, evaluated);
            }
        }

        var subSteps = target.Steps;
        if (CoordinateScaling.MappingFor(target, _layout) is { } mapping && !mapping.IsIdentity)
            subSteps = CoordinateScaling.ApplyToCopy(subSteps, mapping);

        var subBlocks = BlockStructure.Analyze(subSteps);
        var subProbes = new ScreenProbes(_ocr, _capture, _clock, _layout, _hotSpots, calleeVars);
        var subConditions = new ScreenConditionEvaluator(subProbes, calleeVars);
        var activeIds = new HashSet<Guid>(callerRun.ActiveMacroIds) { target.Id };
        var subPasses = target.RepeatCount > 1 ? target.RepeatCount : 1;

        var subRun = new RunContext(
            subSteps,
            subBlocks,
            callerRun.Speed,
            callerRun.Held,
            subConditions,
            subPasses,
            0,
            subSteps.Count,
            calleeVars)
        {
            CallDepth = callerRun.CallDepth + 1,
            ActiveMacroIds = activeIds,
            IsSubRoutine = true,
            RunStartedAt = ActiveTime,
        };

        var callerProbes = _probes;
        _activeRun = subRun;
        _probes = subProbes;
        Message?.Invoke(this, $"📦 Calling sub-macro \"{target.Name}\"…");
        try
        {
            for (var pass = 1; pass <= subPasses; pass++)
            {
                if (pass > 1 && target.RepeatDelayMs > 0)
                    await WaitAsync(target.RepeatDelayMs / Math.Max(0.1, subRun.Speed), token).ConfigureAwait(false);

                subRun.Iteration = pass;
                await RunStepsAsync(subRun, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _activeRun = callerRun;
            _probes = callerProbes;
        }

        callerRun.StepsExecuted += subRun.StepsExecuted;

        if (outputs is { Count: > 0 })
        {
            foreach (var binding in outputs)
            {
                if (binding is null || !MacroVariableDefinition.IsValidName(binding.TargetVariable)) continue;
                var outVal = calleeVars.GetText(binding.SourceExpression);
                var outType = calleeVars.GetTypeOrDefault(binding.SourceExpression);
                callerRun.Variables.Apply(binding.TargetVariable, outType, VariableOperation.Set, outVal);
            }
        }

        Message?.Invoke(this, $"📦 Finished sub-macro \"{target.Name}\".");
    }

    private static string TruncateLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var single = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 60 ? single : single[..60] + "…";
    }
}
