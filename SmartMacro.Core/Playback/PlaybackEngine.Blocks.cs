using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Core.Playback;

public sealed partial class PlaybackEngine
{
    // ---------------- Blocks: If / Else / End If, Repeat / End Repeat ----------------

    /// <summary>Everything one run needs, so the block runner doesn't depend on loose locals.</summary>
    private sealed class RunContext
    {
        public RunContext(List<MacroStep> steps, BlockStructure blocks, double speed, HeldInput held,
            IConditionEvaluator conditions, int? totalIterations, int start, int end, VariableStore? variables = null)
        {
            Start = Math.Clamp(start, 0, steps.Count);
            End = Math.Clamp(end, Start, steps.Count);
            Steps = steps;
            Blocks = blocks;
            Speed = speed;
            Held = held;
            Conditions = conditions;
            TotalIterations = totalIterations;
            Variables = variables ?? new VariableStore();
        }

        public List<MacroStep> Steps { get; }
        public BlockStructure Blocks { get; }
        /// <summary>Playback speed; the editor's live speed selector can change it between steps.</summary>
        public double Speed { get; set; }

        public HeldInput Held { get; }
        public IConditionEvaluator Conditions { get; }
        public int? TotalIterations { get; }
        public VariableStore Variables { get; }

        public List<MacroWatcher>? Watchers { get; init; }
        public Dictionary<Guid, TimeSpan> WatcherLastCheck { get; } = new();
        public Dictionary<Guid, TimeSpan> WatcherLastFired { get; } = new();
        public Dictionary<string, string> LastKnownVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CallDepth { get; init; }
        public Guid MacroId { get; init; }
        public string? MacroName { get; init; }
        public RunContext? Parent { get; init; }
        public HashSet<Guid> ActiveMacroIds { get; init; } = new();
        public bool IsSubRoutine { get; init; }
        public int CheckpointRestarts { get; set; }
        public TimeSpan RunStartedAt { get; set; }
        public int CurrentStepNumber { get; set; } = 1;
        public int CurrentStepIndex { get; set; }
        public Guid? CurrentStepId { get; set; }
        public string CurrentStepTitle { get; set; } = string.Empty;

        public int SourceIndexOf(int index) =>
            SourceIndexes is { } map && index >= 0 && index < map.Count ? map[index] : index;

        /// <summary>First step to play (0 for a normal run).</summary>
        public int Start { get; }

        /// <summary>One past the last step to play (Steps.Count for a normal run).</summary>
        public int End { get; }

        /// <summary>True for "Run step" / "Play from here" (only part of the list plays).</summary>
        public bool IsPartial => Start != 0 || End != Steps.Count;

        public int Iteration { get; set; }
        public int StepsExecuted { get; set; }
        public Dictionary<Guid, StepExecutionStat> StepStats { get; } = new();

        public void RecordStepStat(Guid stepId, double elapsedMs)
        {
            if (stepId == Guid.Empty) return;
            var ms = double.IsFinite(elapsedMs) && elapsedMs > 0 ? elapsedMs : 0.0;
            if (StepStats.TryGetValue(stepId, out var prev))
                StepStats[stepId] = new StepExecutionStat(prev.ExecutionCount + 1, prev.TotalElapsedMs + ms);
            else
                StepStats[stepId] = new StepExecutionStat(1, ms);
        }

        /// <summary>Last answer of each If (by step index), so a loop doesn't log the same answer every turn.</summary>
        public Dictionary<int, bool> LastIfAnswer { get; } = new();

        /// <summary>"Play from here" slice: the macro's index of each played step (null = the list IS the macro).</summary>
        public IReadOnlyList<int>? SourceIndexes { get; init; }

        /// <summary>"Play from here" slice: the macro's step count (0 = the list IS the macro).</summary>
        public int SourceCount { get; init; }

        /// <summary>1-based step number as the editor shows it (the macro's numbering, also for a slice).</summary>
        public int StepNumberOf(int index) =>
            SourceIndexes is { } map && index >= 0 && index < map.Count ? map[index] + 1 : index + 1;

        /// <summary>Step count as the editor shows it.</summary>
        public int StepCountShown => SourceCount > 0 ? SourceCount : Steps.Count;
    }

