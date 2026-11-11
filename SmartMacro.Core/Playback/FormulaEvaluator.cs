using System.Globalization;

namespace SmartMacro.Core.Playback;

/// <summary>
/// Safe, zero-Eval recursive-descent mathematical formula evaluator for <see cref="Models.VariableSourceKind.Formula"/>.
/// Supports <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>, unary <c>+/-</c>, parentheses <c>(...)</c>,
/// <c>{var}</c> or bare variable identifiers, and built-in math functions:
/// <c>min</c>, <c>max</c>, <c>abs</c>, <c>round</c>, <c>floor</c>, <c>ceil</c>, <c>clamp</c>, <c>sqrt</c>, <c>pow</c>.
/// Division or modulo by zero safely returns <c>0</c> instead of throwing or producing NaN/Infinity.
/// </summary>
public static class FormulaEvaluator
{
    public const int MaxExpressionLength = 2_000;
    private const int MaxRecursionDepth = 64;

    /// <summary>
    /// Evaluates <paramref name="expression"/> using <paramref name="variables"/> to resolve <c>{var}</c> placeholders
    /// and bare variable identifiers. Returns <c>0</c> if the expression is empty or invalid.
    /// </summary>
    public static double Evaluate(string? expression, VariableStore? variables = null)
    {
        return TryEvaluate(
            expression,
            name => variables is not null && variables.TryGetNumber(name, out var n) ? n : 0.0,
            out var result,
            out _)
            ? result
            : 0.0;
    }

    /// <summary>
    /// Parses and evaluates <paramref name="expression"/>. Returns <c>true</c> when the syntax is valid,
    /// or <c>false</c> with a human-readable <paramref name="error"/> message.
    /// </summary>
    public static bool TryEvaluate(
        string? expression,
        Func<string, double>? variableLookup,
        out double result,
        out string? error)
    {
        result = 0.0;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Enter a math formula (for example: ({gold} * 1.15) + 50).";
            return false;
        }

        if (expression.Length > MaxExpressionLength)
        {
            error = $"The formula can be at most {MaxExpressionLength:N0} characters.";
            return false;
        }

        var parser = new Parser(expression, variableLookup);
        if (!parser.TryParseExpression(0, out var value))
        {
            error = parser.Error ?? "Invalid formula syntax.";
            return false;
        }

        parser.SkipWhitespace();
        if (!parser.IsAtEnd)
        {
            error = $"Unexpected character '{parser.CurrentChar}' at position {parser.Position + 1}.";
            return false;
        }

        result = double.IsFinite(value) ? value : 0.0;
        return true;
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private readonly Func<string, double>? _variableLookup;
        public int Position;
        public string? Error;

        public Parser(string text, Func<string, double>? variableLookup)
        {
            _text = text.AsSpan();
            _variableLookup = variableLookup;
            Position = 0;
            Error = null;
        }

        public readonly bool IsAtEnd => Position >= _text.Length;
        public readonly char CurrentChar => Position < _text.Length ? _text[Position] : '\0';

        public void SkipWhitespace()
        {
            while (Position < _text.Length && char.IsWhiteSpace(_text[Position]))
                Position++;
        }

