using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Targeting;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Workflow;
using SmartMacro.Mvvm;
using SmartMacro.Services;
using SmartMacro.Services.Ocr;
using SmartMacro.Services.Vision;
using SmartMacro.ViewModels.Steps;

namespace SmartMacro.ViewModels;


public sealed partial class EditorViewModel : ObservableObject
{
    // ---------------- ✂ Break recorded input into steps ----------------

    /// <summary>
    /// Replaces a recorded-input step (parameter = its editor, or the selected step) with normal editable steps
    /// (see <see cref="StepCleanup.ExpandInputSequence"/>); clicks keep their Smart Click Snapshot. Undo puts it back.
    /// </summary>
    public RelayCommand BreakIntoStepsCommand { get; }

    private void BreakIntoSteps(object? parameter)
    {
        if (!HasMacro) return;
        if ((parameter as InputSequenceStepViewModel ?? SelectedStep as InputSequenceStepViewModel) is not { } sequence) return;
        var index = Steps.IndexOf(sequence);
        if (index < 0 || sequence.Model is not InputSequenceStep model) return;

        List<MacroStep> expanded;
        try
        {
            expanded = StepCleanup.ExpandInputSequence(model);
        }
        catch (InvalidOperationException ex)
        {
            _setStatus($"Can't break this recording into steps: {ex.Message}", StatusKind.Warning);
            return;
        }

        if (expanded.Count == 0)
        {
            _setStatus("This recording has nothing that would become a step.", StatusKind.Info);
            return;
        }

        RecordUndo();
        Steps.RemoveAt(index); // the collection handler unhooks the old editor
        var added = new List<StepViewModel>(expanded.Count);
        for (var i = 0; i < expanded.Count; i++)
        {
            var vm = CreateStepViewModel(expanded[i]);
            Steps.Insert(index + i, vm);
            added.Add(vm);
        }

        _selection.Clear();
        SelectedStep = added[0];
        SelectStepsRequested?.Invoke(this, added.ToArray());
        Touch();
        var pictures = expanded.OfType<MouseClickStep>().Count(c => c.HasTargetThumbnail);
        _setStatus($"Step {index + 1} is now {expanded.Count} normal step(s)" +
                   (pictures > 0 ? $"; {pictures} click(s) kept their \"what you clicked\" picture" : "") + ". Ctrl+Z undoes it.", StatusKind.Success);
    }

    // ---------------- ✨ Auto-Fix block structure ----------------

    private bool _canAutoFixBlocks;
    private string _blockProblemText = "";

    /// <summary>
    /// ✨ Auto-Fix block structure: removes orphan Else / End If / End Repeat markers (and extra Elses) and adds the
    /// missing End If / End Repeat (see <see cref="StepCleanup.RepairBlockStructure"/>). Ctrl+Z undoes it.
    /// </summary>
    public RelayCommand AutoFixBlocksCommand { get; }

