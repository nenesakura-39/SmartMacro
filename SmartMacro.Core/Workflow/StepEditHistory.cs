using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Workflow;

/// <summary>
/// Memory-bounded structural Undo / Redo history for the step list.
/// Instead of serializing every step list to JSON on each edit, captures prefix/suffix structural deltas of
/// cloned <see cref="MacroStep"/> instances that safely share immutable <see cref="string"/> image payloads in RAM.
/// </summary>
public sealed class StepEditHistory
{
    public const int DefaultCapacity = 50;
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private readonly List<StepDelta> _undo = new();
    private readonly List<StepDelta> _redo = new();
    private MacroStep[]? _pendingBefore;
    private long _undoBytes;
    private long _redoBytes;

    public StepEditHistory(int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Keep at least one undo step.");
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum retained byte budget must be positive.");
        Capacity = capacity;
        MaxBytes = maxBytes;
    }

    /// <summary>Maximum number of deltas retained on each stack.</summary>
    public int Capacity { get; }

    /// <summary>Maximum approximate byte budget retained across Undo and Redo deltas combined.</summary>
    public long MaxBytes { get; }

    /// <summary>Approximate retained bytes across Undo and Redo deltas.</summary>
    public long RetainedBytes => _undoBytes + _redoBytes;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>True after <see cref="BeginEdit"/> and before the matching <see cref="CommitPending"/>.</summary>
    public bool HasPendingEdit => _pendingBefore is not null;

    /// <summary>Raised whenever Undo / Redo availability or counts change.</summary>
    public event EventHandler? Changed;

    /// <summary>Captures a structural snapshot of <paramref name="steps"/> before an edit runs.</summary>
    public MacroStep[] CaptureState(IReadOnlyList<MacroStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var copy = new MacroStep[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            copy[i] = steps[i].Clone();
        }
        return copy;
    }

    /// <summary>
    /// Marks the start of a structural or batched edit by cloning <paramref name="stepsBeforeEdit"/>.
    /// Call <see cref="CommitPending"/> once the mutation finishes.
    /// </summary>
    public void BeginEdit(IReadOnlyList<MacroStep> stepsBeforeEdit)
    {
        ArgumentNullException.ThrowIfNull(stepsBeforeEdit);
        if (_pendingBefore is not null)
        {
            CommitPending(stepsBeforeEdit);
        }
        _pendingBefore = CaptureState(stepsBeforeEdit);
    }

    /// <summary>
    /// Finalizes a pending edit begun with <see cref="BeginEdit"/> against <paramref name="stepsAfterEdit"/>.
    /// Returns false when there is no pending edit or when no step actually changed.
    /// </summary>
    public bool CommitPending(IReadOnlyList<MacroStep> stepsAfterEdit)
    {
        ArgumentNullException.ThrowIfNull(stepsAfterEdit);
        var before = _pendingBefore;
        _pendingBefore = null;
        if (before is null)
        {
            return false;
        }

        return CommitTransition(before, stepsAfterEdit);
    }

    /// <summary>
    /// Records the structural transition between a pre-captured <paramref name="before"/> state and
    /// <paramref name="stepsAfterEdit"/>. Clears Redo when a change is recorded.
    /// </summary>
    public bool CommitTransition(IReadOnlyList<MacroStep> before, IReadOnlyList<MacroStep> stepsAfterEdit)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(stepsAfterEdit);

        _pendingBefore = null;
        var delta = BuildDelta(before, stepsAfterEdit);
        if (delta is null)
        {
            return false;
        }