    /// <summary>State of one running Repeat block. Lives on a stack, innermost last.</summary>
    private sealed class LoopFrame
    {
        public LoopFrame(int start, TimeSpan startedAt)
        {
            Start = start;
            StartedAt = startedAt;
        }

        /// <summary>Index of the Repeat step.</summary>
        public int Start { get; }

        /// <summary>Active time (paused time not counted) when the loop started.</summary>
        public TimeSpan StartedAt { get; }

        /// <summary>Completed turns.</summary>
        public int Turns { get; set; }

        /// <summary>Active time of the last condition check (While / Until), to space the checks.</summary>
        public TimeSpan? LastCheckAt { get; set; }
    }

    /// <summary>
    /// Runs the step list once, from top to bottom, following the block jumps.
    /// pc ("program counter") is the index of the step to run next.
    /// </summary>
    private async Task RunStepsAsync(RunContext run, CancellationToken token)
    {
        var steps = run.Steps;
        var blocks = run.Blocks;
        var loops = new List<LoopFrame>();
        var pc = run.Start;
        var lastExecutedPc = -1;

        while (pc < run.End)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            try
            {
                await CheckWatchersAsync(run, token).ConfigureAwait(false);

                if (TakePendingSpeed() is { } newSpeed && !newSpeed.Equals(run.Speed))
                {
                    run.Speed = newSpeed;
                    CurrentSpeed = newSpeed;
                    _input.SetPlaybackSpeed(newSpeed);
                    Message?.Invoke(this, $"Playback speed changed to {newSpeed:0.##}× (from step {run.StepNumberOf(pc)}).");
                }

                var currentPc = pc;
                var step = steps[pc];
                var stepNumber = run.StepNumberOf(pc);
                var stepTitle = StepDescriber.DisplayName(step.Kind);
                run.CurrentStepNumber = stepNumber;
                run.CurrentStepIndex = run.SourceIndexOf(pc);
                run.CurrentStepId = step.Id;
                run.CurrentStepTitle = stepTitle;

                if (step.Enabled)
                {
                    var bpResult = _lastBreakpointPausedStepId == step.Id
                        ? default
                        : await EvaluateBreakpointAsync(run, step, token).ConfigureAwait(false);
                    if (_pauseBeforeNextStep || bpResult.Hit)
                    {
                        var watchReason = _pendingPauseReason;
                        _pauseBeforeNextStep = false;
                        _pendingPauseReason = null;
                        _stepMode = DebugStepMode.None;
                        _lastBreakpointPausedStepId = step.Id;

                        var reasonTag = bpResult.Hit
                            ? (string.IsNullOrWhiteSpace(bpResult.Reason) ? "breakpoint" : $"breakpoint ({bpResult.Reason})")
                            : (watchReason is not null ? "watchpoint" : "step");
                        Report(run, pc, loops, reasonTag);

                        var scopeName = run.IsSubRoutine && !string.IsNullOrWhiteSpace(run.MacroName)
                            ? $" in \"{run.MacroName}\""
                            : string.Empty;
                        var msg = bpResult.Hit
                            ? (string.IsNullOrWhiteSpace(bpResult.Reason)
                                ? $"🔴 Breakpoint hit at step #{stepNumber}{scopeName} (F8 resume, F10 step over, F11 step into)."
                                : $"🔴 Conditional breakpoint [{bpResult.Reason}] hit at step #{stepNumber}{scopeName} (F8 resume, F10/F11 step).")
                            : watchReason is not null
                                ? $"{watchReason} — paused before step #{stepNumber}{scopeName} (F8 resume, F10/F11 step)."
                                : $"⏭ Paused at step #{stepNumber}{scopeName} (F8 resume, F10 step over, F11 step into).";
                        Message?.Invoke(this, msg);

                        Pause();
                        await WaitIfPausedAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }

                    if (_stepMode == DebugStepMode.StepInto)
                    {
                        // Arm pause before entering any sub-macro (or the next step) while keeping Resume (F8) able to clear it.
                        _pauseBeforeNextStep = true;
                        _stepMode = DebugStepMode.None;
                    }
                }

                var stepStartedAt = ActiveTime;
                var prevExecuted = run.StepsExecuted;
                var attemptedStep = step.Enabled && step is not (ElseStep or EndIfStep or EndRepeatStep or BeginGroupStep or EndGroupStep);
                _currentStepVisionScore = null;
                var traceOutcome = ExecutionTraceOutcome.Executed;
                string? traceDetail = null;
                string? traceError = null;

                try
                {
                switch (step)
                {
                    case IfStep ifStep:
                    {
                        // A disabled If skips its whole block (both branches).
                        if (!ifStep.Enabled)
                        {
                            pc = blocks.EndOf(pc) + 1;
                            break;
                        }

                        Report(run, pc, loops);
                        var answer = await EvaluateConditionAsync(run, ifStep.Condition, token, label: "If").ConfigureAwait(false);
                        run.StepsExecuted++;
                        traceOutcome = answer.IsTrue ? ExecutionTraceOutcome.ConditionTrue : ExecutionTraceOutcome.ConditionFalse;
                        traceDetail = answer.Text;
                        LogIfAnswer(run, pc, answer.IsTrue, answer.Text);

                        // Optional "when true" action on the spot the condition saw (not for "is NOT on screen").
                        if (answer.IsTrue && answer.Seen && ifStep.ThenAction != FoundTextAction.None)
                        {
                            await PerformFoundActionAsync(ifStep.ThenAction, answer.X + ifStep.ThenOffsetX, answer.Y + ifStep.ThenOffsetY,
                                ifStep.ThenHoldMs, FoundActionTiming.Default, run.Held, token, answer.Match).ConfigureAwait(false);
                        }

                        SyncTimelineToNow();
                        var elseIndex = blocks.ElseOf(pc);
                        pc = answer.IsTrue ? pc + 1 : (elseIndex >= 0 ? elseIndex + 1 : blocks.EndOf(pc) + 1);
                        break;
                    }

                    case ElseStep:
                        // Only reached at the end of the "true" part: skip the Else part.
                        pc = blocks.EndOf(pc) + 1;
                        break;

                    case EndIfStep:
                        pc++;
                        break;

                    case BeginGroupStep groupStep:
                        // A disabled Group skips all steps inside it; an enabled Group has zero runtime overhead.
                        pc = groupStep.Enabled ? pc + 1 : blocks.EndOf(pc) + 1;
                        break;

                    case EndGroupStep:
                        pc++;
                        break;

                    case RepeatStep repeat:
                    {
                        // A disabled Repeat skips its whole block.
                        if (!repeat.Enabled)
                        {
                            pc = blocks.EndOf(pc) + 1;
                            break;
                        }

                        var frame = new LoopFrame(pc, ActiveTime);
                        loops.Add(frame);
                        Report(run, pc, loops);
                        run.StepsExecuted++;
                        if (await StartNextTurnAsync(run, repeat, frame, token).ConfigureAwait(false))
                        {
                            pc++;
                        }
                        else
                        {
                            loops.RemoveAt(loops.Count - 1);
                            pc = blocks.EndOf(pc) + 1;
                        }

                        SyncTimelineToNow();
                        break;
                    }

                    case EndRepeatStep:
                    {
                        var start = blocks.StartOf(pc);
                        if (run.IsPartial && (loops.Count == 0 || loops[^1].Start != start))
                        {
                            // "Play from here" started inside this loop's body: this was its one (partial) turn.
                            pc++;
                            break;
                        }

                        if (loops.Count == 0 || loops[^1].Start != start || steps[start] is not RepeatStep repeat)
                            throw new InvalidOperationException($"End Repeat at step {pc + 1} doesn't belong to the running loop."); // can't happen after validation

                        var frame = loops[^1];
                        frame.Turns++;
                        if (await StartNextTurnAsync(run, repeat, frame, token).ConfigureAwait(false))
                        {
                            pc = start + 1;
                        }
                        else
                        {
                            loops.RemoveAt(loops.Count - 1);
                            pc++;
                        }

                        SyncTimelineToNow();
                        break;
                    }

                    case FeedbackLoopStep feedback when feedback.Enabled:
                        Report(run, pc, loops);
                        await RunFeedbackLoopAsync(run, pc, loops, feedback, token).ConfigureAwait(false);
                        SyncTimelineToNow();
                        run.StepsExecuted++;
                        pc++;
                        break;

                    case SetVariableStep setVar:
                        if (setVar.Enabled)
                        {
                            Report(run, pc, loops);
                            await ExecuteSetVariableAsync(run, loops, setVar, token).ConfigureAwait(false);
                            SyncTimelineToNow();
                            run.StepsExecuted++;
                        }
                        pc++;
                        break;

                    case CheckpointStep cp:
                        if (cp.Enabled)
                        {
                            Report(run, pc, loops);
                            run.StepsExecuted++;
                        }
                        pc++;
                        break;

                    case CallMacroStep call:
                        if (call.Enabled)
                        {
                            Report(run, pc, loops);
                            traceDetail = $"Sub-macro \"{(string.IsNullOrWhiteSpace(call.TargetMacroName) ? call.TargetMacroId.ToString() : call.TargetMacroName.Trim())}\"";
                            await ExecuteCallMacroAsync(call, token).ConfigureAwait(false);
                            SyncTimelineToNow();
                            run.StepsExecuted++;
                        }
                        pc++;
                        break;

                    case FlowControlStep fc:
                    {
                        if (!fc.Enabled)
                        {
                            pc++;
                            break;
                        }

                        Report(run, pc, loops);
                        run.StepsExecuted++;
                        var note = string.IsNullOrWhiteSpace(fc.Message) ? "" : $" ({InterpolateActive(fc.Message)})";
                        switch (fc.Action)
                        {
                            case FlowControlAction.ExitLoop:
                            {
                                if (loops.Count == 0)
                                {
                                    Message?.Invoke(this, $"⏹ Break loop{note}: not inside a Repeat loop; continuing.");
                                    pc++;
                                    break;
                                }
                                var inner = loops[^1];
                                loops.RemoveAt(loops.Count - 1);
                                var endRepeatIdx = blocks.EndOf(inner.Start);
                                Message?.Invoke(this, $"⏹ Break loop{note}: exited Repeat at step {run.StepNumberOf(inner.Start)}.");
                                pc = endRepeatIdx >= 0 ? endRepeatIdx + 1 : pc + 1;
                                break;
                            }

                            case FlowControlAction.ContinueLoop:
                            {
                                if (loops.Count == 0)
                                {
                                    Message?.Invoke(this, $"⏭ Continue loop{note}: not inside a Repeat loop; continuing.");
                                    pc++;
                                    break;
                                }
                                var inner = loops[^1];
                                var endRepeatIdx = blocks.EndOf(inner.Start);
                                Message?.Invoke(this, $"⏭ Continue loop{note}: jumping to next turn of Repeat at step {run.StepNumberOf(inner.Start)}.");
                                pc = endRepeatIdx >= 0 ? endRepeatIdx : pc + 1;
                                break;
                            }

                            default: // StopMacro
                                Message?.Invoke(this, $"🛑 Stop macro triggered at step {run.StepNumberOf(pc)}{note}.");
                                throw new MacroStopRequestedSignal();
                        }

                        SyncTimelineToNow();
                        break;
                    }

                    default:
                        _skipNextStep = false;
                        if (step.Enabled)
                        {
                            // Smooth mouse: a fixed Delay right before a cursor jump glides there (see SmoothMouseMoves),
                            // except while a mouse button is held down (so window/title-bar drags don't move during pauses).
                            _glideTarget = step is DelayStep { Mode: DelayMode.Fixed } && run.Held.Buttons.Count == 0 && pc + 1 < steps.Count ? JumpTargetOf(steps[pc + 1]) : null;
                            _glideDuringDelay = step is DelayStep { Mode: DelayMode.Fixed } delay && run.Held.Buttons.Count == 0 && pc + 1 < steps.Count ? RecordedGlideAfter(delay, steps[pc + 1]) : null;
                            Report(run, pc, loops);
                            if (IsDeadlineSynced(step))
                                await ExecuteDeadlineSyncedAsync(step, run.Speed, run.Held, token).ConfigureAwait(false);
                            else
                                await ExecuteStepAsync(step, run.Speed, run.Held, token).ConfigureAwait(false);
                            run.StepsExecuted++;
                        }

                        pc++;
                        if (_skipNextStep)
                        {
                            _skipNextStep = false;
                            pc = SkipTarget(steps, blocks, pc);
                        }

                        break;
                }
                }
                catch (CheckpointJumpSignal cj)
                {
                    traceOutcome = ExecutionTraceOutcome.Executed;
                    traceDetail = $"Checkpoint \"{cj.CheckpointName}\"";
                    throw;
                }
                catch (MacroStopRequestedSignal)
                {
                    traceOutcome = ExecutionTraceOutcome.Stopped;
                    traceDetail = "Stop macro requested";
                    throw;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    traceOutcome = ExecutionTraceOutcome.Stopped;
                    traceDetail = "Playback stopped";
                    throw;
                }
                catch (Exception ex)
                {
                    traceOutcome = ExecutionTraceOutcome.Failed;
                    traceError = ex.Message;
                    _failureCallStack ??= BuildCallStackSnapshot(_activeRun ?? run);
                    throw;
                }
                finally
                {
                    if (run.StepsExecuted > prevExecuted || attemptedStep || traceOutcome == ExecutionTraceOutcome.Failed)
                    {
                        var elapsedMs = (ActiveTime - stepStartedAt).TotalMilliseconds;
                        run.RecordStepStat(step.Id, elapsedMs);
                        RecordTraceEntry(run, stepNumber, step, elapsedMs, traceOutcome, _currentStepVisionScore, traceError, traceDetail);
                    }
                }

                lastExecutedPc = currentPc;
                _lastBreakpointPausedStepId = null;
                if (_stepMode == DebugStepMode.StepInto
                    || (_stepMode is DebugStepMode.StepOver or DebugStepMode.StepOut && run.CallDepth <= _stepTargetDepth))
                {
                    _stepMode = DebugStepMode.None;
                    _pauseBeforeNextStep = true;
                }
            }
            catch (CheckpointJumpSignal jump)
            {
                run.Held.ReleaseAll(_input);
                loops.Clear();
                _skipNextStep = false;
                SyncTimelineToNow();
                Message?.Invoke(this, $"🏁 Jumped to checkpoint \"{jump.CheckpointName}\" (restart #{run.CheckpointRestarts}).");
                pc = jump.TargetIndex + 1;
            }
        }

