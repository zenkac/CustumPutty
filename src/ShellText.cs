using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

public sealed partial class ShellEngine
{
    private void RegisterText()
    {
        commands["grep"] = c => Grep(c, 'G'); commands["egrep"] = c => Grep(c, 'E'); commands["fgrep"] = c => Grep(c, 'F');
        commands["sort"] = SortCommand; commands["uniq"] = Uniq; commands["cut"] = Cut; commands["tr"] = Tr; commands["tee"] = Tee;
        commands["sed"] = Sed; commands["awk"] = Awk; commands["gawk"] = Awk; commands["xargs"] = Xargs; commands["diff"] = Diff;
        commands["find"] = Find; commands["tar"] = Tar; commands["gzip"] = Gzip; commands["gunzip"] = c => Gzip(c, true); commands["zcat"] = Zcat;
        commands["nl"] = c => { var inputs = Inputs(c, c.Args.Where(a => !a.StartsWith('-')).ToList(), out int status); int n = 1; foreach (var (_, text) in inputs) foreach (var line in SplitLines(text)) c.Line(line.Length == 0 ? "" : $"{n++,6}\t{line}"); return status; };
        commands["rev"] = c => { var inputs = Inputs(c, c.Args, out int status); foreach (var (_, text) in inputs) foreach (var line in SplitLines(text)) c.Line(new string(line.Reverse().ToArray())); return status; };
    }

    // ───────── Regular expressions (POSIX basic / extended) ─────────

    private static string PosixClass(string pattern, ref int i)
    {
        int j = i + 1; if (j < pattern.Length && pattern[j] == '^') j++; if (j < pattern.Length && pattern[j] == ']') j++;
        while (j < pattern.Length && pattern[j] != ']') { if (pattern[j] == '[' && j + 1 < pattern.Length && pattern[j + 1] == ':') { int end = pattern.IndexOf(":]", j + 2, StringComparison.Ordinal); if (end > 0) { j = end + 2; continue; } } j++; }
        if (j >= pattern.Length) return @"\[";
        string body = pattern[(i + 1)..j]
            .Replace("[:alpha:]", "a-zA-Z").Replace("[:digit:]", "0-9").Replace("[:alnum:]", "a-zA-Z0-9").Replace("[:space:]", @"\s").Replace("[:blank:]", @" \t")
            .Replace("[:upper:]", "A-Z").Replace("[:lower:]", "a-z").Replace("[:punct:]", @"!-/:-@\[-`{-~").Replace("[:xdigit:]", "0-9A-Fa-f");
        body = body.Replace("\\", "\\\\").Replace("[", @"\[");
        i = j; return "[" + body + "]";
    }