        public bool TryParseExpression(int depth, out double value)
        {
            value = 0.0;
            if (depth > MaxRecursionDepth)
            {
                Error = "Formula parentheses or functions are nested too deeply.";
                return false;
            }

            if (!TryParseTerm(depth, out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                if (IsAtEnd) break;

                var op = CurrentChar;
                if (op is not ('+' or '-')) break;
                Position++;

                if (!TryParseTerm(depth, out var right))
                    return false;

                value = op == '+' ? value + right : value - right;
                if (!double.IsFinite(value)) value = 0.0;
            }

            return true;
        }

        private bool TryParseTerm(int depth, out double value)
        {
            if (!TryParseUnary(depth, out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                if (IsAtEnd) break;

                var op = CurrentChar;
                if (op is not ('*' or '/' or '%')) break;
                Position++;

                if (!TryParseUnary(depth, out var right))
                    return false;

                value = op switch
                {
                    '*' => value * right,
                    '/' => Math.Abs(right) < 1e-15 ? 0.0 : value / right,
                    '%' => Math.Abs(right) < 1e-15 ? 0.0 : value % right,
                    _ => value,
                };
                if (!double.IsFinite(value)) value = 0.0;
            }

            return true;
        }

        private bool TryParseUnary(int depth, out double value)
        {
            value = 0.0;
            if (depth > MaxRecursionDepth)
            {
                Error = "Formula parentheses or functions are nested too deeply.";
                return false;
            }

            SkipWhitespace();
            if (!IsAtEnd && CurrentChar is '+' or '-')
            {
                var sign = CurrentChar;
                Position++;
                if (!TryParseUnary(depth + 1, out var operand))
                    return false;
                value = sign == '-' ? -operand : operand;
                return true;
            }

            return TryParsePrimary(depth, out value);
        }

        private bool TryParsePrimary(int depth, out double value)
        {
            value = 0.0;
            SkipWhitespace();
            if (IsAtEnd)
            {
                Error = "Unexpected end of formula.";
                return false;
            }

            var c = CurrentChar;

            // Parenthesized sub-expression: ( ... )
            if (c == '(')
            {
                Position++;
                if (!TryParseExpression(depth + 1, out value))
                    return false;
                SkipWhitespace();
                if (IsAtEnd || CurrentChar != ')')
                {
                    Error = "Missing closing parenthesis ')'.";
                    return false;
                }
                Position++;
                return true;
            }

            // Braced variable placeholder: {varName}
            if (c == '{')
            {
                Position++;
                var start = Position;
                while (!IsAtEnd && CurrentChar != '}')
                    Position++;
                if (IsAtEnd || CurrentChar != '}')
                {
                    Error = "Missing closing brace '}' for variable placeholder.";
                    return false;
                }
                var varName = _text[start..Position].Trim().ToString();
                Position++;
                if (varName.Length == 0 || !Models.MacroVariableDefinition.IsValidName(varName))
                {
                    Error = $"Invalid variable placeholder \"{{{varName}}}\".";
                    return false;
                }
                value = _variableLookup?.Invoke(varName) ?? 0.0;
                return true;
            }

            // Numeric literal: digits with optional decimal point and optional scientific exponent (e.g. 1.5e-3)
            if (char.IsAsciiDigit(c) || c == '.')
            {
                var start = Position;
                var hasDot = false;
                var hasExp = false;
                var hasDigit = false;
                while (!IsAtEnd)
                {
                    var ch = CurrentChar;
                    if (char.IsAsciiDigit(ch))
                    {
                        hasDigit = true;
                        Position++;
                    }
                    else if (ch == '.' && !hasDot && !hasExp)
                    {
                        hasDot = true;
                        Position++;
                    }
                    else if ((ch is 'e' or 'E') && hasDigit && !hasExp)
                    {
                        hasExp = true;
                        hasDigit = false; // require at least one exponent digit
                        Position++;
                        if (!IsAtEnd && CurrentChar is '+' or '-')
                            Position++;
                    }
                    else
                    {
                        break;
                    }
                }

                var slice = _text[start..Position];
                if (!hasDigit || !double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    Error = $"Invalid number \"{slice.ToString()}\".";
                    return false;
                }
                return true;
            }

            // Identifier: either a function call func(a, b, ...) or a bare variable identifier
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = Position;
                Position++;
                while (!IsAtEnd && (char.IsAsciiLetterOrDigit(CurrentChar) || CurrentChar == '_'))
                    Position++;

                var ident = _text[start..Position].ToString();
                SkipWhitespace();
                if (!IsAtEnd && CurrentChar == '(')
                {
                    Position++; // consume '('
                    var args = new List<double>(3);
                    SkipWhitespace();
                    if (!IsAtEnd && CurrentChar != ')')
                    {
                        while (true)
                        {
                            if (!TryParseExpression(depth + 1, out var arg))
                                return false;
                            args.Add(arg);
                            SkipWhitespace();
                            if (!IsAtEnd && CurrentChar == ',')
                            {
                                Position++;
                                continue;
                            }
                            break;
                        }
                    }

                    if (IsAtEnd || CurrentChar != ')')
                    {
                        Error = $"Missing closing parenthesis ')' for function \"{ident}\".";
                        return false;
                    }
                    Position++; // consume ')'
                    return TryEvaluateFunction(ident, args, out value, out Error);
                }

                // Bare identifier: constant pi / e / true / false or variable lookup
                if (string.Equals(ident, "pi", StringComparison.OrdinalIgnoreCase))
                {
                    value = Math.PI;
                    return true;
                }
                if (string.Equals(ident, "e", StringComparison.OrdinalIgnoreCase))
                {
                    value = Math.E;
                    return true;
                }
                if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase))
                {
                    value = 1.0;
                    return true;
                }
                if (string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
                {
                    value = 0.0;
                    return true;
                }

                value = _variableLookup?.Invoke(ident) ?? 0.0;
                return true;
            }

            Error = $"Unexpected character '{c}' at position {Position + 1}.";
            return false;
        }

