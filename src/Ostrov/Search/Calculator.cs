using System.Globalization;

namespace Ostrov.Search;

/// <summary>
/// Мини-калькулятор для быстрого вопроса (Alt+Q): если в строке чистая арифметика, ответ считается сразу, без запроса к ИИ.
/// Умеет: + − × ÷ (знаки * / x х × ÷ ·), скобки, степень (^ или **), запятая и точка как десятичный разделитель,
/// проценты («15% от 200», «200 + 15%», «200 − 15%», «200 * 15%»), корень и функции (sqrt/корень/√, sin, cos, tan/tg, ln, log, abs),
/// число π (pi, π), неявное умножение («2(3+4)», «2π»), хвостовой «=». Всё остальное (слова, пустые и недописанные
/// примеры, деление на ноль) — не пример, <see cref="TryEvaluate"/> вернёт false, и строка пойдёт к ИИ как обычно.
/// Просто число («42», «-5», «(7)») примером не считается.
/// </summary>
internal static class Calculator
{
    /// <summary>Углы тригонометрии в градусах (как в калькуляторе Windows) или в радианах; переключается в настройках.</summary>
    public static bool Degrees { get; set; } = true;

    private const int MaxLength = 200;
    private const int MaxDepth = 60;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryEvaluate(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return false;
        var s = Normalize(text);
        if (!Tokenize(s, out var tokens) || tokens.Count == 0) return false;
        try
        {
            var parser = new Parser(tokens);
            var result = parser.Run();
            if (!parser.Meaningful || !double.IsFinite(result)) return false;
            value = result == 0 ? 0 : result; // без «−0»
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Число для буфера обмена: до 12 значащих цифр, десятичная запятая, без разделителей разрядов («1234,5»).</summary>
    public static string FormatPlain(double v)
    {
        if (v == 0) return "0";
        var abs = Math.Abs(v);
        string s;
        if (abs >= 1e15 || abs < 1e-6)
        {
            s = v.ToString("G12", Inv); // порядок через E: «1.23E+20»
        }
        else
        {
            var magnitude = (int)Math.Floor(Math.Log10(abs));
            var decimals = Math.Clamp(11 - magnitude, 0, 15);
            s = Math.Round(v, decimals).ToString("0.###############", Inv);
        }
        return s.Replace('.', ',');
    }

    /// <summary>Число для показа: как <see cref="FormatPlain"/>, но целая часть от пяти цифр разбита неразрывными пробелами («12 345,5»).</summary>
    public static string FormatDisplay(double v)
    {
        var plain = FormatPlain(v);
        if (plain.Contains('E')) return plain;
        var comma = plain.IndexOf(',');
        var intPart = comma < 0 ? plain : plain[..comma];
        var rest = comma < 0 ? "" : plain[comma..];
        var negative = intPart.StartsWith('-');
        var digits = negative ? intPart[1..] : intPart;
        if (digits.Length < 5) return plain;
        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) grouped.Append(' ');
            grouped.Append(digits[i]);
        }
        return (negative ? "-" : "") + grouped + rest;
    }

    // ───── Разбор ─────

    private enum K { Num, Plus, Minus, Star, Slash, Caret, LParen, RParen, Percent, Of, Func, Const }

    private readonly record struct Tok(K Kind, double Num = 0, string Name = "");

    private static string Normalize(string text)
    {
        var s = text.Trim()
            .Replace('−', '-').Replace('–', '-').Replace('—', '-') // минус, короткое и длинное тире
            .Replace('×', '*').Replace('·', '*').Replace('⋅', '*').Replace('∙', '*') // × · ⋅ ∙
            .Replace('÷', '/')                                                                      // ÷
            .Replace("**", "^")
            .Replace(' ', ' ');
        while (s.EndsWith('=')) s = s[..^1].TrimEnd(); // «2+2=»
        return s;
    }

    private static readonly Dictionary<string, string> Functions = new()
    {
        ["sqrt"] = "sqrt", ["корень"] = "sqrt", ["sin"] = "sin", ["cos"] = "cos",
        ["tan"] = "tan", ["tg"] = "tan", ["ln"] = "ln", ["log"] = "log", ["lg"] = "log", ["abs"] = "abs"
    };

    private static bool Tokenize(string s, out List<Tok> tokens)
    {
        tokens = [];
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                var start = i;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                if (i + 1 < s.Length && (s[i] == '.' || s[i] == ',') && char.IsAsciiDigit(s[i + 1]))
                {
                    i++;
                    while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                }
                if (!double.TryParse(s[start..i].Replace(',', '.'), NumberStyles.Float, Inv, out var n)) return false;
                tokens.Add(new Tok(K.Num, n));
                continue;
            }

            switch (c)
            {
                case '+': tokens.Add(new Tok(K.Plus)); i++; continue;
                case '-': tokens.Add(new Tok(K.Minus)); i++; continue;
                case '*': tokens.Add(new Tok(K.Star)); i++; continue;
                case '/': tokens.Add(new Tok(K.Slash)); i++; continue;
                case '^': tokens.Add(new Tok(K.Caret)); i++; continue;
                case '(': tokens.Add(new Tok(K.LParen)); i++; continue;
                case ')': tokens.Add(new Tok(K.RParen)); i++; continue;
                case '%': tokens.Add(new Tok(K.Percent)); i++; continue;
                case '√': tokens.Add(new Tok(K.Func, Name: "sqrt")); i++; continue; // √
            }

            if (!char.IsLetter(c)) return false;
            var wordStart = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var word = s[wordStart..i].ToLowerInvariant();