        if (_pendingPauseReason is { } finalWatchReason && lastExecutedPc >= 0 && !token.IsCancellationRequested)
        {
            var finalStep = steps[lastExecutedPc];
            var finalNum = run.StepNumberOf(lastExecutedPc);
            run.CurrentStepNumber = finalNum;
            run.CurrentStepIndex = run.SourceIndexOf(lastExecutedPc);
            run.CurrentStepId = finalStep.Id;
            run.CurrentStepTitle = StepDescriber.DisplayName(finalStep.Kind);
            _pauseBeforeNextStep = false;
            _pendingPauseReason = null;
            _stepMode = DebugStepMode.None;
            Report(run, lastExecutedPc, loops, "watchpoint");
            Message?.Invoke(this, $"{finalWatchReason} — paused at end of {(run.IsSubRoutine && !string.IsNullOrWhiteSpace(run.MacroName) ? $"\"{run.MacroName}\"" : "macro")} (F8 resume).");
            Pause();
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Where to continue when a Find step says "skip the next step": past the step at <paramref name="next"/>, or past
    /// the whole If / Repeat block that starts there. A block end (Else / End If / End Repeat) isn't a step to skip,
    /// so playback simply carries on there (the block keeps working normally).
    /// </summary>
    private static int SkipTarget(List<MacroStep> steps, BlockStructure blocks, int next)
    {
        if (next >= steps.Count) return next;
        return steps[next] switch
        {
            IfStep or RepeatStep or BeginGroupStep => blocks.EndOf(next) + 1,
            ElseStep or EndIfStep or EndRepeatStep or EndGroupStep => next,
            _ => next + 1,
        };
    }

