namespace Cotabby.Core.Macros;

/// <summary>
/// Safe arithmetic for macros. Supports <c>+ - * / ^</c>, parentheses, unary sign, decimals
/// and a trailing <c>%</c> meaning percent. <c>x</c>, <c>X</c> and <c>×</c> multiply and
/// <c>÷</c> divides. A hand-written parser that can only ever produce a number.
/// </summary>
/// <remarks>
/// The preview shows <c>= 10</c> and accepting inserts only the result. A bare number with
/// no operator is not a result, so the macro stays out of the way of ordinary typing.
/// </remarks>
public sealed class ArithmeticEvaluator : IMacroEvaluator
{
    public MacroResult? Evaluate(string query)
    {
        // A trailing = is the user's cue to compute now.
        var literal = query.EndsWith('=') ? query[..^1] : query;
        if (literal.Length == 0) return null;

        var normalized = literal.Replace('x', '*').Replace('X', '*').Replace('×', '*').Replace('÷', '/');
        var parser = new Parser(normalized);
        if (parser.Parse() is not { } value || !parser.UsedOperator) return null;
        if (Format(value) is not { } text) return null;

        return new MacroResult($"= {text}", text);
    }

    /// <summary>
    /// Whole values print without a decimal point. Everything else prints with up to 10
    /// significant digits and trailing zeros trimmed. Non-finite values return null.
    /// </summary>
    public static string? Format(double value) =>
        double.IsFinite(value) ? MacroNumber.Format(value, integerLimit: 1e15, significantDigits: 10) : null;

    /// <summary>
    /// Recursive descent over the operator grammar. <see cref="UsedOperator"/> tells an
    /// expression apart from a lone or merely signed number.
    /// </summary>
    private sealed class Parser(string text)
    {
        private readonly char[] _characters = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        private int _index;
        private bool _valid = true;

        public bool UsedOperator { get; private set; }

        public double? Parse()
        {
            double value = ParseExpression();
            return _valid && _index == _characters.Length ? value : null;
        }

        private double ParseExpression()
        {
            double value = ParseTerm();
            while (Peek() is '+' or '-')
            {
                char op = Advance();
                double rhs = ParseTerm();
                value = op == '+' ? value + rhs : value - rhs;
            }
            return value;
        }

        private double ParseTerm()
        {
            double value = ParsePower();
            while (Peek() is '*' or '/')
            {
                char op = Advance();
                double rhs = ParsePower();
                if (op == '*')
                {
                    value *= rhs;
                }
                else if (rhs == 0)
                {
                    _valid = false;
                    return 0;
                }
                else
                {
                    value /= rhs;
                }
            }
            return value;
        }

        private double ParsePower()
        {
            double value = ParseUnary();
            if (Peek() != '^') return value;
            Advance();
            return Math.Pow(value, ParsePower()); // right associative
        }

        private double ParseUnary()
        {
            // A sign is not an operator, so a lone signed number stays a non-expression.
            if (Peek() is '+' or '-')
            {
                bool negative = _characters[_index++] == '-';
                double operand = ParsePostfix();
                return negative ? -operand : operand;
            }
            return ParsePostfix();
        }

        private double ParsePostfix()
        {
            double value = ParsePrimary();
            while (Peek() == '%')
            {
                Advance();
                value /= 100;
            }
            return value;
        }

        private double ParsePrimary()
        {
            if (Peek() != '(') return ParseNumber();
            _index++;
            double value = ParseExpression();
            if (Peek() == ')')
            {
                _index++;
            }
            else
            {
                _valid = false;
            }
            return value;
        }

        private double ParseNumber()
        {
            int start = _index;
            while (Peek() is { } c && (char.IsNumber(c) || c == '.'))
            {
                _index++;
            }
            if (MacroNumber.ParseDouble(_characters.AsSpan(start, _index - start)) is { } value) return value;
            _valid = false;
            return 0;
        }

        private char? Peek() => _index < _characters.Length ? _characters[_index] : null;

        // Consumes an operator and records that the input is an expression.
        private char Advance()
        {
            UsedOperator = true;
            return _characters[_index++];
        }
    }
}