    /// <summary>True while the If / Repeat markers don't match up in a way Auto-Fix can repair (shows the banner).</summary>
    public bool CanAutoFixBlocks
    {
        get => _canAutoFixBlocks;
        private set
        {
            if (SetProperty(ref _canAutoFixBlocks, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The banner text: the first block problem (with its step number) and how many more there are.</summary>
    public string BlockProblemText
    {
        get => _blockProblemText;
        private set => SetProperty(ref _blockProblemText, value);
    }

    // Called from Revalidate with the fresh block analysis.
    private void UpdateBlockFixBanner(BlockStructure blocks)
    {
        var models = Steps.Select(s => s.Model).ToList();
        var fixable = !blocks.IsValid && StepCleanup.NeedsBlockRepair(models);
        CanAutoFixBlocks = fixable;
        if (!fixable)
        {
            BlockProblemText = "";
            return;
        }

        var first = blocks.Problems[0];
        var more = blocks.Problems.Count - 1;
        BlockProblemText = $"The If / Repeat blocks don't match up. Step {first.StepIndex + 1}: {first.Message}" +
                           (more > 0 ? $" (+{more} more problem{(more == 1 ? "" : "s")})" : "");
    }

    private void AutoFixBlocks()
    {
        if (!HasMacro) return;
        var models = Steps.Select(s => s.Model).ToList();
        var before = Snapshot();
        var result = StepCleanup.RepairBlockStructure(models); // fixes the copy in place
        if (!result.Changed)
        {
            _setStatus(BlockRepairResult.NothingToFixText, StatusKind.Info);
            return;
        }

        _history.Record(before);
        RaiseHistoryChanged();

        // Keep the editors of the steps that stayed (same objects), make new ones for the added End markers.
        var existing = new Dictionary<MacroStep, StepViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var vm in Steps) existing[vm.Model] = vm;
        var keepSelected = SelectedStep is { } current && models.Contains(current.Model) ? current : null;
        foreach (var old in Steps) old.Modified -= OnStepModified;
        Steps.Clear();
        foreach (var step in models)
            Steps.Add(existing.TryGetValue(step, out var kept) ? kept : CreateStepViewModel(step));

        _selection.Clear();
        SelectedStep = keepSelected ?? Steps.ElementAtOrDefault(Math.Clamp(result.FirstChangedIndex, 0, Math.Max(0, Steps.Count - 1)));
        Touch();
        _log.Info($"✨ Auto-Fix block structure: {result.Summary}");
        _setStatus($"✨ Fixed the blocks: {result.Summary} Ctrl+Z undoes it.", StatusKind.Success);
    }

    // ---------------- Smart Click Snapshots: 🔍 Convert step to full "Find image & click" ----------------

    /// <summary>
    /// Replaces a recorded Mouse click (parameter = its editor, or the selected step) with a Find image step that looks for
    /// the click's recorded picture and does the same click on it (see <see cref="SmartClickConversion.ToFindImageStep"/>).
    /// Undo puts the click back.
    /// </summary>
    public RelayCommand ConvertClickToFindImageCommand { get; }

    private void ConvertClickToFindImage(object? parameter)
    {
        if (!HasMacro) return;
        if ((parameter as MouseClickStepViewModel ?? SelectedStep as MouseClickStepViewModel) is not { } click) return;
        var index = Steps.IndexOf(click);
        if (index < 0 || click.Model is not MouseClickStep model) return;

        FindImageStep find;
        try
        {
            find = SmartClickConversion.ToFindImageStep(model);
        }
        catch (InvalidOperationException ex)
        {
            _setStatus(ex.Message, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var vm = CreateStepViewModel(find);
        Steps[index] = vm; // the collection handler unhooks the old editor and hooks the new one
        _selection.Clear();
        SelectedStep = vm;
        SelectStepsRequested?.Invoke(this, new[] { vm });
        Touch();
        _setStatus($"Step {index + 1} is now \"Find image & click\": it looks for what you clicked anywhere on screen (up to {SmartClickConversion.ConvertedWaitMs / 1000} s) and clicks it. Ctrl+Z undoes it.", StatusKind.Success);
    }

    /// <summary>The Find step / If-Repeat condition above a step whose found spot "the last found spot" would use.</summary>
    private Core.Models.ScreenSearchStep? ReferenceFor(StepViewModel step)
    {
        var index = Steps.IndexOf(step);
        return index < 0 ? null : SmartClickConversion.LastFoundReference(Steps.Select(s => s.Model).ToList(), index);
    }

    // ---------------- Undo / Redo (Ctrl+Z / Ctrl+Y) ----------------

    /// <summary>↶ Undo the last structural edit (add, delete, move, duplicate, wrap, clean up, paste, quick pattern).</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>↷ Redo what Undo took back.</summary>
    public RelayCommand RedoCommand { get; }

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public string UndoTooltip => _history.CanUndo
        ? $"Undo (Ctrl+Z) — {_history.UndoCount} change{(_history.UndoCount == 1 ? "" : "s")} can be undone"
        : "Undo (Ctrl+Z) — nothing to undo yet";

    public string RedoTooltip => _history.CanRedo ? "Redo (Ctrl+Y)" : "Redo (Ctrl+Y) — nothing to redo";

    /// <summary>The step list as a snapshot string (for Undo / Redo).</summary>
    private string Snapshot() => StepSnapshots.Capture(Steps.Select(s => s.Model));

    /// <summary>Call right before a structural edit, so it can be undone.</summary>
    private void RecordUndo()
    {
        if (!HasMacro) return;
        _history.Record(Snapshot());
        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTooltip));
        OnPropertyChanged(nameof(RedoTooltip));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void Undo()
    {
        if (!HasMacro || !_history.TryUndo(Snapshot(), out var snapshot)) return;
        RestoreSnapshot(snapshot);
        RaiseHistoryChanged();
        _setStatus($"Undone.{(_history.CanUndo ? $" {_history.UndoCount} more change(s) can be undone." : "")} Ctrl+Y redoes it.", StatusKind.Info);
    }

    private void Redo()
    {
        if (!HasMacro || !_history.TryRedo(Snapshot(), out var snapshot)) return;
        RestoreSnapshot(snapshot);
        RaiseHistoryChanged();
        _setStatus("Redone.", StatusKind.Info);
    }

    /// <summary>Puts a snapshot back into the list, keeping the selection on the same step (by ID) where possible.</summary>
    private void RestoreSnapshot(string snapshot)
    {
        var selectedId = SelectedStep?.Id;
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        List<MacroStep> restored;
        try
        {
            restored = StepSnapshots.Restore(snapshot);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            _log.Error("An undo snapshot couldn't be read.", ex);
            return;
        }

        ReplaceSteps(restored);
        _selection.Clear();
        SelectedStep = Steps.FirstOrDefault(s => s.Id == selectedId)
                       ?? (Steps.Count == 0 ? null : Steps[Math.Clamp(selectedIndex, 0, Steps.Count - 1)]);
        Touch();
    }

    // ---------------- Copy / Paste (Ctrl+C / Ctrl+V) ----------------

    /// <summary>Copies the selected steps (a single If / Repeat takes its whole block) to the clipboard.</summary>
    public RelayCommand CopySelectedStepCommand { get; }

    /// <summary>Pastes copied steps after the selected step (or at the end), also from another macro.</summary>
    public RelayCommand PasteStepCommand { get; }

    private void CopySelectedSteps()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;
        var models = Steps.Select(s => s.Model).ToList();
        var indexes = StepClipboard.ExpandSelection(models, selected.Select(s => Steps.IndexOf(s)).ToList());
        if (indexes is null)
        {
            _setStatus("Copy whole blocks: select from the If to its End If (or Repeat to End Repeat), or just the If / Repeat step itself.", StatusKind.Warning);
            return;
        }

        var text = StepClipboard.Serialize(indexes.Select(i => models[i]).ToList());
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log.Warning("The clipboard is busy; try copying again.", ex);
            _setStatus("The clipboard is busy (another app is using it). Try Ctrl+C again.", StatusKind.Warning);
            return;
        }

        _setStatus(indexes.Count == 1 ? $"Copied step {indexes[0] + 1}. Press Ctrl+V to paste it here or in another macro."
            : $"Copied {indexes.Count} steps ({indexes[0] + 1}–{indexes[^1] + 1}). Press Ctrl+V to paste them here or in another macro.", StatusKind.Info);
    }

    private void PasteSteps()
    {
        if (!HasMacro) return;
        string? text = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log.Warning("The clipboard is busy; try pasting again.", ex);
        }

        if (!StepClipboard.TryParse(text, out var pasted, out var error))
        {
            _setStatus(error ?? "Nothing to paste.", StatusKind.Warning);
            return;
        }

        RecordUndo();
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var index = InsertOutsideFolded(StepListEditing.InsertIndex(Steps.Count, selectedIndex, AddAtEnd ? InsertPosition.AtEnd : InsertPosition.AfterSelected));
        var added = new List<StepViewModel>();
        foreach (var model in pasted)
        {
            var vm = CreateStepViewModel(model);
            Steps.Insert(index + added.Count, vm);
            added.Add(vm);
        }

        _selection = added;
        SelectedStep = added[0];
        SelectStepsRequested?.Invoke(this, added);
        Touch();
        _setStatus(added.Count == 1 ? $"Pasted 1 step as step {index + 1}. Ctrl+Z undoes it." : $"Pasted {added.Count} steps as steps {index + 1}–{index + added.Count}. Ctrl+Z undoes it.", StatusKind.Success);
    }


    private void AddStep(object? parameter, InsertPosition position, bool forcePosition = false)
    {
        if (!HasMacro || parameter is not StepKind kind) return;

        var model = MacroFactory.CreateStep(kind, _defaultDelayMs());
        var selectedIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
        var models = Steps.Select(s => s.Model).ToList();
        var index = forcePosition
            ? StepListEditing.InsertIndex(Steps.Count, selectedIndex, position)
            : StepListEditing.InsertIndex(models, selectedIndex, position, kind);

        // A folded If / Repeat is one row on screen: "after the selected step" means after its whole block, never
        // inside the hidden part (the new step would be selected but invisible).
        index = InsertOutsideFolded(index);

        // A standalone Else / End If / End Repeat is only added where it closes (or splits) an open block, so the
        // toolbox can't create orphan markers (an End If at the top of the macro with no If before it).
        if (StepListEditing.CheckMarkerInsert(models, index, model) is { } markerProblem)
        {
            _setStatus(markerProblem, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var step = CreateStepViewModel(model);
        Steps.Insert(index, step);

        // If and Repeat come with their End, so the new block is complete straight away.
        var closer = kind switch
        {
            StepKind.If => MacroFactory.CreateStep(StepKind.EndIf),
            StepKind.Repeat => MacroFactory.CreateStep(StepKind.EndRepeat),
            _ => null,
        };
        if (closer is not null) Steps.Insert(index + 1, CreateStepViewModel(closer));

        _selection.Clear();
        SelectedStep = step; // the list scrolls the new step into view
        Touch();
        if (closer is not null)
            _setStatus($"Added {StepDescriber.DisplayName(kind)} and {StepDescriber.DisplayName(closer.Kind)} as steps {index + 1}–{index + 2}. Add steps between them, or select steps and use \"Wrap in\".", StatusKind.Info);
        else
            _setStatus($"Added {StepDescriber.DisplayName(kind)} as step {index + 1}{(index == Steps.Count - 1 ? " (at the end)" : "")}.", StatusKind.Info);
    }

    /// <summary>
    /// Puts an If … End If or Repeat … End Repeat around the selected steps. The selection must be steps next to
    /// each other and must contain whole blocks (not just the If of a block).
    /// </summary>
    private void WrapSelection(StepKind kind)
    {
        if (!HasMacro) return;
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;

        var first = Steps.IndexOf(selected[0]);
        var last = Steps.IndexOf(selected[^1]);
        if (last - first + 1 != selected.Count)
        {
            _setStatus("Select steps that are next to each other (click the first, then Shift+click the last), then wrap them.", StatusKind.Warning);
            return;
        }

        // A folded If / Repeat in the selection is one row on screen: wrap its whole block (header through its End).
        (first, last, _) = StepListEditing.GrowToCollapsedBlocks(Steps.Select(s => s.Model).ToList(), first, last, _collapsed);
        var count = last - first + 1;

        var inner = BlockStructure.Analyze(Steps.Skip(first).Take(count).Select(s => s.Model).ToList());
        if (!inner.IsValid)
        {
            _setStatus("The selection contains only part of a block. Select whole blocks (from If to End If, or Repeat to End Repeat) to wrap them.", StatusKind.Warning);
            return;
        }

        RecordUndo();
        var opener = CreateStepViewModel(MacroFactory.CreateStep(kind));
        var closer = CreateStepViewModel(MacroFactory.CreateStep(kind == StepKind.If ? StepKind.EndIf : StepKind.EndRepeat));
        Steps.Insert(last + 1, closer);
        Steps.Insert(first, opener);
        SelectedStep = opener;
        Touch();
        _setStatus($"Wrapped {count} step{(count == 1 ? "" : "s")} in {StepDescriber.DisplayName(kind)}. Set it up in Properties.", StatusKind.Success);
    }

    /// <summary>
    /// "Convert recording to clean steps": collapses runs of recorded Delay + Move mouse steps. With several steps
    /// selected (next to each other) only those are cleaned; otherwise the whole macro. Drags (moves while a button is
    /// held) and steps with a comment or turned off are kept. Total waiting time stays the same.
    /// </summary>
    private void CleanUpMousePaths()
    {
        if (!HasMacro) return;
        var selected = SelectedInOrder();
        var models = Steps.Select(s => s.Model).ToList();
        var first = 0;
        var last = models.Count - 1;
        var scope = "the macro";
        if (selected.Count > 1)
        {
            first = Steps.IndexOf(selected[0]);
            last = Steps.IndexOf(selected[^1]);
            scope = "the selected steps";
        }

        var before = Snapshot();
        var result = StepCleanup.CollapseMouseMoves(models, first, last);
        if (result.RemovedSteps <= 0)
        {
            _setStatus($"Nothing to clean up in {scope}: no runs of Delay + Move mouse steps outside drags.", StatusKind.Info);
            return;
        }

        _history.Record(before);
        RaiseHistoryChanged();

        var existing = new Dictionary<MacroStep, StepViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var vm in Steps) existing[vm.Model] = vm;
        var keepSelected = SelectedStep is { } current && result.Steps.Contains(current.Model) ? current : null;
        foreach (var old in Steps) old.Modified -= OnStepModified;
        Steps.Clear();
        foreach (var step in result.Steps)
            Steps.Add(existing.TryGetValue(step, out var kept) ? kept : CreateStepViewModel(step));

        _selection.Clear();
        SelectedStep = keepSelected ?? Steps.ElementAtOrDefault(Math.Min(first, Steps.Count - 1));
        Touch();
        _setStatus($"Cleaned up {result.CollapsedRuns} mouse path(s) in {scope}: {result.RemovedSteps:N0} step(s) fewer, same total timing. Ctrl+Z undoes it.", StatusKind.Success);
    }

    /// <summary>
    /// Deletes the selected steps. Deleting an If / Repeat (or its End If / End Repeat) also removes the other markers
    /// of that block (If, Else, End If / Repeat, End Repeat) but keeps the steps that were inside, so a delete can
    /// never leave an orphan End If / Else / End Repeat behind (see <see cref="StepListEditing.PlanDelete"/>).
    /// </summary>
    private void DeleteSelectedSteps()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return;

        // A folded If / Repeat is one row on screen: deleting it deletes the whole block (Ctrl+Z brings it back).
        var plan = StepListEditing.PlanDelete(Steps.Select(s => s.Model).ToList(), selected.Select(s => Steps.IndexOf(s)), _collapsed);
        if (plan.Indexes.Count == 0) return;
        var toRemove = plan.Indexes.Select(i => Steps[i]).ToList();

        RecordUndo();
        var firstIndex = plan.Indexes[0];
        foreach (var step in toRemove.OrderByDescending(s => Steps.IndexOf(s)).ToList())
            Steps.Remove(step);

        _selection.Clear();
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(firstIndex, 0, Steps.Count - 1)];
        Touch();
        if (plan.Note is not null) _setStatus(plan.Note, StatusKind.Info);
    }