    private void Report(RunContext run, int index, List<LoopFrame> loops, string? extra = null)
    {
        var stepNumber = run.StepNumberOf(index);
        var step = run.Steps[index];
        var stepTitle = StepDescriber.DisplayName(step.Kind);
        run.CurrentStepNumber = stepNumber;
        run.CurrentStepIndex = run.SourceIndexOf(index);
        run.CurrentStepId = step.Id;
        run.CurrentStepTitle = stepTitle;

        // Live progress bar: what plays now and how long it should take (only when it has a known length).
        Volatile.Write(ref _stepTiming, ExpectedDurationMs(step, run.Speed) is { } expected
            ? new StepTiming(step.Id, UiActiveTime(), expected)
            : null);

        string? loopText = null;
        if (loops.Count > 0 && run.Steps[loops[^1].Start] is RepeatStep repeat)
        {
            var turn = loops[^1].Turns + 1;
            loopText = repeat.Mode == RepeatMode.Times ? $"loop turn {turn} of {repeat.Count}" : $"loop turn {turn}";
        }

        if (extra is not null) loopText = loopText is null ? extra : $"{loopText} · {extra}";

        ProgressChanged?.Invoke(this, new PlaybackProgress(
            run.Iteration,
            run.TotalIterations,
            stepNumber,
            run.StepCountShown,
            step,
            loopText,
            run.MacroId == Guid.Empty ? null : run.MacroId,
            run.MacroName,
            run.CallDepth));
    }

