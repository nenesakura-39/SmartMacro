using System.Globalization;
using System.Text;
using SmartMacro.Core.Models;

namespace SmartMacro.Core.Playback;

/// <summary>Reads plain text from the OS clipboard (implemented by the WPF host; mocked in unit tests).</summary>
public interface IClipboardReader
{
    string? ReadText();
}

/// <summary>Immutable snapshot of one variable's current value for the live Variables panel.</summary>
public readonly record struct VariableSnapshot(string Name, MacroVariableType Type, string Value);

/// <summary>
/// Thread-safe, plain-data variable store for one macro execution frame.
/// Supports <see cref="MacroVariableType.Number"/>, <see cref="MacroVariableType.Text"/>, and
/// <see cref="MacroVariableType.Boolean"/>, <c>{name}</c> string interpolation, structured comparisons, and resilient
/// OCR number extraction.
/// </summary>
public sealed class VariableStore
{
    private sealed class Entry
    {
        public required string Name { get; init; }
        public MacroVariableType Type { get; set; }
        public double NumberValue { get; set; }
        public string TextValue { get; set; } = string.Empty;
        public bool BoolValue { get; set; }

        public string Format() => Type switch
        {
            MacroVariableType.Number => FormatNumber(NumberValue),
            MacroVariableType.Boolean => BoolValue ? "true" : "false",
            _ => TextValue,
        };
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<IReadOnlyList<VariableSnapshot>>? Changed;

    public VariableStore(IEnumerable<MacroVariableDefinition>? definitions = null)
    {
        if (definitions is not null) InitializeFrom(definitions);
    }

    public void InitializeFrom(IEnumerable<MacroVariableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        IReadOnlyList<VariableSnapshot> snap;
        lock (_gate)
        {
            _entries.Clear();
            foreach (var def in definitions)
            {
                if (def is null || string.IsNullOrWhiteSpace(def.Name)) continue;
                var name = def.Name.Trim();
                var entry = new Entry { Name = name, Type = def.Type };
                AssignRaw(entry, def.DefaultValue);
                _entries[name] = entry;
            }

            snap = BuildSnapshotNoLock();
        }

        Changed?.Invoke(this, snap);
    }

    public bool TryGetFormatted(string name, out string value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            value = string.Empty;
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(name.Trim(), out var entry))
            {
                value = entry.Format();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public bool HasVariable(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate) return _entries.ContainsKey(name.Trim());
    }

    public MacroVariableType GetTypeOrDefault(string? name, MacroVariableType fallback = MacroVariableType.Text)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        lock (_gate)
        {
            return _entries.TryGetValue(name.Trim(), out var entry) ? entry.Type : fallback;
        }
    }

    public string GetText(string? name) =>
        !string.IsNullOrWhiteSpace(name) && TryGetFormatted(name, out var val) ? val : string.Empty;