    /// <summary>
    /// Duplicate (Ctrl+D): copies the selected steps (next to each other) right after them. Block-aware: an If / Repeat (or
    /// its Else / End) copies the WHOLE block with every step inside, so the copy is a valid block of its own
    /// (<see cref="StepListEditing.PlanDuplicate"/>). Not next to each other: only the focused step (or its block).
    /// </summary>
    private void DuplicateSelectedStep()
    {
        if (SelectedStep is not { } step) return;
        var models = Steps.Select(s => s.Model).ToList();
        var selected = SelectedInOrder();
        int first, last;
        if (selected.Count > 1 && Steps.IndexOf(selected[^1]) - Steps.IndexOf(selected[0]) + 1 == selected.Count)
        {
            first = Steps.IndexOf(selected[0]);
            last = Steps.IndexOf(selected[^1]);
        }
        else
        {
            first = last = Steps.IndexOf(step);
        }

        // A folded block always copies whole (what you see is one row).
        if (Enumerable.Range(first, last - first + 1).Any(i => Steps[i].IsCollapsed))
            (first, last, _) = StepListEditing.GrowToWholeBlocks(models, first, last);

        var plan = StepListEditing.PlanDuplicate(models, first, last);
        if (!plan.Allowed)
        {
            if (plan.Problem is not null) _setStatus(plan.Problem, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var added = new List<StepViewModel>();
        foreach (var copy in StepListEditing.DuplicateSteps(models, plan))
        {
            var vm = CreateStepViewModel(copy);
            Steps.Insert(plan.InsertAt + added.Count, vm);
            added.Add(vm);
        }

        // A copy of a folded block starts folded too.
        for (var i = 0; i < added.Count; i++)
        {
            if (Steps[plan.First + i].IsCollapsed) _collapsed.Add(added[i].Id);
        }

        _selection = added.Count > 1 ? added : new List<StepViewModel>();
        SelectedStep = added[0];
        if (added.Count > 1) SelectStepsRequested?.Invoke(this, added);
        Touch();
        RefreshListView();
        if (plan.TookWholeBlocks || plan.Count > 1)
            _setStatus(plan.TookWholeBlocks
                ? $"Duplicated the whole block ({plan.Count} steps), right after the original."
                : $"Duplicated {plan.Count} steps.", StatusKind.Info);
    }

    /// <summary>
    /// An insert position moved out of folded blocks (see <see cref="StepListEditing.InsertIndexOutsideCollapsed"/>).
    /// Used by add, paste, Quick patterns and blueprints.
    /// </summary>
    private int InsertOutsideFolded(int index) =>
        StepListEditing.InsertIndexOutsideCollapsed(Steps.Select(s => s.Model).ToList(), index, _collapsed);

    private bool CanMove(bool up)
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0) return false;
        return up ? Steps.IndexOf(selected[0]) > 0 : Steps.IndexOf(selected[^1]) < Steps.Count - 1;
    }

