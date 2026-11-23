using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

public enum ShellLineKind { Output, Note }
public sealed record ShellLine(string Text, ShellLineKind Kind = ShellLineKind.Output);
/// <summary>One simple command that ran, kept so lessons can check what was typed and what it printed.</summary>
public sealed record ShellRun(string Line, string Name, IReadOnlyList<string> Args, string Output, string Error, int Exit, string Where);

/// <summary>
/// A practice Linux shell. Files, processes and the FTP server are all simulated in memory:
/// nothing here reads or writes the real disk, starts a process or opens a network connection.
/// </summary>
public sealed partial class ShellEngine
{
    public static readonly DateTime Start = new(2026, 11, 23, 9, 30, 0);
    public const string User = "user", Host = "eqp-server", Home = "/home/user";

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<string, Func<Ctx, int>> commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    private readonly List<string> history = [], remoteHistory = [];
    private readonly List<ShellLine> output = [];
    private readonly List<ShellRun> runs = [];
    private readonly Stack<StringBuilder> captures = new();
    private readonly Queue<(string Prompt, bool Secret, Action<string> Handler)> asks = new();
    private List<string> positional = [];
    private string previousDir = Home;
    private int lastExit;
    private int historyIndex = -1, tabPresses;
    private string historyDraft = "", tabLine = "";
    private bool reverseSearch, reverseFailed;
    private string reverseQuery = "", reverseDraft = "";
    private int reverseIndex;
    private Action<string>? pending;
    private string pendingPrompt = "";
    private bool pendingSecret;
    private string? editingPath;

    public ShellEngine() { Register(); Reset(); }

    public event Action? Changed;
    /// <summary>Raised when "vi file" runs: (absolute path, current text). Call <see cref="EditorClosed"/> afterwards.</summary>
    public event Action<string, string>? EditRequested;

    public ShellFs Fs { get; private set; } = null!;
    public ShellFs RemoteFs { get; private set; } = null!;
    public string Cwd { get; private set; } = Home;
    public IReadOnlyList<ShellLine> Output => output;
    public IReadOnlyList<ShellRun> Runs => runs;
    public IReadOnlyList<string> History => history;
    public string Input { get; private set; } = "";
    public int Caret { get; private set; }
    public bool Secret => pending != null && pendingSecret;
    public bool Editing { get; private set; }
    public string? EditingPath => editingPath;
    public int LastExit => lastExit;
    /// <summary>Terminal width in characters, set by the view (used by ls columns).</summary>
    public int Columns { get; set; } = 100;
    public DateTime Now => Start.AddSeconds(Math.Floor(clock.Elapsed.TotalSeconds));

    // Practice counters, reset by Reset.
    public int TabCompletions { get; private set; }
    public int TabListings { get; private set; }
    public int HistoryRecalls { get; private set; }
    public int ReverseSearches { get; private set; }
    public int ClearCount { get; private set; }
    public int InterruptCount { get; private set; }
    public int EditorSaves { get; private set; }
    public int EditorOpens { get; private set; }

    public string Prompt =>
        reverseSearch ? $"({(reverseFailed ? "failed " : "")}reverse-i-search)`{reverseQuery}': "
        : pending != null ? pendingPrompt
        : remote is { } session ? (session.Kind == "sftp" ? "sftp> " : "ftp> ")
        : $"\u001b[01;32m{User}@{Host}\u001b[0m:\u001b[01;34m{DisplayPath(Cwd)}\u001b[0m$ ";

    public static string DisplayPath(string path) => path == Home ? "~" : path.StartsWith(Home + "/") ? "~" + path[Home.Length..] : path;

    /// <summary>Restores the sample server: files, processes, variables and aliases. History is kept.</summary>
    public void Reset(string? cwd = null)
    {
        Fs = ShellWorld.Local(Start); RemoteFs = ShellWorld.Remote(Start);
        processes.Clear(); processes.AddRange(ShellWorld.Processes());
        variables.Clear(); aliases.Clear(); output.Clear(); runs.Clear(); asks.Clear(); captures.Clear(); positional = [];
        remote = null; pending = null; Editing = false; editingPath = null; reverseSearch = false;
        Input = ""; Caret = 0; lastExit = 0; historyIndex = -1; tabPresses = 0;
        TabCompletions = TabListings = HistoryRecalls = ReverseSearches = ClearCount = InterruptCount = EditorSaves = EditorOpens = 0;
        foreach (var (key, value) in new Dictionary<string, string> { ["HOME"] = Home, ["USER"] = User, ["LOGNAME"] = User, ["HOSTNAME"] = Host, ["SHELL"] = "/bin/bash", ["LANG"] = "C.UTF-8", ["PATH"] = "/usr/local/bin:/usr/bin:/bin:/opt/eqp/bin", ["EQP_HOME"] = "/opt/eqp", ["TERM"] = "xterm" })
            variables[key] = value;
        Cwd = previousDir = Home;
        // ~/.bashrc defines the familiar aliases (ll, la, grep --color).
        if (Fs.Find(Home + "/.bashrc") is { } bashrc) foreach (var line in bashrc.Content.Split('\n')) { var m = Regex.Match(line, @"^\s*alias\s+([\w.-]+)='([^']*)'"); if (m.Success) aliases[m.Groups[1].Value] = m.Groups[2].Value; }
        if (cwd != null && Fs.Find(Resolve(cwd)) is { IsDir: true } start) Cwd = start.Path;
        Note("연습용 가상 서버입니다 · 실제 장비나 네트워크에 연결되지 않으며 파일은 메모리에만 있습니다");
        Print($"Last login: {Start.AddMinutes(-18):ddd MMM d HH:mm:ss yyyy} from practice", ShellLineKind.Output);
        Changed?.Invoke();
    }