    private void LogIfAnswer(RunContext run, int index, bool isTrue, string text)
    {
        // Inside loops the same If can run thousands of times: only log the first answer and changes.
        if (run.LastIfAnswer.TryGetValue(index, out var previous) && previous == isTrue) return;
        run.LastIfAnswer[index] = isTrue;
        Message?.Invoke(this, $"If (step {run.StepNumberOf(index)}): {(isTrue ? "yes" : "no")}. {text}");
    }

    /// <summary>
    /// Decides whether a Repeat block runs another turn: count reached, safety limits, then (While / Until) the
    /// condition. Returns false to leave the loop normally; throws <see cref="LoopLimitException"/> when a limit is
    /// reached and the loop is set to stop the macro.
    /// </summary>
    private async Task<bool> StartNextTurnAsync(RunContext run, RepeatStep repeat, LoopFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var number = frame.Start + 1;
        var turns = frame.Turns;
        string Turns(int n) => n == 1 ? "1 turn" : $"{n:N0} turns";

        if (repeat.Mode == RepeatMode.Times && turns >= repeat.Count)
        {
            Message?.Invoke(this, $"Repeat (step {number}) finished: {Turns(turns)}.");
            return false;
        }

        if (repeat.Mode != RepeatMode.Times && repeat.MaxIterations > 0 && turns >= repeat.MaxIterations)
            return LimitReached(repeat, number, $"reached its limit of {Turns(repeat.MaxIterations)}");

        Condition? condition = null;
        if (repeat.Mode != RepeatMode.Times)
        {
            condition = repeat.Condition ?? throw new InvalidOperationException($"The Repeat at step {number} has no condition.");
            var primaryOrSecondarySearch = condition.Search ?? (condition.HasSecondCondition ? condition.SecondCondition?.Search : null);
            if (condition.Kind != ConditionKind.VariableCompare || primaryOrSecondarySearch is not null)
            {
                var search = primaryOrSecondarySearch ?? throw new InvalidOperationException($"The condition of the Repeat at step {number} is damaged.");

                // Keep condition checks at least "check every" apart, so a short (or empty) loop can't hammer the CPU.
                if (frame.LastCheckAt is { } lastCheck)
                {
                    var wait = TimeSpan.FromMilliseconds(search.CheckIntervalMs) - (ActiveTime - lastCheck);
                    if (wait > TimeSpan.Zero) await WaitAsync(wait.TotalMilliseconds, token).ConfigureAwait(false);
                }
            }
        }

        if (repeat.MaxDurationMs > 0 && ActiveTime - frame.StartedAt >= TimeSpan.FromMilliseconds(repeat.MaxDurationMs))
            return LimitReached(repeat, number, $"reached its time limit of {repeat.MaxDurationMs / 1000.0:0.#} s after {Turns(turns)}");

        if (condition is null) return true; // Times: no condition to check

        // When the loop would end because its target is NOT seen (While "… is on screen", Until "… is NOT on screen"),
        // a single missed frame isn't trusted: the target may just be re-spawning (a game button that appears again
        // 100-250 ms after a click). Keep looking for up to the grace window and only end when it stays away.
        bool KeepGoing(bool isTrue) => repeat.Mode == RepeatMode.While ? isTrue : !isTrue;
        var grace = repeat.EndsWhenTargetMissing ? repeat.EffectiveKeepCheckingForMs : 0;
        var answer = grace > 0
            ? await EvaluateConditionAsync(run, condition, token,
                stopWhen: c => KeepGoing(c.IsTrue),
                window: TimeSpan.FromMilliseconds(Math.Max(grace, condition.Search!.TimeoutMs)), label: "Repeat").ConfigureAwait(false)
            : await EvaluateConditionAsync(run, condition, token, label: "Repeat").ConfigureAwait(false);
        frame.LastCheckAt = ActiveTime;

        var keepGoing = KeepGoing(answer.IsTrue);
        if (!keepGoing)
        {
            var reason = repeat.Mode == RepeatMode.While ? "its condition is false" : "its condition is true";
            if (grace > 0) reason += $" (kept checking for {grace} ms)";
            Message?.Invoke(this, $"Repeat (step {number}) finished after {Turns(turns)}: {reason}. {answer.Text}");
        }

        return keepGoing;
    }