    /// <summary>Move up / down / to top / to bottom for the selected steps (they must be next to each other).</summary>
    private void MoveSelected(MoveDirection direction)
    {
        if (!TryGetSelectedRange(out var first, out var last)) return;
        var models = Steps.Select(s => s.Model).ToList();
        // A folded If / Repeat is one row on screen: it moves with everything it hides (header to End, as one unit), and
        // moving past a folded block jumps over the whole block instead of landing inside it where it can't be seen.
        Apply(StepListEditing.PlanMove(models, first, last, direction, _collapsed));
    }

    /// <summary>
    /// Drag and drop: moves the selected steps (whole blocks when an If / Repeat is part of them) in front of the
    /// step that is now at <paramref name="dropIndex"/> (Steps.Count = after the last step).
    /// </summary>
    public void MoveSelectionTo(int dropIndex)
    {
        if (!HasMacro || !TryGetSelectedRange(out var first, out var last)) return;
        Apply(StepListEditing.PlanDrop(Steps.Select(s => s.Model).ToList(), first, last, dropIndex));
    }

    private bool TryGetSelectedRange(out int first, out int last)
    {
        first = last = -1;
        var selected = SelectedInOrder();
        if (selected.Count == 0) return false;

        first = Steps.IndexOf(selected[0]);
        last = Steps.IndexOf(selected[^1]);
        if (last - first + 1 == selected.Count) return true;

        _setStatus("To move several steps at once, select steps that are next to each other (click the first, then Shift+click the last).", StatusKind.Warning);
        return false;
    }

