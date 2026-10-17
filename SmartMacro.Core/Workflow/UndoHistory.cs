using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;

namespace SmartMacro.Core.Workflow;

/// <summary>
/// Undo / Redo for the workflow editor: a stack of snapshots of the step list (the steps as JSON, see
/// <see cref="StepSnapshots"/>), taken right BEFORE every structural edit (add, delete, move, duplicate, wrap, clean up,
/// paste, quick pattern). At most <see cref="Capacity"/> snapshots are kept; the oldest go first.
/// Undo puts the current list on the Redo stack and hands back the previous one; a new edit clears Redo.
/// </summary>
public sealed class UndoHistory
{
    public const int DefaultCapacity = 50;

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();

    public UndoHistory(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Keep at least one undo step.");
        Capacity = capacity;
    }

    /// <summary>Most snapshots kept on each stack.</summary>
    public int Capacity { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>Raised whenever CanUndo / CanRedo may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Call right before a structural edit with the snapshot of the list as it is now. Clears Redo. A snapshot equal to
    /// the newest one isn't stored twice.
    /// </summary>
    public void Record(string snapshotBeforeEdit)
    {
        ArgumentNullException.ThrowIfNull(snapshotBeforeEdit);
        if (_undo.Count == 0 || _undo[^1] != snapshotBeforeEdit) Push(_undo, snapshotBeforeEdit);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Steps back: returns the snapshot to restore, and remembers <paramref name="current"/> for Redo.
    /// False when there is nothing to undo.
    /// </summary>
    public bool TryUndo(string current, out string snapshot)
    {
        ArgumentNullException.ThrowIfNull(current);
        snapshot = string.Empty;
        if (_undo.Count == 0) return false;

        snapshot = Pop(_undo);
        Push(_redo, current);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Steps forward again after an Undo. False when there is nothing to redo.</summary>
    public bool TryRedo(string current, out string snapshot)
    {
        ArgumentNullException.ThrowIfNull(current);
        snapshot = string.Empty;
        if (_redo.Count == 0) return false;

        snapshot = Pop(_redo);
        Push(_undo, current);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Forgets everything (another macro was opened, changes were discarded).</summary>
    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Push(List<string> stack, string value)
    {
        stack.Add(value);
        if (stack.Count > Capacity) stack.RemoveRange(0, stack.Count - Capacity);
    }

    private static string Pop(List<string> stack)
    {
        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }
}

/// <summary>Turns a step list into a snapshot string and back (the same JSON as macro files, so only known step types load).</summary>
public static class StepSnapshots
{
    public static string Capture(IEnumerable<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return MacroJson.Serialize(steps.ToList());
    }

    /// <summary>The steps of a snapshot (with the same IDs as when it was taken).</summary>
    public static List<MacroStep> Restore(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var steps = MacroJson.Deserialize<List<MacroStep>>(snapshot) ?? new List<MacroStep>();
        steps.RemoveAll(s => s is null);
        return steps;
    }
}