    // ───────────────────────── Input line ─────────────────────────

    /// <summary>Types text into the input line (printable characters only).</summary>
    public void Type(string text) { foreach (var key in ViEngine.Tokens(text)) Key(key); }
    /// <summary>Types a line and presses Enter.</summary>
    public void Run(string line) { Type(line); Key("<CR>"); }

    public void Key(string key)
    {
        if (Editing) return;
        if (reverseSearch && ReverseKey(key)) { Changed?.Invoke(); return; }
        if (key != "<Tab>") tabPresses = 0;
        switch (key)
        {
            case "<CR>": Submit(); break;
            case "<BS>": case "<C-h>": if (Caret > 0) { Input = Input.Remove(Caret - 1, 1); Caret--; } break;
            case "<Del>": if (Caret < Input.Length) Input = Input.Remove(Caret, 1); break;
            case "<Left>": case "<C-b>": Caret = Math.Max(0, Caret - 1); break;
            case "<Right>": case "<C-f>": Caret = Math.Min(Input.Length, Caret + 1); break;
            case "<Home>": case "<C-a>": Caret = 0; break;
            case "<End>": case "<C-e>": Caret = Input.Length; break;
            case "<C-u>": Input = Input[Caret..]; Caret = 0; break;
            case "<C-k>": Input = Input[..Caret]; break;
            case "<C-w>":
            {
                int c = Caret; while (c > 0 && Input[c - 1] == ' ') c--; while (c > 0 && Input[c - 1] != ' ') c--;
                Input = Input.Remove(c, Caret - c); Caret = c; break;
            }
            case "<Up>": case "<C-p>": Recall(true); break;
            case "<Down>": case "<C-n>": Recall(false); break;
            case "<Tab>": Complete(); break;
            case "<C-c>": Interrupt(); break;
            case "<C-l>": output.Clear(); ClearCount++; break;
            case "<C-d>": if (Input.Length == 0) EndOfInput(); else if (Caret < Input.Length) Input = Input.Remove(Caret, 1); break;
            case "<C-r>": if (pending == null) { reverseSearch = true; reverseQuery = ""; reverseIndex = 0; reverseFailed = false; reverseDraft = Input; ReverseSearches++; } break;
            default:
                if (key.Length == 1 && !char.IsControl(key[0]) || key.Length == 2 && char.IsSurrogatePair(key, 0)) { Input = Input.Insert(Caret, key); Caret += key.Length; }
                break;
        }
        Changed?.Invoke();
    }

    private bool ReverseKey(string key)
    {
        switch (key)
        {
            case "<C-r>": reverseIndex++; ReverseFind(); return true;
            case "<BS>": if (reverseQuery.Length > 0) reverseQuery = reverseQuery[..^1]; reverseIndex = 0; ReverseFind(); return true;
            case "<C-g>": reverseSearch = false; Input = reverseDraft; Caret = Input.Length; return true;
            case "<Esc>": reverseSearch = false; Caret = Input.Length; return true;
            case "<C-c>": reverseSearch = false; return false;
        }
        if (key.Length == 1 && !char.IsControl(key[0]) || key.Length == 2 && char.IsSurrogatePair(key, 0)) { reverseQuery += key; reverseIndex = 0; ReverseFind(); return true; }
        reverseSearch = false; Caret = Input.Length; return false;
    }

    private void ReverseFind()
    {
        var matches = history.AsEnumerable().Reverse().Where(h => reverseQuery.Length > 0 && h.Contains(reverseQuery, StringComparison.Ordinal)).Distinct().ToList();
        if (matches.Count == 0) { reverseFailed = reverseQuery.Length > 0; return; }
        reverseFailed = false; reverseIndex = Math.Min(reverseIndex, matches.Count - 1);
        Input = matches[reverseIndex]; Caret = Math.Max(0, Input.IndexOf(reverseQuery, StringComparison.Ordinal));
    }