        private static bool TryEvaluateFunction(string name, List<double> args, out double result, out string? error)
        {
            result = 0.0;
            error = null;
            switch (name.ToLowerInvariant())
            {
                case "abs":
                    if (args.Count != 1) { error = "abs(x) takes 1 argument."; return false; }
                    result = Math.Abs(args[0]);
                    return true;

                case "floor":
                    if (args.Count != 1) { error = "floor(x) takes 1 argument."; return false; }
                    result = Math.Floor(args[0]);
                    return true;

                case "ceil":
                case "ceiling":
                    if (args.Count != 1) { error = "ceil(x) takes 1 argument."; return false; }
                    result = Math.Ceiling(args[0]);
                    return true;

                case "sqrt":
                    if (args.Count != 1) { error = "sqrt(x) takes 1 argument."; return false; }
                    result = args[0] <= 0.0 ? 0.0 : Math.Sqrt(args[0]);
                    return true;

                case "round":
                    if (args.Count is < 1 or > 2) { error = "round(x [, decimals]) takes 1 or 2 arguments."; return false; }
                    var decimals = args.Count == 2 ? Math.Clamp((int)Math.Round(args[1]), 0, 6) : 0;
                    result = Math.Round(args[0], decimals, MidpointRounding.AwayFromZero);
                    return true;

                case "min":
                    if (args.Count < 2) { error = "min(a, b, ...) takes at least 2 arguments."; return false; }
                    result = args[0];
                    for (var i = 1; i < args.Count; i++)
                        if (args[i] < result) result = args[i];
                    return true;

                case "max":
                    if (args.Count < 2) { error = "max(a, b, ...) takes at least 2 arguments."; return false; }
                    result = args[0];
                    for (var i = 1; i < args.Count; i++)
                        if (args[i] > result) result = args[i];
                    return true;

                case "clamp":
                    if (args.Count != 3) { error = "clamp(value, min, max) takes 3 arguments."; return false; }
                    var lo = Math.Min(args[1], args[2]);
                    var hi = Math.Max(args[1], args[2]);
                    result = Math.Clamp(args[0], lo, hi);
                    return true;

                case "pow":
                    if (args.Count != 2) { error = "pow(base, exponent) takes 2 arguments."; return false; }
                    var p = Math.Pow(args[0], args[1]);
                    result = double.IsFinite(p) ? p : 0.0;
                    return true;

                default:
                    error = $"Unknown math function \"{name}\". Supported functions: min, max, abs, round, floor, ceil, clamp, sqrt, pow.";
                    return false;
            }
        }
    }
}