    private bool LimitReached(RepeatStep repeat, int number, string what)
    {
        var text = $"Repeat (step {number}) {what}.";
        if (repeat.WhenLimitReached == LoopLimitBehavior.ExitLoop)
        {
            Message?.Invoke(this, text + " Leaving the loop and continuing after its End Repeat.");
            return false;
        }

        throw new LoopLimitException(text + " The macro was stopped to be safe. If that many turns are expected, raise the limit, or set the loop to continue when the limit is reached.");
    }

    /// <summary>The answer to a condition plus a sentence for the log.</summary>
    private readonly record struct ConditionAnswer(bool IsTrue, string Text, bool Seen = false, int X = 0, int Y = 0, ScreenRect Match = default);

    /// <summary>
    /// Checks a condition, waiting up to its "keep checking for" time for it to become true (0 = look once).
    /// Uses the same detection as the Find steps; paused time doesn't count; Pause / Stop / Esc work between and during
    /// checks. When the condition sees its text / image / colour, that spot becomes the "last found" spot for clicks.
    /// </summary>
    private async Task<ConditionAnswer> EvaluateConditionAsync(RunContext run, Condition condition, CancellationToken token,
        Func<ConditionCheck, bool>? stopWhen = null, TimeSpan? window = null, string label = "Condition")
    {
        var conditions = run.Conditions;
        var isPureVariable = condition.Kind == ConditionKind.VariableCompare
            && (!condition.HasSecondCondition || condition.SecondCondition?.Kind == ConditionKind.VariableCompare);
        if (isPureVariable)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var vCheck = await conditions.CheckAsync(condition, null, token).ConfigureAwait(false);
            return new ConditionAnswer(vCheck.IsTrue, $"({conditions.Describe(condition)}: {(vCheck.IsTrue ? "true" : "false")}) {vCheck.Details}");
        }