        _redo.Clear();
        _redoBytes = 0;
        Push(_undo, ref _undoBytes, _redo, ref _redoBytes, delta);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Steps back one structural delta relative to <paramref name="currentSteps"/>, returning the restored list and
    /// pushing the delta onto Redo.
    /// </summary>
    public bool TryUndo(IReadOnlyList<MacroStep> currentSteps, out List<MacroStep> restoredSteps)
    {
        ArgumentNullException.ThrowIfNull(currentSteps);
        _pendingBefore = null;
        restoredSteps = new List<MacroStep>();
        if (_undo.Count == 0)
        {
            return false;
        }

        var delta = _undo[^1];
        if (!TryApplyDelta(currentSteps, delta, reverse: true, out restoredSteps))
        {
            return false;
        }

        Pop(_undo, ref _undoBytes);
        Push(_redo, ref _redoBytes, _undo, ref _undoBytes, delta);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Steps forward one structural delta relative to <paramref name="currentSteps"/> after an Undo.
    /// </summary>
    public bool TryRedo(IReadOnlyList<MacroStep> currentSteps, out List<MacroStep> restoredSteps)
    {
        ArgumentNullException.ThrowIfNull(currentSteps);
        _pendingBefore = null;
        restoredSteps = new List<MacroStep>();
        if (_redo.Count == 0)
        {
            return false;
        }

        var delta = _redo[^1];
        if (!TryApplyDelta(currentSteps, delta, reverse: false, out restoredSteps))
        {
            return false;
        }

        Pop(_redo, ref _redoBytes);
        Push(_undo, ref _undoBytes, _redo, ref _redoBytes, delta);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Clears all pending, Undo, and Redo state.</summary>
    public void Clear()
    {
        var hadAny = _undo.Count > 0 || _redo.Count > 0 || _pendingBefore is not null;
        _pendingBefore = null;
        _undo.Clear();
        _redo.Clear();
        _undoBytes = 0;
        _redoBytes = 0;
        if (hadAny)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Push(List<StepDelta> stack, ref long stackBytes, List<StepDelta> otherStack, ref long otherBytes, StepDelta delta)
    {
        stack.Add(delta);
        stackBytes += delta.EstimatedBytes;

        if (stack.Count > Capacity)
        {
            var excess = stack.Count - Capacity;
            for (var i = 0; i < excess; i++)
            {
                stackBytes -= stack[i].EstimatedBytes;
            }
            stack.RemoveRange(0, excess);
        }

        while (stack.Count > 1 && _undoBytes + _redoBytes > MaxBytes)
        {
            stackBytes -= stack[0].EstimatedBytes;
            stack.RemoveAt(0);
        }

        while (otherStack.Count > 0 && _undoBytes + _redoBytes > MaxBytes)
        {
            otherBytes -= otherStack[0].EstimatedBytes;
            otherStack.RemoveAt(0);
        }
    }

    private static StepDelta Pop(List<StepDelta> stack, ref long stackBytes)
    {
        var delta = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        stackBytes -= delta.EstimatedBytes;
        return delta;
    }

    private static StepDelta? BuildDelta(IReadOnlyList<MacroStep> before, IReadOnlyList<MacroStep> after)
    {
        var beforeCount = before.Count;
        var afterCount = after.Count;
        var minCount = Math.Min(beforeCount, afterCount);

        var prefix = 0;
        while (prefix < minCount && StepStructuralEquality.AreEqual(before[prefix], after[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        var remaining = minCount - prefix;
        while (suffix < remaining
               && StepStructuralEquality.AreEqual(before[beforeCount - 1 - suffix], after[afterCount - 1 - suffix]))
        {
            suffix++;
        }

        var beforeMidLen = beforeCount - prefix - suffix;
        var afterMidLen = afterCount - prefix - suffix;
        if (beforeMidLen == 0 && afterMidLen == 0)
        {
            return null;
        }

        var beforeMiddle = new MacroStep[beforeMidLen];
        for (var i = 0; i < beforeMidLen; i++)
        {
            beforeMiddle[i] = before[prefix + i].Clone();
        }

        var afterMiddle = new MacroStep[afterMidLen];
        for (var i = 0; i < afterMidLen; i++)
        {
            afterMiddle[i] = after[prefix + i].Clone();
        }

        var bytes = EstimateDeltaBytes(beforeMiddle, afterMiddle);
        return new StepDelta(prefix, suffix, beforeMiddle, afterMiddle, bytes);
    }

    private static bool TryApplyDelta(
        IReadOnlyList<MacroStep> currentSteps,
        StepDelta delta,
        bool reverse,
        out List<MacroStep> restoredSteps)
    {
        var expectedMiddle = reverse ? delta.AfterMiddle : delta.BeforeMiddle;
        var replacementMiddle = reverse ? delta.BeforeMiddle : delta.AfterMiddle;

        var expectedTotal = delta.PrefixCount + expectedMiddle.Length + delta.SuffixCount;
        if (delta.PrefixCount < 0
            || delta.SuffixCount < 0
            || currentSteps.Count != expectedTotal)
        {
            restoredSteps = new List<MacroStep>();
            return false;
        }

        var restored = new List<MacroStep>(delta.PrefixCount + replacementMiddle.Length + delta.SuffixCount);
        for (var i = 0; i < delta.PrefixCount; i++)
        {
            restored.Add(currentSteps[i].Clone());
        }

        for (var i = 0; i < replacementMiddle.Length; i++)
        {
            restored.Add(replacementMiddle[i].Clone());
        }

        var suffixStart = currentSteps.Count - delta.SuffixCount;
        for (var i = 0; i < delta.SuffixCount; i++)
        {
            restored.Add(currentSteps[suffixStart + i].Clone());
        }

        restoredSteps = restored;
        return true;
    }

    private static long EstimateDeltaBytes(MacroStep[] beforeMiddle, MacroStep[] afterMiddle)
    {
        var seenStrings = new HashSet<string>(ReferenceEqualityComparer.Instance);
        var seenObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 64; // base delta record + arrays

        foreach (var step in beforeMiddle)
        {
            bytes += StepMemoryEstimator.EstimateObjectBytes(step, seenObjects, seenStrings);
        }

        foreach (var step in afterMiddle)
        {
            bytes += StepMemoryEstimator.EstimateObjectBytes(step, seenObjects, seenStrings);
        }

        return bytes;
    }

    internal sealed record StepDelta(
        int PrefixCount,
        int SuffixCount,
        MacroStep[] BeforeMiddle,
        MacroStep[] AfterMiddle,
        long EstimatedBytes);

    internal static class StepStructuralEquality
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();

        public static bool AreEqual(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            var type = left.GetType();
            if (type != right.GetType())
            {
                return false;
            }

            if (left is string ls && right is string rs)
            {
                return string.Equals(ls, rs, StringComparison.Ordinal);
            }

            if (type.IsPrimitive || type.IsEnum || left is Guid || left is decimal)
            {
                return left.Equals(right);
            }

            if (left is IList leftList && right is IList rightList)
            {
                if (leftList.Count != rightList.Count)
                {
                    return false;
                }

                for (var i = 0; i < leftList.Count; i++)
                {
                    if (!AreEqual(leftList[i], rightList[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (left is IDictionary leftDict && right is IDictionary rightDict)
            {
                if (leftDict.Count != rightDict.Count)
                {
                    return false;
                }

                foreach (DictionaryEntry entry in leftDict)
                {
                    if (!rightDict.Contains(entry.Key) || !AreEqual(entry.Value, rightDict[entry.Key]))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (type.IsValueType)
            {
                return left.Equals(right);
            }

            var props = s_properties.GetOrAdd(type, static t => DiscoverSerializedProperties(t));
            foreach (var prop in props)
            {
                if (!AreEqual(prop.GetValue(left), prop.GetValue(right)))
                {
                    return false;
                }
            }

            return true;
        }

        internal static PropertyInfo[] GetSerializableProperties(Type type) =>
            s_properties.GetOrAdd(type, static t => DiscoverSerializedProperties(t));

        private static PropertyInfo[] DiscoverSerializedProperties(Type type)
        {
            var all = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var list = new List<PropertyInfo>(all.Length);
            foreach (var prop in all)
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var ignore = prop.GetCustomAttribute<JsonIgnoreAttribute>();
                if (ignore is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }

                list.Add(prop);
            }

            return list.ToArray();
        }
    }

    private static class StepMemoryEstimator
    {
        public static long EstimateObjectBytes(
            object? value,
            HashSet<object> seenObjects,
            HashSet<string> seenStrings)
        {
            if (value is null)
            {
                return 0;
            }

            if (value is string str)
            {
                if (str.Length == 0 || !seenStrings.Add(str))
                {
                    return 0;
                }

                return 24L + str.Length * 2L;
            }

            var type = value.GetType();
            if (type.IsValueType)
            {
                return 0;
            }

            if (!seenObjects.Add(value))
            {
                return 0;
            }

            if (value is IList list)
            {
                long total = 64L + list.Count * 8L;
                for (var i = 0; i < list.Count; i++)
                {
                    total += EstimateObjectBytes(list[i], seenObjects, seenStrings);
                }
                return total;
            }

            if (value is IDictionary dict)
            {
                long total = 96L + dict.Count * 24L;
                foreach (DictionaryEntry entry in dict)
                {
                    total += EstimateObjectBytes(entry.Key, seenObjects, seenStrings);
                    total += EstimateObjectBytes(entry.Value, seenObjects, seenStrings);
                }
                return total;
            }

            long objectBytes = value is MacroStep ? 256L : 64L;
            var props = StepStructuralEquality.GetSerializableProperties(type);
            foreach (var prop in props)
            {
                objectBytes += EstimateObjectBytes(prop.GetValue(value), seenObjects, seenStrings);
            }

            return objectBytes;
        }
    }
}