    /// <summary>Carries out a planned move (or explains why it isn't possible) and keeps the moved steps selected.</summary>
    private void Apply(MovePlan plan)
    {
        if (!plan.Allowed)
        {
            if (plan.Problem is not null) _setStatus(plan.Problem, StatusKind.Warning);
            return;
        }

        RecordUndo();
        var moved = Steps.Skip(plan.First).Take(plan.Count).ToList();
        if (plan.NewFirst < plan.First)
        {
            for (var i = 0; i < plan.Count; i++) Steps.Move(plan.First + i, plan.NewFirst + i);
        }
        else
        {
            for (var i = plan.Count - 1; i >= 0; i--) Steps.Move(plan.First + i, plan.NewFirst + i);
        }

        _selection = moved;
        SelectedStep = moved[0];
        SelectStepsRequested?.Invoke(this, moved);
        Touch();

        var where = plan.NewFirst == 0 ? "to the top" : plan.NewFirst + plan.Count == Steps.Count ? "to the bottom" : $"to step {plan.NewFirst + 1}";
        var what = plan.Count == 1 ? "Moved the step" : $"Moved {plan.Count} steps";
        var blocks = plan.TookWholeBlocks ? " (whole If / Repeat blocks were moved together)" : "";
        if (plan.TookWholeBlocks || Math.Abs(plan.NewFirst - plan.First) > 1)
            _setStatus($"{what} {where}{blocks}.", StatusKind.Info);
    }

}