    private void Recall(bool older)
    {
        if (pending != null) return;
        var list = remote != null ? remoteHistory : history;
        if (list.Count == 0) return;
        if (historyIndex < 0) historyDraft = Input;
        int next = Math.Min(historyIndex + (older ? 1 : -1), list.Count - 1);
        if (next < 0) { historyIndex = -1; Input = historyDraft; Caret = Input.Length; return; }
        historyIndex = next; Input = list[list.Count - 1 - next]; Caret = Input.Length; HistoryRecalls++;
    }

    private void Interrupt()
    {
        Echo(Prompt + (Secret ? "" : Input) + "^C");
        Input = ""; Caret = 0; historyIndex = -1; lastExit = 130; InterruptCount++;
        if (pending != null)
        {
            pending = null; asks.Clear();
            if (remote is { LoggedIn: false, Kind: not "ftp" }) remote = null;
        }
    }

    private void EndOfInput()
    {
        if (pending != null) { Interrupt(); return; }
        if (remote != null) { Echo(Prompt); RemoteCommand("bye"); return; }
        Echo(Prompt);
        Note("logout · 연습 터미널은 닫히지 않습니다 (실제 서버에서는 Ctrl+D 로 접속이 끊깁니다)");
    }

    private void Submit()
    {
        string line = Input; Input = ""; Caret = 0; historyIndex = -1;
        if (pending is { } handler)
        {
            Echo(pendingPrompt + (pendingSecret ? "" : line));
            pending = null; handler(line); NextAsk();
            return;
        }
        Echo(Prompt + line);
        if (remote != null)
        {
            if (line.Trim().Length > 0 && (remoteHistory.Count == 0 || remoteHistory[^1] != line)) remoteHistory.Add(line);
            RemoteCommand(line); NextAsk(); return;
        }
        var expanded = ExpandHistory(line); if (expanded == null) return;
        if (expanded != line) Print(expanded);
        if (expanded.Trim().Length > 0 && (history.Count == 0 || history[^1] != expanded)) { history.Add(expanded); if (history.Count > 500) history.RemoveAt(0); }
        ExecuteLine(expanded);
        NextAsk();
    }

    private string? ExpandHistory(string line)
    {
        if (!line.Contains('!')) return line;
        string? missing = null;
        string result = Regex.Replace(line, @"(?<![\w'""\\])!(!|-?\d+|[A-Za-z][\w.-]*)", m =>
        {
            string token = m.Groups[1].Value; string? found = null;
            if (token == "!") found = history.LastOrDefault();
            else if (int.TryParse(token, out int number)) found = number > 0 ? history.ElementAtOrDefault(number - 1) : history.ElementAtOrDefault(history.Count + number);
            else found = history.LastOrDefault(h => h.StartsWith(token, StringComparison.Ordinal));
            if (found == null) { missing ??= m.Value; return m.Value; }
            return found;
        });
        if (missing != null) { Print($"bash: {missing}: event not found"); lastExit = 1; return null; }
        return result;
    }

    private void Ask(string prompt, bool secret, Action<string> handler)
    {
        if (pending == null) { pending = handler; pendingPrompt = prompt; pendingSecret = secret; }
        else asks.Enqueue((prompt, secret, handler));
    }

    private void NextAsk()
    {
        if (pending == null && asks.Count > 0) { var next = asks.Dequeue(); pending = next.Handler; pendingPrompt = next.Prompt; pendingSecret = next.Secret; }
    }

    private void Complete()
    {
        if (pending != null) return;
        tabPresses = tabLine == Input ? tabPresses + 1 : 1; tabLine = Input;
        string before = Input[..Caret];
        int start = before.Length; while (start > 0 && !" \t|;&<>".Contains(before[start - 1])) start--;
        string word = before[start..], head = before[..start].TrimEnd();
        bool first = head.Length == 0 || head.EndsWith('|') || head.EndsWith(';') || head.EndsWith('&') || remote != null && head.Length == 0;
        var candidates = new List<string>();
        if (first && !word.Contains('/'))
            candidates = (remote != null ? RemoteCommandNames : commands.Keys.Concat(aliases.Keys)).Where(c => c.StartsWith(word, StringComparison.Ordinal)).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        else
        {
            var fs = remote is { LoggedIn: true } && head.Length > 0 && !head.StartsWith("lcd") && !head.StartsWith("put") && !head.StartsWith("mput") && !head.StartsWith("!") ? RemoteFs : Fs;
            string baseDir = fs == RemoteFs ? remote!.Cwd : Cwd;
            string dirPart = word.Contains('/') ? word[..(word.LastIndexOf('/') + 1)] : "", namePart = word[dirPart.Length..];
            string dirAbs = dirPart.Length == 0 ? baseDir : ShellFs.Combine(baseDir, dirPart.StartsWith('~') && fs == Fs ? Home + dirPart[1..] : dirPart);
            if (fs.Find(dirAbs) is { IsDir: true } dir)
                candidates = dir.Children.Values.Where(c => c.Name.StartsWith(namePart, StringComparison.Ordinal) && (namePart.StartsWith('.') || !c.Name.StartsWith('.')))
                    .Select(c => dirPart + c.Name + (c.IsDir ? "/" : "")).ToList();
        }
        if (candidates.Count == 0) return;
        if (candidates.Count == 1)
        {
            string done = candidates[0] + (candidates[0].EndsWith('/') ? "" : " ");
            Input = Input[..start] + done + Input[Caret..]; Caret = start + done.Length; TabCompletions++; tabLine = Input; tabPresses = 0; return;
        }
        string common = candidates.Aggregate((a, b) => { int n = 0; while (n < a.Length && n < b.Length && a[n] == b[n]) n++; return a[..n]; });
        // Like bash: complete the shared part first; the next ambiguous Tab beeps, the one after lists the choices.
        if (common.Length > word.Length) { Input = Input[..start] + common + Input[Caret..]; Caret = start + common.Length; TabCompletions++; tabLine = Input; tabPresses = 0; return; }
        if (tabPresses >= 2)
        {
            Echo(Prompt + Input);
            Print(Columnize(candidates.Select(c => c.TrimEnd('/').Split('/').Last() + (c.EndsWith('/') ? "/" : "")).ToList()).TrimEnd('\n'));
            TabListings++; tabPresses = 0;
        }
    }