            // Одинокая «x» / «х» между числами — знак умножения («2x3», «2 x (3+4)»).
            if (word is "x" or "х")
            {
                var prevOk = tokens.Count > 0 && tokens[^1].Kind is K.Num or K.RParen or K.Percent or K.Const;
                var j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                var nextOk = j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] is '(' or '.');
                if (!prevOk || !nextOk) return false;
                tokens.Add(new Tok(K.Star));
                continue;
            }

            if (word is "от" or "of") tokens.Add(new Tok(K.Of));       // «от»
            else if (word is "pi" or "π") tokens.Add(new Tok(K.Const));      // π
            else if (Functions.TryGetValue(word, out var fn)) tokens.Add(new Tok(K.Func, Name: fn));
            else return false; // любое другое слово — это не пример
        }
        return true;
    }

    private sealed class Parser(List<Tok> tokens)
    {
        private int _pos, _depth;

        /// <summary>Было ли в записи хоть одно действие (операция, функция, процент, π): просто число примером не считается.</summary>
        public bool Meaningful { get; private set; }

        private static FormatException Fail() => new();

        private Tok? Peek => _pos < tokens.Count ? tokens[_pos] : null;

        private Tok Next() => _pos < tokens.Count ? tokens[_pos++] : throw Fail();

        private void Expect(K kind)
        {
            if (Next().Kind != kind) throw Fail();
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Fail();
        }

        public double Run()
        {
            var v = Expr();
            if (_pos != tokens.Count) throw Fail();
            return v;
        }

        // expr := term (('+' | '-') term)*. Процент справа от + и − считается от левой части: 200 + 15% = 230.
        private double Expr()
        {
            var (left, _) = Term();
            while (Peek is { Kind: K.Plus or K.Minus } op)
            {
                _pos++;
                Meaningful = true;
                var (right, isPercent) = Term();
                if (isPercent) right *= left;
                left = op.Kind == K.Plus ? left + right : left - right;
            }
            return left;
        }

        // term := unary (('*' | '/' | «от» | неявное умножение) unary)*
        private (double Value, bool IsPercent) Term()
        {
            var (left, isPercent) = Unary();
            while (true)
            {
                switch (Peek)
                {
                    case { Kind: K.Star or K.Slash } op:
                    {
                        _pos++;
                        Meaningful = true;
                        var (r, _) = Unary();
                        left = op.Kind == K.Star ? left * r : left / r;
                        isPercent = false;
                        continue;
                    }
                    case { Kind: K.Of }:
                    {
                        if (!isPercent) throw Fail(); // «от» только после процента: «15% от 200»
                        _pos++;
                        Meaningful = true;
                        var (r, _) = Unary();
                        left *= r;
                        isPercent = false;
                        continue;
                    }
                    case { Kind: K.LParen or K.Func or K.Const }:
                    {
                        Meaningful = true; // неявное умножение: 2(3+4), 2π, 2 sqrt 9
                        var (r, _) = Unary();
                        left *= r;
                        isPercent = false;
                        continue;
                    }
                }
                return (left, isPercent);
            }
        }

        private (double Value, bool IsPercent) Unary()
        {
            switch (Peek)
            {
                case { Kind: K.Plus }:
                    _pos++;
                    return Unary();
                case { Kind: K.Minus }:
                {
                    _pos++;
                    Enter();
                    var (v, p) = Unary();
                    _depth--;
                    return (-v, p);
                }
                default:
                    return Power();
            }
        }

        // power := postfix ('^' unary)?  — правоассоциативная: 2^3^2 = 2^9; «-2^2» = −4 (минус стоит выше).
        private (double Value, bool IsPercent) Power()
        {
            var (b, isPercent) = Postfix();
            if (Peek is { Kind: K.Caret })
            {
                _pos++;
                Meaningful = true;
                Enter();
                var (e, _) = Unary();
                _depth--;
                return (Math.Pow(b, e), false);
            }
            return (b, isPercent);
        }

        private (double Value, bool IsPercent) Postfix()
        {
            var v = Primary();
            var isPercent = false;
            while (Peek is { Kind: K.Percent })
            {
                _pos++;
                Meaningful = true;
                v /= 100;
                isPercent = true;
            }
            return (v, isPercent);
        }

        private double Primary()
        {
            var tok = Next();
            switch (tok.Kind)
            {
                case K.Num:
                    return tok.Num;
                case K.Const:
                    Meaningful = true;
                    return Math.PI;
                case K.LParen:
                {
                    Enter();
                    var v = Expr();
                    Expect(K.RParen);
                    _depth--;
                    return v;
                }
                case K.Func:
                {
                    Meaningful = true;
                    Enter();
                    double arg;
                    if (Peek is { Kind: K.LParen })
                    {
                        _pos++;
                        arg = Expr();
                        Expect(K.RParen);
                    }
                    else
                    {
                        arg = Unary().Value; // «sqrt 16», «√16», «sin 30»
                    }
                    _depth--;
                    return Apply(tok.Name, arg);
                }
                default:
                    throw Fail();
            }
        }

        private static double Apply(string name, double x) => name switch
        {
            "sqrt" => Math.Sqrt(x),
            "sin" => Snap(Math.Sin(Rad(x))),
            "cos" => Snap(Math.Cos(Rad(x))),
            "tan" => Tan(x),
            "ln" => Math.Log(x),
            "log" => Math.Log10(x),
            "abs" => Math.Abs(x),
            _ => throw Fail()
        };

        private static double Rad(double x) => Degrees ? x * Math.PI / 180 : x;

        // Нулевые «хвосты» вроде sin(180°) = 1,2E-16 считаем нулём.
        private static double Snap(double r) => Math.Abs(r) < 1e-12 ? 0 : r;

        private static double Tan(double x)
        {
            var c = Snap(Math.Cos(Rad(x)));
            return c == 0 ? double.NaN : Snap(Math.Sin(Rad(x))) / c; // tan(90°) не определён
        }
    }
}