        var search = condition.Search
            ?? (condition.HasSecondCondition ? condition.SecondCondition?.Search : null)
            ?? throw new InvalidOperationException("This condition has no valid search settings. The macro may be damaged.");
        var timeout = window ?? TimeSpan.FromMilliseconds(search.TimeoutMs);
        stopWhen ??= c => c.IsTrue;
        var timer = conditions.NewTimer(condition);
        var started = ActiveTime;

        ConditionCheck check;
        while (true)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            check = await conditions.CheckAsync(condition, timer, token).ConfigureAwait(false);
            if (check.Confidence > 0 || check.Seen || condition.Kind is ConditionKind.ImageVisible or ConditionKind.ColorVisible or ConditionKind.TextVisible)
            {
                var score = Math.Clamp(check.Confidence, 0, 1);
                _currentStepVisionScore = score;
                _lastVisionScore = score;
            }
            if (timer is not null) RecordProbeTime(check.ElapsedMs);
            if (check.Seen) RememberFound(check.X, check.Y, check.Match);

            if (stopWhen(check) || ActiveTime - started >= timeout) break;

            await WaitAsync(search.CheckIntervalMs, token).ConfigureAwait(false);
        }

        ResetTimeline(); // checking takes as long as it takes; start timing again from here
        if (check.Seen) ReportTarget(check.X, check.Y, check.Match, check.Confidence, label, check.ElapsedMs);
        var stats = timer is null ? string.Empty : " " + timer.Describe(ActiveTime - started);
        return new ConditionAnswer(check.IsTrue, $"({conditions.Describe(condition)}: {(check.IsTrue ? "true" : "false")}) {check.Details}{stats}",
            check.Seen, check.X, check.Y, check.Match);
    }
}