    public bool TryGetNumber(string name, out double value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name.Trim(), out var entry))
            {
                if (entry.Type == MacroVariableType.Number)
                {
                    value = entry.NumberValue;
                    return true;
                }

                if (entry.Type == MacroVariableType.Boolean)
                {
                    value = entry.BoolValue ? 1 : 0;
                    return true;
                }

                return TryParseScreenNumber(entry.TextValue, out value);
            }
        }

        value = 0;
        return false;
    }

    public string SetRaw(string name, MacroVariableType fallbackType, string? rawValue)
    {
        return Apply(name, fallbackType, VariableOperation.Set, rawValue ?? string.Empty);
    }

    public string Apply(string name, MacroVariableType fallbackType, VariableOperation operation, string incomingText)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Variable name cannot be empty.");

        var trimmed = name.Trim();
        string formatted;
        IReadOnlyList<VariableSnapshot> snap;

        lock (_gate)
        {
            if (!_entries.TryGetValue(trimmed, out var entry))
            {
                entry = new Entry
                {
                    Name = trimmed,
                    Type = operation is VariableOperation.Add or VariableOperation.Subtract
                        ? MacroVariableType.Number
                        : operation == VariableOperation.AppendText
                            ? MacroVariableType.Text
                            : fallbackType,
                };
                _entries[trimmed] = entry;
            }

            switch (operation)
            {
                case VariableOperation.Set:
                    AssignRaw(entry, incomingText);
                    break;

                case VariableOperation.Add:
                case VariableOperation.Subtract:
                {
                    var delta = ParseNumberOrZero(incomingText);
                    if (operation == VariableOperation.Subtract) delta = -delta;
                    if (entry.Type == MacroVariableType.Number)
                    {
                        entry.NumberValue += delta;
                    }
                    else
                    {
                        var current = ParseNumberOrZero(entry.Format());
                        entry.Type = MacroVariableType.Number;
                        entry.NumberValue = current + delta;
                    }

                    break;
                }

                case VariableOperation.AppendText:
                {
                    var currentText = entry.Format();
                    entry.Type = MacroVariableType.Text;
                    entry.TextValue = currentText + incomingText;
                    break;
                }
            }

            formatted = entry.Format();
            snap = BuildSnapshotNoLock();
        }

        Changed?.Invoke(this, snap);
        return formatted;
    }

    /// <summary>
    /// Replaces <c>{varName}</c> placeholders with their current variable values. Unknown placeholders or unmatched
    /// braces are left untouched so literal braces in text never fail.
    /// </summary>
    public string Interpolate(string? template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template ?? string.Empty;

        lock (_gate)
        {
            if (_entries.Count == 0) return template;

            var sb = new StringBuilder(template.Length + 16);
            var i = 0;
            while (i < template.Length)
            {
                var open = template.IndexOf('{', i);
                if (open < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }

                sb.Append(template, i, open - i);
                var close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    sb.Append(template, open, template.Length - open);
                    break;
                }

                var token = template[(open + 1)..close].Trim();
                if (MacroVariableDefinition.IsValidName(token) && _entries.TryGetValue(token, out var entry))
                {
                    sb.Append(entry.Format());
                    i = close + 1;
                }
                else
                {
                    // Keep unknown token intact and advance past '{' so nested "{foo {bar}}" still resolves "{bar}".
                    sb.Append('{');
                    i = open + 1;
                }
            }

            return sb.ToString();
        }
    }

    public bool EvaluateComparison(VariableComparison comparison, bool negate, out string details)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var varName = (comparison.VariableName ?? string.Empty).Trim();
        var rightResolved = Interpolate(comparison.RightValue ?? string.Empty);

        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(varName, out entry);
        }

        var leftText = entry?.Format() ?? "0";
        bool rawAnswer;

        var isNumericOp = comparison.Operator is VariableCompareOperator.GreaterThan
            or VariableCompareOperator.GreaterOrEqual
            or VariableCompareOperator.LessThan
            or VariableCompareOperator.LessOrEqual;

        if (isNumericOp || entry?.Type == MacroVariableType.Number)
        {
            var leftNum = entry?.Type == MacroVariableType.Number ? entry.NumberValue : ParseNumberOrZero(leftText);
            var rightNum = ParseNumberOrZero(rightResolved);
            rawAnswer = comparison.Operator switch
            {
                VariableCompareOperator.Equals => Math.Abs(leftNum - rightNum) < 1e-9,
                VariableCompareOperator.NotEquals => Math.Abs(leftNum - rightNum) >= 1e-9,
                VariableCompareOperator.GreaterThan => leftNum > rightNum,
                VariableCompareOperator.GreaterOrEqual => leftNum >= rightNum,
                VariableCompareOperator.LessThan => leftNum < rightNum,
                VariableCompareOperator.LessOrEqual => leftNum <= rightNum,
                VariableCompareOperator.Contains => FormatNumber(leftNum).Contains(rightResolved, StringComparison.OrdinalIgnoreCase),
                VariableCompareOperator.StartsWith => FormatNumber(leftNum).StartsWith(rightResolved, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            leftText = FormatNumber(leftNum);
        }
        else if (entry?.Type == MacroVariableType.Boolean)
        {
            var leftBool = entry.BoolValue;
            var rightBool = ParseBool(rightResolved);
            rawAnswer = comparison.Operator switch
            {
                VariableCompareOperator.Equals => leftBool == rightBool,
                VariableCompareOperator.NotEquals => leftBool != rightBool,
                _ => string.Equals(leftText, rightResolved, StringComparison.OrdinalIgnoreCase),
            };
        }
        else
        {
            var actualText = entry?.TextValue ?? string.Empty;
            leftText = $"\"{actualText}\"";
            rawAnswer = comparison.Operator switch
            {
                VariableCompareOperator.Equals => string.Equals(actualText, rightResolved, StringComparison.OrdinalIgnoreCase),
                VariableCompareOperator.NotEquals => !string.Equals(actualText, rightResolved, StringComparison.OrdinalIgnoreCase),
                VariableCompareOperator.Contains => actualText.Contains(rightResolved, StringComparison.OrdinalIgnoreCase),
                VariableCompareOperator.StartsWith => actualText.StartsWith(rightResolved, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var finalAnswer = negate ? !rawAnswer : rawAnswer;
        details = $"Variable {{{varName}}} = {leftText} (compared {DescribeOperator(comparison.Operator)} \"{rightResolved}\").";
        return finalAnswer;
    }

    public static string DescribeOperator(VariableCompareOperator op) => op switch
    {
        VariableCompareOperator.Equals => "==",
        VariableCompareOperator.NotEquals => "!=",
        VariableCompareOperator.GreaterThan => ">",
        VariableCompareOperator.GreaterOrEqual => ">=",
        VariableCompareOperator.LessThan => "<",
        VariableCompareOperator.LessOrEqual => "<=",
        VariableCompareOperator.Contains => "contains",
        VariableCompareOperator.StartsWith => "starts with",
        _ => op.ToString(),
    };

    public IReadOnlyList<VariableSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotNoLock();
        }
    }

    private IReadOnlyList<VariableSnapshot> BuildSnapshotNoLock() =>
        _entries.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new VariableSnapshot(e.Name, e.Type, e.Format()))
            .ToArray();

    private static void AssignRaw(Entry entry, string raw)
    {
        switch (entry.Type)
        {
            case MacroVariableType.Number:
                entry.NumberValue = ParseNumberOrZero(raw);
                break;
            case MacroVariableType.Boolean:
                entry.BoolValue = ParseBool(raw);
                break;
            default:
                entry.TextValue = raw ?? string.Empty;
                break;
        }
    }

    private static double ParseNumberOrZero(string? text) =>
        TryParseScreenNumber(text, out var n) ? n : 0;

    public static bool ParseBool(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.Equals("true", StringComparison.OrdinalIgnoreCase)
            || t.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || t.Equals("1", StringComparison.OrdinalIgnoreCase)
            || t.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the first signed integer or decimal number from OCR text (e.g. "Gold: 12,450" → 12450, "$1,299.50" → 1299.5).
    /// Also normalizes common OCR digit look-alikes ('O'/'o' → '0', 'l'/'I' → '1' when adjacent to digits).
    /// </summary>
    public static bool TryParseScreenNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var direct)
            && !double.IsNaN(direct) && !double.IsInfinity(direct))
        {
            value = direct;
            return true;
        }

        // Scan for the first numeric run (allowing '-' sign, ',' thousand separators, and '.' decimal point).
        var s = text.Trim();
        var sb = new StringBuilder(16);
        var started = false;
        var seenDot = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!started)
            {
                if (c is >= '0' and <= '9')
                {
                    if (i > 0 && s[i - 1] == '-') sb.Append('-');
                    sb.Append(c);
                    started = true;
                }

                continue;
            }

            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if ((c is 'O' or 'o') && sb.Length > 0)
            {
                sb.Append('0');
            }
            else if (c == ',' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                // Thousand separator: skip comma
            }
            else if (c == '.' && !seenDot && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                seenDot = true;
                sb.Append('.');
            }
            else
            {
                break;
            }
        }

        if (sb.Length == 0) return false;
        if (double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string FormatNumber(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n)) return "0";
        if (Math.Abs(n - Math.Round(n)) < 1e-9 && Math.Abs(n) < 1e15)
            return ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture);
        return n.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