    /// <summary>POSIX regular expression to .NET. Basic (grep, sed) treats + ? | ( ) { } as literals unless escaped.</summary>
    internal static string PosixToNet(string pattern, bool extended)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i]; bool first = i == 0 || extended && pattern[i - 1] is '(' or '|' || !extended && i >= 2 && pattern[i - 2] == '\\' && pattern[i - 1] is '(' or '|';
            if (c == '\\' && i + 1 < pattern.Length)
            {
                char d = pattern[++i];
                if (d is '<' or '>') sb.Append(@"\b");
                else if (d is 'w' or 'W' or 's' or 'S' or 'b' or 'B' or 'd' or 'D') sb.Append('\\').Append(d);
                else if (char.IsAsciiDigit(d) && d != '0') sb.Append('\\').Append(d);
                else if (!extended && d is '+' or '?' or '|' or '(' or ')') sb.Append(d);
                else if (!extended && d == '{') { int end = pattern.IndexOf("\\}", i, StringComparison.Ordinal); if (end > 0) { sb.Append('{').Append(pattern[(i + 1)..end]).Append('}'); i = end + 1; } else sb.Append(@"\{"); }
                else if (d == 'n') sb.Append(@"\n");
                else if (d == 't') sb.Append(@"\t");
                else sb.Append(Regex.Escape(d.ToString()));
                continue;
            }
            switch (c)
            {
                case '.': case '*': sb.Append(c); break;
                case '[': sb.Append(PosixClass(pattern, ref i)); break;
                case '^': sb.Append(first || !extended && i == 0 ? "^" : extended ? "^" : @"\^"); break;
                case '$': sb.Append(i == pattern.Length - 1 || extended ? "$" : pattern[(i + 1)..].StartsWith("\\)") || pattern[(i + 1)..].StartsWith("\\|") ? "$" : @"\$"); break;
                case '+': case '?': case '|': case '(': case ')': sb.Append(extended ? c.ToString() : "\\" + c); break;
                case '{':
                    if (extended && Regex.IsMatch(pattern[i..], @"^\{\d*(,\d*)?\}")) { int end = pattern.IndexOf('}', i); sb.Append(pattern[i..(end + 1)]); i = end; }
                    else sb.Append(@"\{");
                    break;
                case '}': sb.Append(@"\}"); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }

    private static Regex? BuildRegex(IEnumerable<string> patterns, bool extended, bool fixedText, bool ignore, bool word, bool whole, out string? error)
    {
        error = null;
        var parts = patterns.SelectMany(p => p.Split('\n')).Select(p => fixedText ? Regex.Escape(p) : PosixToNet(p, extended)).ToList();
        string body = parts.Count == 1 ? parts[0] : string.Join("|", parts.Select(p => "(?:" + p + ")"));
        if (word) body = @"(?<![\p{L}\p{N}_])(?:" + body + @")(?![\p{L}\p{N}_])";
        if (whole) body = "^(?:" + body + ")$";
        try { return new Regex(body, RegexOptions.CultureInvariant | (ignore ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(300)); }
        catch (ArgumentException) { error = "Unmatched ( or \\( or [ in the pattern"; return null; }
    }

    // ───────── grep ─────────

    private int Grep(Ctx c, char mode)
    {
        var args = new List<string>(); var patterns = new List<string>();
        for (int i = 0; i < c.Args.Count; i++)
        {
            string a = c.Args[i];
            if (a == "-e" && i + 1 < c.Args.Count) { patterns.Add(c.Args[++i]); continue; }
            if (a.StartsWith("-e") && a.Length > 2 && !a.StartsWith("--")) { patterns.Add(a[2..]); continue; }
            if (a.StartsWith("--regexp=")) { patterns.Add(a[9..]); continue; }
            args.Add(a);
        }
        var o = Options(args, "ivnclLrRwoEFhHqsxP", "ABCm");
        if (o.Error != null) return Usage(c, o.Error, 2);
        var operands = o.Rest.ToList();
        if (patterns.Count == 0)
        {
            if (operands.Count == 0) { c.Err.Append("Usage: grep [OPTION]... PATTERNS [FILE]...\nTry 'grep --help' for more information.\n"); return 2; }
            patterns.Add(operands[0]); operands.RemoveAt(0);
        }
        bool extended = mode == 'E' || o.Has('E') || o.Has('P'), fixedText = mode == 'F' || o.Has('F'), ignore = o.Has('i') || o.Long.Contains("ignore-case");
        bool invert = o.Has('v') || o.Long.Contains("invert-match"), numbers = o.Has('n') || o.Long.Contains("line-number"), recursive = o.Has('r') || o.Has('R') || o.Long.Contains("recursive");
        var regex = BuildRegex(patterns, extended, fixedText, ignore, o.Has('w') || o.Long.Contains("word-regexp"), o.Has('x'), out string? error);
        if (regex == null) return c.Fail(error!, 2);
        int after = int.TryParse(o.Value('A') ?? o.Value('C'), out int a1) ? a1 : 0, before = int.TryParse(o.Value('B') ?? o.Value('C'), out int b1) ? b1 : 0;
        int maxCount = int.TryParse(o.Value('m'), out int m1) ? m1 : int.MaxValue;
        string? colorMode = o.LongValue("color") ?? o.LongValue("colour");
        bool color = c.Tty && colorMode is "auto" or "always" or "" || colorMode == "always";
        string? include = o.LongValue("include"), exclude = o.LongValue("exclude");
        var targets = new List<(string Name, string Text)>(); bool errors = false;
        if (operands.Count == 0 && recursive) operands.Add(".");
        if (operands.Count == 0) targets.Add(("(standard input)", c.Stdin ?? ""));
        foreach (var operand in operands)
        {
            if (operand == "-") { targets.Add(("(standard input)", c.Stdin ?? "")); continue; }
            var node = Fs.Find(Resolve(operand));
            if (node == null) { if (!o.Has('s')) c.Err.Append($"grep: {operand}: No such file or directory\n"); errors = true; continue; }
            if (node.IsDir)
            {
                if (!recursive) { if (!o.Has('s')) c.Err.Append($"grep: {operand}: Is a directory\n"); continue; }
                foreach (var file in node.Walk().Where(n => !n.IsDir))
                {
                    if (include != null && !ShellFs.GlobRegex(include).IsMatch(file.Name)) continue;
                    if (exclude != null && ShellFs.GlobRegex(exclude).IsMatch(file.Name)) continue;
                    if (file.Binary) continue;
                    string relative = file.Path[node.Path.Length..].TrimStart('/');
                    targets.Add(((operand.EndsWith('/') ? operand : operand + "/") + relative, file.Content));
                }
                continue;
            }
            if (node.Binary) continue;
            targets.Add((operand, node.Content));
        }
        bool names = (targets.Count > 1 || recursive) && !o.Has('h') || o.Has('H');
        string Name(string name) => color ? $"\u001b[35m{name}\u001b[0m" : name;
        string Sep(char s) => color ? $"\u001b[36m{s}\u001b[0m" : s.ToString();
        string Number(int n) => color ? $"\u001b[32m{n}\u001b[0m" : n.ToString(C);
        string Mark(string line) => !color || invert ? line : regex.Replace(line, m => m.Length == 0 ? "" : $"\u001b[01;31m{m.Value}\u001b[0m");
        bool any = false;
        foreach (var (name, text) in targets)
        {
            var lines = SplitLines(text);
            var hits = new List<int>();
            for (int i = 0; i < lines.Count && hits.Count < maxCount; i++) if (regex.IsMatch(lines[i]) != invert) hits.Add(i);
            if (hits.Count > 0) any = true;
            if (o.Has('q')) { if (any) return 0; continue; }
            if (o.Has('c')) { c.Line((names ? Name(name) + Sep(':') : "") + hits.Count); continue; }
            if (o.Has('l')) { if (hits.Count > 0) c.Line(Name(name)); continue; }
            if (o.Has('L')) { if (hits.Count == 0) c.Line(Name(name)); continue; }
            if (o.Has('o'))
            {
                foreach (int i in hits) foreach (Match m in regex.Matches(lines[i])) if (m.Length > 0) c.Line((names ? Name(name) + Sep(':') : "") + (numbers ? Number(i + 1) + Sep(':') : "") + (color ? $"\u001b[01;31m{m.Value}\u001b[0m" : m.Value));
                continue;
            }
            var show = new SortedSet<int>(); var hitSet = hits.ToHashSet();
            foreach (int i in hits) for (int k = Math.Max(0, i - before); k <= Math.Min(lines.Count - 1, i + after); k++) show.Add(k);
            int previous = -2;
            foreach (int i in show)
            {
                if ((after > 0 || before > 0) && previous >= 0 && i > previous + 1) c.Line(color ? "\u001b[36m--\u001b[0m" : "--");
                char sep = hitSet.Contains(i) ? ':' : '-';
                c.Line((names ? Name(name) + Sep(sep) : "") + (numbers ? Number(i + 1) + Sep(sep) : "") + (hitSet.Contains(i) ? Mark(lines[i]) : lines[i]));
                previous = i;
            }
        }
        if (o.Has('q')) return errors ? 2 : 1;
        return errors && !any ? 2 : any ? 0 : 1;
    }

    // ───────── sort, uniq, cut, tr, tee ─────────

    private static double LeadingNumber(string text, bool human)
    {
        var m = Regex.Match(text, @"^\s*(-?\d+(\.\d+)?)([KMGTkmgt]?)");
        if (!m.Success) return 0;
        double value = double.Parse(m.Groups[1].Value, C);
        if (human) value *= m.Groups[3].Value.ToUpperInvariant() switch { "K" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, _ => 1 };
        return value;
    }

    private int SortCommand(Ctx c)
    {
        var o = Options(c.Args, "nrufhbsV", "kto");
        if (o.Error != null) return Usage(c, o.Error, 2);
        var inputs = Inputs(c, o.Rest, out int status);
        var lines = inputs.SelectMany(x => SplitLines(x.Text)).ToList();
        string? sep = o.Value('t'); bool numeric = o.Has('n'), human = o.Has('h'), reverse = o.Has('r'), fold = o.Has('f');
        int keyStart = 0, keyEnd = int.MaxValue;
        if (o.Value('k') is string key)
        {
            var m = Regex.Match(key, @"^(\d+)(?:\.\d+)?([a-zA-Z]*)(?:,(\d+)(?:\.\d+)?([a-zA-Z]*))?$");
            if (!m.Success) return c.Fail($"invalid number at field start: invalid count at start of ‘{key}’", 2);
            keyStart = int.Parse(m.Groups[1].Value) - 1; if (m.Groups[3].Success && m.Groups[3].Length > 0) keyEnd = int.Parse(m.Groups[3].Value) - 1;
            string flags = m.Groups[2].Value + m.Groups[4].Value; numeric |= flags.Contains('n'); human |= flags.Contains('h'); reverse |= flags.Contains('r'); fold |= flags.Contains('f');
        }
        string Key(string line)
        {
            if (o.Value('k') == null) return line;
            var fields = sep != null ? line.Split(sep[0]) : Regex.Split(line.Trim(), @"\s+");
            if (keyStart >= fields.Length) return "";
            return string.Join(sep ?? " ", fields.Skip(keyStart).Take(Math.Min(fields.Length, keyEnd == int.MaxValue ? fields.Length : keyEnd + 1) - keyStart));
        }
        Comparison<string> compare = (a, b) =>
        {
            string ka = Key(a), kb = Key(b); int result;
            if (numeric || human) result = LeadingNumber(ka, human).CompareTo(LeadingNumber(kb, human));
            else result = fold ? string.Compare(ka, kb, StringComparison.OrdinalIgnoreCase) : string.CompareOrdinal(ka, kb);
            if (result == 0 && !o.Has('s') && !o.Has('u')) result = string.CompareOrdinal(a, b);
            return reverse ? -result : result;
        };
        var sorted = lines.ToList(); sorted.Sort((a, b) => compare(a, b));
        if (o.Has('u')) { var kept = new List<string>(); foreach (var line in sorted) if (kept.Count == 0 || (numeric || human ? LeadingNumber(Key(line), human) != LeadingNumber(Key(kept[^1]), human) : !string.Equals(Key(line), Key(kept[^1]), fold ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) kept.Add(line); sorted = kept; }
        string text = string.Concat(sorted.Select(l => l + "\n"));
        if (o.Value('o') is string outFile) { var problem = WriteRedirect(outFile, text, false); if (problem != null) return c.Fail(problem.Replace("bash: ", ""), 2); }
        else c.Out.Append(text);
        return status;
    }

    private int Uniq(Ctx c)
    {
        var o = Options(c.Args, "cdui");
        if (o.Error != null) return Usage(c, o.Error, 1);
        var inputs = Inputs(c, o.Rest.Take(1).ToList(), out int status);
        var lines = inputs.SelectMany(x => SplitLines(x.Text)).ToList();
        var comparison = o.Has('i') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int i = 0; i < lines.Count;)
        {
            int j = i; while (j < lines.Count && string.Equals(lines[j], lines[i], comparison)) j++;
            int count = j - i;
            if ((!o.Has('d') || count > 1) && (!o.Has('u') || count == 1)) c.Line(o.Has('c') ? $"{count,7} {lines[i]}" : lines[i]);
            i = j;
        }
        return status;
    }

    private static List<(int From, int To)>? Ranges(string list)
    {
        var result = new List<(int, int)>();
        foreach (var part in list.Split(','))
        {
            var m = Regex.Match(part, @"^(\d*)-?(\d*)$"); if (!m.Success || part.Length == 0) return null;
            int from = m.Groups[1].Length > 0 ? int.Parse(m.Groups[1].Value) : 1;
            int to = part.Contains('-') ? m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value) : int.MaxValue : from;
            if (from < 1) return null; result.Add((from, to));
        }
        return result;
    }

    private int Cut(Ctx c)
    {
        var o = Options(c.Args, "s", "dfcb");
        if (o.Error != null) return Usage(c, o.Error, 1);
        string? fields = o.Value('f'), chars = o.Value('c') ?? o.Value('b');
        if (fields == null && chars == null) return Usage(c, "you must specify a list of bytes, characters, or fields", 1);
        var ranges = Ranges(fields ?? chars!); if (ranges == null) return c.Fail("invalid field value", 1);
        bool In(int n) => ranges.Any(r => n >= r.From && n <= r.To);
        string delimiter = o.Value('d') ?? "\t";
        if (delimiter.Length != 1) return c.Fail("the delimiter must be a single character", 1);
        var inputs = Inputs(c, o.Rest, out int status);
        foreach (var (_, text) in inputs) foreach (var line in SplitLines(text))
        {
            if (chars != null) { c.Line(new string(line.Where((_, i) => In(i + 1)).ToArray())); continue; }
            if (!line.Contains(delimiter[0])) { if (!o.Has('s')) c.Line(line); continue; }
            var parts = line.Split(delimiter[0]);
            c.Line(string.Join(delimiter, parts.Where((_, i) => In(i + 1))));
        }
        return status;
    }

    private static string ExpandSet(string set)
    {
        set = set.Replace("[:lower:]", "a-z").Replace("[:upper:]", "A-Z").Replace("[:digit:]", "0-9").Replace("[:space:]", " \t\n").Replace("[:alpha:]", "a-zA-Z").Replace("[:alnum:]", "a-zA-Z0-9");
        set = Unescape(set); var sb = new StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-' && set[i] <= set[i + 2]) { for (char ch = set[i]; ch <= set[i + 2]; ch++) sb.Append(ch); i += 2; }
            else sb.Append(set[i]);
        }
        return sb.ToString();
    }

    private int Tr(Ctx c)
    {
        var o = Options(c.Args, "dsc");
        if (o.Rest.Count == 0 || !o.Has('d') && !o.Has('s') && o.Rest.Count < 2) return Usage(c, "missing operand", 1);
        string input = c.Stdin ?? ""; string first = ExpandSet(o.Rest[0]), second = o.Rest.Count > 1 ? ExpandSet(o.Rest[1]) : "";
        var sb = new StringBuilder();
        foreach (char ch in input)
        {
            int index = first.IndexOf(ch);
            if (o.Has('d')) { if (index < 0) sb.Append(ch); continue; }
            char mapped = index >= 0 && second.Length > 0 ? second[Math.Min(index, second.Length - 1)] : ch;
            string squeeze = second.Length > 0 ? second : first;
            if (o.Has('s') && sb.Length > 0 && sb[^1] == mapped && squeeze.Contains(mapped)) continue;
            sb.Append(mapped);
        }
        c.Out.Append(sb); return 0;
    }

    private int Tee(Ctx c)
    {
        var o = Options(c.Args, "ai");
        string text = c.Stdin ?? ""; int status = 0;
        foreach (var file in o.Rest) { var problem = WriteRedirect(file, text, o.Has('a')); if (problem != null) { c.Err.Append("tee: " + problem.Replace("bash: ", "") + "\n"); status = 1; } }
        c.Out.Append(text); return status;
    }

    // ───────── sed ─────────

    private sealed class SedCommand
    {
        public string? Start, End; public bool Negate; public char Name; public Regex? Pattern; public string Replacement = ""; public bool Global, Print; public int Occurrence = 1; public string Text = "";
        public bool InRange;
    }

    private int Sed(Ctx c)
    {
        var scripts = new List<string>(); var args = new List<string>(); bool quiet = false, extended = false, inPlace = false; string backup = "";
        for (int i = 0; i < c.Args.Count; i++)
        {
            string a = c.Args[i];
            if (a == "-e" && i + 1 < c.Args.Count) { scripts.Add(c.Args[++i]); continue; }
            if (a == "-n" || a == "--quiet") { quiet = true; continue; }
            if (a is "-E" or "-r") { extended = true; continue; }
            if (a.StartsWith("-i")) { inPlace = true; backup = a[2..]; continue; }
            if (Regex.IsMatch(a, "^-[nEr]+$")) { quiet |= a.Contains('n'); extended |= a.Contains('E') || a.Contains('r'); continue; }
            args.Add(a);
        }
        if (scripts.Count == 0) { if (args.Count == 0) { c.Err.Append("Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...\n"); return 1; } scripts.Add(args[0]); args.RemoveAt(0); }
        List<SedCommand> program;
        try { program = ParseSed(string.Join("\n", scripts), extended); }
        catch (FormatException error) { return c.Fail("-e expression #1, char 1: " + error.Message, 1); }
        if (inPlace && args.Count == 0) return c.Fail("no input files", 1);
        int status = 0;
        var inputs = inPlace ? args.Select(a => (a, Fs.Find(Resolve(a)))).ToList() : [];
        if (inPlace)
        {
            foreach (var (name, node) in inputs)
            {
                if (node == null) { c.Err.Append($"sed: can't read {name}: No such file or directory\n"); status = 2; continue; }
                if (node.IsDir) { c.Err.Append($"sed: couldn't edit {name}: not a regular file\n"); status = 4; continue; }
                var result = new StringBuilder(); RunSed(program, SplitLines(node.Content), quiet, result);
                if (backup.Length > 0) Fs.WriteFile(node.Path + backup, node.Content, Now);
                node.Content = result.ToString(); node.Modified = Now;
            }
            return status;
        }
        var texts = Inputs(c, args, out status);
        RunSed(program, texts.SelectMany(t => SplitLines(t.Text)).ToList(), quiet, c.Out);
        return status;
    }

    private static List<SedCommand> ParseSed(string script, bool extended)
    {
        var list = new List<SedCommand>(); int i = 0;
        string ReadDelimited(char d)
        {
            var sb = new StringBuilder();
            while (i < script.Length && script[i] != d) { if (script[i] == '\\' && i + 1 < script.Length) { if (script[i + 1] == d) { sb.Append(d); i += 2; continue; } sb.Append(script[i]).Append(script[i + 1]); i += 2; continue; } sb.Append(script[i++]); }
            if (i >= script.Length) throw new FormatException($"unterminated `s' command");
            i++; return sb.ToString();
        }
        string? Address()
        {
            if (i >= script.Length) return null;
            if (char.IsAsciiDigit(script[i])) { int start = i; while (i < script.Length && char.IsAsciiDigit(script[i])) i++; return script[start..i]; }
            if (script[i] == '$') { i++; return "$"; }
            if (script[i] == '/') { i++; return "/" + ReadDelimited('/'); }
            return null;
        }
        while (i < script.Length)
        {
            while (i < script.Length && script[i] is ' ' or ';' or '\n' or '\t') i++;
            if (i >= script.Length) break;
            var cmd = new SedCommand { Start = Address() };
            if (cmd.Start != null && i < script.Length && script[i] == ',') { i++; cmd.End = Address() ?? throw new FormatException("unexpected `,'"); }
            while (i < script.Length && script[i] == ' ') i++;
            if (i < script.Length && script[i] == '!') { cmd.Negate = true; i++; }
            if (i >= script.Length) throw new FormatException("missing command");
            cmd.Name = script[i++];
            switch (cmd.Name)
            {
                case 's':
                {
                    char d = script[i++]; string pattern = ReadDelimited(d); cmd.Replacement = ReadDelimited(d);
                    string flags = ""; while (i < script.Length && script[i] is 'g' or 'p' or 'i' or 'I' or >= '0' and <= '9') flags += script[i++];
                    cmd.Global = flags.Contains('g'); cmd.Print = flags.Contains('p'); if (int.TryParse(new string(flags.Where(char.IsAsciiDigit).ToArray()), out int n)) cmd.Occurrence = n;
                    try { cmd.Pattern = new Regex(PosixToNet(pattern, extended), (flags.Contains('i') || flags.Contains('I') ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300)); }
                    catch (ArgumentException) { throw new FormatException("invalid regular expression"); }
                    break;
                }
                case 'a': case 'i': case 'c':
                {
                    if (i < script.Length && script[i] == '\\') i++;
                    while (i < script.Length && script[i] == ' ') i++;
                    int end = script.IndexOf('\n', i); if (end < 0) end = script.Length;
                    cmd.Text = script[i..end]; i = end; break;
                }
                case 'd': case 'p': case 'q': case '=': break;
                default: throw new FormatException($"unknown command: `{cmd.Name}'");
            }
            if (cmd.Start is string s && s.StartsWith('/')) cmd.Start = "/" + PosixToNet(s[1..], extended);
            if (cmd.End is string e && e.StartsWith('/')) cmd.End = "/" + PosixToNet(e[1..], extended);
            list.Add(cmd);
        }
        return list;
    }

    private static void RunSed(List<SedCommand> program, List<string> lines, bool quiet, StringBuilder output)
    {
        bool Hit(string address, string line, int number, int count) =>
            address == "$" ? number == count : address.StartsWith('/') ? Regex.IsMatch(line, address[1..], RegexOptions.None, TimeSpan.FromMilliseconds(300)) : int.TryParse(address, out int n) && n == number;
        foreach (var cmd in program) cmd.InRange = false;
        for (int index = 0; index < lines.Count; index++)
        {
            string space = lines[index]; int number = index + 1; bool deleted = false, quit = false; var append = new List<string>();
            foreach (var cmd in program)
            {
                bool selected;
                if (cmd.Start == null) selected = true;
                else if (cmd.End == null) selected = Hit(cmd.Start, space, number, lines.Count);
                else if (cmd.InRange) { selected = true; if (Hit(cmd.End, space, number, lines.Count) || int.TryParse(cmd.End, out int end) && number >= end) cmd.InRange = false; }
                else if (Hit(cmd.Start, space, number, lines.Count)) { selected = true; cmd.InRange = !(int.TryParse(cmd.End, out int end) && number >= end); }
                else selected = false;
                if (selected == cmd.Negate) continue;
                switch (cmd.Name)
                {
                    case 'd': deleted = true; break;
                    case 'p': output.Append(space).Append('\n'); break;
                    case '=': output.Append(number).Append('\n'); break;
                    case 'q': quit = true; break;
                    case 'a': append.Add(cmd.Text); break;
                    case 'i': output.Append(cmd.Text).Append('\n'); break;
                    case 'c': output.Append(cmd.Text).Append('\n'); deleted = true; break;
                    case 's':
                    {
                        int seen = 0; bool changed = false;
                        space = cmd.Pattern!.Replace(space, m =>
                        {
                            seen++;
                            if (!cmd.Global && seen != cmd.Occurrence || cmd.Global && seen < cmd.Occurrence) return m.Value;
                            changed = true; return SedReplacement(cmd.Replacement, m);
                        });
                        if (changed && cmd.Print) output.Append(space).Append('\n');
                        break;
                    }
                }
                if (deleted || quit) break;
            }
            if (!deleted && !quiet) output.Append(space).Append('\n');
            foreach (var text in append) output.Append(text).Append('\n');
            if (quit) break;
        }
    }

    private static string SedReplacement(string replacement, Match m)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            char ch = replacement[i];
            if (ch == '&') { sb.Append(m.Value); continue; }
            if (ch == '\\' && i + 1 < replacement.Length)
            {
                char d = replacement[++i];
                if (char.IsAsciiDigit(d)) sb.Append(m.Groups[d - '0'].Value);
                else sb.Append(d switch { 'n' => '\n', 't' => '\t', _ => d });
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ───────── awk ─────────

    private int Awk(Ctx c)
    {
        string? separator = null; var assigns = new List<string>(); var rest = new List<string>(); string? program = null;
        for (int i = 0; i < c.Args.Count; i++)
        {
            string a = c.Args[i];
            if (program == null && a == "-F" && i + 1 < c.Args.Count) { separator = c.Args[++i]; continue; }
            if (program == null && a.StartsWith("-F") && a.Length > 2) { separator = a[2..]; continue; }
            if (program == null && a == "-v" && i + 1 < c.Args.Count) { assigns.Add(c.Args[++i]); continue; }
            if (program == null) { program = a; continue; }
            rest.Add(a);
        }
        if (program == null) { c.Err.Append("usage: awk [-F fs][-v var=value][prog | -f progfile][file ...]\n"); return 2; }
        AwkProgram compiled;
        try { compiled = AwkProgram.Compile(program); }
        catch (FormatException error) { c.Err.Append($"awk: syntax error: {error.Message}\n awk: {program}\n"); return 2; }
        var inputs = Inputs(c, rest.Where(r => !r.Contains('=')).ToList(), out int status);
        foreach (var assign in assigns.Concat(rest.Where(r => r.Contains('=') && Regex.IsMatch(r, @"^[A-Za-z_]\w*="))))
        { int eq = assign.IndexOf('='); compiled.SetVariable(assign[..eq], assign[(eq + 1)..]); }
        try { compiled.Run(inputs, separator == "\\t" ? "\t" : separator, c.Out); }
        catch (FormatException error) { c.Err.Append($"awk: {error.Message}\n"); return 2; }
        catch (RegexMatchTimeoutException) { c.Err.Append("awk: regular expression too complex\n"); return 2; }
        return status;
    }

    // ───────── xargs ─────────

    private int Xargs(Ctx c)
    {
        string? replace = null; int perCommand = int.MaxValue; bool trace = false, noEmpty = false; int i = 0;
        for (; i < c.Args.Count; i++)
        {
            string a = c.Args[i];
            if (a == "-I" && i + 1 < c.Args.Count) { replace = c.Args[++i]; continue; }
            if (a.StartsWith("-I") && a.Length > 2) { replace = a[2..]; continue; }
            if (a == "-n" && i + 1 < c.Args.Count && int.TryParse(c.Args[i + 1], out int n)) { perCommand = Math.Max(1, n); i++; continue; }
            if (a == "-t") { trace = true; continue; }
            if (a is "-r" or "--no-run-if-empty") { noEmpty = true; continue; }
            if (a == "-0") continue;
            break;
        }
        var command = c.Args.Skip(i).ToList(); if (command.Count == 0) command = ["echo"];
        string input = c.Stdin ?? ""; int status = 0;
        if (replace != null)
        {
            foreach (var line in SplitLines(input).Where(l => l.Trim().Length > 0))
            {
                var argv = command.Select(a => a.Replace(replace, line.Trim())).ToList();
                if (trace) c.Err.Append(string.Join(" ", argv) + "\n");
                if (RunNested(c, argv) != 0) status = 123;
            }
            return status;
        }
        var items = Regex.Matches(input, "\"[^\"]*\"|'[^']*'|\\S+").Select(m => m.Value.Trim('"', '\'')).ToList();
        if (items.Count == 0) { if (noEmpty || command[0] != "echo") return 0; c.Line(""); return 0; }
        for (int start = 0; start < items.Count; start += perCommand)
        {
            var argv = command.Concat(items.Skip(start).Take(perCommand)).ToList();
            if (trace) c.Err.Append(string.Join(" ", argv) + "\n");
            if (RunNested(c, argv) != 0) status = 123;
        }
        return status;
    }

    // ───────── diff ─────────

    private int Diff(Ctx c)
    {
        var o = Options(c.Args, "uqiwyN", "U");
        if (o.Error != null) return Usage(c, o.Error, 2);
        if (o.Rest.Count != 2) { c.Err.Append($"diff: {(o.Rest.Count < 2 ? "missing operand after '" + o.Rest.FirstOrDefault() + "'" : "extra operand '" + o.Rest[2] + "'")}\ndiff: Try 'diff --help' for more information.\n"); return 2; }
        var texts = new List<(string Name, ShellNode Node)>();
        foreach (var name in o.Rest)
        {
            var node = Fs.Find(Resolve(name));
            if (node == null) { c.Err.Append($"diff: {name}: No such file or directory\n"); return 2; }
            texts.Add((name, node));
        }
        if (texts[0].Node.IsDir && !texts[1].Node.IsDir && texts[1].Node.Parent != null) texts[0] = (texts[0].Name + "/" + texts[1].Node.Name, Fs.Find(ShellFs.Combine(texts[0].Node.Path, texts[1].Node.Name))!);
        if (texts.Any(t => t.Node == null || t.Node.IsDir)) return c.Fail("디렉터리 비교는 연습 터미널에서 지원하지 않습니다 · 파일 두 개를 지정하세요", 2);
        var a = SplitLines(texts[0].Node.Content); var b = SplitLines(texts[1].Node.Content);
        Func<string, string> norm = s => o.Has('w') ? Regex.Replace(s, @"\s+", "") : o.Has('i') ? s.ToLowerInvariant() : s;
        int n = a.Count, m = b.Count; var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--) for (int j = m - 1; j >= 0; j--) lcs[i, j] = norm(a[i]) == norm(b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(char Op, int A, int B)>(); int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && norm(a[x]) == norm(b[y])) { ops.Add(('=', x++, y++)); }
            else if (y < m && (x >= n || lcs[x, y + 1] >= lcs[x + 1, y])) ops.Add(('+', x, y++));
            else ops.Add(('-', x++, y));
        }
        if (ops.All(op => op.Op == '=')) return 0;
        if (o.Has('q')) { c.Line($"Files {texts[0].Name} and {texts[1].Name} differ"); return 1; }
        if (o.Has('u') || o.Value('U') != null)
        {
            int context = int.TryParse(o.Value('U'), out int u) ? u : 3;
            c.Line($"--- {texts[0].Name}\t{texts[0].Node.Modified:yyyy-MM-dd HH:mm:ss}.000000000 +0900");
            c.Line($"+++ {texts[1].Name}\t{texts[1].Node.Modified:yyyy-MM-dd HH:mm:ss}.000000000 +0900");
            for (int k = 0; k < ops.Count;)
            {
                if (ops[k].Op == '=') { k++; continue; }
                int start = Math.Max(0, k - context), end = k;
                while (true)
                {
                    while (end < ops.Count && ops[end].Op != '=') end++;
                    int next = end; while (next < ops.Count && ops[next].Op == '=') next++;
                    if (next < ops.Count && next - end <= context * 2) { end = next; continue; }
                    end = Math.Min(ops.Count, end + context); break;
                }
                var hunk = ops.Skip(start).Take(end - start).ToList();
                int aStart = hunk.First().A + 1, bStart = hunk.First().B + 1, aCount = hunk.Count(h => h.Op != '+'), bCount = hunk.Count(h => h.Op != '-');
                c.Line($"@@ -{(aCount == 0 ? aStart - 1 : aStart)}{(aCount == 1 ? "" : "," + aCount)} +{(bCount == 0 ? bStart - 1 : bStart)}{(bCount == 1 ? "" : "," + bCount)} @@");
                foreach (var h in hunk) c.Line(h.Op == '=' ? " " + a[h.A] : h.Op == '-' ? "-" + a[h.A] : "+" + b[h.B]);
                k = end;
            }
            return 1;
        }
        for (int k = 0; k < ops.Count;)
        {
            if (ops[k].Op == '=') { k++; continue; }
            int end = k; while (end < ops.Count && ops[end].Op != '=') end++;
            var removed = ops.Skip(k).Take(end - k).Where(op => op.Op == '-').ToList(); var added = ops.Skip(k).Take(end - k).Where(op => op.Op == '+').ToList();
            string Range(int first, int count) => count <= 1 ? first.ToString(C) : $"{first},{first + count - 1}";
            int aLine = ops[k].A, bLine = ops[k].B;
            if (removed.Count > 0 && added.Count > 0) c.Line($"{Range(removed[0].A + 1, removed.Count)}c{Range(added[0].B + 1, added.Count)}");
            else if (removed.Count > 0) c.Line($"{Range(removed[0].A + 1, removed.Count)}d{bLine}");
            else c.Line($"{aLine}a{Range(added[0].B + 1, added.Count)}");
            foreach (var r in removed) c.Line("< " + a[r.A]);
            if (removed.Count > 0 && added.Count > 0) c.Line("---");
            foreach (var r in added) c.Line("> " + b[r.B]);
            k = end;
        }
        return 1;
    }

    // ───────── find ─────────

    private abstract class FindNode { public abstract bool Eval(ShellNode node, string shown, int depth); }
    private sealed class FindAnd(FindNode left, FindNode right) : FindNode { public override bool Eval(ShellNode n, string s, int d) => left.Eval(n, s, d) && right.Eval(n, s, d); }
    private sealed class FindOr(FindNode left, FindNode right) : FindNode { public override bool Eval(ShellNode n, string s, int d) => left.Eval(n, s, d) || right.Eval(n, s, d); }
    private sealed class FindNot(FindNode inner) : FindNode { public override bool Eval(ShellNode n, string s, int d) => !inner.Eval(n, s, d); }
    private sealed class FindTest(Func<ShellNode, string, int, bool> test) : FindNode { public override bool Eval(ShellNode n, string s, int d) => test(n, s, d); }

    private sealed class FindError(string message) : Exception(message);

    private int Find(Ctx c)
    {
        var args = c.Args; int index = 0; var paths = new List<string>();
        while (index < args.Count && !(args[index].StartsWith('-') && args[index].Length > 1) && args[index] is not ("(" or "!" or ")")) paths.Add(args[index++]);
        if (paths.Count == 0) paths.Add(".");
        int maxDepth = int.MaxValue, minDepth = 0; bool hasAction = false, deleting = false;
        var deletes = new List<(ShellNode Node, string Shown)>(); var batches = new List<(List<string> Command, List<string> Items)>();
        string? lastPredicate = null;
        FindNode Primary()
        {
            if (index >= args.Count) throw new FindError("expected an expression");
            string token = args[index++];
            string Arg() { if (index >= args.Count) throw new FindError($"missing argument to `{token}'"); return args[index++]; }
            int Number(string text, out char sign) { sign = text.Length > 0 && text[0] is '+' or '-' ? text[0] : '='; if (!int.TryParse(text.TrimStart('+', '-'), out int v)) throw new FindError($"invalid argument `{text}' to `{token}'"); return v; }
            bool Compare(double value, int target, char sign) => sign == '+' ? value > target : sign == '-' ? value < target : (int)value == target;
            if (token.StartsWith('-')) lastPredicate = token;
            switch (token)
            {
                case "(":
                {
                    var inner = Or(); if (index >= args.Count || args[index] != ")") throw new FindError("invalid expression; I was expecting to find a ')' somewhere but did not see one.");
                    index++; return inner;
                }
                case "!": case "-not": return new FindNot(Unary());
                case "-name": case "-iname":
                {
                    // The starting point is matched by the name it was given ("." stays ".").
                    var regex = ShellFs.GlobRegex(Arg(), token == "-iname");
                    return new FindTest((n, s, d) => regex.IsMatch(d == 0 ? (ShellFs.BaseName(s) is { Length: > 0 } b ? b : "/") : n.Name));
                }
                case "-path": case "-wholename": case "-ipath": { var regex = ShellFs.GlobRegex(Arg(), token == "-ipath"); return new FindTest((_, s, _) => regex.IsMatch(s)); }
                case "-type":
                {
                    string kind = Arg(); if (kind is not ("f" or "d" or "l")) throw new FindError($"Unknown argument to -type: {kind}");
                    return new FindTest((n, _, _) => kind == "d" ? n.IsDir : kind == "f" && !n.IsDir);
                }
                case "-mtime": case "-mmin": case "-ctime": case "-atime": case "-cmin": case "-amin":
                {
                    int target = Number(Arg(), out char sign); bool minutes = token.EndsWith("min");
                    return new FindTest((n, _, _) => { double age = minutes ? Math.Floor((Now - n.Modified).TotalMinutes) : Math.Floor((Now - n.Modified).TotalDays); return Compare(age, target, sign); });
                }
                case "-size":
                {
                    string text = Arg(); char unit = char.IsLetter(text[^1]) ? text[^1] : 'b'; int target = Number(char.IsLetter(text[^1]) ? text[..^1] : text, out char sign);
                    long bytes = unit switch { 'c' => 1, 'k' => 1024, 'M' => 1024 * 1024, 'G' => 1024L * 1024 * 1024, 'w' => 2, _ => 512 };
                    return new FindTest((n, _, _) => !n.IsDir && Compare(Math.Ceiling(n.Size / (double)bytes), target, sign));
                }
                case "-newer":
                {
                    string other = Arg(); var reference = Fs.Find(Resolve(other)) ?? throw new FindError($"‘{other}’: No such file or directory");
                    return new FindTest((n, _, _) => n.Modified > reference.Modified);
                }
                case "-empty": return new FindTest((n, _, _) => n.IsDir ? n.Children.Count == 0 : n.Size == 0);
                case "-user": { string user = Arg(); return new FindTest((n, _, _) => n.Owner == user); }
                case "-perm": { string perm = Arg(); string mode = ApplyMode("---------", perm.TrimStart('-', '/'), false); return new FindTest((n, _, _) => perm.StartsWith('-') ? mode.Select((ch, i) => ch == '-' || n.Mode[i] == ch).All(x => x) : n.Mode == mode); }
                case "-executable": return new FindTest((n, _, _) => n.Executable);
                case "-maxdepth": maxDepth = Number(Arg(), out _); return new FindTest((_, _, _) => true);
                case "-mindepth": minDepth = Number(Arg(), out _); return new FindTest((_, _, _) => true);
                case "-true": return new FindTest((_, _, _) => true);
                case "-false": return new FindTest((_, _, _) => false);
                case "-print": case "-print0": hasAction = true; return new FindTest((_, s, _) => { c.Line(s); return true; });
                case "-ls":
                    hasAction = true;
                    return new FindTest((n, s, _) => { c.Line($"{(n.Path.GetHashCode() & 0xFFFFF) + 131072,9} {(n.IsDir ? 4 : (n.Size + 4095) / 4096 * 4),6} {(n.IsDir ? 'd' : '-')}{n.Mode} {(n.IsDir ? 2 : 1),3} {n.Owner,-8} {n.Owner,-8} {n.Size,8} {Stamp(n.Modified)} {s}"); return true; });
                case "-delete":
                    hasAction = true; deleting = true;
                    return new FindTest((n, s, _) => { deletes.Add((n, s)); return true; });
                case "-exec": case "-ok": case "-execdir":
                {
                    hasAction = true; var command = new List<string>(); bool batch = false;
                    while (true)
                    {
                        if (index >= args.Count) throw new FindError($"missing argument to `{token}'");
                        string part = args[index++];
                        if (part == ";") break;
                        if (part == "+" && command.Count > 0 && command[^1] == "{}") { batch = true; command.RemoveAt(command.Count - 1); break; }
                        command.Add(part);
                    }
                    if (command.Count == 0) throw new FindError($"missing argument to `{token}'");
                    if (batch) { var entry = (command, new List<string>()); batches.Add(entry); return new FindTest((_, s, _) => { entry.Item2.Add(s); return true; }); }
                    return new FindTest((_, s, _) => RunNested(c, command.Select(p => p.Replace("{}", s)).ToList()) == 0);
                }
                case "-quit": return new FindTest((_, _, _) => true);
            }
            if (token.StartsWith('-')) throw new FindError($"unknown predicate `{token}'");
            throw new FindError($"paths must precede expression: `{token}'");
        }
        FindNode Unary() => Primary();
        FindNode And()
        {
            var left = Unary();
            while (index < args.Count && args[index] is not ("-o" or "-or" or ")"))
            {
                if (args[index] is "-a" or "-and") index++;
                left = new FindAnd(left, Unary());
            }
            return left;
        }
        FindNode Or()
        {
            var left = And();
            while (index < args.Count && args[index] is "-o" or "-or") { index++; left = new FindOr(left, And()); }
            return left;
        }
        FindNode expression;
        try
        {
            expression = index < args.Count ? Or() : new FindTest((_, _, _) => true);
            if (index < args.Count) throw new FindError(args[index] == ")" ? "invalid expression; you have too many ')'" : $"paths must precede expression: `{args[index]}'");
        }
        catch (FindError error)
        {
            c.Err.Append("find: " + error.Message + "\n");
            if (error.Message.StartsWith("paths must precede") && lastPredicate is "-name" or "-iname" or "-path")
            {
                c.Err.Append($"find: possible unquoted pattern after predicate `{lastPredicate}'?\n");
                c.Note("와일드카드(*)를 따옴표로 감싸세요 · 예: find . -name \"*.log\" (따옴표가 없으면 셸이 먼저 현재 폴더의 파일 이름으로 바꿔 버립니다)");
            }
            return 1;
        }
        int status = 0;
        foreach (var path in paths)
        {
            var root = Fs.Find(Resolve(path));
            if (root == null) { c.Err.Append($"find: ‘{path}’: No such file or directory\n"); status = 1; continue; }
            void Walk(ShellNode node, string shown, int depth)
            {
                if (!deleting && depth >= minDepth && depth <= maxDepth) { if (expression.Eval(node, shown, depth) && !hasAction) c.Line(shown); }
                if (node.IsDir && depth < maxDepth)
                    foreach (var child in node.Children.Values.ToList()) Walk(child, (shown.EndsWith('/') ? shown : shown + "/") + child.Name, depth + 1);
                if (deleting && depth >= minDepth && depth <= maxDepth) { if (expression.Eval(node, shown, depth) && !hasAction) c.Line(shown); }
            }
            Walk(root, path, 0);
        }
        foreach (var (command, items) in batches) if (items.Count > 0) RunNested(c, command.Concat(items).ToList());
        foreach (var (node, shown) in deletes)
        {
            if (node.Parent == null || shown is "." or "..") continue;
            if (node.IsDir && node.Children.Count > 0) { c.Err.Append($"find: cannot delete ‘{shown}’: Directory not empty\n"); status = 1; continue; }
            if (node.Parent.Owner != "user") { c.Err.Append($"find: cannot delete ‘{shown}’: Permission denied\n"); status = 1; continue; }
            ShellFs.Remove(node);
        }
        if (deletes.Count > 0) FixCwd(c);
        return status;
    }

    // ───────── tar, gzip ─────────

    private int Tar(Ctx c)
    {
        var args = c.Args.ToList();
        if (args.Count > 0 && !args[0].StartsWith('-') && Regex.IsMatch(args[0], "^[cxtzjvfC]+$")) args[0] = "-" + args[0];
        var o = Options(args, "cxtzjvpkO", "fC");
        if (o.Error != null) return Usage(c, o.Error, 2);
        int modes = (o.Has('c') ? 1 : 0) + (o.Has('x') ? 1 : 0) + (o.Has('t') ? 1 : 0);
        if (modes != 1) { c.Err.Append(modes == 0 ? "tar: You must specify one of the '-Acdtrux', '--delete' or '--test-label' options\n" : "tar: You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option\n"); c.Err.Append("Try 'tar --help' or 'tar --usage' for more information.\n"); return 2; }
        string? file = o.Value('f'); string baseDir = o.Value('C') is string dir ? Resolve(dir) : Cwd; bool verbose = o.Has('v');
        if (o.Value('C') is string changeDir && Fs.Find(baseDir) is not { IsDir: true }) { c.Err.Append($"tar: {changeDir}: Cannot open: No such file or directory\ntar: Error is not recoverable: exiting now\n"); return 2; }
        if (file == null) { c.Err.Append(o.Has('c') ? "tar: Refusing to write archive contents to terminal (missing -f option?)\n" : "tar: Refusing to read archive contents from terminal (missing -f option?)\n"); c.Err.Append("tar: Error is not recoverable: exiting now\n"); return 2; }
        string archivePath = Resolve(file);
        if (o.Has('c'))
        {
            if (o.Rest.Count == 0) { c.Err.Append("tar: Cowardly refusing to create an empty archive\nTry 'tar --help' or 'tar --usage' for more information.\n"); return 2; }
            var entries = new List<ShellArchiveEntry>(); int status = 0; bool stripped = false;
            foreach (var operand in o.Rest)
            {
                var node = Fs.Find(ShellFs.Combine(baseDir, operand));
                if (node == null) { c.Err.Append($"tar: {operand}: Cannot stat: No such file or directory\n"); status = 2; continue; }
                string prefix = operand.TrimEnd('/'); if (prefix.StartsWith('/')) { prefix = prefix.TrimStart('/'); stripped = true; }
                foreach (var item in node.Walk())
                {
                    if (item.Path == archivePath) continue;
                    string relative = item == node ? prefix : prefix + item.Path[node.Path.Length..];
                    entries.Add(new ShellArchiveEntry(relative, item.IsDir, item.Content, item.Mode, item.Modified, item.FixedSize));
                    if (verbose) c.Line(item.IsDir ? relative + "/" : relative);
                }
            }
            if (stripped) c.Err.Insert(0, "tar: Removing leading `/' from member names\n");
            if (Fs.Find(ShellFs.Parent(archivePath)) is not { IsDir: true }) { c.Err.Append($"tar: {file}: Cannot open: No such file or directory\ntar: Error is not recoverable: exiting now\n"); return 2; }
            long raw = entries.Sum(e => 512 + (e.FixedSize ?? Encoding.UTF8.GetByteCount(e.Content)));
            var archive = Fs.WriteFile(archivePath, "", Now); archive.Archive = entries; archive.Gzip = o.Has('z') || o.Has('j');
            archive.FixedSize = archive.Gzip ? 120 + raw / 5 : (raw + 10239) / 10240 * 10240;
            if (status != 0) c.Err.Append("tar: Exiting with failure status due to previous errors\n");
            return status;
        }
        var source = Fs.Find(archivePath);
        if (source == null) { c.Err.Append($"tar: {file}: Cannot open: No such file or directory\ntar: Error is not recoverable: exiting now\n"); return 2; }
        if (source.Corrupt) { c.Err.Append("gzip: stdin: invalid compressed data--format violated\ntar: Child returned status 1\ntar: Error is not recoverable: exiting now\n"); c.Note("이 압축 파일은 FTP ASCII 모드로 받아서 깨졌습니다 · ftp 에서 bin 으로 바꾼 뒤 다시 받으세요"); return 2; }
        if (source.Archive == null) { c.Err.Append(o.Has('z') ? "gzip: stdin: not in gzip format\ntar: Child returned status 1\ntar: Error is not recoverable: exiting now\n" : "tar: This does not look like a tar archive\ntar: Exiting with failure status due to previous errors\n"); return 2; }
        if (o.Has('z') && !source.Gzip) { c.Err.Append("gzip: stdin: not in gzip format\ntar: Child returned status 1\ntar: Error is not recoverable: exiting now\n"); return 2; }
        var wanted = o.Rest;
        var members = source.Archive.Where(e => wanted.Count == 0 || wanted.Any(w => e.Path == w.TrimEnd('/') || e.Path.StartsWith(w.TrimEnd('/') + "/"))).ToList();
        foreach (var w in wanted.Where(w => !source.Archive.Any(e => e.Path == w.TrimEnd('/') || e.Path.StartsWith(w.TrimEnd('/') + "/")))) c.Err.Append($"tar: {w}: Not found in archive\n");
        if (o.Has('t'))
        {
            foreach (var e in members) c.Line(verbose ? $"{(e.IsDir ? 'd' : '-')}{e.Mode} user/user {(e.IsDir ? 0 : e.FixedSize ?? Encoding.UTF8.GetByteCount(e.Content)),8} {e.Modified:yyyy-MM-dd HH:mm} {e.Path}{(e.IsDir ? "/" : "")}" : e.Path + (e.IsDir ? "/" : ""));
            return 0;
        }
        foreach (var e in members)
        {
            string target = ShellFs.Combine(baseDir, e.Path);
            if (e.IsDir) Fs.MakeDirs(target, e.Modified);
            else { var node = Fs.WriteFile(target, e.Content, e.Modified, e.Mode); node.FixedSize = e.FixedSize; node.Owner = "user"; }
            if (verbose) c.Line(e.Path + (e.IsDir ? "/" : ""));
        }
        return 0;
    }

    private int Gzip(Ctx c) => Gzip(c, false);

    private int Gzip(Ctx c, bool decompress)
    {
        var o = Options(c.Args, "dkvlfc");
        decompress |= o.Has('d');
        if (o.Rest.Count == 0) { c.Err.Append("gzip: compressed data not written to a terminal. Use -f to force compression.\n"); return 1; }
        int status = 0;
        if (o.Has('l')) c.Line("         compressed        uncompressed  ratio uncompressed_name");
        foreach (var arg in o.Rest)
        {
            string path = Resolve(arg); var node = Fs.Find(path);
            if (node == null && decompress && !arg.EndsWith(".gz")) node = Fs.Find(path += ".gz");
            if (node == null) { c.Err.Append($"gzip: {arg}: No such file or directory\n"); status = 1; continue; }
            if (node.IsDir) { c.Err.Append($"gzip: {arg} is a directory -- ignored\n"); status = 2; continue; }
            if (o.Has('l'))
            {
                long original = node.Compressed != null ? Encoding.UTF8.GetByteCount(node.Compressed) : node.Archive?.Sum(e => 512 + Encoding.UTF8.GetByteCount(e.Content)) ?? node.Size;
                c.Line($"{node.Size,19} {original,19} {(original == 0 ? 0 : 100.0 - node.Size * 100.0 / original),5:0.0}% {node.Path[..^3]}"); continue;
            }
            if (!decompress)
            {
                if (node.Name.EndsWith(".gz") || node.Name.EndsWith(".tgz")) { c.Err.Append($"gzip: {arg} already has .gz suffix -- unchanged\n"); status = 2; continue; }
                var parent = node.Parent!; string name = node.Name + ".gz";
                var packed = new ShellNode(name, false, node.Modified, node.Mode) { Owner = node.Owner, Gzip = true };
                if (node.Archive != null) { packed.Archive = node.Archive; packed.FixedSize = 120 + node.Size / 5; }
                else { packed.Compressed = node.Content; packed.FixedSize = 30 + Encoding.UTF8.GetByteCount(node.Content) / 4; }
                if (parent.Children.ContainsKey(name)) ShellFs.Remove(parent.Children[name]);
                parent.Add(packed); if (!o.Has('k')) ShellFs.Remove(node);
                if (o.Has('v')) c.Err.Append($"{arg}:\t{75.0:0.0}% -- replaced with {arg}.gz\n");
                continue;
            }
            if (!node.Name.EndsWith(".gz") && !node.Name.EndsWith(".tgz")) { c.Err.Append($"gzip: {arg}: unknown suffix -- ignored\n"); status = 2; continue; }
            if (node.Corrupt) { c.Err.Append($"gzip: {arg}: invalid compressed data--format violated\n"); status = 1; continue; }
            if (!node.Gzip) { c.Err.Append($"gzip: {arg}: not in gzip format\n"); status = 1; continue; }
            string plain = node.Name.EndsWith(".tgz") ? node.Name[..^4] + ".tar" : node.Name[..^3];
            var restored = new ShellNode(plain, false, node.Modified, node.Mode) { Owner = node.Owner };
            if (node.Archive != null) { restored.Archive = node.Archive; restored.FixedSize = (node.Archive.Sum(e => 512 + (e.FixedSize ?? Encoding.UTF8.GetByteCount(e.Content))) + 10239) / 10240 * 10240; }
            else restored.Content = node.Compressed ?? "";
            var dir = node.Parent!; if (dir.Children.ContainsKey(plain)) ShellFs.Remove(dir.Children[plain]);
            dir.Add(restored); if (!o.Has('k')) ShellFs.Remove(node);
        }
        return status;
    }

    private int Zcat(Ctx c)
    {
        int status = 0;
        foreach (var arg in c.Args.Where(a => !a.StartsWith('-')))
        {
            var node = Fs.Find(Resolve(arg));
            if (node == null) { c.Err.Append($"gzip: {arg}: No such file or directory\n"); status = 1; continue; }
            if (node.Compressed != null) c.Out.Append(node.Compressed);
            else if (node.Archive != null) c.Note($"{arg} 은(는) tar 묶음이라 내용이 바이너리입니다 · tar tzf {arg} 로 목록을 보세요");
            else { c.Err.Append($"gzip: {arg}: not in gzip format\n"); status = 1; }
        }
        return status;
    }
}