    // ───────────────────────── Terminal output ─────────────────────────

    private void Echo(string text) => Add(new ShellLine(text));
    private void Add(ShellLine line) { output.Add(line); if (output.Count > 3000) output.RemoveRange(0, output.Count - 3000); }
    private void Print(string text, ShellLineKind kind = ShellLineKind.Output)
    {
        if (captures.Count > 0 && kind == ShellLineKind.Output) { captures.Peek().Append(text).Append('\n'); return; }
        foreach (var line in text.Split('\n')) Add(new ShellLine(line.TrimEnd('\r'), kind));
    }
    private void Note(string text) => Print("※ " + text, ShellLineKind.Note);
    private void Emit(string text)
    {
        if (text.Length == 0) return;
        if (captures.Count > 0) { captures.Peek().Append(text); return; }
        Print(text.EndsWith('\n') ? text[..^1] : text);
    }

    public static string StripAnsi(string text) => Regex.Replace(text, "\u001b\\[[0-9;]*m", "");

    /// <summary>Column layout like ls on a terminal.</summary>
    private string Columnize(IReadOnlyList<string> names, IReadOnlyList<string>? shown = null)
    {
        if (names.Count == 0) return "";
        int width = names.Max(DisplayWidth) + 2, perLine = Math.Max(1, Math.Min(names.Count, Columns / Math.Max(1, width)));
        int rows = (names.Count + perLine - 1) / perLine; var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < perLine; c++)
            {
                int index = c * rows + r; if (index >= names.Count) break;
                string name = names[index]; bool lastColumn = (c + 1) * rows + r >= names.Count;
                sb.Append(shown != null ? shown[index] : name);
                if (!lastColumn) sb.Append(' ', width - DisplayWidth(name));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in StripAnsi(text)) width += c >= 0x1100 && (c <= 0x115F || c >= 0x2E80 && c <= 0xA4CF || c >= 0xAC00 && c <= 0xD7A3 || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFF60 || c >= 0xFFE0 && c <= 0xFFE6) ? 2 : 1;
        return width;
    }

    // ───────────────────────── Parsing ─────────────────────────

    private sealed class ShellSyntax(string message) : Exception(message);
    private sealed record Item(string Text, bool IsOp, string? Glob = null);
    private sealed class Cmd { public List<Item> Words { get; } = []; public List<(string Op, Item? Target)> Redirects { get; } = []; }

    private static string EscapeGlob(string text) { var sb = new StringBuilder(); foreach (char c in text) { if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\'); sb.Append(c); } return sb.ToString(); }

    private List<Item> Lex(string s, int depth, bool expand = true)
    {
        var items = new List<Item>(); int i = 0;
        char Peek(int at) => at < s.Length ? s[at] : '\0';
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t') { i++; continue; }
            if (c == '#') break;
            if (c == '|') { if (Peek(i + 1) == '|') { items.Add(new("||", true)); i += 2; } else { items.Add(new("|", true)); i++; } continue; }
            if (c == '&') { if (Peek(i + 1) == '&') { items.Add(new("&&", true)); i += 2; } else if (Peek(i + 1) == '>') { items.Add(new("&>", true)); i += 2; } else { items.Add(new("&", true)); i++; } continue; }
            if (c == ';') { items.Add(new(";", true)); i++; continue; }
            if (c == '>') { if (Peek(i + 1) == '>') { items.Add(new(">>", true)); i += 2; } else { items.Add(new(">", true)); i++; } continue; }
            if (c == '<') { items.Add(new("<", true)); i++; continue; }
            if (c == '2' && Peek(i + 1) == '>')
            {
                if (s[(i + 2)..].StartsWith("&1")) { items.Add(new("2>&1", true)); i += 4; }
                else if (Peek(i + 2) == '>') { items.Add(new("2>>", true)); i += 3; }
                else { items.Add(new("2>", true)); i += 2; }
                continue;
            }
            var text = new StringBuilder(); var glob = new StringBuilder(); bool hasGlob = false, wordStart = true;
            void Literal(string value) { text.Append(value); glob.Append(EscapeGlob(value)); }
            while (i < s.Length)
            {
                c = s[i];
                if (c is ' ' or '\t' or '|' or '&' or ';' or '>' or '<') break;
                if (c == '\'')
                {
                    int end = s.IndexOf('\'', i + 1);
                    if (end < 0) throw new ShellSyntax("unexpected EOF while looking for matching `''");
                    Literal(s[(i + 1)..end]); i = end + 1; wordStart = false; continue;
                }
                if (c == '"')
                {
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`') { Literal(s[i + 1].ToString()); i += 2; continue; }
                        if (s[i] == '$') { i = Dollar(s, i, Literal, depth, expand); continue; }
                        if (s[i] == '`') { i = Backtick(s, i, Literal, depth, expand); continue; }
                        Literal(s[i].ToString()); i++;
                    }
                    if (i >= s.Length) throw new ShellSyntax("unexpected EOF while looking for matching `\"'");
                    i++; wordStart = false; continue;
                }
                if (c == '\\') { if (i + 1 < s.Length) { Literal(s[i + 1].ToString()); i += 2; } else i++; wordStart = false; continue; }
                if (c == '$') { i = Dollar(s, i, Literal, depth, expand); wordStart = false; continue; }
                if (c == '`') { i = Backtick(s, i, Literal, depth, expand); wordStart = false; continue; }
                if (c == '~' && wordStart && (i + 1 >= s.Length || s[i + 1] is '/' or ' ' or '\t')) { Literal(Home); i++; wordStart = false; continue; }
                if (c is '*' or '?' or '[') { text.Append(c); glob.Append(c); hasGlob = true; i++; wordStart = false; continue; }
                text.Append(c); glob.Append(c); i++; wordStart = false;
                // NAME=~/x expands the tilde after '=' like bash
                if (c == '=' && i < s.Length && s[i] == '~' && Regex.IsMatch(text.ToString(), @"^[A-Za-z_]\w*=$")) wordStart = true;
            }
            items.Add(new Item(text.ToString(), false, hasGlob ? glob.ToString() : null));
        }
        return items;
    }

    private int Dollar(string s, int i, Action<string> append, int depth, bool expand = true)
    {
        if (i + 1 >= s.Length) { append("$"); return i + 1; }
        char n = s[i + 1];
        if (n == '(')
        {
            int level = 0, j = i + 1;
            for (; j < s.Length; j++) { if (s[j] == '(') level++; else if (s[j] == ')') { level--; if (level == 0) break; } }
            if (j >= s.Length) throw new ShellSyntax("unexpected EOF while looking for matching `)'");
            append(expand ? Capture(s[(i + 2)..j], depth).TrimEnd('\n') : ""); return j + 1;
        }
        if (n == '{')
        {
            int end = s.IndexOf('}', i + 2); if (end < 0) throw new ShellSyntax("bad substitution");
            append(Variable(s[(i + 2)..end])); return end + 1;
        }
        if (n is '?' or '$' or '#' or '@' or '*' || char.IsAsciiDigit(n)) { append(Variable(n.ToString())); return i + 2; }
        if (char.IsLetter(n) || n == '_')
        {
            int j = i + 1; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
            append(Variable(s[(i + 1)..j])); return j;
        }
        append("$"); return i + 1;
    }

    private int Backtick(string s, int i, Action<string> append, int depth, bool expand = true)
    {
        int end = s.IndexOf('`', i + 1); if (end < 0) throw new ShellSyntax("unexpected EOF while looking for matching ``'");
        append(expand ? Capture(s[(i + 1)..end], depth).TrimEnd('\n') : ""); return end + 1;
    }

    private string Variable(string name) => name switch
    {
        "?" => lastExit.ToString(),
        "$" => "3355",
        "#" => positional.Count.ToString(),
        "@" or "*" => string.Join(" ", positional),
        "PWD" => Cwd,
        "OLDPWD" => previousDir,
        "RANDOM" => Random.Shared.Next(32768).ToString(),
        _ when name.Length == 1 && char.IsAsciiDigit(name[0]) => name == "0" ? "bash" : positional.ElementAtOrDefault(name[0] - '1') ?? "",
        _ => variables.GetValueOrDefault(name, "")
    };

    private string Capture(string command, int depth)
    {
        var buffer = new StringBuilder(); captures.Push(buffer);
        try { ExecuteLine(command, depth + 1); } finally { captures.Pop(); }
        return buffer.ToString();
    }

    private static List<(List<Cmd> Pipeline, string Next)> Parse(List<Item> items)
    {
        var result = new List<(List<Cmd>, string)>(); var pipeline = new List<Cmd>(); var cmd = new Cmd();
        for (int k = 0; k < items.Count; k++)
        {
            var item = items[k];
            if (!item.IsOp) { cmd.Words.Add(item); continue; }
            switch (item.Text)
            {
                case ">": case ">>": case "<": case "2>": case "2>>": case "&>":
                    if (k + 1 >= items.Count || items[k + 1].IsOp) throw new ShellSyntax($"syntax error near unexpected token `{(k + 1 < items.Count ? items[k + 1].Text : "newline")}'");
                    cmd.Redirects.Add((item.Text, items[++k])); break;
                case "2>&1": cmd.Redirects.Add(("2>&1", null)); break;
                case "|":
                    if (cmd.Words.Count == 0) throw new ShellSyntax("syntax error near unexpected token `|'");
                    pipeline.Add(cmd); cmd = new Cmd(); break;
                default:
                    if (cmd.Words.Count == 0 && cmd.Redirects.Count == 0) { if (pipeline.Count > 0 || item.Text != ";") throw new ShellSyntax($"syntax error near unexpected token `{item.Text}'"); break; }
                    pipeline.Add(cmd); result.Add((pipeline, item.Text)); pipeline = []; cmd = new Cmd(); break;
            }
        }
        if (cmd.Words.Count > 0 || cmd.Redirects.Count > 0) pipeline.Add(cmd);
        else if (pipeline.Count > 0) throw new ShellSyntax("syntax error near unexpected token `|'");
        if (pipeline.Count > 0) result.Add((pipeline, ";"));
        return result;
    }

    // ───────────────────────── Execution ─────────────────────────

    private int ExecuteLine(string line, int depth = 0)
    {
        if (depth > 8) { Print("bash: 실행이 너무 깊게 중첩되었습니다"); return 1; }
        // Check the whole line's syntax first, then expand each part just before it runs,
        // so "$?" and "$(...)" see the results of the commands before them.
        List<(string Text, string Next)> parts;
        try { parts = SplitSequence(line); Parse(Lex(line, depth, expand: false)); }
        catch (ShellSyntax error) { Print("bash: " + error.Message); lastExit = 2; return 2; }
        int status = lastExit; string previous = ";";
        foreach (var (text, next) in parts)
        {
            bool skip = previous == "&&" && status != 0 || previous == "||" && status == 0;
            previous = next;
            if (skip) continue;
            if (next == "&") Note("연습 터미널은 & (백그라운드 실행)도 바로 실행합니다");
            List<(List<Cmd> Pipeline, string Next)> sequence;
            try { sequence = Parse(Lex(text, depth)); }
            catch (ShellSyntax error) { Print("bash: " + error.Message); lastExit = 2; return 2; }
            foreach (var (pipeline, _) in sequence) { status = RunPipeline(pipeline, line, depth); lastExit = status; }
            if (Editing || pending != null || remote != null) break;
        }
        return status;
    }

    /// <summary>Splits a command line at ; &amp;&amp; || &amp; outside quotes, $( ) and backticks.</summary>
    private static List<(string Text, string Next)> SplitSequence(string s)
    {
        var result = new List<(string, string)>(); int start = 0, depth = 0; char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == '\\' && quote == '"' && i + 1 < s.Length) { i++; continue; } if (c == quote) quote = '\0'; continue; }
            if (c == '\\') { i++; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '#' && depth == 0 && (i == 0 || char.IsWhiteSpace(s[i - 1]))) break;
            if (c == '$' && i + 1 < s.Length && s[i + 1] == '(') { depth++; i++; continue; }
            if (c == '(' && depth > 0) { depth++; continue; }
            if (c == ')' && depth > 0) { depth--; continue; }
            if (depth > 0) continue;
            string? op = c == ';' ? ";" : c == '&' && i + 1 < s.Length && s[i + 1] == '&' ? "&&" : c == '|' && i + 1 < s.Length && s[i + 1] == '|' ? "||" : c == '&' && !(i + 1 < s.Length && s[i + 1] == '>') && !(i > 0 && s[i - 1] == '>') ? "&" : null;
            if (op == null) continue;
            result.Add((s[start..i], op)); i += op.Length - 1; start = i + 1;
        }
        if (start < s.Length) result.Add((s[start..], ";"));
        return result.Where(r => r.Item1.Trim().Length > 0 && !r.Item1.TrimStart().StartsWith('#')).ToList();
    }

    private List<string> Expand(IEnumerable<Item> words)
    {
        var result = new List<string>();
        foreach (var word in words)
        {
            if (word.Glob != null) { var matches = Fs.Glob(word.Glob, Cwd, Home); if (matches.Count > 0) { result.AddRange(matches); continue; } }
            result.Add(word.Text);
        }
        return result;
    }

    private int RunPipeline(List<Cmd> pipeline, string line, int depth)
    {
        string? input = null; int status = 0;
        for (int k = 0; k < pipeline.Count; k++)
        {
            var cmd = pipeline[k]; bool last = k == pipeline.Count - 1;
            var args = Expand(cmd.Words);
            if (args.Count > 0 && aliases.TryGetValue(args[0], out var alias) && depth < 4)
            {
                try { args = Expand(Lex(alias, depth).Where(x => !x.IsOp)).Concat(args.Skip(1)).ToList(); } catch (ShellSyntax) { }
            }
            string? outFile = null, errFile = null; bool append = false, errAppend = false, errToOut = false; string? stdin = k == 0 ? null : input; bool failed = false;
            foreach (var (op, target) in cmd.Redirects)
            {
                string path = target?.Text ?? "";
                switch (op)
                {
                    case ">": outFile = path; append = false; break;
                    case ">>": outFile = path; append = true; break;
                    case "&>": outFile = path; append = false; errToOut = true; break;
                    case "2>": errFile = path; errAppend = false; break;
                    case "2>>": errFile = path; errAppend = true; break;
                    case "2>&1": errToOut = true; break;
                    case "<":
                        var node = Fs.Find(Resolve(path));
                        if (node == null || node.IsDir) { Print($"bash: {path}: {(node == null ? "No such file or directory" : "Is a directory")}"); failed = true; }
                        else stdin = node.Content;
                        break;
                }
            }
            if (failed) { status = 1; input = ""; continue; }
            var ctx = new Ctx(this, args.Count > 0 ? args[0] : "", args.Skip(1).ToList(), stdin, last && outFile == null && captures.Count == 0, depth, k > 0 || !last);
            status = args.Count == 0 ? 0 : Dispatch(ctx);
            string outText = ctx.Out.ToString(), errText = ctx.Err.ToString();
            runs.Add(new ShellRun(line, ctx.Name, ctx.Args, StripAnsi(outText), errText, status, remote != null ? remote.Kind : "shell"));
            if (runs.Count > 2000) runs.RemoveRange(0, 500);
            if (errToOut) { outText += errText; errText = ""; }
            if (errFile != null && errFile != "/dev/null") { var problem = WriteRedirect(errFile, errText, errAppend); if (problem != null) Print(problem); errText = ""; }
            else if (errFile == "/dev/null") errText = "";
            if (errText.Length > 0) Print(errText.TrimEnd('\n'));
            foreach (var note in ctx.Notes) Note(note);
            if (outFile != null)
            {
                if (outFile != "/dev/null") { var problem = WriteRedirect(outFile, StripAnsi(outText), append); if (problem != null) { Print(problem); status = 1; } }
                outText = "";
            }
            if (last) Emit(outText); else input = StripAnsi(outText);
            if (Editing || pending != null && k < pipeline.Count - 1) break;
        }
        return status;
    }

    private string? WriteRedirect(string path, string text, bool append)
    {
        string absolute = Resolve(path); var node = Fs.Find(absolute);
        if (node is { IsDir: true }) return $"bash: {path}: Is a directory";
        var parent = Fs.Find(ShellFs.Parent(absolute));
        if (parent is not { IsDir: true }) return $"bash: {path}: No such file or directory";
        if (append && node != null) { node.Content += text; node.Modified = Now; }
        else Fs.WriteFile(absolute, text, Now);
        return null;
    }

    private int Dispatch(Ctx ctx)
    {
        string name = ctx.Name;
        var assignment = Regex.Match(name, @"^([A-Za-z_]\w*)=(.*)$", RegexOptions.Singleline);
        if (assignment.Success && ctx.Args.Count == 0) { variables[assignment.Groups[1].Value] = assignment.Groups[2].Value; return 0; }
        if (name.Contains('/')) return RunScript(ctx, true);
        if (commands.TryGetValue(name, out var handler))
        {
            if (ctx.Args.Count == 1 && ctx.Args[0] == "--help" && Manual.TryGetValue(name, out var help)) { ctx.Line(help); return 0; }
            return handler(ctx);
        }
        ctx.Err.Append($"{name}: command not found\n");
        string? close = commands.Keys.Where(c => c.Length > 1).OrderBy(c => Distance(c, name)).FirstOrDefault(c => Distance(c, name) <= (name.Length > 3 ? 2 : 1));
        if (close != null) ctx.Note($"혹시 '{close}' 를 입력하려던 것인가요? (리눅스 명령은 대소문자와 철자를 정확히 구분합니다)");
        return 127;
    }

    /// <summary>Edit distance where swapping two neighbouring letters counts as one typo.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i; for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Runs a script file line by line. Scripts run in a subshell, so cd inside them does not move the prompt.</summary>
    private int RunScript(Ctx ctx, bool needsExecute, bool source = false)
    {
        string shown = ctx.Name; var node = Fs.Find(Resolve(shown));
        if (node == null) { ctx.Err.Append($"bash: {shown}: No such file or directory\n"); return 127; }
        if (node.IsDir) { ctx.Err.Append($"bash: {shown}: Is a directory\n"); return 126; }
        if (needsExecute && !node.Executable)
        {
            ctx.Err.Append($"bash: {shown}: Permission denied\n");
            ctx.Note("실행 권한(x)이 없습니다 · chmod +x " + shown.Split('/').Last() + " 로 권한을 주거나 bash " + shown + " 로 실행하세요");
            return 126;
        }
        if (node.Binary) { ctx.Note("연습 서버의 프로그램은 실제로 실행되지 않습니다"); return 0; }
        var savedPositional = positional; var savedCwd = Cwd; positional = ctx.Args.ToList();
        var buffer = new StringBuilder(); captures.Push(buffer); int status = 0;
        try
        {
            foreach (var raw in node.Content.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
                status = ExecuteLine(line, ctx.Depth + 1);
                if (Editing || pending != null) break;
            }
        }
        finally { captures.Pop(); positional = savedPositional; if (!source) { if (Fs.Find(savedCwd) != null) Cwd = savedCwd; } }
        ctx.Out.Append(buffer);
        return status;
    }

    internal string Resolve(string path)
    {
        if (path == "~") return Home;
        if (path.StartsWith("~/")) return ShellFs.Normalize(Home + path[1..]);
        return ShellFs.Combine(Cwd, path.Length == 0 ? "." : path);
    }

    // ───────────────────────── Editor bridge ─────────────────────────

    private void OpenEditor(string path, string content) { Editing = true; editingPath = path; EditorOpens++; EditRequested?.Invoke(path, content); }

    /// <summary>":w" from the embedded vi. Returns an error message, or null when saved.</summary>
    public string? SaveFromEditor(string name, string text)
    {
        string path = editingPath != null && (name == ShellFs.BaseName(editingPath) || name == editingPath) ? editingPath : name.StartsWith('/') ? ShellFs.Normalize(name) : Resolve(name);
        var node = Fs.Find(path);
        if (node is { IsDir: true }) return $"E502: \"{name}\" 은 디렉터리입니다";
        if (Fs.Find(ShellFs.Parent(path)) is not { IsDir: true }) return $"E212: 저장할 수 없습니다 (폴더가 없습니다: {ShellFs.Parent(path)})";
        if (node?.Binary == true) return "E212: 바이너리 파일은 연습 편집기로 저장할 수 없습니다";
        string content = text.Length > 0 && !text.EndsWith('\n') ? text + "\n" : text;
        Fs.WriteFile(path, content, Now); EditorSaves++;
        return null;
    }

    public void EditorClosed() { Editing = false; editingPath = null; Changed?.Invoke(); }

    // ───────────────────────── Lesson helpers ─────────────────────────

    public bool Ran(string name, Func<ShellRun, bool>? condition = null) => runs.Any(r => r.Name == name && (condition == null || condition(r)));
    public bool Exists(string path) => Fs.Find(Resolve(path)) != null;
    public string? ReadFile(string path) => Fs.Find(Resolve(path)) is { IsDir: false } node ? node.Content : null;
    public bool IsExecutable(string path) => Fs.Find(Resolve(path)) is { IsDir: false, Executable: true };
    public bool Typed(string fragment) => history.Any(h => h.Contains(fragment, StringComparison.Ordinal)) || remoteHistory.Any(h => h.Contains(fragment, StringComparison.Ordinal));
    public string Screen => string.Join("\n", output.Select(l => StripAnsi(l.Text)));

    private sealed class Ctx(ShellEngine shell, string name, List<string> args, string? stdin, bool tty, int depth, bool piped)
    {
        public ShellEngine Shell { get; } = shell;
        public string Name { get; } = name;
        public List<string> Args { get; } = args;
        public string? Stdin { get; } = stdin;
        /// <summary>True when output goes straight to the terminal (colors and columns allowed).</summary>
        public bool Tty { get; } = tty;
        public int Depth { get; } = depth;
        public bool Piped { get; } = piped;
        public StringBuilder Out { get; } = new();
        public StringBuilder Err { get; } = new();
        public List<string> Notes { get; } = [];
        public void Line(string text) => Out.Append(text).Append('\n');
        public int Fail(string message, int code = 1) { Err.Append(Name).Append(": ").Append(message).Append('\n'); return code; }
        public void Note(string text) => Notes.Add(text);
    }
}
