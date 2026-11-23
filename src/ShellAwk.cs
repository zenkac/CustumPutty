using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

/// <summary>
/// A compact awk interpreter for the practice shell: patterns, BEGIN/END, fields, variables,
/// associative arrays, if/while/for, print/printf and the common string functions.
/// </summary>
internal sealed class AwkProgram
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal readonly struct Val
    {
        private readonly double number; private readonly string? text; private readonly byte kind; // 0 number, 1 string, 2 input (strnum)
        private Val(double number, string? text, byte kind) { this.number = number; this.text = text; this.kind = kind; }
        public static Val Num(double value) => new(value, null, 0);
        public static Val Str(string value) => new(0, value, 1);
        public static Val Input(string value) => new(0, value, 2);
        public static readonly Val Empty = new(0, "", 2);
        public double Number => kind == 0 ? number : Parse(text!);
        public string Text => kind == 0 ? Format(number) : text!;
        public bool Numeric => kind == 0 || kind == 2 && IsNumber(text!);
        public bool Truthy => kind == 0 ? number != 0 : kind == 2 && IsNumber(text!) ? Parse(text!) != 0 : text!.Length > 0;
    }

    private static readonly Regex NumberPrefix = new(@"^\s*[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)", RegexOptions.Compiled);
    private static double Parse(string text) { var m = NumberPrefix.Match(text); return m.Success && double.TryParse(m.Value, NumberStyles.Float, C, out double v) ? v : 0; }
    private static bool IsNumber(string text) => text.Trim().Length > 0 && double.TryParse(text.Trim(), NumberStyles.Float, C, out _);
    private static string Format(double value) => value == Math.Floor(value) && Math.Abs(value) < 1e16 ? ((long)value).ToString(C) : value.ToString("G6", C);

    private sealed class Next : Exception;
    private sealed class Stop(int code) : Exception { public int Code { get; } = code; }

    // ───────── Runtime state ─────────
    private readonly Dictionary<string, Val> variables = [];
    private readonly Dictionary<string, Dictionary<string, Val>> arrays = [];
    private List<string> fields = [];
    private string record = "";
    private long nr, fnr;
    private string fs = " ", ofs = " ", ors = "\n", filename = "";
    private StringBuilder output = new();
    private long steps;
    private readonly List<(int Kind, Func<bool>? Pattern, Action? Body)> rules = [];

    public void SetVariable(string name, string value) => SetVar(name, Val.Input(ShellEngineUnescape(value)));
    private static string ShellEngineUnescape(string value) => value.Replace("\\t", "\t").Replace("\\n", "\n");

    public void Run(List<(string Name, string Text)> inputs, string? separator, StringBuilder sink)
    {
        output = sink; if (separator != null) fs = separator;
        try
        {
            foreach (var rule in rules.Where(r => r.Kind == 0)) rule.Body?.Invoke();
            if (rules.Any(r => r.Kind != 0))
                foreach (var (name, text) in inputs)
                {
                    filename = name == "-" ? "" : name; fnr = 0;
                    var lines = text.Split('\n').ToList(); if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                    foreach (var line in lines)
                    {
                        nr++; fnr++; SetRecord(line);
                        try
                        {
                            foreach (var rule in rules.Where(r => r.Kind == 1))
                            {
                                if (rule.Pattern != null && !rule.Pattern()) continue;
                                if (rule.Body == null) Emit(record + ors); else rule.Body();
                            }
                        }
                        catch (Next) { }
                    }
                }
        }
        catch (Stop) { }
        try { foreach (var rule in rules.Where(r => r.Kind == 2)) rule.Body?.Invoke(); }
        catch (Stop) { }
    }

    private void Emit(string text) { output.Append(text); if (output.Length > 2_000_000) throw new FormatException("output too large"); }
    private void Tick() { if (++steps > 2_000_000) throw new FormatException("실행이 너무 오래 걸려 멈췄습니다 (무한 반복?)"); }

    private void SetRecord(string line) { record = line; fields = Split(line, fs); }
    private static List<string> Split(string line, string separator)
    {
        if (separator == " ") return line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (line.Length == 0) return [];
        if (separator.Length == 1 && separator != "\\") return line.Split(separator[0]).ToList();
        return Regex.Split(line, ShellEngine.PosixToNet(separator, true)).ToList();
    }
    private Val GetField(int index)
    {
        if (index < 0) throw new FormatException($"trying to access out of range field {index}");
        if (index == 0) return Val.Input(record);
        return index <= fields.Count ? Val.Input(fields[index - 1]) : Val.Empty;
    }
    private void SetField(int index, Val value)
    {
        if (index == 0) { SetRecord(value.Text); return; }
        while (fields.Count < index) fields.Add("");
        fields[index - 1] = value.Text; record = string.Join(ofs, fields);
    }
    private Val GetVar(string name) => name switch
    {
        "NR" => Val.Num(nr), "FNR" => Val.Num(fnr), "NF" => Val.Num(fields.Count), "FS" => Val.Str(fs), "OFS" => Val.Str(ofs), "ORS" => Val.Str(ors), "FILENAME" => Val.Str(filename), "SUBSEP" => Val.Str("\u001c"),
        _ => variables.TryGetValue(name, out var v) ? v : Val.Empty
    };
    private void SetVar(string name, Val value)
    {
        switch (name)
        {
            case "NF": { int n = (int)value.Number; while (fields.Count < n) fields.Add(""); if (fields.Count > n) fields.RemoveRange(n, fields.Count - n); record = string.Join(ofs, fields); return; }
            case "FS": fs = value.Text; return;
            case "OFS": ofs = value.Text; return;
            case "ORS": ors = value.Text; return;
            case "NR": nr = (long)value.Number; return;
        }
        variables[name] = value;
    }
    private Dictionary<string, Val> Array(string name) { if (!arrays.TryGetValue(name, out var a)) arrays[name] = a = []; return a; }

    // ───────── Lexer ─────────
    private enum K { Number, String, Regex, Name, Func, Op, Newline, End }
    private readonly record struct Tok(K Kind, string Text);
    private static readonly HashSet<string> Builtins = ["length", "substr", "index", "split", "toupper", "tolower", "int", "sprintf", "gsub", "sub", "sqrt", "match", "rand", "srand", "exp", "log", "sin", "cos", "atan2", "system", "close"];
    private static readonly HashSet<string> Keywords = ["BEGIN", "END", "if", "else", "while", "for", "in", "print", "printf", "next", "exit", "delete", "do", "getline", "function", "return", "break", "continue"];

    private List<Tok> tokens = [];
    private int pos;

    private static List<Tok> Lex(string s)
    {
        var list = new List<Tok>(); int i = 0;
        bool OperandNext() => list.Count == 0 || list[^1].Kind is K.Op or K.Newline && !(list[^1].Text is ")" or "]") || list[^1].Kind == K.Name && Keywords.Contains(list[^1].Text) && list[^1].Text is not ("getline");
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n') { i += 2; continue; }
            if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '\n') { list.Add(new(K.Newline, "\n")); i++; continue; }
            if (char.IsAsciiDigit(c) || c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
            {
                var m = Regex.Match(s[i..], @"^(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)"); list.Add(new(K.Number, m.Value)); i += m.Length; continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { char d = s[++i]; sb.Append(d switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', '/' => '/', _ => d }); i++; continue; }
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new FormatException("non-terminated string");
                i++; list.Add(new(K.String, sb.ToString())); continue;
            }
            if (c == '/' && OperandNext())
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '/') { if (s[i] == '\\' && i + 1 < s.Length) { if (s[i + 1] == '/') { sb.Append('/'); i += 2; continue; } sb.Append(s[i]).Append(s[i + 1]); i += 2; continue; } sb.Append(s[i++]); }
                if (i >= s.Length) throw new FormatException("non-terminated regular expression");
                i++; list.Add(new(K.Regex, sb.ToString())); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                string word = s[i..j]; i = j;
                list.Add(new(Builtins.Contains(word) ? K.Func : K.Name, word)); continue;
            }
            string[] ops = ["**=", "&&", "||", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "%=", "^=", "++", "--", "!~", "**", ">>"];
            string? op = ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
            if (op != null) { list.Add(new(K.Op, op == "**" ? "^" : op == "**=" ? "^=" : op)); i += op.Length; continue; }
            if ("{}()[];,+-*/%^!<>=~?:$|".Contains(c)) { list.Add(new(K.Op, c.ToString())); i++; continue; }
            throw new FormatException($"unexpected character '{c}'");
        }
        list.Add(new(K.End, ""));
        return list;
    }

    // ───────── Parser ─────────
    public static AwkProgram Compile(string source)
    {
        var program = new AwkProgram { tokens = Lex(source) };
        program.ParseProgram();
        return program;
    }

    private Tok Peek(int offset = 0) => tokens[Math.Min(pos + offset, tokens.Count - 1)];
    private bool IsOp(string text) => Peek().Kind == K.Op && Peek().Text == text;
    private bool IsWord(string text) => Peek().Kind == K.Name && Peek().Text == text;
    private bool Accept(string text) { if (IsOp(text) || IsWord(text)) { pos++; return true; } return false; }
    private void Expect(string text) { if (!Accept(text)) throw new FormatException($"'{text}' 가 필요합니다 (위치: {(Peek().Kind == K.End ? "끝" : Peek().Text)})"); }
    private void Newlines() { while (Peek().Kind == K.Newline) pos++; }
    private void Terminators() { while (Peek().Kind == K.Newline || IsOp(";")) pos++; }

    private void ParseProgram()
    {
        Terminators();
        while (Peek().Kind != K.End)
        {
            if (Accept("BEGIN")) { Newlines(); rules.Add((0, null, Block())); }
            else if (Accept("END")) { Newlines(); rules.Add((2, null, Block())); }
            else if (IsWord("function")) throw new FormatException("function 정의는 연습 터미널에서 지원하지 않습니다");
            else
            {
                Func<bool>? pattern = null;
                if (!IsOp("{")) { var expr = Expr(); pattern = () => expr().Truthy; }
                Action? body = IsOp("{") ? Block() : null;
                rules.Add((1, pattern, body));
            }
            Terminators();
        }
    }

    private Action Block()
    {
        Expect("{"); var list = new List<Action>(); Terminators();
        while (!IsOp("}")) { if (Peek().Kind == K.End) throw new FormatException("'}' 가 없습니다"); list.Add(Statement()); Terminators(); }
        pos++;
        var array = list.ToArray(); return () => { foreach (var a in array) a(); };
    }

    private Action Simple(Action action) { if (!(IsOp(";") || IsOp("}") || Peek().Kind is K.Newline or K.End)) throw new FormatException($"문장이 끝나야 하는 곳에 '{Peek().Text}' 가 있습니다"); return action; }

    private Action Statement()
    {
        if (IsOp("{")) return Block();
        if (IsOp(";")) { pos++; return () => { }; }
        if (Accept("if"))
        {
            Expect("("); var cond = Expr(); Expect(")"); Newlines(); var then = Statement();
            int save = pos; Terminators();
            if (Accept("else")) { Newlines(); var other = Statement(); return () => { if (cond().Truthy) then(); else other(); }; }
            pos = save; return () => { if (cond().Truthy) then(); };
        }
        if (Accept("while"))
        {
            Expect("("); var cond = Expr(); Expect(")"); Newlines(); var body = Statement();
            return () => { while (cond().Truthy) { Tick(); body(); } };
        }
        if (Accept("for"))
        {
            Expect("(");
            if (Peek().Kind == K.Name && Peek(1).Kind == K.Name && Peek(1).Text == "in")
            {
                string name = Peek().Text; pos += 2; string arrayName = Peek().Text; pos++; Expect(")"); Newlines(); var body = Statement();
                return () => { foreach (var key in Array(arrayName).Keys.ToList()) { Tick(); SetVar(name, Val.Input(key)); body(); } };
            }
            Func<Val>? init = IsOp(";") ? null : Expr(); Expect(";"); Func<Val>? cond = IsOp(";") ? null : Expr(); Expect(";"); Func<Val>? step = IsOp(")") ? null : Expr(); Expect(")"); Newlines();
            var loop = Statement();
            return () => { init?.Invoke(); while (cond == null || cond().Truthy) { Tick(); loop(); step?.Invoke(); } };
        }
        if (Accept("next")) return Simple(() => throw new Next());
        if (Accept("exit")) { Func<Val>? code = IsOp(";") || IsOp("}") || Peek().Kind is K.Newline or K.End ? null : Expr(); return Simple(() => throw new Stop((int)(code?.Invoke().Number ?? 0))); }
        if (Accept("delete"))
        {
            string name = Peek().Text; pos++;
            if (Accept("[")) { var key = Subscript(); Expect("]"); return Simple(() => Array(name).Remove(key().Text)); }
            return Simple(() => Array(name).Clear());
        }
        if (Accept("print"))
        {
            var items = PrintList();
            return Simple(() => Emit((items.Count == 0 ? record : string.Join(ofs, items.Select(i => i().Text))) + ors));
        }
        if (Accept("printf"))
        {
            var items = PrintList(); if (items.Count == 0) throw new FormatException("printf 에 형식이 필요합니다");
            return Simple(() => Emit(ShellEngine.Format(items[0]().Text, items.Skip(1).Select(i => i().Text).ToList(), false)));
        }
        if (IsWord("getline")) throw new FormatException("getline 은 연습 터미널에서 지원하지 않습니다");
        var expr = Expr(); return Simple(() => expr());
    }

    private bool noGreater;
    private List<Func<Val>> PrintList()
    {
        var items = new List<Func<Val>>();
        if (IsOp(";") || IsOp("}") || Peek().Kind is K.Newline or K.End) return items;
        bool paren = IsOp("(") && ParenIsWholeList();
        if (paren) pos++;
        noGreater = !paren;
        try
        {
            items.Add(Expr()); while (Accept(",")) { Newlines(); items.Add(Expr()); }
        }
        finally { noGreater = false; }
        if (paren) Expect(")");
        if (IsOp(">") || IsOp(">>") || IsOp("|")) throw new FormatException("print 결과를 파일로 보내기(>)는 연습 터미널의 awk 에서 지원하지 않습니다 · 셸에서 awk ... > 파일 로 쓰세요");
        return items;
    }

    private bool ParenIsWholeList()
    {
        int depth = 0;
        for (int i = pos; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == K.Op && t.Text == "(") depth++;
            else if (t.Kind == K.Op && t.Text == ")") { depth--; if (depth == 0) { var next = tokens[Math.Min(i + 1, tokens.Count - 1)]; return next.Kind is K.Newline or K.End || next.Kind == K.Op && next.Text is ";" or "}" or ">" or ">>" or "|"; } }
        }
        return false;
    }

    private Func<Val> Subscript()
    {
        var parts = new List<Func<Val>> { Expr() }; while (Accept(",")) parts.Add(Expr());
        if (parts.Count == 1) return parts[0];
        return () => Val.Str(string.Join("\u001c", parts.Select(p => p().Text)));
    }

    private sealed record LValue(Func<Val> Get, Action<Val> Set);

    private LValue? TryLValue()
    {
        if (Peek().Kind == K.Name && !Keywords.Contains(Peek().Text))
        {
            string name = Peek().Text; pos++;
            if (Accept("["))
            {
                var key = Subscript(); Expect("]");
                return new(() => { var array = Array(name); string k = key().Text; return array.TryGetValue(k, out var v) ? v : array[k] = Val.Empty; }, v => Array(name)[key().Text] = v);
            }
            return new(() => GetVar(name), v => SetVar(name, v));
        }
        if (IsOp("$"))
        {
            pos++; var index = Primary();
            return new(() => GetField((int)index().Number), v => SetField((int)index().Number, v));
        }
        return null;
    }

    private Func<Val> Expr()
    {
        int save = pos; var target = TryLValue();
        if (target != null && Peek().Kind == K.Op && Peek().Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=")
        {
            string op = Peek().Text; pos++; Newlines(); var value = Expr();
            return () =>
            {
                Val result = op == "=" ? Copy(value()) : Val.Num(Arith(op[0], target.Get().Number, value().Number));
                target.Set(result); return result;
            };
        }
        pos = save; return Ternary();
    }

    private static Val Copy(Val value) => value;

    private static double Arith(char op, double a, double b) => op switch
    {
        '+' => a + b, '-' => a - b, '*' => a * b,
        '/' => b == 0 ? throw new FormatException("division by zero") : a / b,
        '%' => b == 0 ? throw new FormatException("division by zero in %") : a % b,
        _ => Math.Pow(a, b)
    };

    private Func<Val> Ternary()
    {
        var cond = Or();
        if (Accept("?")) { Newlines(); var a = Ternary(); Newlines(); Expect(":"); Newlines(); var b = Ternary(); return () => cond().Truthy ? a() : b(); }
        return cond;
    }

    private Func<Val> Or()
    {
        var left = And();
        while (Accept("||")) { Newlines(); var l = left; var r = And(); left = () => Val.Num(l().Truthy || r().Truthy ? 1 : 0); }
        return left;
    }

    private Func<Val> And()
    {
        var left = In();
        while (Accept("&&")) { Newlines(); var l = left; var r = In(); left = () => Val.Num(l().Truthy && r().Truthy ? 1 : 0); }
        return left;
    }

    private Func<Val> In()
    {
        var left = MatchOp();
        while (IsWord("in")) { pos++; string name = Peek().Text; pos++; var l = left; left = () => Val.Num(Array(name).ContainsKey(l().Text) ? 1 : 0); }
        return left;
    }

    private Func<Val> MatchOp()
    {
        var left = Compare();
        while (IsOp("~") || IsOp("!~"))
        {
            bool negate = Peek().Text == "!~"; pos++;
            var l = left; Func<Regex> regex = RegexOperand();
            left = () => Val.Num(regex().IsMatch(l().Text) != negate ? 1 : 0);
        }
        return left;
    }

    private Func<Regex> RegexOperand()
    {
        if (Peek().Kind == K.Regex) { var r = MakeRegex(Peek().Text); pos++; return () => r; }
        var dynamicPattern = Compare(); return () => MakeRegex(dynamicPattern().Text);
    }

    private static Regex MakeRegex(string pattern)
    {
        try { return new Regex(ShellEngine.PosixToNet(pattern, true), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300)); }
        catch (ArgumentException) { throw new FormatException($"정규식이 올바르지 않습니다: /{pattern}/"); }
    }

    private Func<Val> Compare()
    {
        var left = Concat();
        if (Peek().Kind == K.Op && Peek().Text is "<" or "<=" or "==" or "!=" or ">=" || Peek().Kind == K.Op && Peek().Text == ">" && !noGreater)
        {
            string op = Peek().Text; pos++; var l = left; var r = Concat();
            return () =>
            {
                Val a = l(), b = r();
                int cmp = a.Numeric && b.Numeric ? a.Number.CompareTo(b.Number) : string.CompareOrdinal(a.Text, b.Text);
                bool result = op switch { "<" => cmp < 0, "<=" => cmp <= 0, "==" => cmp == 0, "!=" => cmp != 0, ">=" => cmp >= 0, _ => cmp > 0 };
                return Val.Num(result ? 1 : 0);
            };
        }
        return left;
    }

    private bool StartsOperand() => Peek().Kind is K.Number or K.String or K.Func || Peek().Kind == K.Name && !Keywords.Contains(Peek().Text) || IsOp("$") || IsOp("(") || IsOp("!") && !(Peek(1).Kind == K.Op && Peek(1).Text is "=" or "~") || IsOp("++") || IsOp("--") || IsOp("-") && false;

    private Func<Val> Concat()
    {
        var left = Additive();
        while (StartsOperand())
        {
            var l = left; var r = Additive(); left = () => Val.Str(l().Text + r().Text);
        }
        return left;
    }

    private Func<Val> Additive()
    {
        var left = Multiplicative();
        while (IsOp("+") || IsOp("-")) { char op = Peek().Text[0]; pos++; var l = left; var r = Multiplicative(); left = () => Val.Num(Arith(op, l().Number, r().Number)); }
        return left;
    }

    private Func<Val> Multiplicative()
    {
        var left = Unary();
        while (IsOp("*") || IsOp("/") || IsOp("%")) { char op = Peek().Text[0]; pos++; var l = left; var r = Unary(); left = () => Val.Num(Arith(op, l().Number, r().Number)); }
        return left;
    }

    private Func<Val> Unary()
    {
        if (Accept("!")) { var inner = Unary(); return () => Val.Num(inner().Truthy ? 0 : 1); }
        if (Accept("-")) { var inner = Unary(); return () => Val.Num(-inner().Number); }
        if (Accept("+")) { var inner = Unary(); return () => Val.Num(inner().Number); }
        return Power();
    }

    private Func<Val> Power()
    {
        var left = Postfix();
        if (Accept("^")) { var right = Unary(); var l = left; return () => Val.Num(Math.Pow(l().Number, right().Number)); }
        return left;
    }

    private Func<Val> Postfix()
    {
        if (IsOp("++") || IsOp("--"))
        {
            double delta = Peek().Text == "++" ? 1 : -1; pos++;
            var target = TryLValue() ?? throw new FormatException("++ / -- 뒤에는 변수가 와야 합니다");
            return () => { var v = Val.Num(target.Get().Number + delta); target.Set(v); return v; };
        }
        int save = pos; var lvalue = TryLValue();
        if (lvalue != null)
        {
            if (IsOp("++") || IsOp("--")) { double delta = Peek().Text == "++" ? 1 : -1; pos++; return () => { double old = lvalue.Get().Number; lvalue.Set(Val.Num(old + delta)); return Val.Num(old); }; }
            return lvalue.Get;
        }
        pos = save; return Primary();
    }

    private Func<Val> Primary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case K.Number: { pos++; double v = double.Parse(t.Text, C); return () => Val.Num(v); }
            case K.String: { pos++; string s = t.Text; return () => Val.Str(s); }
            case K.Regex: { pos++; var r = MakeRegex(t.Text); return () => Val.Num(r.IsMatch(record) ? 1 : 0); }
            case K.Func: pos++; return Call(t.Text);
            case K.Name:
                if (Keywords.Contains(t.Text)) throw new FormatException($"'{t.Text}' 를 여기에 쓸 수 없습니다");
                return TryLValue()!.Get;
        }
        if (Accept("("))
        {
            var inner = Expr();
            if (IsOp(",")) { var parts = new List<Func<Val>> { inner }; while (Accept(",")) parts.Add(Expr()); Expect(")"); if (!IsWord("in")) throw new FormatException("(a, b) 다음에는 in 이 와야 합니다"); pos++; string name = Peek().Text; pos++; return () => Val.Num(Array(name).ContainsKey(string.Join("\u001c", parts.Select(p => p().Text))) ? 1 : 0); }
            Expect(")"); return inner;
        }
        if (IsOp("$")) { var lvalue = TryLValue()!; return lvalue.Get; }
        throw new FormatException(t.Kind == K.End ? "식이 끝나지 않았습니다" : $"예상하지 못한 '{t.Text}'");
    }

    private Func<Val> Call(string name)
    {
        var args = new List<Func<Val>>(); string? arrayArg = null; LValue? targetArg = null;
        if (Accept("("))
        {
            if (!IsOp(")"))
            {
                int index = 0;
                do
                {
                    Newlines();
                    if (name == "split" && index == 1) { arrayArg = Peek().Text; pos++; }
                    else if (name is "sub" or "gsub" && index == 2) { int save = pos; targetArg = TryLValue(); if (targetArg == null) { pos = save; args.Add(Expr()); } }
                    else if (name is "sub" or "gsub" or "match" or "split" && index == (name == "split" ? 2 : name == "match" ? 1 : 0) && Peek().Kind == K.Regex) { string pattern = Peek().Text; pos++; args.Add(() => Val.Str("\u0000re:" + pattern)); }
                    else args.Add(Expr());
                    index++;
                } while (Accept(","));
            }
            Expect(")");
        }
        else if (name != "length") throw new FormatException($"{name} 다음에 ( 가 필요합니다");
        string Arg(int i) => i < args.Count ? args[i]().Text : "";
        double Num(int i) => i < args.Count ? args[i]().Number : 0;
        Regex PatternArg(int i) { string p = Arg(i); return MakeRegex(p.StartsWith("\u0000re:") ? p[4..] : p); }
        switch (name)
        {
            case "length": return () => Val.Num(args.Count == 0 ? record.Length : Arg(0).Length);
            case "substr":
                return () =>
                {
                    string s = Arg(0); double start = Math.Round(Num(1)); double length = args.Count > 2 ? Math.Round(Num(2)) : double.MaxValue;
                    double from = Math.Max(1, start), to = Math.Min(s.Length + 1, start + length);
                    return Val.Str(to <= from ? "" : s.Substring((int)from - 1, (int)(to - from)));
                };
            case "index": return () => Val.Num(Arg(0).IndexOf(Arg(1), StringComparison.Ordinal) + 1);
            case "toupper": return () => Val.Str(Arg(0).ToUpperInvariant());
            case "tolower": return () => Val.Str(Arg(0).ToLowerInvariant());
            case "int": return () => Val.Num(Math.Truncate(Num(0)));
            case "sqrt": return () => Val.Num(Math.Sqrt(Num(0)));
            case "exp": return () => Val.Num(Math.Exp(Num(0)));
            case "log": return () => Val.Num(Math.Log(Num(0)));
            case "sin": return () => Val.Num(Math.Sin(Num(0)));
            case "cos": return () => Val.Num(Math.Cos(Num(0)));
            case "atan2": return () => Val.Num(Math.Atan2(Num(0), Num(1)));
            case "rand": return () => Val.Num(Random.Shared.NextDouble());
            case "srand": return () => Val.Num(0);
            case "close": return () => Val.Num(0);
            case "system": return () => throw new FormatException("system() 은 연습 터미널에서 실행하지 않습니다");
            case "sprintf": return () => Val.Str(ShellEngine.Format(Arg(0), args.Skip(1).Select(a => a().Text).ToList(), false));
            case "split":
                if (arrayArg == null) throw new FormatException("split(문자열, 배열[, 구분자]) 형식으로 쓰세요");
                return () =>
                {
                    var array = Array(arrayArg); array.Clear();
                    string separator = args.Count > 1 ? Arg(1) : fs; if (separator.StartsWith("\u0000re:")) separator = separator[4..];
                    var parts = Split(Arg(0), separator); for (int i = 0; i < parts.Count; i++) array[(i + 1).ToString(C)] = Val.Input(parts[i]);
                    return Val.Num(parts.Count);
                };
            case "match":
                return () => { var m = PatternArg(1).Match(Arg(0)); SetVar("RSTART", Val.Num(m.Success ? m.Index + 1 : 0)); SetVar("RLENGTH", Val.Num(m.Success ? m.Length : -1)); return Val.Num(m.Success ? m.Index + 1 : 0); };
            case "sub": case "gsub":
                return () =>
                {
                    var regex = PatternArg(0); string replacement = Arg(1);
                    string source = targetArg != null ? targetArg.Get().Text : args.Count > 2 ? Arg(2) : record; int count = 0;
                    string result = regex.Replace(source, m =>
                    {
                        count++; var sb = new StringBuilder();
                        for (int i = 0; i < replacement.Length; i++) { if (replacement[i] == '\\' && i + 1 < replacement.Length && replacement[i + 1] == '&') { sb.Append('&'); i++; } else if (replacement[i] == '&') sb.Append(m.Value); else sb.Append(replacement[i]); }
                        return sb.ToString();
                    }, name == "sub" ? 1 : -1);
                    if (targetArg != null) targetArg.Set(Val.Str(result)); else if (args.Count <= 2) SetRecord(result);
                    return Val.Num(count);
                };
        }
        throw new FormatException($"지원하지 않는 함수: {name}");
    }
}
