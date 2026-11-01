using System.Globalization;

namespace Wcc.Core.References;

/// <summary>Immutable values from the original pixel at the current modifier stage.</summary>
public readonly record struct WccImageFormulaPixel(int X, int Y, int Width, int Height,
    byte Red, byte Green, byte Blue, byte Alpha)
{
    public double U => Width > 0 ? (double)X / Width : 0;
    public double V => Height > 0 ? (double)Y / Height : 0;
}

public delegate double WccImagePixelFormula(WccImageFormulaPixel pixel);

/// <summary>
/// Bounded, pure numeric WFL preview subset. No game state, strings, user functions,
/// random operations or code execution. Unsupported input stays available to the
/// authoritative engine comparison; it never falls back to a guessed numeric value.
/// </summary>
public static class WccImageFormulaCompiler
{
    public static bool TryCompile(string formula, out WccImagePixelFormula? compiled, out string? diagnostic)
    {
        compiled = null; diagnostic = null;
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 4096)
        {
            diagnostic = "Enter a numeric pixel formula of at most 4096 characters.";
            return false;
        }
        try
        {
            var parser = new Parser(formula);
            var expression = parser.Parse();
            compiled = pixel => expression.Evaluate(pixel).Value;
            return true;
        }
        catch (FormatException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    // WFL distinguishes integer arithmetic from decimals with three places.
    // Keep this type information through intermediate operations (5 / 2 * 2 = 4).
    private readonly record struct Number(double Value, bool Integer = true)
    {
        public bool Truth => Value != 0;
        public static Number Make(double value, bool integer)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > 9_000_000_000_000_000d)
                throw new ArithmeticException("The local pixel formula produced an out-of-range or non-numeric value.");
            return new(integer ? Math.Truncate(value) : (double)(decimal.Truncate((decimal)value * 1000m) / 1000m), integer);
        }
    }

    private abstract record Node
    {
        public abstract Number Evaluate(WccImageFormulaPixel pixel);
    }
    private sealed record Literal(Number Number) : Node
    {
        public override Number Evaluate(WccImageFormulaPixel pixel) => Number;
    }
    private sealed record Variable(string Name) : Node
    {
        public override Number Evaluate(WccImageFormulaPixel p) => Name switch
        {
            "x" => new(p.X), "y" => new(p.Y), "width" => new(p.Width), "height" => new(p.Height),
            "u" => Number.Make(p.U, false), "v" => Number.Make(p.V, false),
            "red" => new(p.Red), "green" => new(p.Green), "blue" => new(p.Blue), "alpha" => new(p.Alpha),
            _ => throw new ArithmeticException("Unsupported pixel variable.")
        };
    }
    private sealed record Unary(string Operator, Node Child) : Node
    {
        public override Number Evaluate(WccImageFormulaPixel p)
        {
            var value = Child.Evaluate(p);
            return Operator switch
            {
                "+" => value, "-" => Number.Make(-value.Value, value.Integer),
                "not" => new(value.Truth ? 0 : 1), _ => throw new ArithmeticException("Unsupported unary operation.")
            };
        }
    }
    private sealed record Binary(string Operator, Node Left, Node Right) : Node
    {
        public override Number Evaluate(WccImageFormulaPixel p)
        {
            var left = Left.Evaluate(p); var right = Right.Evaluate(p);
            var a = left.Value; var b = right.Value; var integer = left.Integer && right.Integer;
            return Operator switch
            {
                "+" => Number.Make(a + b, integer), "-" => Number.Make(a - b, integer),
                "*" => Number.Make(a * b, integer),
                "/" when b != 0 => Number.Make(a / b, integer),
                "%" when b != 0 => Number.Make(a % b, true),
                "^" => Number.Make(Math.Pow(a, b), integer && b >= 0),
                "=" or "==" => new(a == b ? 1 : 0), "!=" => new(a != b ? 1 : 0),
                "<" => new(a < b ? 1 : 0), "<=" => new(a <= b ? 1 : 0),
                ">" => new(a > b ? 1 : 0), ">=" => new(a >= b ? 1 : 0),
                "and" => new(left.Truth && right.Truth ? 1 : 0),
                "or" => new(left.Truth || right.Truth ? 1 : 0),
                _ => throw new ArithmeticException("The local formula cannot preview division by zero or this operation; compare it in Wesnoth.")
            };
        }
    }
    private sealed record Function(string Name, Node[] Arguments) : Node
    {
        public override Number Evaluate(WccImageFormulaPixel p)
        {
            Number Arg(int index) => Arguments[index].Evaluate(p);
            if (Name == "if")
            {
                for (var i = 0; i < Arguments.Length - 1; i += 2)
                    if (Arg(i).Truth) return Arg(i + 1);
                return Arg(Arguments.Length - 1);
            }
            if (Name == "pi") return Number.Make(Math.PI, false);
            if (Name is "min" or "max" or "sum")
            {
                var value = Arg(0);
                for (var i = 1; i < Arguments.Length; i++)
                {
                    var next = Arg(i);
                    value = Name switch
                    {
                        "min" => value.Value <= next.Value ? value : next,
                        "max" => value.Value >= next.Value ? value : next,
                        _ => Number.Make(value.Value + next.Value, value.Integer && next.Integer)
                    };
                }
                return value;
            }
            var first = Arg(0); var a = first.Value;
            return Name switch
            {
                "abs" => Number.Make(Math.Abs(a), first.Integer),
                "as_decimal" => Number.Make(a, false),
                "ceil" => Number.Make(Math.Ceiling(a), true), "floor" => Number.Make(Math.Floor(a), true),
                "round" => Number.Make(Math.Round(a, MidpointRounding.AwayFromZero), true),
                "sgn" => new(Math.Sign(a)), "frac" => Number.Make(a - Math.Truncate(a), false),
                "sqrt" => Number.Make(Math.Sqrt(a), false), "cbrt" => Number.Make(Math.Cbrt(a), false),
                "sin" => Number.Make(Math.Sin(a * Math.PI / 180d), false),
                "cos" => Number.Make(Math.Cos(a * Math.PI / 180d), false),
                "tan" => Number.Make(Math.Tan(a * Math.PI / 180d), false),
                "asin" => Number.Make(Math.Asin(a) * 180d / Math.PI, false),
                "acos" => Number.Make(Math.Acos(a) * 180d / Math.PI, false),
                "atan" => Number.Make(Math.Atan(a) * 180d / Math.PI, false),
                "exp" => Number.Make(Math.Exp(a), false),
                "log" => Number.Make(Arguments.Length == 1 ? Math.Log(a) : Math.Log(a, Arg(1).Value), false),
                "hypot" => Number.Make(Math.Sqrt(a * a + Math.Pow(Arg(1).Value, 2)), false),
                "root" => Root(first, Arg(1)),
                "clamp" => Clamp(first, Arg(1), Arg(2)),
                "lerp" => Number.Make(a + (Arg(1).Value - a) * Arg(2).Value, false),
                _ => throw new ArithmeticException("Unsupported local pixel formula function.")
            };
        }
        private static Number Root(Number value, Number degree)
        {
            if (degree.Value == 0) throw new ArithmeticException("A root degree cannot be zero.");
            var odd = degree.Integer && Math.Abs(degree.Value % 2) == 1;
            return Number.Make(value.Value < 0 && odd
                ? -Math.Pow(-value.Value, 1 / degree.Value) : Math.Pow(value.Value, 1 / degree.Value), false);
        }
        private static Number Clamp(Number value, Number min, Number max)
        {
            if (min.Value > max.Value) throw new ArithmeticException("The clamp minimum exceeds its maximum.");
            return value.Value < min.Value ? min : value.Value > max.Value ? max : value;
        }
    }

    private sealed class Parser(string text)
    {
        private int _position, _nodes, _depth;
        public Node Parse()
        {
            var result = Expression(); White();
            if (_position != text.Length) throw Error("Unsupported syntax");
            return result;
        }
        private Node Count(Node node)
        {
            if (++_nodes > 256) throw Error("The pixel formula exceeds 256 operations");
            return node;
        }
        private Node Expression()
        {
            if (++_depth > 48) throw Error("The pixel formula is nested too deeply");
            try
            {
                if (Word("not")) return Count(new Unary("not", Expression()));
                return Or();
            }
            finally { _depth--; }
        }
        private Node Or()
        {
            var value = And();
            while (Word("or")) value = Count(new Binary("or", value, And()));
            return value;
        }
        private Node And()
        {
            var value = Comparison();
            while (Word("and")) value = Count(new Binary("and", value, Comparison()));
            return value;
        }
        private Node Comparison()
        {
            var value = Add();
            while (true)
            {
                var op = Take("<=", ">=", "!=", "==", "=", "<", ">");
                if (op is null) return value;
                value = Count(new Binary(op, value, Add()));
            }
        }
        private Node Add()
        {
            var value = Multiply();
            while (true)
            {
                var op = Take("+", "-"); if (op is null) return value;
                value = Count(new Binary(op, value, Multiply()));
            }
        }
        private Node Multiply()
        {
            var value = Modulus();
            while (true)
            {
                var op = Take("*", "/"); if (op is null) return value;
                value = Count(new Binary(op, value, Modulus()));
            }
        }
        private Node Modulus()
        {
            var value = Power();
            while (Take("%") is not null) value = Count(new Binary("%", value, Power()));
            return value;
        }
        private Node Power()
        {
            if (++_depth > 48) throw Error("The pixel formula is nested too deeply");
            try
            {
                var value = Prefix();
                return Take("^") is not null ? Count(new Binary("^", value, Power())) : value;
            }
            finally { _depth--; }
        }
        private Node Prefix()
        {
            if (++_depth > 48) throw Error("The pixel formula is nested too deeply");
            try
            {
                var op = Take("+", "-");
                if (op is not null) return Count(new Unary(op, Prefix()));
                if (Word("not")) return Count(new Unary("not", Expression()));
                if (Take("(") is not null)
                {
                    var value = Expression(); Need(")"); return value;
                }
                White(); var start = _position;
                if (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
                {
                    var dot = false;
                    while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
                    {
                        if (text[_position] == '.' && dot) throw Error("Invalid number");
                        dot |= text[_position++] == '.';
                    }
                    if (!double.TryParse(text.AsSpan(start, _position - start), NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value > 1_000_000_000)
                        throw Error("Invalid or oversized number");
                    return Count(new Literal(Number.Make(value, !dot)));
                }
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '_' or '.')) _position++;
                if (_position == start) throw Error("Expected a numeric value");
                var name = text[start.._position];
                if (Take("(") is not null) return Call(name);
                if (name.StartsWith("self.", StringComparison.Ordinal)) name = name[5..];
                if (name is not ("x" or "y" or "u" or "v" or "width" or "height" or "red" or "green" or "blue" or "alpha"))
                    throw Error("Unsupported pixel variable '" + name + "'");
                return Count(new Variable(name));
            }
            finally { _depth--; }
        }
        private Node Call(string name)
        {
            var arguments = new List<Node>();
            var list = Take("[") is not null;
            if (list && name is not ("min" or "max" or "sum")) throw Error("Lists are supported only by min, max and sum");
            var ending = list ? "]" : ")";
            if (Take(ending) is null)
            {
                do { arguments.Add(Expression()); if (arguments.Count > 32) throw Error("Too many numeric arguments"); }
                while (Take(",") is not null);
                Need(ending);
            }
            if (list) Need(")");
            var count = arguments.Count;
            var valid = name switch
            {
                "if" => count >= 3 && count % 2 == 1,
                "min" or "max" or "sum" => list && count > 0,
                "pi" => count == 0,
                "clamp" or "lerp" => count == 3,
                "hypot" or "root" => count == 2,
                "log" => count is 1 or 2,
                "abs" or "as_decimal" or "ceil" or "floor" or "round" or "sgn" or "frac" or
                "sqrt" or "cbrt" or "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "exp" => count == 1,
                _ => false
            };
            if (!valid) throw Error("Unsupported function or argument count for '" + name + "'");
            return Count(new Function(name, arguments.ToArray()));
        }
        private void Need(string token)
        {
            if (Take(token) is null) throw Error("Expected '" + token + "'");
        }
        private bool Word(string word)
        {
            White();
            if (!text.AsSpan(_position).StartsWith(word, StringComparison.Ordinal)) return false;
            var end = _position + word.Length;
            if (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '.')) return false;
            _position = end; return true;
        }
        private string? Take(params string[] tokens)
        {
            White();
            foreach (var token in tokens)
                if (text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
                { _position += token.Length; return token; }
            return null;
        }
        private void White()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }
        private FormatException Error(string message) => new(message + " at position " + _position.ToString(CultureInfo.InvariantCulture) + ". The local preview supports a bounded numeric WFL subset.");
    }
}
