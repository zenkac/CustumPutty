using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

public enum ViMode { Normal, Insert, Visual, VisualLine, VisualBlock, Command }

/// <summary>One editor window (vim "window") showing the shared practice buffer.</summary>
public sealed class ViWindow
{
    internal ViWindow(int id) { Id = id; }
    public int Id { get; }
    public int Row { get; internal set; }
    public int Col { get; internal set; }
    public int Top { get; internal set; }
    /// <summary>Visible text rows; supplied by the view so scrolling follows the cursor.</summary>
    public int Height { get; internal set; } = 20;
    internal int Want;
}

public abstract class ViLayout { }
public sealed class ViLeaf(ViWindow window) : ViLayout { public ViWindow Window { get; } = window; }
/// <summary>Vertical = side by side (:vsplit); otherwise stacked (:split).</summary>
public sealed class ViSplit(bool vertical) : ViLayout { public bool Vertical { get; } = vertical; public List<ViLayout> Children { get; } = []; }

/// <summary>
/// A small, self-contained vi editor model for practice. It never touches files,
/// processes or the network: ":w" only marks the in-memory text as saved (or hands
/// it to <see cref="SaveHandler"/> when the practice shell embeds the editor).
/// Keys are fed as tokens: single characters, or names such as &lt;Esc&gt; and &lt;C-w&gt;.
/// </summary>
public sealed class ViEngine
{
    private readonly List<string> lines = [""];
    private List<string> saved = [""];
    private readonly List<string> pending = [];
    private readonly Stack<(string[] Lines, int Row, int Col)> undo = new(), redo = new();
    private readonly List<string> recent = [];
    private bool undoOpen, replaying, recordingInsert, hlsearch = true;
    private int undoHold;
    private List<string> lastChange = [], changeKeys = [];
    private string commandLine = "", lastSearch = "", lastEx = "";
    private char commandPrefix = ':';
    private bool lastSearchForward = true, commandFromVisual, blockToEnd;
    private (char Kind, string Ch) lastFind;
    private int nextWindowId = 1, visualRow, visualCol;
    private ViMode commandReturn = ViMode.Normal;

    private sealed record Reg(string Text, bool Linewise, bool Block = false);
    private readonly Dictionary<char, Reg> registers = [];
    private char? selectedRegister, recording, lastMacro;
    private bool recordAppend;
    private readonly List<string> recordBuffer = [];
    private int macroDepth, macroKeys;
    private bool macroAbort;
    private readonly Dictionary<char, (int Row, int Col)> marks = [];
    private (int Count, int Row, int Length, bool Line)? insertRepeat;
    private (char Kind, int First, int Last, int Col, int InsertCol, int Length, bool ToEnd)? blockInsert;
    private (int Row, int Start, List<string> Words, int Index, string Prefix)? completion;
    private (ViMode Mode, int AnchorRow, int AnchorCol, int Row, int Col)? lastVisual;
    private readonly List<string> exHistory = [], searchHistory = [];
    private int historyIndex = -1;
    private string historyDraft = "";
    private (string Head, List<string> Items, int Index)? tabState;
    private List<string>? printed;
    private (string Pattern, string Replacement, string Flags)? lastSubstitute;
    private string? fileTypeOverride;
    private Regex? cachedRegex;
    private string cachedPattern = "\0";
    private bool cachedIgnore;

    public ViEngine() { Current = new ViWindow(nextWindowId++); Root = new ViLeaf(Current); }

    public event Action? Changed;
    public IReadOnlyList<string> Lines => lines;
    public string Text => string.Join("\n", lines);
    public ViMode Mode { get; private set; }
    public bool IsVisual => Mode is ViMode.Visual or ViMode.VisualLine or ViMode.VisualBlock;
    public ViLayout Root { get; private set; }
    public ViWindow Current { get; private set; }
    public int LayoutVersion { get; private set; }
    public string FileName { get; set; } = "practice.txt";
    public bool Modified => !lines.SequenceEqual(saved);
    public string Message { get; private set; } = "";
    public bool MessageIsError { get; private set; }
    public string CommandText => Mode == ViMode.Command ? commandPrefix + commandLine : "";
    public bool CommandIsSearch => Mode == ViMode.Command && commandPrefix != ':';
    public string PendingKeys => string.Concat(pending.Select(Display));
    public string? Highlight => hlsearch && lastSearch.Length > 0 ? lastSearch : null;
    public (int Row, int Col) VisualAnchor => (visualRow, visualCol);
    public IReadOnlyList<string> RecentKeys => recent;
    public IReadOnlyList<ViWindow> Windows => Leaves(Root).ToArray();
    /// <summary>Register being recorded with q, or null.</summary>
    public char? Recording => recording;
    /// <summary>Columns of a Ctrl+v selection in the current window.</summary>
    public (int Left, int Right, bool ToEnd)? BlockColumns => Mode == ViMode.VisualBlock ? (Math.Min(visualCol, BlockCursorCol), Math.Max(visualCol, BlockCursorCol), blockToEnd) : null;
    private int BlockCursorCol => Math.Max(Current.Col, Current.Want == int.MaxValue ? 0 : Math.Min(Current.Want, 2000));

    /// <summary>Set by the practice shell: ":w" writes through it and ":q" leaves through <see cref="ExitHandler"/>.</summary>
    public bool Embedded { get; set; }
    /// <summary>Receives (file name, text) on ":w"; returns an error message or null.</summary>
    public Func<string, string, string?>? SaveHandler { get; set; }
    public Action? ExitHandler { get; set; }

    // Options. They survive Load, like settings in a real vim session.
    public bool ShowNumbers { get; private set; }
    public bool RelativeNumbers { get; private set; }
    public bool CursorLine { get; private set; }
    public bool ListChars { get; private set; }
    public bool IgnoreCase { get; private set; }
    public bool SmartCase { get; private set; }
    public int ShiftWidth { get; private set; } = 4;
    public int TabStop { get; private set; } = 4;
    public string ColorScheme { get; private set; } = "default";
    public bool SyntaxOn { get; private set; } = true;
    public string FileType => fileTypeOverride ?? DetectFileType(FileName);

    // Practice counters, reset by Load. Lessons read them to confirm a key was used.
    public int UndoCount { get; private set; }
    public int RedoCount { get; private set; }
    public int RepeatCount { get; private set; }
    public int WindowSwitches { get; private set; }
    public int SaveCount { get; private set; }
    public int QuitBlockedCount { get; private set; }
    public int DiscardCount { get; private set; }
    public int SearchCount { get; private set; }
    public int QuitCount { get; private set; }
    public int MacroRecords { get; private set; }
    public int MacroRuns { get; private set; }
    public int MarkSets { get; private set; }
    public int MarkJumps { get; private set; }
    public int IndentOps { get; private set; }
    public int BlockInserts { get; private set; }
    public int Increments { get; private set; }
    public int ColorChanges { get; private set; }
    public int SyntaxChanges { get; private set; }
    public int OptionChanges { get; private set; }
    public int Sources { get; private set; }
    public int GlobalCount { get; private set; }
    public int NormalCount { get; private set; }
    public int SortCount { get; private set; }
    public int CompletionCount { get; private set; }
    public int RegisterViews { get; private set; }
    public int TextObjects { get; private set; }
    public int CountOnlySubstitutes { get; private set; }

    public void Load(string text, int row = 0, int col = 0)
    {
        lines.Clear(); lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        saved = [.. lines];
        pending.Clear(); undo.Clear(); redo.Clear(); recent.Clear(); lastChange = []; changeKeys = [];
        undoOpen = recordingInsert = false; undoHold = 0; commandLine = ""; lastSearch = ""; lastEx = ""; hlsearch = true;
        registers.Clear(); marks.Clear(); recordBuffer.Clear(); recording = lastMacro = selectedRegister = null;
        insertRepeat = null; blockInsert = null; completion = null; lastVisual = null; lastSubstitute = null; fileTypeOverride = null; tabState = null; printed = null;
        macroDepth = macroKeys = 0; macroAbort = false; blockToEnd = false;
        // Editing options start fresh for each lesson; display options (numbers, colors) carry over.
        ShiftWidth = TabStop = 4; IgnoreCase = SmartCase = false;
        Mode = ViMode.Normal; Message = ""; MessageIsError = false;
        UndoCount = RedoCount = RepeatCount = WindowSwitches = SaveCount = QuitBlockedCount = DiscardCount = SearchCount = QuitCount = 0;
        MacroRecords = MacroRuns = MarkSets = MarkJumps = IndentOps = BlockInserts = Increments = ColorChanges = SyntaxChanges = OptionChanges = 0;
        Sources = GlobalCount = NormalCount = SortCount = CompletionCount = RegisterViews = TextObjects = CountOnlySubstitutes = 0;
        int height = Current.Height;
        Current = new ViWindow(nextWindowId++) { Height = height }; Root = new ViLeaf(Current); LayoutVersion++;
        Current.Row = Math.Clamp(row, 0, lines.Count - 1); Current.Col = col; Clamp(Current); Current.Want = Current.Col;
        Changed?.Invoke();
    }

    /// <summary>Feeds a key string such as "dd", "ihello&lt;Esc&gt;" or ":vsp&lt;CR&gt;".</summary>
    public void FeedKeys(string keys) { foreach (var key in Tokens(keys)) Feed(key); }

    public static IEnumerable<string> Tokens(string keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == '<')
            {
                int end = keys.IndexOf('>', i + 1);
                if (end > i + 1 && IsKeyName(keys[(i + 1)..end])) { string name = keys[(i + 1)..end]; yield return name == "lt" ? "<" : keys[i..(end + 1)]; i = end; continue; }
            }
            if (char.IsHighSurrogate(keys[i]) && i + 1 < keys.Length) { yield return keys.Substring(i, 2); i++; continue; }
            yield return keys[i].ToString();
        }
    }

    private static readonly HashSet<string> KeyNames = ["Esc", "CR", "BS", "Del", "Tab", "Left", "Right", "Up", "Down", "Home", "End", "lt"];
    private static bool IsKeyName(string name) => KeyNames.Contains(name) || name.Length == 3 && name.StartsWith("C-") && char.IsAsciiLetterLower(name[2]);

    public void Feed(string key)
    {
        recent.Add(Display(key)); if (recent.Count > 40) recent.RemoveAt(0);
        macroKeys = 0; macroAbort = false;
        try { FeedCore(key); }
        catch (RegexMatchTimeoutException) { Fail("검색 패턴이 너무 복잡합니다"); }
        Changed?.Invoke();
    }

    private void FeedCore(string key)
    {
        if (key is "<C-c>" or "<C-[>") key = "<Esc>";
        if (recording != null && macroDepth == 0) recordBuffer.Add(key);
        if ((Mode == ViMode.Normal || IsVisual) && pending.Count == 0 && macroDepth == 0) { Message = ""; MessageIsError = false; }
        var before = IsVisual ? (Mode, visualRow, visualCol, Current.Row, Current.Col) : ((ViMode, int, int, int, int)?)null;
        if (recordingInsert && !replaying) changeKeys.Add(key);
        switch (Mode)
        {
            case ViMode.Insert: InsertKey(key); break;
            case ViMode.Command: CommandKey(key); break;
            default: NormalKey(key); break;
        }
        if (undoOpen && Mode != ViMode.Insert) EndUndo();
        if (before is { } selection && !IsVisual) RememberVisual(selection);
        foreach (var window in Windows) Clamp(window);
        Scroll(Current);
    }

    private void RememberVisual((ViMode Mode, int AnchorRow, int AnchorCol, int Row, int Col) selection)
    {
        lastVisual = selection;
        bool forward = selection.AnchorRow < selection.Row || selection.AnchorRow == selection.Row && selection.AnchorCol <= selection.Col;
        marks['<'] = forward ? (selection.AnchorRow, selection.AnchorCol) : (selection.Row, selection.Col);
        marks['>'] = forward ? (selection.Row, selection.Col) : (selection.AnchorRow, selection.AnchorCol);
    }

    public void SetHeight(ViWindow window, int rows) { window.Height = Math.Max(1, rows); Scroll(window); }

    /// <summary>Mouse click: focus a window and place the cursor.</summary>
    public void Click(ViWindow window, int row, int col)
    {
        if (!Windows.Contains(window)) return;
        if (window != Current) { Current = window; WindowSwitches++; }
        pending.Clear();
        if (Mode == ViMode.Command) Mode = ViMode.Normal;
        window.Row = Math.Clamp(row, 0, lines.Count - 1); window.Col = col; Clamp(window); window.Want = window.Col;
        Changed?.Invoke();
    }

    /// <summary>Right-click paste, like PuTTY: text goes in only while typing (insert mode).</summary>
    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (Mode != ViMode.Insert)
        {
            Info("붙여넣기는 입력 모드에서 합니다 · i 를 누른 뒤 마우스 오른쪽 버튼 (명령 모드에서 붙여넣으면 글자가 명령으로 실행됩니다)");
            Changed?.Invoke(); return;
        }
        var w = Current; BeginUndo(); completion = null; blockInsert = null;
        string line = lines[w.Row]; int col = Math.Min(w.Col, line.Length);
        var parts = (line[..col] + text + line[col..]).Split('\n');
        int lastLength = parts[^1].Length - (line.Length - col);
        lines[w.Row] = parts[0]; lines.InsertRange(w.Row + 1, parts.Skip(1));
        w.Row += parts.Length - 1; w.Col = parts.Length == 1 ? col + text.Length : lastLength;
        Changed?.Invoke();
    }

    public string RegisterText(char name) => registers.TryGetValue(char.ToLowerInvariant(name), out var reg) ? reg.Text : "";
    public (int Row, int Col)? Mark(char name) => marks.TryGetValue(name, out var mark) ? mark : null;

    // ───────────────────────── Normal and visual mode ─────────────────────────

    private enum Result { Done, Incomplete, Invalid }

    private void NormalKey(string key)
    {
        if (key == "<Esc>")
        {
            if (pending.Count > 0) pending.Clear();
            else if (IsVisual) { Mode = ViMode.Normal; blockToEnd = false; }
            return;
        }
        pending.Add(key);
        var tokens = pending.ToList();
        bool changing = false;
        Result result;
        try { result = Execute(tokens, ref changing); }
        catch (ViError error) { Fail(error.Message); result = Result.Invalid; }
        catch (RegexMatchTimeoutException) { Fail("검색 패턴이 너무 복잡합니다"); result = Result.Invalid; }
        if (result == Result.Incomplete) return;
        pending.Clear(); selectedRegister = null;
        if (result == Result.Invalid && macroDepth > 0) macroAbort = true;
        if (result == Result.Done && changing && !replaying)
        {
            if (Mode == ViMode.Insert) { recordingInsert = true; changeKeys = tokens; }
            else lastChange = tokens;
        }
    }

    private Result Execute(List<string> t, ref bool changing)
    {
        int i = 0, count = ReadCount(t, ref i);
        if (i < t.Count && t[i] == "\"")
        {
            if (i + 1 >= t.Count) return Result.Incomplete;
            char name = t[i + 1].Length == 1 ? t[i + 1][0] : '\0';
            if (!(char.IsAsciiLetterOrDigit(name) || name is '"' or '-' or '_' or ':' or '/')) return Fail("레지스터 이름은 a~z, 0~9, \" 중 하나입니다");
            selectedRegister = name; i += 2;
            int more = ReadCount(t, ref i); if (more > 0) count = Math.Max(1, count) * more;
        }
        if (i >= t.Count) return Result.Incomplete;
        int n = Math.Max(1, count);
        var w = Current;
        string k = t[i];
        bool visual = IsVisual;

        string? op = k is "d" or "y" or "c" or ">" or "<" or "=" ? k : null;
        int opEnd = i + 1;
        if (k == "g")
        {
            if (i + 1 >= t.Count) return Result.Incomplete;
            if (t[i + 1] is "U" or "u" or "~") { op = "g" + t[i + 1]; opEnd = i + 2; }
        }
        if (op != null)
        {
            if (visual) { changing = op != "y" && Mode != ViMode.VisualBlock; VisualOperator(op); return Result.Done; }
            if (opEnd >= t.Count) return Result.Incomplete;
            changing = op != "y";
            string tail = op[^1].ToString();
            bool doubled = op.Length == 1 ? t[opEnd] == op : t[opEnd] == tail;
            if (op.Length == 2 && t[opEnd] == "g")
            {
                if (opEnd + 1 >= t.Count) return Result.Incomplete;
                doubled = t[opEnd + 1] == tail;
            }
            if (doubled) { LineOperator(op, w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done; }
            int j = opEnd, inner = ReadCount(t, ref j);
            if (j >= t.Count) return Result.Incomplete;
            int total = n * Math.Max(1, inner);
            if (t[j] is "i" or "a")
            {
                if (j + 1 >= t.Count) return Result.Incomplete;
                var range = TextObject(t[j] == "i", t[j + 1], total);
                if (range is not { } r) return Fail($"{t[j]}{t[j + 1]} 로 고를 수 있는 범위가 없습니다");
                TextObjects++; ApplyRange(op, r); return Result.Done;
            }
            if (op == "c" && t[j] is "w" or "W" && w.Col < lines[w.Row].Length && !char.IsWhiteSpace(lines[w.Row][w.Col])) t[j] = t[j] == "w" ? "e" : "E";
            var motion = Motion(t, j, total, count > 0 || inner > 0, true);
            if (motion.State == Result.Incomplete) return Result.Incomplete;
            if (motion.State == Result.Invalid || !motion.Known) return Result.Invalid;
            // Vim: "dw"/"cw" on the last word of a line stops at that line's end.
            if (t[j] is "w" or "W" && motion.Row > w.Row && !motion.Linewise) motion = motion with { Row = w.Row, Col = lines[w.Row].Length, Inclusive = false };
            Operator(op, motion);
            return Result.Done;
        }

        var move = Motion(t, i, n, count > 0, false);
        if (move.State == Result.Incomplete) return Result.Incomplete;
        if (move.Known)
        {
            if (move.State == Result.Invalid) return Result.Invalid;
            if (k is "G" or "n" or "N" or "*" or "#" or "%" or "'" or "`" or "H" or "M" or "L" or "{" or "}" || k == "g" && t[i + 1] == "g") SetJump();
            w.Row = move.Row; w.Col = move.Col;
            if (move.Linewise && !move.KeepColumn) { w.Col = FirstNonBlank(w.Row); }
            if (move.KeepColumn) w.Col = Math.Min(w.Want, Math.Max(0, lines[w.Row].Length - 1)); else w.Want = move.Col == int.MaxValue ? int.MaxValue : w.Col;
            if (move.EndOfLine) w.Want = int.MaxValue;
            if (Mode == ViMode.VisualBlock) blockToEnd = move.EndOfLine || move.KeepColumn && blockToEnd;
            return Result.Done;
        }

        if (visual)
        {
            bool block = Mode == ViMode.VisualBlock;
            switch (k)
            {
                case "o": (visualRow, w.Row) = (w.Row, visualRow); (visualCol, w.Col) = (w.Col, visualCol); w.Want = w.Col; return Result.Done;
                case "O":
                    if (block) { (visualCol, w.Col) = (w.Col, visualCol); w.Want = w.Col; }
                    else { (visualRow, w.Row) = (w.Row, visualRow); (visualCol, w.Col) = (w.Col, visualCol); w.Want = w.Col; }
                    return Result.Done;
                case "x": case "<Del>": changing = !block; VisualOperator("d"); return Result.Done;
                case "s": changing = !block; VisualOperator("c"); return Result.Done;
                case "~": changing = true; VisualOperator("g~"); return Result.Done;
                case "U": changing = true; VisualOperator("gU"); return Result.Done;
                case "u": changing = true; VisualOperator("gu"); return Result.Done;
                case "D": case "X": changing = !block; if (block) blockToEnd = true; else Mode = ViMode.VisualLine; VisualOperator("d"); return Result.Done;
                case "C": case "S": case "R": changing = !block; if (block) blockToEnd = true; else Mode = ViMode.VisualLine; VisualOperator("c"); return Result.Done;
                case "Y": if (block) blockToEnd = true; else Mode = ViMode.VisualLine; VisualOperator("y"); return Result.Done;
                case "I": case "A":
                    if (!block) return Fail("I · A 로 여러 줄에 입력하려면 Ctrl+v (블록 선택)로 고르세요");
                    {
                        var (first, last, left, right) = BlockArea(); bool toEnd = blockToEnd; Mode = ViMode.Normal; blockToEnd = false;
                        StartBlockInsert(k[0], first, last, k == "I" ? left : toEnd ? -1 : right + 1, toEnd);
                        return Result.Done;
                    }
                case "r":
                    if (i + 1 >= t.Count) return Result.Incomplete;
                    if (t[i + 1] == "<Esc>") return Result.Done;
                    if (t[i + 1].Length != 1 && !(t[i + 1].Length == 2 && char.IsSurrogatePair(t[i + 1], 0))) return Result.Invalid;
                    changing = !block; VisualReplace(t[i + 1]); return Result.Done;
                case "i": case "a":
                {
                    if (i + 1 >= t.Count) return Result.Incomplete;
                    var range = TextObject(k == "i", t[i + 1], n);
                    if (range is not { } r) return Fail($"{k}{t[i + 1]} 로 고를 수 있는 범위가 없습니다");
                    TextObjects++;
                    visualRow = r.StartRow; visualCol = r.StartCol;
                    w.Row = r.EndRow; w.Col = Math.Max(r.StartRow == r.EndRow ? r.StartCol : 0, r.EndCol - 1);
                    if (r.Linewise) Mode = ViMode.VisualLine;
                    return Result.Done;
                }
                case "<C-a>": case "<C-x>": changing = true; VisualIncrement(k == "<C-a>" ? n : -n, false); return Result.Done;
                case "g" when i + 1 < t.Count && t[i + 1] is "<C-a>" or "<C-x>": changing = true; VisualIncrement(t[i + 1] == "<C-a>" ? n : -n, true); return Result.Done;
                case "J":
                {
                    var (a, b) = VisualRows(); Mode = ViMode.Normal; w.Row = a;
                    if (a + 1 >= lines.Count) return Result.Invalid;
                    Begin(ref changing);
                    for (int x = 0; x < Math.Max(1, b - a) && a + 1 < lines.Count; x++)
                    { string left = lines[a].TrimEnd(), right = lines[a + 1].TrimStart(); lines[a] = right.Length == 0 ? left : left.Length == 0 ? right : left + " " + right; lines.RemoveAt(a + 1); w.Col = left.Length; }
                    return Result.Done;
                }
                case "p": case "P":
                {
                    var reg = Fetch();
                    if (reg == null) return Fail("붙여넣을 내용이 없습니다.");
                    var mode = Mode; var (first, _) = VisualRows(); changing = true;
                    selectedRegister = '_'; VisualOperator("d"); selectedRegister = null;
                    bool after = mode == ViMode.VisualLine && reg.Linewise && first >= lines.Count;
                    PutRegister(reg, after, 1); return Result.Done;
                }
                case ".": case "<C-r>":
                    return Fail("비주얼 모드에서는 지원하지 않는 키입니다. Esc 로 나온 뒤 사용하세요.");
            }
        }

        switch (k)
        {
            case "i": Begin(ref changing); Mode = ViMode.Insert; StartRepeat(count, false); return Result.Done;
            case "a": Begin(ref changing); if (lines[w.Row].Length > 0) w.Col++; Mode = ViMode.Insert; StartRepeat(count, false); return Result.Done;
            case "I": Begin(ref changing); w.Col = FirstNonBlank(w.Row); if (lines[w.Row].Trim().Length == 0) w.Col = lines[w.Row].Length; Mode = ViMode.Insert; StartRepeat(count, false); return Result.Done;
            case "A": Begin(ref changing); w.Col = lines[w.Row].Length; Mode = ViMode.Insert; StartRepeat(count, false); return Result.Done;
            case "o": case "O":
            {
                Begin(ref changing);
                string indent = Indent(lines[w.Row]); int at = k == "o" ? w.Row + 1 : w.Row;
                lines.Insert(at, indent); w.Row = at; w.Col = indent.Length; Mode = ViMode.Insert; StartRepeat(count, true); return Result.Done;
            }
            case "x": case "<Del>":
                if (lines[w.Row].Length == 0) return Result.Invalid;
                Begin(ref changing); DeleteChars(w.Row, w.Col, Math.Min(n, lines[w.Row].Length - w.Col)); return Result.Done;
            case "X":
                if (w.Col == 0) return Result.Invalid;
                Begin(ref changing); { int from = Math.Max(0, w.Col - n); DeleteChars(w.Row, from, w.Col - from); w.Col = from; } return Result.Done;
            case "D": Begin(ref changing); StoreChars(w.Row, w.Col, lines[w.Row].Length); lines[w.Row] = lines[w.Row][..Math.Min(w.Col, lines[w.Row].Length)]; return Result.Done;
            case "C": Begin(ref changing); StoreChars(w.Row, w.Col, lines[w.Row].Length); lines[w.Row] = lines[w.Row][..Math.Min(w.Col, lines[w.Row].Length)]; w.Col = lines[w.Row].Length; Mode = ViMode.Insert; return Result.Done;
            case "s": Begin(ref changing); if (lines[w.Row].Length > 0) DeleteChars(w.Row, w.Col, Math.Min(n, lines[w.Row].Length - w.Col)); Mode = ViMode.Insert; return Result.Done;
            case "S": changing = true; LineOperator("c", w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done;
            case "Y": LineOperator("y", w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done;
            case "p": case "P":
            {
                var reg = Fetch();
                if (reg == null) return Fail(selectedRegister is char name && name != '"' ? $"레지스터 \"{name} 가 비어 있습니다" : "붙여넣을 내용이 없습니다. 먼저 yy · dd · yw 등으로 복사하세요.");
                Begin(ref changing); PutRegister(reg, k == "p", n); return Result.Done;
            }
            case "r":
                if (i + 1 >= t.Count) return Result.Incomplete;
                {
                    string ch = t[i + 1]; string line = lines[w.Row];
                    if (ch == "<Esc>") return Result.Done;
                    if (ch.Length != 1 && ch != "<CR>" && !(ch.Length == 2 && char.IsSurrogatePair(ch, 0))) return Result.Invalid;
                    if (w.Col + n > line.Length) return Result.Invalid;
                    Begin(ref changing);
                    if (ch == "<CR>") { lines[w.Row] = line[..w.Col]; lines.Insert(w.Row + 1, line[(w.Col + n)..]); w.Row++; w.Col = 0; }
                    else { lines[w.Row] = line[..w.Col] + string.Concat(Enumerable.Repeat(ch, n)) + line[(w.Col + n)..]; w.Col += (n - 1) * ch.Length; }
                    return Result.Done;
                }
            case "~":
            {
                string line = lines[w.Row]; if (line.Length == 0) return Result.Invalid;
                Begin(ref changing); int end = Math.Min(line.Length, w.Col + n);
                lines[w.Row] = line[..w.Col] + Toggle(line[w.Col..end]) + line[end..]; w.Col = Math.Min(end, line.Length - 1); return Result.Done;
            }
            case "J":
            {
                int joins = Math.Max(1, n - 1); if (w.Row + 1 >= lines.Count) return Result.Invalid;
                Begin(ref changing);
                for (int x = 0; x < joins && w.Row + 1 < lines.Count; x++)
                {
                    string left = lines[w.Row].TrimEnd(), right = lines[w.Row + 1].TrimStart();
                    lines[w.Row] = right.Length == 0 ? left : left.Length == 0 ? right : left + " " + right; lines.RemoveAt(w.Row + 1); w.Col = left.Length;
                }
                return Result.Done;
            }
            case "u": Undo(n); return Result.Done;
            case "<C-r>": Redo(n); return Result.Done;
            case ".":
                if (lastChange.Count == 0) return Fail("반복할 변경이 없습니다.");
                RepeatCount++; replaying = true;
                try { var keys = lastChange.ToList(); pending.Clear(); foreach (var key in keys) { if (Mode == ViMode.Insert) InsertKey(key); else NormalKey(key); } if (Mode == ViMode.Insert) InsertKey("<Esc>"); }
                finally { replaying = false; }
                return Result.Done;
            case "v": case "V": case "<C-v>":
            {
                var target = k == "v" ? ViMode.Visual : k == "V" ? ViMode.VisualLine : ViMode.VisualBlock;
                if (Mode == target) { Mode = ViMode.Normal; blockToEnd = false; }
                else { if (!visual) { visualRow = w.Row; visualCol = w.Col; blockToEnd = false; w.Want = w.Col; } Mode = target; }
                return Result.Done;
            }
            case ":": StartCommand(':'); return Result.Done;
            case "/": StartCommand('/'); return Result.Done;
            case "?": StartCommand('?'); return Result.Done;
            case "<C-d>": case "<C-u>":
            {
                int half = Math.Max(1, w.Height / 2), delta = k == "<C-d>" ? half : -half;
                w.Row = Math.Clamp(w.Row + delta, 0, lines.Count - 1); w.Top = Math.Clamp(w.Top + delta, 0, Math.Max(0, lines.Count - 1)); w.Col = FirstNonBlank(w.Row); return Result.Done;
            }
            case "<C-g>": Info($"\"{FileName}\"{(Modified ? " [수정됨]" : "")} {lines.Count}줄 · 현재 {w.Row + 1}번째 줄 ({(w.Row + 1) * 100 / lines.Count}%)"); return Result.Done;
            case "<C-w>":
                if (i + 1 >= t.Count) return Result.Incomplete;
                return WindowCommand(t[i + 1]);
            case "<C-a>": case "<C-x>":
            {
                Begin(ref changing);
                var end = IncrementAt(w.Row, w.Col, k == "<C-a>" ? n : -n);
                if (end is not int last) return Fail("커서 위치부터 줄 끝까지 숫자가 없습니다");
                w.Col = last; Increments++; return Result.Done;
            }
            case "Z":
                if (i + 1 >= t.Count) return Result.Incomplete;
                if (t[i + 1] == "Z") { RunCommand("x"); return Result.Done; }
                if (t[i + 1] == "Q") { RunCommand("q!"); return Result.Done; }
                return Result.Invalid;
            case "z":
                if (i + 1 >= t.Count) return Result.Incomplete;
                switch (t[i + 1])
                {
                    case "z": case ".": w.Top = Math.Max(0, w.Row - w.Height / 2); return Result.Done;
                    case "t": case "<CR>": w.Top = w.Row; return Result.Done;
                    case "b": case "-": w.Top = Math.Max(0, w.Row - w.Height + 1); return Result.Done;
                }
                return Result.Invalid;
            case "q":
                if (recording is char target2)
                {
                    int drop = Math.Min(recordBuffer.Count, t.Count);
                    recordBuffer.RemoveRange(recordBuffer.Count - drop, drop);
                    string text = Encode(recordBuffer);
                    if (recordAppend && registers.TryGetValue(target2, out var old)) text = old.Text + text;
                    registers[target2] = new Reg(text, false); recording = null; MacroRecords++;
                    Info($"매크로 @{target2} 녹화 완료 · {Visible(text, 50)}");
                    return Result.Done;
                }
                if (i + 1 >= t.Count) return Result.Incomplete;
                {
                    char name = t[i + 1].Length == 1 ? t[i + 1][0] : '\0';
                    if (!(char.IsAsciiLetterOrDigit(name) || name == '"')) return Fail("q 다음에는 녹화할 레지스터 이름(a~z)을 누르세요");
                    recording = char.ToLowerInvariant(name); recordAppend = char.IsAsciiLetterUpper(name); recordBuffer.Clear();
                    return Result.Done;
                }
            case "@":
                if (i + 1 >= t.Count) return Result.Incomplete;
                {
                    char name = t[i + 1].Length == 1 ? t[i + 1][0] : '\0';
                    if (!(char.IsAsciiLetterOrDigit(name) || name is '@' or ':' or '"')) return Result.Invalid;
                    return RunMacro(name, n);
                }
            case "m":
                if (i + 1 >= t.Count) return Result.Incomplete;
                {
                    char name = t[i + 1].Length == 1 ? t[i + 1][0] : '\0';
                    if (!(char.IsAsciiLetter(name) || name is '\'' or '`')) return Result.Invalid;
                    marks[name == '`' ? '\'' : name] = (w.Row, w.Col); MarkSets++;
                    Info($"마크 {name} 를 {w.Row + 1}번째 줄에 표시했습니다 · '{name} 로 돌아옵니다");
                    return Result.Done;
                }
            case "&":
                if (lastSubstitute is not { } previous) return Fail("E35: 이전에 바꾼 내용이 없습니다");
                changing = true; SubstituteWith(previous.Pattern, previous.Replacement, "", w.Row, w.Row); return Result.Done;
            case "g":
                if (i + 1 >= t.Count) return Result.Incomplete;
                switch (t[i + 1])
                {
                    case "v":
                    {
                        if (lastVisual is not { } last) return Fail("이전에 선택한 영역이 없습니다");
                        Mode = last.Mode; visualRow = Math.Clamp(last.AnchorRow, 0, lines.Count - 1); visualCol = last.AnchorCol;
                        w.Row = Math.Clamp(last.Row, 0, lines.Count - 1); w.Col = last.Col; w.Want = w.Col; return Result.Done;
                    }
                    case "J":
                    {
                        if (w.Row + 1 >= lines.Count) return Result.Invalid;
                        Begin(ref changing);
                        for (int x = 0; x < Math.Max(1, n - 1) && w.Row + 1 < lines.Count; x++) { w.Col = lines[w.Row].Length; lines[w.Row] += lines[w.Row + 1]; lines.RemoveAt(w.Row + 1); }
                        return Result.Done;
                    }
                    case "&":
                        if (lastSubstitute is not { } previous2) return Fail("E35: 이전에 바꾼 내용이 없습니다");
                        changing = true; SubstituteWith(previous2.Pattern, previous2.Replacement, previous2.Flags, 0, lines.Count - 1); return Result.Done;
                }
                return Result.Invalid;
        }
        return Result.Invalid;
    }

    private void Begin(ref bool changing) { changing = true; BeginUndo(); }
    private void StartRepeat(int count, bool line) { if (count > 1) insertRepeat = (Math.Min(count, 500), Current.Row, lines[Current.Row].Length, line); }

    private sealed class ViError(string message) : Exception(message);
    private Result Fail(string message) { Message = message; MessageIsError = true; if (macroDepth > 0) macroAbort = true; return Result.Invalid; }
    private void Info(string message) { Message = message; MessageIsError = false; }

    private static int ReadCount(List<string> t, ref int i)
    {
        int value = 0; bool any = false;
        while (i < t.Count && t[i].Length == 1 && char.IsAsciiDigit(t[i][0]) && (any || t[i] != "0")) { value = Math.Min(9999, value * 10 + (t[i][0] - '0')); any = true; i++; }
        return value;
    }

    private void SetJump() => marks['\''] = (Current.Row, Current.Col);

    // ───────────────────────── Macros ─────────────────────────

    private Result RunMacro(char name, int count)
    {
        if (name == '@')
        {
            if (lastMacro is not char previous) return Fail("E748: 이전에 실행한 매크로가 없습니다");
            name = previous;
        }
        if (name == ':')
        {
            if (lastEx.Length == 0) return Fail("E30: 이전에 실행한 명령이 없습니다");
            lastMacro = ':'; pending.Clear();
            for (int x = 0; x < count; x++) RunCommand(lastEx);
            return Result.Done;
        }
        char key = char.ToLowerInvariant(name);
        if (!registers.TryGetValue(key, out var reg) || reg.Text.Length == 0 && !reg.Linewise) return Fail($"레지스터 {name} 가 비어 있습니다. 먼저 q{name} … q 로 매크로를 녹화하세요");
        if (macroDepth >= 40) return Fail("E132: 매크로가 너무 깊게 중첩되었습니다");
        lastMacro = key; MacroRuns++;
        var tokens = Decode(reg.Text + (reg.Linewise ? "\r" : "")).ToList();
        pending.Clear(); macroDepth++;
        try
        {
            for (int x = 0; x < count && !macroAbort; x++)
                foreach (var token in tokens)
                {
                    if (macroAbort) break;
                    if (++macroKeys > 100000) { Fail("매크로 실행을 멈췄습니다 (키 100,000개 초과)"); break; }
                    FeedCore(token);
                }
        }
        finally { macroDepth--; if (macroDepth == 0) macroAbort = false; pending.Clear(); }
        return Result.Done;
    }

    private static string Encode(IEnumerable<string> keys) => string.Concat(keys.Select(key => key switch
    {
        "<Esc>" => "\u001b", "<CR>" => "\r", "<BS>" => "\b", "<Tab>" => "\t", "<Del>" => "\u007f",
        "<Left>" => "", "<Right>" => "", "<Up>" => "", "<Down>" => "", "<Home>" => "", "<End>" => "",
        _ when key.StartsWith("<C-") && key.Length == 5 => ((char)(key[3] - 'a' + 1)).ToString(),
        _ => key
    }));

    private static IEnumerable<string> Decode(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length) { yield return text.Substring(i, 2); i++; continue; }
            yield return c switch
            {
                '\u001b' => "<Esc>", '\r' or '\n' => "<CR>", '\b' => "<BS>", '\t' => "<Tab>", '\u007f' => "<Del>",
                '' => "<Left>", '' => "<Right>", '' => "<Up>", '' => "<Down>", '' => "<Home>", '' => "<End>",
                _ when c >= 1 && c <= 26 => $"<C-{(char)('a' + c - 1)}>",
                _ => c.ToString()
            };
        }
    }

    /// <summary>Register text as vim's :registers shows it (^[ for Esc, ^J for line breaks).</summary>
    private static string Visible(string text, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\n' => "^J", '\r' => "^M", '\u001b' => "^[", '\u007f' => "^?",
                '' => "<Left>", '' => "<Right>", '' => "<Up>", '' => "<Down>", '' => "<Home>", '' => "<End>",
                _ when c < 32 => "^" + (char)(c + 64),
                _ => c.ToString()
            });
            if (sb.Length > max) { sb.Length = max; sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    // ───────────────────────── Motions ─────────────────────────

    private readonly record struct Move(Result State, bool Known, int Row, int Col, bool Linewise, bool Inclusive, bool KeepColumn = false, bool EndOfLine = false);
    private static Move Unknown => new(Result.Done, false, 0, 0, false, false);
    private static Move Pending => new(Result.Incomplete, true, 0, 0, false, false);
    private static Move Bad => new(Result.Invalid, true, 0, 0, false, false);

    private Move Motion(List<string> t, int i, int n, bool hasCount, bool forOperator)
    {
        var w = Current; int row = w.Row, col = w.Col; string line = lines[row];
        Move Char(int r, int c, bool inclusive = false) => new(Result.Done, true, r, c, false, inclusive);
        Move Line(int r, bool keep = false) => new(Result.Done, true, Math.Clamp(r, 0, lines.Count - 1), keep ? w.Col : 0, true, false, keep);
        switch (t[i])
        {
            case "h": case "<Left>": case "<BS>": return col == 0 ? Bad : Char(row, Math.Max(0, col - n));
            case "l": case "<Right>": case " ":
                if (col >= (forOperator ? line.Length : Math.Max(0, line.Length - 1))) return Bad;
                return Char(row, Math.Min(col + n, forOperator ? line.Length : Math.Max(0, line.Length - 1)));
            case "j": case "<Down>": return row + 1 >= lines.Count ? Bad : Line(row + n, true);
            case "k": case "<Up>": return row == 0 ? Bad : Line(row - n, true);
            case "+": case "<CR>": return row + 1 >= lines.Count ? Bad : Line(row + n);
            case "-": return row == 0 ? Bad : Line(row - n);
            case "0": case "<Home>": return Char(row, 0);
            case "^": return Char(row, FirstNonBlank(row));
            case "$": case "<End>":
            {
                int r = Math.Min(lines.Count - 1, row + n - 1);
                return new(Result.Done, true, r, Math.Max(0, lines[r].Length - 1), false, true, false, true);
            }
            case "w": case "W": case "b": case "B": case "e": case "E":
            {
                bool big = t[i] is "W" or "B" or "E"; int index = Index(row, col);
                for (int x = 0; x < n; x++) index = t[i].ToLowerInvariant() switch { "w" => NextWord(index, big), "b" => PrevWord(index, big), _ => WordEnd(index, big) };
                var (r, c) = Position(index);
                bool inclusive = t[i] is "e" or "E";
                if (!forOperator && t[i] is "w" or "W" && r == lines.Count - 1 && c >= lines[r].Length) c = Math.Max(0, lines[r].Length - 1);
                return Char(r, c, inclusive);
            }
            case "G": return Line(hasCount ? n - 1 : lines.Count - 1);
            case "g":
                if (i + 1 >= t.Count) return Pending;
                return t[i + 1] == "g" ? Line(hasCount ? n - 1 : 0) : Unknown;
            case "H": return Line(Math.Min(lines.Count - 1, w.Top + (hasCount ? n - 1 : 0)));
            case "M": return Line(Math.Min(lines.Count - 1, w.Top + Math.Min(w.Height, lines.Count - w.Top) / 2));
            case "L": return Line(Math.Min(lines.Count - 1, w.Top + w.Height - 1));
            case "f": case "F": case "t": case "T":
            {
                if (i + 1 >= t.Count) return Pending;
                if (t[i + 1].Length != 1) return Bad;
                lastFind = (t[i][0], t[i + 1]);
                return Find(t[i][0], t[i + 1][0], n);
            }
            case ";": case ",":
            {
                if (lastFind.Ch is null) return Bad;
                char kind = lastFind.Kind; if (t[i] == ",") kind = kind switch { 'f' => 'F', 'F' => 'f', 't' => 'T', _ => 't' };
                return Find(kind, lastFind.Ch[0], n);
            }
            case "n": case "N":
            {
                if (lastSearch.Length == 0) { Fail("검색어가 없습니다. 먼저 /단어 로 검색하세요."); return Bad; }
                bool forward = t[i] == "n" ? lastSearchForward : !lastSearchForward;
                var found = Search(lastSearch, forward, n);
                return found is { } p ? Char(p.Row, p.Col) : Bad;
            }
            case "*": case "#":
            {
                string word = WordAt(row, col); if (word.Length == 0) { Fail("커서 아래에 단어가 없습니다."); return Bad; }
                lastSearch = "\\<" + word + "\\>"; lastSearchForward = t[i] == "*"; hlsearch = true; SearchCount++;
                var found = Search(lastSearch, lastSearchForward, n);
                return found is { } p ? Char(p.Row, p.Col) : Bad;
            }
            case "%":
            {
                var match = Bracket(row, col);
                return match is { } m ? Char(m.Row, m.Col, true) : Bad;
            }
            case "}": case "{":
            {
                int r = row; bool down = t[i] == "}";
                for (int x = 0; x < n; x++)
                {
                    r += down ? 1 : -1;
                    while (r > 0 && r < lines.Count - 1 && lines[r].Trim().Length > 0) r += down ? 1 : -1;
                    r = Math.Clamp(r, 0, lines.Count - 1);
                }
                return Char(r, down && r == lines.Count - 1 && lines[r].Trim().Length > 0 ? Math.Max(0, lines[r].Length - 1) : 0);
            }
            case "'": case "`":
            {
                if (i + 1 >= t.Count) return Pending;
                char name = t[i + 1].Length == 1 ? t[i + 1][0] : '\0';
                if (name == '`') name = '\'';
                if (!marks.TryGetValue(name, out var mark)) { Fail($"E20: 마크 {name} 가 없습니다 · 먼저 m{name} 로 표시하세요"); return Bad; }
                int r = Math.Clamp(mark.Row, 0, lines.Count - 1); MarkJumps++;
                return t[i] == "'" ? Line(r) : Char(r, Math.Min(mark.Col, Math.Max(0, lines[r].Length - 1)));
            }
        }
        return Unknown;
    }

    private Move Find(char kind, char target, int n)
    {
        var w = Current; string line = lines[w.Row]; int c = w.Col;
        bool forward = kind is 'f' or 't';
        for (int x = 0; x < n; x++)
        {
            int start = forward ? c + 1 + (kind == 't' && x == 0 ? 1 : 0) : c - 1 - (kind == 'T' && x == 0 ? 1 : 0);
            int found = forward ? (start < line.Length ? line.IndexOf(target, start) : -1) : (start >= 0 ? line.LastIndexOf(target, start) : -1);
            if (found < 0) return Bad;
            c = found;
        }
        if (kind == 't') c--; else if (kind == 'T') c++;
        return new(Result.Done, true, w.Row, c, false, kind is 'f' or 't');
    }

    // Flat text helpers: '\n' joins lines, so word motions can cross line ends.
    private string Flat => string.Join("\n", lines);
    private int Index(int row, int col) { int index = 0; for (int r = 0; r < row; r++) index += lines[r].Length + 1; return index + Math.Min(col, lines[row].Length); }
    private (int Row, int Col) Position(int index)
    {
        for (int r = 0; r < lines.Count; r++) { if (index <= lines[r].Length) return (r, Math.Max(0, index)); index -= lines[r].Length + 1; }
        return (lines.Count - 1, lines[^1].Length);
    }
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static int Class(string text, int i, bool big)
    {
        if (i < 0 || i >= text.Length) return -1;
        char c = text[i];
        if (c == '\n') return i == 0 || text[i - 1] == '\n' ? 3 : 0;
        if (char.IsWhiteSpace(c)) return 0;
        if (big) return 1;
        return IsWordChar(c) ? 1 : 2;
    }
    private int NextWord(int i, bool big)
    {
        string s = Flat; int cls = Class(s, i, big);
        if (cls is 1 or 2) while (Class(s, i, big) == cls) i++;
        else if (cls == 3) i++;
        while (Class(s, i, big) == 0) i++;
        return Math.Min(i, s.Length);
    }
    private int PrevWord(int i, bool big)
    {
        string s = Flat; i--;
        while (Class(s, i, big) == 0) i--;
        if (i < 0) return 0;
        int cls = Class(s, i, big); if (cls == 3) return i;
        while (Class(s, i - 1, big) == cls) i--;
        return i;
    }
    private int WordEnd(int i, bool big)
    {
        string s = Flat; i++;
        while (Class(s, i, big) is 0 or 3) i++;
        if (i >= s.Length) return Math.Max(0, s.Length - 1);
        int cls = Class(s, i, big);
        while (Class(s, i + 1, big) == cls) i++;
        return i;
    }
    private string WordAt(int row, int col)
    {
        string line = lines[row]; if (line.Length == 0) return "";
        int c = Math.Min(col, line.Length - 1);
        while (c < line.Length && !IsWordChar(line[c])) c++;
        if (c >= line.Length) return "";
        int start = c, end = c;
        while (start > 0 && IsWordChar(line[start - 1])) start--;
        while (end < line.Length && IsWordChar(line[end])) end++;
        return line[start..end];
    }
    private (int Row, int Col)? Bracket(int row, int col)
    {
        string pairs = "()[]{}"; string line = lines[row]; int c = col;
        while (c < line.Length && pairs.IndexOf(line[c]) < 0) c++;
        if (c >= line.Length) return null;
        int kind = pairs.IndexOf(line[c]); char open = pairs[kind & ~1], close = pairs[kind | 1]; bool forward = (kind & 1) == 0;
        string s = Flat; int i = Index(row, c), depth = 0;
        for (; i >= 0 && i < s.Length; i += forward ? 1 : -1)
        {
            if (s[i] == open) depth += forward ? 1 : -1; else if (s[i] == close) depth += forward ? -1 : 1;
            if (depth == 0) return Position(i);
        }
        return null;
    }

    // ───────────────────────── Search (vim regular expressions) ─────────────────────────

    private Regex Compile(string pattern, bool? ignore = null)
    {
        bool ic = ignore ?? (IgnoreCase && !(SmartCase && pattern.Any(char.IsUpper)));
        if (pattern == cachedPattern && ic == cachedIgnore && cachedRegex != null) return cachedRegex;
        var regex = VimRegex.Convert(pattern, ic) ?? new Regex(Regex.Escape(pattern), ic ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250));
        cachedPattern = pattern; cachedIgnore = ic; cachedRegex = regex;
        return regex;
    }

    private (int Row, int Col)? Search(string pattern, bool forward, int n)
    {
        string s = Flat; int at = Index(Current.Row, Current.Col); bool wrapped = false;
        var starts = new List<int>();
        foreach (Match m in Compile(pattern).Matches(s)) { if (starts.Count == 0 || starts[^1] != m.Index) starts.Add(m.Index); if (starts.Count > 20000) break; }
        if (starts.Count == 0) { Fail($"E486: 찾을 수 없습니다: {pattern}"); return null; }
        for (int x = 0; x < n; x++)
        {
            int next = -1;
            if (forward) { foreach (int p in starts) if (p > at) { next = p; break; } if (next < 0) { next = starts[0]; wrapped = true; } }
            else { for (int k = starts.Count - 1; k >= 0; k--) if (starts[k] < at) { next = starts[k]; break; } if (next < 0) { next = starts[^1]; wrapped = true; } }
            at = next;
        }
        if (wrapped) Info(forward ? "끝까지 검색해 처음부터 다시 찾았습니다" : "처음까지 검색해 끝에서부터 다시 찾았습니다");
        else Info((forward ? "/" : "?") + pattern);
        return Position(at);
    }

    /// <summary>Search matches on one line, for the hlsearch highlight.</summary>
    public IEnumerable<(int Start, int Length)> HighlightRanges(int row)
    {
        if (!hlsearch || lastSearch.Length == 0 || row < 0 || row >= lines.Count || lines[row].Length == 0) return [];
        try
        {
            var result = new List<(int, int)>();
            foreach (Match m in Compile(lastSearch).Matches(lines[row])) { if (m.Length > 0) result.Add((m.Index, m.Length)); if (result.Count > 200) break; }
            return result;
        }
        catch (RegexMatchTimeoutException) { return []; }
    }

    // ───────────────────────── Registers ─────────────────────────

    private void Store(string text, bool linewise, bool yank, bool block = false)
    {
        char? name = selectedRegister;
        if (name == '_') return;
        var reg = new Reg(text, linewise, block);
        if (name is char letter && char.IsAsciiLetter(letter))
        {
            char key = char.ToLowerInvariant(letter);
            if (char.IsAsciiLetterUpper(letter) && registers.TryGetValue(key, out var old))
                reg = old.Linewise || linewise ? new Reg(old.Text + "\n" + text, true) : new Reg(old.Text + text, false);
            registers[key] = reg;
        }
        else if (yank) registers['0'] = reg;
        else if (linewise || text.Contains('\n'))
        {
            for (char c = '9'; c > '1'; c--) if (registers.TryGetValue((char)(c - 1), out var previous)) registers[c] = previous;
            registers['1'] = reg;
        }
        else registers['-'] = reg;
        registers['"'] = reg;
    }

    private Reg? Fetch()
    {
        char name = selectedRegister ?? '"';
        if (char.IsAsciiLetterUpper(name)) name = char.ToLowerInvariant(name);
        if (name == '_') return null;
        if (name == ':') return lastEx.Length > 0 ? new Reg(lastEx, false) : null;
        if (name == '/') return lastSearch.Length > 0 ? new Reg(lastSearch, false) : null;
        return registers.TryGetValue(name, out var reg) && (reg.Text.Length > 0 || reg.Linewise) ? reg : null;
    }

    // ───────────────────────── Operators ─────────────────────────

    private void Operator(string op, Move m)
    {
        var w = Current;
        if (m.Linewise) { LineOperator(op, Math.Min(w.Row, m.Row), Math.Max(w.Row, m.Row)); return; }
        int a = Index(w.Row, w.Col), b = Index(m.Row, m.Col);
        if (a > b) (a, b) = (b, a);
        if (m.Inclusive) b++;
        var (sr, sc) = Position(a); var (er, ec) = Position(b);
        // Vim's exclusive rule: an end in column 0 of a later line stops at the end of the previous line.
        if (!m.Inclusive && ec == 0 && er > sr) { er--; ec = lines[er].Length; if (sc <= FirstNonBlank(sr) && lines[sr].Trim().Length > 0) { LineOperator(op, sr, er); return; } }
        RangeOperator(op, sr, sc, er, ec);
    }

    /// <summary>Charwise operator from (sr, sc) up to, but not including, (er, ec).</summary>
    private void RangeOperator(string op, int sr, int sc, int er, int ec)
    {
        var w = Current;
        if (op is ">" or "<" or "=") { LineOperator(op, sr, er); return; }
        int a = Index(sr, sc), b = Index(er, Math.Min(ec, lines[er].Length));
        string flat = Flat, text = flat[a..Math.Max(a, b)];
        if (op is "gU" or "gu" or "g~")
        {
            BeginUndo(); string changed = flat[..a] + Transform(op, text) + flat[Math.Max(a, b)..];
            lines.Clear(); lines.AddRange(changed.Split('\n')); w.Row = sr; w.Col = sc; return;
        }
        Store(text, false, op == "y");
        if (op == "y") { w.Row = sr; w.Col = sc; Info(text.Contains('\n') ? $"{text.Count(c => c == '\n') + 1}줄 복사됨" : $"{text.Length}글자 복사됨"); return; }
        BeginUndo();
        lines[sr] = lines[sr][..sc] + lines[er][Math.Min(ec, lines[er].Length)..];
        if (er > sr) lines.RemoveRange(sr + 1, er - sr);
        w.Row = sr; w.Col = sc;
        if (op == "c") Mode = ViMode.Insert;
    }

    private void LineOperator(string op, int first, int last)
    {
        var w = Current;
        string block = string.Join("\n", lines.Skip(first).Take(last - first + 1));
        if (op == "y") { Store(block, true, true); Info(last > first ? $"{last - first + 1}줄 복사됨" : "1줄 복사됨"); return; }
        BeginUndo();
        if (op is ">" or "<")
        {
            for (int r = first; r <= last; r++)
            {
                if (op == ">") { if (lines[r].Length > 0) lines[r] = new string(' ', ShiftWidth) + lines[r]; }
                else { int remove = 0; while (remove < ShiftWidth && remove < lines[r].Length && lines[r][remove] == ' ') remove++; if (remove == 0 && lines[r].StartsWith('\t')) remove = 1; lines[r] = lines[r][remove..]; }
            }
            w.Row = first; w.Col = FirstNonBlank(first); if (last > first) Info($"{last - first + 1}줄 {(op == ">" ? "들여쓰기" : "내어쓰기")}"); return;
        }
        if (op == "=")
        {
            int changed = Reindent(first, last); IndentOps++;
            w.Row = first; w.Col = FirstNonBlank(first);
            Info(FileType == "python" ? "python 은 들여쓰기가 문법이라 = 로 바꾸지 않습니다" : $"{last - first + 1}줄 자동 정렬 ({changed}줄 바뀜 · {FileType} 규칙, 한 단계 {ShiftWidth}칸)");
            return;
        }
        if (op is "gU" or "gu" or "g~")
        {
            for (int r = first; r <= last; r++) lines[r] = Transform(op, lines[r]);
            w.Row = first; w.Col = Math.Min(w.Col, Math.Max(0, lines[first].Length - 1)); return;
        }
        Store(block, true, false);
        if (op == "c")
        {
            string indent = Indent(lines[first]);
            lines.RemoveRange(first, last - first + 1); lines.Insert(first, indent);
            w.Row = first; w.Col = indent.Length; Mode = ViMode.Insert; return;
        }
        lines.RemoveRange(first, last - first + 1);
        if (lines.Count == 0) lines.Add("");
        w.Row = Math.Min(first, lines.Count - 1); w.Col = FirstNonBlank(w.Row);
        if (last > first) Info($"{last - first + 1}줄 삭제됨");
    }

    private static string Transform(string op, string text) => op switch
    {
        "gU" => text.ToUpperInvariant(),
        "gu" => text.ToLowerInvariant(),
        _ => Toggle(text)
    };

    private (int First, int Last) VisualRows() => (Math.Min(visualRow, Current.Row), Math.Max(visualRow, Current.Row));
    private (int First, int Last, int Left, int Right) BlockArea() { var (a, b) = VisualRows(); int cur = BlockCursorCol; return (a, b, Math.Min(visualCol, cur), Math.Max(visualCol, cur)); }

    private void VisualOperator(string op)
    {
        var w = Current; var mode = Mode; Mode = ViMode.Normal;
        if (mode == ViMode.VisualBlock) { BlockOperator(op); return; }
        if (mode == ViMode.VisualLine || op is ">" or "<" or "=") { var (a, b) = VisualRows(); LineOperator(op, a, b); return; }
        int s = Index(visualRow, visualCol), e = Index(w.Row, w.Col); if (s > e) (s, e) = (e, s);
        var (sr, sc) = Position(s); var (er, ec) = Position(e);
        RangeOperator(op, sr, sc, er, Math.Min(ec + 1, lines[er].Length));
        if (op != "c") { w.Row = sr; w.Col = sc; }
    }

    private void BlockOperator(string op)
    {
        var w = Current; var (first, last, left, right) = BlockArea(); bool toEnd = blockToEnd; blockToEnd = false;
        int End(int r) => toEnd ? lines[r].Length : Math.Min(lines[r].Length, right + 1);
        switch (op)
        {
            case "y": case "d": case "c":
            {
                var pieces = new List<string>();
                for (int r = first; r <= last; r++) { string line = lines[r]; pieces.Add(left < line.Length ? line[left..End(r)] : ""); }
                Store(string.Join("\n", pieces), false, op == "y", true);
                w.Row = first; w.Col = left;
                if (op == "y") { Info($"{last - first + 1}줄 블록 복사됨"); return; }
                BeginUndo();
                for (int r = first; r <= last; r++) { string line = lines[r]; if (left < line.Length) lines[r] = line[..left] + line[End(r)..]; }
                if (op == "c") StartBlockInsert('I', first, last, left, false);
                return;
            }
            case ">": case "<":
                BeginUndo();
                for (int r = first; r <= last; r++)
                {
                    string line = lines[r]; if (line.Length <= left) continue;
                    if (op == ">") lines[r] = line[..left] + new string(' ', ShiftWidth) + line[left..];
                    else { int remove = 0; while (remove < ShiftWidth && left + remove < line.Length && line[left + remove] == ' ') remove++; lines[r] = line.Remove(left, remove); }
                }
                w.Row = first; w.Col = left; return;
            case "=": LineOperator("=", first, last); return;
            case "gU": case "gu": case "g~":
                BeginUndo();
                for (int r = first; r <= last; r++) { string line = lines[r]; if (left >= line.Length) continue; lines[r] = line[..left] + Transform(op, line[left..End(r)]) + line[End(r)..]; }
                w.Row = first; w.Col = left; return;
        }
    }

    private void StartBlockInsert(char kind, int first, int last, int col, bool toEnd)
    {
        var w = Current; BeginUndo();
        int at = col < 0 ? lines[first].Length : col;
        if (kind == 'A' && lines[first].Length < at) lines[first] = lines[first].PadRight(at);
        if (kind == 'I' && lines[first].Length < at) at = lines[first].Length;
        w.Row = first; w.Col = at;
        blockInsert = (kind, first, last, col, at, lines[first].Length, toEnd);
        Mode = ViMode.Insert;
    }

    private void ApplyBlockInsert((char Kind, int First, int Last, int Col, int InsertCol, int Length, bool ToEnd) block)
    {
        var w = Current; if (w.Row != block.First) return;
        string line = lines[block.First]; int added = line.Length - block.Length;
        if (added <= 0 || block.InsertCol + added > line.Length) return;
        string text = line.Substring(block.InsertCol, added);
        for (int r = block.First + 1; r <= block.Last && r < lines.Count; r++)
        {
            string l = lines[r];
            if (block.Kind == 'I') { if (l.Length < block.Col) continue; lines[r] = l.Insert(block.Col, text); }
            else if (block.ToEnd || block.Col < 0) lines[r] = l + text;
            else { if (l.Length < block.Col) l = l.PadRight(block.Col); lines[r] = l.Insert(block.Col, text); }
        }
        BlockInserts++;
        Info($"{block.Last - block.First + 1}줄에 \"{text}\" 를 {(block.Kind == 'A' ? "덧붙였습니다" : "넣었습니다")}");
    }

    private void VisualReplace(string ch)
    {
        var w = Current; var mode = Mode; Mode = ViMode.Normal; BeginUndo();
        if (mode == ViMode.VisualBlock)
        {
            var (first, last, left, right) = BlockArea(); bool toEnd = blockToEnd; blockToEnd = false;
            for (int r = first; r <= last; r++) { string line = lines[r]; if (left >= line.Length) continue; int end = toEnd ? line.Length : Math.Min(line.Length, right + 1); lines[r] = line[..left] + string.Concat(Enumerable.Repeat(ch, end - left)) + line[end..]; }
            w.Row = first; w.Col = left; return;
        }
        if (mode == ViMode.VisualLine)
        {
            var (a, b) = VisualRows();
            for (int r = a; r <= b; r++) lines[r] = string.Concat(Enumerable.Repeat(ch, lines[r].Length));
            w.Row = a; w.Col = 0; return;
        }
        int s = Index(visualRow, visualCol), e = Index(w.Row, w.Col); if (s > e) (s, e) = (e, s);
        string flat = Flat; e = Math.Min(e + 1, flat.Length);
        var sb = new StringBuilder(flat[..s]); foreach (char c in flat[s..e]) sb.Append(c == '\n' ? "\n" : ch); sb.Append(flat[e..]);
        lines.Clear(); lines.AddRange(sb.ToString().Split('\n'));
        (w.Row, w.Col) = Position(s);
    }

    private void DeleteChars(int row, int col, int count)
    {
        string line = lines[row]; count = Math.Min(count, line.Length - col); if (count <= 0) return;
        Store(line.Substring(col, count), false, false);
        lines[row] = line.Remove(col, count);
    }
    private void StoreChars(int row, int from, int to) { string line = lines[row]; from = Math.Min(from, line.Length); Store(line[from..Math.Max(from, Math.Min(to, line.Length))], false, false); }

    private void PutRegister(Reg reg, bool after, int n)
    {
        var w = Current;
        if (reg.Block)
        {
            var parts = reg.Text.Split('\n'); int width = parts.Max(p => p.Length);
            int col = after && lines[w.Row].Length > 0 ? Math.Min(w.Col + 1, lines[w.Row].Length) : Math.Min(w.Col, lines[w.Row].Length);
            for (int k = 0; k < parts.Length; k++)
            {
                int r = w.Row + k; if (r >= lines.Count) lines.Add("");
                string line = lines[r]; if (line.Length < col) line = line.PadRight(col);
                string piece = line.Length > col ? parts[k].PadRight(width) : parts[k];
                lines[r] = line[..col] + piece + line[col..];
            }
            w.Col = col; return;
        }
        if (reg.Linewise)
        {
            var block = Enumerable.Repeat(reg.Text.Split('\n'), n).SelectMany(x => x).ToList();
            int at = after ? Math.Min(w.Row + 1, lines.Count) : w.Row; lines.InsertRange(at, block); w.Row = at; w.Col = FirstNonBlank(at);
            if (block.Count > 1) Info($"{block.Count}줄 붙여넣음"); return;
        }
        string text = string.Concat(Enumerable.Repeat(reg.Text, n)); string current = lines[w.Row];
        int at2 = after && current.Length > 0 ? Math.Min(w.Col + 1, current.Length) : Math.Min(w.Col, current.Length);
        string combined = current[..at2] + text + current[at2..]; var pieces = combined.Split('\n');
        lines[w.Row] = pieces[0]; lines.InsertRange(w.Row + 1, pieces.Skip(1));
        if (pieces.Length == 1) w.Col = Math.Max(0, at2 + text.Length - 1); else w.Col = at2;
    }

    /// <summary>Adds delta to the first number at or after col. Returns the column of its last digit.</summary>
    private int? IncrementAt(int row, int col, long delta)
    {
        string line = lines[row]; int d = Math.Min(col, line.Length);
        if (d < line.Length && char.IsAsciiDigit(line[d])) { while (d > 0 && char.IsAsciiDigit(line[d - 1])) d--; }
        else
        {
            if (d < line.Length && line[d] == '-' && d + 1 < line.Length && char.IsAsciiDigit(line[d + 1])) d++;
            while (d < line.Length && !char.IsAsciiDigit(line[d])) d++;
            if (d >= line.Length) return null;
        }
        int start = d; if (start > 0 && line[start - 1] == '-' && (start == 1 || !IsWordChar(line[start - 2]))) start--;
        int end = d; while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
        if (end - d > 17 || !long.TryParse(line[start..end], out long value)) return null;
        string digits = line[d..end]; long result = value + delta;
        string text = result.ToString();
        if (digits.Length > 1 && digits[0] == '0') text = (result < 0 ? "-" : "") + Math.Abs(result).ToString().PadLeft(digits.Length, '0');
        lines[row] = line[..start] + text + line[end..];
        return start + text.Length - 1;
    }

    private void VisualIncrement(long delta, bool progressive)
    {
        var w = Current; var mode = Mode; var (first, last) = VisualRows();
        int left = mode == ViMode.VisualBlock ? BlockArea().Left : 0;
        int startCol = mode == ViMode.Visual ? (visualRow < w.Row || visualRow == w.Row && visualCol <= w.Col ? visualCol : w.Col) : left;
        Mode = ViMode.Normal; blockToEnd = false; BeginUndo(); int changed = 0;
        for (int r = first; r <= last; r++)
        {
            int from = mode == ViMode.VisualBlock ? left : mode == ViMode.Visual && r == first ? startCol : 0;
            if (IncrementAt(r, from, progressive ? delta * (changed + 1) : delta) != null) changed++;
        }
        w.Row = first; w.Col = Math.Min(startCol, Math.Max(0, lines[first].Length - 1)); Increments++;
        Info(changed == 0 ? "선택한 줄에 숫자가 없습니다" : $"{changed}줄의 숫자를 {(progressive ? "차례로 " : "")}바꿨습니다");
    }

    // ───────────────────────── Text objects ─────────────────────────

    private readonly record struct TextRange(int StartRow, int StartCol, int EndRow, int EndCol, bool Linewise);

    private void ApplyRange(string op, TextRange r)
    {
        if (r.Linewise) { LineOperator(op, r.StartRow, r.EndRow); return; }
        if (r.StartRow == r.EndRow && r.StartCol == r.EndCol)
        {
            Current.Row = r.StartRow; Current.Col = r.StartCol;
            if (op == "c") { BeginUndo(); Mode = ViMode.Insert; }
            return;
        }
        RangeOperator(op, r.StartRow, r.StartCol, r.EndRow, r.EndCol);
    }

    private TextRange? TextObject(bool inner, string kind, int count)
    {
        var w = Current; int row = w.Row; string line = lines[row];
        switch (kind)
        {
            case "w": case "W":
            {
                if (line.Length == 0) return null;
                bool big = kind == "W"; int c = Math.Min(w.Col, line.Length - 1);
                int Cls(int i) => char.IsWhiteSpace(line[i]) ? 0 : big || IsWordChar(line[i]) ? 1 : 2;
                int s = c, e = c; int cls = Cls(c);
                while (s > 0 && Cls(s - 1) == cls) s--;
                while (e < line.Length && Cls(e) == cls) e++;
                for (int x = 1; x < count && e < line.Length; x++) { int next = Cls(e); while (e < line.Length && Cls(e) == next) e++; }
                if (!inner)
                {
                    if (cls != 0)
                    {
                        if (e < line.Length && char.IsWhiteSpace(line[e])) { while (e < line.Length && char.IsWhiteSpace(line[e])) e++; }
                        else while (s > 0 && char.IsWhiteSpace(line[s - 1])) s--;
                    }
                    else if (e < line.Length) { int next = Cls(e); while (e < line.Length && Cls(e) == next) e++; }
                }
                return new(row, s, row, e, false);
            }
            case "\"": case "'": case "`":
            {
                char q = kind[0]; var quotes = new List<int>();
                for (int i = 0; i < line.Length; i++) if (line[i] == q && (i == 0 || line[i - 1] != '\\')) quotes.Add(i);
                int c = Math.Min(w.Col, Math.Max(0, line.Length - 1)); int open = -1, close = -1;
                for (int p = 0; p + 1 < quotes.Count; p += 2)
                {
                    if (c >= quotes[p] && c <= quotes[p + 1] || quotes[p] > c) { open = quotes[p]; close = quotes[p + 1]; break; }
                }
                if (open < 0) return null;
                if (inner) return new(row, open + 1, row, close, false);
                int end = close + 1, start = open;
                if (end < line.Length && line[end] == ' ') { while (end < line.Length && line[end] == ' ') end++; }
                else while (start > 0 && line[start - 1] == ' ') start--;
                return new(row, start, row, end, false);
            }
            case "(": case ")": case "b": case "{": case "}": case "B": case "[": case "]": case "<": case ">":
            {
                (char open, char close) = kind switch { "(" or ")" or "b" => ('(', ')'), "{" or "}" or "B" => ('{', '}'), "[" or "]" => ('[', ']'), _ => ('<', '>') };
                string s = Flat; int at = Index(row, Math.Min(w.Col, line.Length)); int o = -1;
                if (at < s.Length && s[at] == open) o = at;
                else
                {
                    // On a closing bracket this finds its own opening bracket.
                    int depth = 0;
                    for (int i = at - 1; i >= 0; i--)
                    {
                        if (s[i] == close) depth++;
                        else if (s[i] == open) { if (depth == 0) { o = i; break; } depth--; }
                    }
                }
                for (int x = 1; x < count && o > 0; x++)
                {
                    int depth = 0, found = -1;
                    for (int i = o - 1; i >= 0; i--) { if (s[i] == close) depth++; else if (s[i] == open) { if (depth == 0) { found = i; break; } depth--; } }
                    if (found < 0) break; o = found;
                }
                if (o < 0) return null;
                int level = 0, cl = -1;
                for (int i = o; i < s.Length; i++) { if (s[i] == open) level++; else if (s[i] == close) { level--; if (level == 0) { cl = i; break; } } }
                if (cl < 0) return null;
                var (openRow, openCol) = Position(o); var (closeRow, closeCol) = Position(cl);
                if (!inner) return new(openRow, openCol, closeRow, closeCol + 1, false);
                // A block whose braces sit on their own lines selects the lines in between.
                if (closeRow > openRow && openCol == lines[openRow].TrimEnd().Length - 1 && lines[closeRow][..closeCol].Trim().Length == 0)
                {
                    if (closeRow - openRow < 2) return new(openRow, openCol + 1, openRow, openCol + 1, false);
                    return new(openRow + 1, 0, closeRow - 1, lines[closeRow - 1].Length, true);
                }
                var (innerRow, innerCol) = Position(o + 1);
                return new(innerRow, innerCol, closeRow, closeCol, false);
            }
            case "p":
            {
                bool blank = line.Trim().Length == 0; int a = row, b = row;
                while (a > 0 && (lines[a - 1].Trim().Length == 0) == blank) a--;
                while (b + 1 < lines.Count && (lines[b + 1].Trim().Length == 0) == blank) b++;
                if (!inner)
                {
                    int extra = b;
                    while (extra + 1 < lines.Count && (lines[extra + 1].Trim().Length == 0) != blank) extra++;
                    if (extra > b) b = extra; else while (a > 0 && (lines[a - 1].Trim().Length == 0) != blank) a--;
                }
                return new(a, 0, b, lines[b].Length, true);
            }
        }
        return null;
    }

    // ───────────────────────── Auto indent (=) ─────────────────────────

    private int Reindent(int first, int last)
    {
        if (FileType == "python") return 0;
        var levels = FileType == "sh" ? ShellLevels() : BraceLevels();
        int changed = 0;
        for (int r = first; r <= last; r++)
        {
            string body = lines[r].TrimStart(' ', '\t');
            string text = body.Length == 0 ? "" : levels[r] + body;
            if (text != lines[r]) { lines[r] = text; changed++; }
        }
        return changed;
    }

    private string Pad(int level) => new(' ', Math.Max(0, level) * ShiftWidth);

    private string[] BraceLevels()
    {
        var result = new string[lines.Count]; int depth = 0; bool comment = false; string? previous = null;
        var cases = new Stack<int>();
        for (int r = 0; r < lines.Count; r++)
        {
            string t = lines[r].Trim();
            if (comment) { result[r] = Pad(depth) + " "; if (t.Contains("*/")) comment = false; continue; }
            if (t.StartsWith('#')) { result[r] = ""; continue; }
            int lead = 0; foreach (char c in t) { if (c is '}' or ')' or ']') lead++; else break; }
            int level = Math.Max(0, depth - lead);
            bool label = Regex.IsMatch(t, @"^(case\b[^:]*|default\s*):");
            while (cases.Count > 0 && depth - lead < cases.Peek()) cases.Pop();
            if (cases.Count > 0 && !label && depth - lead >= cases.Peek()) level++;
            if (label && (cases.Count == 0 || cases.Peek() != depth)) cases.Push(depth);
            if (previous != null && lead == 0 && !t.StartsWith('{') && Regex.IsMatch(previous, @"^(\}\s*)?((if|for|while)\b.*\)|else(\s+if\b.*\))?)$")) level++;
            result[r] = t.Length == 0 ? "" : Pad(level);
            depth = Math.Max(0, depth + BraceDelta(t, ref comment));
            if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith("/*")) previous = StripLineComment(t);
        }
        return result;
    }

    private static string StripLineComment(string t)
    {
        bool inString = false; char quote = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (inString) { if (c == '\\') i++; else if (c == quote) inString = false; continue; }
            if (c is '"' or '\'') { inString = true; quote = c; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] is '/' or '*') return t[..i].TrimEnd();
        }
        return t;
    }

    private static int BraceDelta(string t, ref bool comment)
    {
        int delta = 0; bool inString = false; char quote = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (comment) { if (c == '*' && i + 1 < t.Length && t[i + 1] == '/') { comment = false; i++; } continue; }
            if (inString) { if (c == '\\') i++; else if (c == quote) inString = false; continue; }
            if (c is '"' or '\'') { inString = true; quote = c; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') break;
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { comment = true; i++; continue; }
            if (c is '{' or '(' or '[') delta++; else if (c is '}' or ')' or ']') delta--;
        }
        return delta;
    }

    private string[] ShellLevels()
    {
        var result = new string[lines.Count]; int depth = 0; var bodies = new Stack<bool>();
        for (int r = 0; r < lines.Count; r++)
        {
            string t = lines[r].Trim();
            if (t.Length == 0) { result[r] = ""; continue; }
            if (t.StartsWith('#')) { result[r] = Pad(depth); continue; }
            string code = Regex.Replace(t, @"\s+#.*$", "");
            string word = Regex.Match(code, @"^[^\s;]+").Value;
            int level = depth;
            if (word is "fi" or "done" or "}") { depth = Math.Max(0, depth - 1); level = depth; }
            else if (word == "esac") { if (bodies.Count > 0 && bodies.Pop()) depth--; depth = Math.Max(0, depth - 1); level = depth; }
            else if (word is "else" or "elif") level = Math.Max(0, depth - 1);
            else if (bodies.Count > 0 && Regex.IsMatch(code, @"^[^()\s][^()]*\)") && !code.StartsWith("$("))
            {
                if (bodies.Peek()) { depth--; bodies.Pop(); bodies.Push(false); }
                level = depth;
                if (!code.EndsWith(";;")) { depth++; bodies.Pop(); bodies.Push(true); }
                result[r] = Pad(level); continue;
            }
            result[r] = Pad(level);
            if (word == "case" && Regex.IsMatch(code, @"\bin$")) { depth++; bodies.Push(false); continue; }
            if (code == ";;" || code.EndsWith(";;")) { if (bodies.Count > 0 && bodies.Peek()) { depth--; bodies.Pop(); bodies.Push(false); } continue; }
            if (word is "else" or "elif") continue;
            if (Regex.IsMatch(code, @"(^|[;\s])(then|do)$") || code.EndsWith('{')) depth++;
        }
        return result;
    }

    // ───────────────────────── Insert mode ─────────────────────────

    private void InsertKey(string key)
    {
        var w = Current; string line = lines[w.Row]; w.Col = Math.Min(w.Col, line.Length);
        if (key is not ("<C-n>" or "<C-p>")) completion = null;
        switch (key)
        {
            case "<Esc>":
                if (blockInsert is { } block) { blockInsert = null; ApplyBlockInsert(block); }
                else if (insertRepeat is { } repeat) { insertRepeat = null; ApplyInsertRepeat(repeat); }
                Mode = ViMode.Normal; if (w.Col > 0) w.Col--; w.Want = w.Col;
                if (recordingInsert && !replaying) { lastChange = changeKeys.ToList(); recordingInsert = false; }
                return;
            case "<CR>":
            {
                blockInsert = null;
                string indent = Indent(line); lines[w.Row] = line[..w.Col]; lines.Insert(w.Row + 1, indent + line[w.Col..].TrimStart(' '));
                w.Row++; w.Col = indent.Length; return;
            }
            case "<BS>":
                if (w.Col > 0) { lines[w.Row] = line.Remove(w.Col - 1, 1); w.Col--; }
                else if (w.Row > 0) { w.Col = lines[w.Row - 1].Length; lines[w.Row - 1] += line; lines.RemoveAt(w.Row); w.Row--; }
                return;
            case "<Del>":
                if (w.Col < line.Length) lines[w.Row] = line.Remove(w.Col, 1);
                else if (w.Row + 1 < lines.Count) { lines[w.Row] += lines[w.Row + 1]; lines.RemoveAt(w.Row + 1); }
                return;
            case "<Tab>": lines[w.Row] = line.Insert(w.Col, new string(' ', TabStop)); w.Col += TabStop; return;
            case "<C-w>":
            {
                int c = w.Col; while (c > 0 && line[c - 1] == ' ') c--; while (c > 0 && line[c - 1] != ' ') c--;
                lines[w.Row] = line.Remove(c, w.Col - c); w.Col = c; return;
            }
            case "<C-u>": lines[w.Row] = line[w.Col..]; w.Col = 0; return;
            case "<C-n>": case "<C-p>": Complete(key == "<C-n>"); return;
            case "<Left>": if (w.Col > 0) w.Col--; return;
            case "<Right>": if (w.Col < line.Length) w.Col++; return;
            case "<Up>": if (w.Row > 0) { w.Row--; w.Col = Math.Min(w.Col, lines[w.Row].Length); } return;
            case "<Down>": if (w.Row + 1 < lines.Count) { w.Row++; w.Col = Math.Min(w.Col, lines[w.Row].Length); } return;
            case "<Home>": w.Col = 0; return;
            case "<End>": w.Col = line.Length; return;
        }
        if (key.Length != 1 && !(key.Length == 2 && char.IsSurrogatePair(key, 0))) return;
        lines[w.Row] = line.Insert(w.Col, key); w.Col += key.Length;
    }

    private void ApplyInsertRepeat((int Count, int Row, int Length, bool Line) repeat)
    {
        var w = Current;
        if (repeat.Line)
        {
            string text = lines[w.Row];
            for (int x = 1; x < repeat.Count; x++) lines.Insert(w.Row + 1, text);
            w.Row += repeat.Count - 1; w.Col = Math.Min(w.Col, lines[w.Row].Length); return;
        }
        if (w.Row != repeat.Row) return;
        string line = lines[w.Row]; int added = line.Length - repeat.Length;
        if (added <= 0 || w.Col - added < 0) return;
        string typed = line.Substring(w.Col - added, added);
        string more = string.Concat(Enumerable.Repeat(typed, repeat.Count - 1));
        lines[w.Row] = line.Insert(w.Col, more); w.Col += more.Length;
    }

    private void Complete(bool forward)
    {
        var w = Current; string line = lines[w.Row];
        if (completion is not { } state || state.Row != w.Row)
        {
            int start = w.Col; while (start > 0 && IsWordChar(line[start - 1])) start--;
            string prefix = line[start..w.Col]; int cursor = Index(w.Row, start);
            var after = new List<string>(); var before = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(Flat, @"[\p{L}\p{N}_]+"))
            {
                if (m.Index == cursor || m.Length <= prefix.Length || !m.Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
                (m.Index > cursor ? after : before).Add(m.Value);
            }
            var words = after.Concat(before).Where(seen.Add).ToList();
            state = (w.Row, start, words, -1, prefix); completion = state;
        }
        if (state.Words.Count == 0) { Fail("일치하는 단어가 없습니다 (Ctrl+n 자동 완성)"); return; }
        int total = state.Words.Count, index = state.Index + (forward ? 1 : -1);
        if (index >= total) index = -1; if (index < -1) index = total - 1;
        string word = index < 0 ? state.Prefix : state.Words[index];
        int end = Math.Min(w.Col, line.Length);
        lines[w.Row] = line[..state.Start] + word + line[end..]; w.Col = state.Start + word.Length;
        completion = state with { Index = index }; CompletionCount++;
        Info(index < 0 ? "-- 자동 완성 · 처음 입력으로 돌아왔습니다" : $"-- 키워드 자동 완성 (^N ^P) {index + 1} / {total}");
    }

    // ───────────────────────── Command line ─────────────────────────

    private void StartCommand(char prefix)
    {
        commandFromVisual = IsVisual;
        commandReturn = prefix == ':' ? ViMode.Normal : Mode;
        commandPrefix = prefix; commandLine = prefix == ':' && commandFromVisual ? "'<,'>" : ""; Mode = ViMode.Command;
        historyIndex = -1; tabState = null;
    }

    private void CommandKey(string key)
    {
        if (key != "<Tab>") tabState = null;
        switch (key)
        {
            case "<Esc>": Mode = commandPrefix == ':' && commandFromVisual ? ViMode.Normal : commandReturn; commandLine = ""; return;
            case "<BS>": if (commandLine.Length == 0) { Mode = commandReturn; return; } commandLine = commandLine[..^1]; return;
            case "<C-w>": { var trimmed = commandLine.TrimEnd(); int cut = trimmed.LastIndexOf(' ') + 1; commandLine = trimmed[..cut]; return; }
            case "<C-u>": commandLine = ""; return;
            case "<Tab>": if (commandPrefix == ':') CompleteCommandLine(); return;
            case "<Up>": case "<Down>": RecallHistory(key == "<Up>"); return;
            case "<CR>":
            {
                string text = commandLine; char prefix = commandPrefix; Mode = commandReturn; commandLine = "";
                var history = prefix == ':' ? exHistory : searchHistory;
                if (text.Trim().Length > 0 && (history.Count == 0 || history[^1] != text)) { history.Add(text); if (history.Count > 50) history.RemoveAt(0); }
                if (prefix == ':')
                {
                    if (commandFromVisual) Mode = ViMode.Normal;
                    if (text.Trim().Length > 0) lastEx = text;
                    RunCommand(text);
                }
                else
                {
                    if (text.Length == 0) text = lastSearch;
                    if (text.Length == 0) { Fail("검색어를 입력하세요."); return; }
                    lastSearch = text; lastSearchForward = prefix == '/'; hlsearch = true; SearchCount++;
                    if (Search(text, lastSearchForward, 1) is { } p) { if (!IsVisual) SetJump(); Current.Row = p.Row; Current.Col = p.Col; Current.Want = p.Col; }
                }
                return;
            }
        }
        if (key.Length == 1 || key.Length == 2 && char.IsSurrogatePair(key, 0)) commandLine += key;
    }

    private void RecallHistory(bool older)
    {
        var history = commandPrefix == ':' ? exHistory : searchHistory;
        if (historyIndex < 0) historyDraft = commandLine;
        var matches = history.Where(h => h.StartsWith(historyDraft, StringComparison.Ordinal)).Reverse().ToList();
        if (matches.Count == 0) return;
        historyIndex = Math.Clamp(historyIndex + (older ? 1 : -1), -1, matches.Count - 1);
        commandLine = historyIndex < 0 ? historyDraft : matches[historyIndex];
    }

    private static readonly string[] ExCommands = ["colorscheme", "copy", "delete", "edit", "global", "join", "marks", "move", "nohlsearch", "normal", "only", "print", "quit", "redo", "registers", "set", "sort", "source", "split", "substitute", "syntax", "undo", "vglobal", "vsplit", "write", "wq", "xit", "yank"];
    private static readonly string[] SetNames = ["number", "nonumber", "relativenumber", "norelativenumber", "hlsearch", "nohlsearch", "ignorecase", "noignorecase", "smartcase", "nosmartcase", "list", "nolist", "cursorline", "nocursorline", "shiftwidth=", "tabstop=", "filetype="];

    private void CompleteCommandLine()
    {
        if (tabState is { } state && commandLine == state.Head + state.Items[state.Index])
        {
            int next = (state.Index + 1) % state.Items.Count; tabState = state with { Index = next }; commandLine = state.Head + state.Items[next]; return;
        }
        Match m; IEnumerable<string> pool; string head, prefix;
        if ((m = Regex.Match(commandLine, @"^(\s*(?:colo|colorscheme|color|colors)\s+)(\S*)$")).Success) pool = ViColorSchemes.Names;
        else if ((m = Regex.Match(commandLine, @"^(\s*(?:set|se)\s+(?:\S+\s+)*)(\S*)$")).Success) pool = SetNames;
        else if ((m = Regex.Match(commandLine, @"^(\s*(?:'<,'>|%)?)([a-z]*)$")).Success) pool = ExCommands;
        else return;
        head = m.Groups[1].Value; prefix = m.Groups[2].Value;
        var items = pool.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (items.Count == 0) return;
        tabState = (head, items, 0); commandLine = head + items[0];
    }

    /// <summary>Runs an ex command (without the leading ':').</summary>
    public void RunCommand(string input)
    {
        try { Ex(input); }
        catch (ViError error) { Fail(error.Message); }
        catch (RegexMatchTimeoutException) { Fail("검색 패턴이 너무 복잡합니다"); }
    }

    private static bool IsDelimiter(char c) => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '\\' or '"' or '|');

    private void Ex(string input)
    {
        var w = Current; string cmd = input.TrimStart(' ', '\t', ':');
        if (cmd.Trim().Length == 0) return;
        int first = w.Row, last = w.Row; bool ranged = false;
        if (cmd.StartsWith('%')) { first = 0; last = lines.Count - 1; cmd = cmd[1..]; ranged = true; }
        else
        {
            int i = 0; int? a = Address(cmd, ref i, w.Row);
            if (a != null)
            {
                first = last = a.Value; ranged = true;
                if (i < cmd.Length && cmd[i] is ',' or ';')
                {
                    bool relative = cmd[i] == ';'; i++;
                    int? b = Address(cmd, ref i, relative ? Math.Clamp(first, 0, lines.Count - 1) : w.Row);
                    if (b == null) throw new ViError("E14: 범위가 올바르지 않습니다");
                    last = b.Value;
                }
                cmd = cmd[i..];
            }
        }
        if (first > last) (first, last) = (last, first);
        int lo = Math.Clamp(first, 0, lines.Count - 1), hi = Math.Clamp(last, 0, lines.Count - 1);
        string body = cmd.TrimStart();
        if (body.Trim().Length == 0)
        {
            if (ranged) { SetJump(); w.Row = hi; w.Col = FirstNonBlank(hi); }
            return;
        }
        if (body[0] == 's' && body.Length > 1 && IsDelimiter(body[1])) { Substitute(body[1..], ranged ? lo : w.Row, ranged ? hi : w.Row); return; }
        if (TryGlobal(body, ranged, lo, hi)) return;
        if (body[0] is '>' or '<') { int times = body.TakeWhile(c => c == body[0]).Count(); for (int x = 0; x < times; x++) LineOperator(body[0].ToString(), lo, hi); return; }
        if (body[0] == '&')
        {
            if (lastSubstitute is not { } previous) throw new ViError("E35: 이전에 바꾼 내용이 없습니다");
            SubstituteWith(previous.Pattern, previous.Replacement, body.StartsWith("&&") ? previous.Flags : body[1..].Trim(), lo, hi); return;
        }
        if (body[0] == '!') throw new ViError("연습창에서는 :! 셸 명령을 실행하지 않습니다 · [터미널 명령어] 연습을 이용하세요");
        if (body[0] == '=') { Info((ranged ? hi + 1 : lines.Count).ToString()); return; }
        if (body[0] == '#') { Print(lo, hi, true); return; }
        string name = new(body.TakeWhile(char.IsAsciiLetter).ToArray());
        string rest = body[name.Length..]; bool bang = rest.StartsWith('!'); if (bang) rest = rest[1..];
        string arg = rest.Trim();
        switch (name)
        {
            case "w": case "write": Write(arg); return;
            case "sav": case "saveas": if (arg.Length == 0) throw new ViError("E471: 파일 이름이 필요합니다"); if (!Embedded) FileName = arg; Write(Embedded ? arg : ""); return;
            case "q": case "quit": case "clo": case "close": Quit(bang, name is "clo" or "close"); return;
            case "qa": case "qall": case "quita": case "quitall": Only(silent: true); Quit(bang, false); return;
            case "wq": case "x": case "xit": case "exit": case "wqa": case "wqall": case "xa": case "xall":
                if (name is "x" or "xit" or "exit" && !Modified && !Embedded) { Quit(true, false); return; }
                if (name is "wqa" or "wqall" or "xa" or "xall") Only(silent: true);
                Write(arg); if (!MessageIsError) Quit(true, false, saved: true); return;
            case "wa": case "wall": Write(""); return;
            case "e": case "edit":
                if (arg.Length > 0) throw new ViError("연습창은 다른 파일을 열 수 없습니다 · 지금 파일만 편집합니다");
                if (Modified && !bang) throw new ViError("E37: 저장하지 않은 변경이 있습니다 (:e! 로 버리고 다시 읽기)");
                BeginUndo(); lines.Clear(); lines.AddRange(saved); if (bang) DiscardCount++; Info("마지막으로 저장한 상태로 되돌렸습니다"); return;
            case "sp": case "split": case "new": Split(false); return;
            case "vs": case "vsp": case "vsplit": case "vnew": case "vne": Split(true); return;
            case "on": case "only": Only(); return;
            case "noh": case "nohlsearch": hlsearch = false; return;
            case "se": case "set": case "setl": case "setlocal": SetOptions(arg); return;
            case "d": case "de": case "del": case "delete": WithRegister(arg, () => LineOperator("d", lo, hi)); return;
            case "y": case "ya": case "yank": WithRegister(arg, () => LineOperator("y", lo, hi)); return;
            case "pu": case "put": WithRegister(arg, () => { var reg = Fetch() ?? throw new ViError("E353: 레지스터가 비어 있습니다"); w.Row = hi; PutRegister(reg with { Linewise = true }, !bang, 1); }); return;
            case "u": case "undo": Undo(1); return;
            case "red": case "redo": Redo(1); return;
            case "h": case "help": Info("오른쪽 [단축키 모음]에서 명령과 설명을 볼 수 있습니다."); return;
            case "m": case "mo": case "move":
            {
                int p = 0; int? target = Address(arg, ref p, w.Row) ?? throw new ViError("E14: 옮길 위치를 입력하세요 (예: :m 0, :m $, :m +1)");
                MoveLines(lo, hi, Math.Clamp(target.Value, -1, lines.Count - 1)); return;
            }
            case "t": case "co": case "copy":
            {
                int p = 0; int? target = Address(arg, ref p, w.Row) ?? throw new ViError("E14: 복사할 위치를 입력하세요 (예: :t ., :t $, :t 0)");
                CopyLines(lo, hi, Math.Clamp(target.Value, -1, lines.Count - 1)); return;
            }
            case "j": case "join": Join(lo, ranged && hi > lo ? hi : Math.Min(lines.Count - 1, lo + 1), !bang); return;
            case "sor": case "sort": Sort(ranged ? lo : 0, ranged ? hi : lines.Count - 1, bang, arg); return;
            case "norm": case "normal": Normal(rest.StartsWith(' ') ? rest[1..] : rest, ranged, lo, hi); return;
            case "p": case "print": case "nu": case "number": case "P": Print(lo, hi, name is "nu" or "number"); return;
            case "reg": case "registers": case "di": case "display": ShowRegisters(arg); return;
            case "marks": ShowMarks(); return;
            case "ma": case "mark": case "k":
                if (arg.Length != 1 || !char.IsAsciiLetter(arg[0])) throw new ViError("E191: 마크 이름은 a~z 입니다");
                marks[arg[0]] = (hi, 0); MarkSets++; return;
            case "delm": case "delmarks":
                if (bang) { foreach (var key in marks.Keys.Where(char.IsAsciiLetterLower).ToList()) marks.Remove(key); return; }
                foreach (char c in arg.Where(char.IsAsciiLetter)) marks.Remove(c); return;
            case "so": case "source": Source(arg); return;
            case "colo": case "color": case "colorscheme": case "colors":
                if (arg.Length == 0) { Info(ColorScheme); return; }
                ColorScheme = ViColorSchemes.Find(arg) ?? throw new ViError($"E185: 색 구성표를 찾을 수 없습니다: {arg} · :colo 다음에 Tab 으로 이름을 고를 수 있습니다");
                ColorChanges++; return;
            case "sy": case "syn": case "synt": case "syntax":
                switch (arg)
                {
                    case "on": case "enable": SyntaxOn = true; SyntaxChanges++; return;
                    case "off": case "clear": case "reset": SyntaxOn = false; SyntaxChanges++; return;
                    case "": Info(SyntaxOn ? "syntax on" : "syntax off"); return;
                }
                throw new ViError("E410: :syntax on 또는 :syntax off 로 입력하세요");
            case "filet": case "filetype": if (arg.Length == 0) Info("filetype=" + FileType); return;
            case "let": case "map": case "nmap": case "nnoremap": case "noremap": case "imap": case "inoremap": case "vmap": case "vnoremap": case "xnoremap": case "au": case "autocmd": case "function": case "call":
                throw new ViError($":{name} 는 연습창에서 실행하지 않습니다 (실제 .vimrc 에서는 쓸 수 있습니다)");
            case "bn": case "bnext": case "bp": case "bprevious": case "ls": case "buffers": case "b": case "buffer":
                throw new ViError("연습창은 파일(버퍼)이 하나뿐입니다");
        }
        if (body.Trim() == "$") { SetJump(); w.Row = lines.Count - 1; w.Col = FirstNonBlank(w.Row); return; }
        throw new ViError($"E492: 알 수 없는 명령입니다: {input.Trim()}");
    }

    private void WithRegister(string arg, Action action)
    {
        var saveRegister = selectedRegister;
        if (arg.Length > 0)
        {
            if (arg.Length != 1 || !(char.IsAsciiLetterOrDigit(arg[0]) || arg[0] is '"' or '_' or '-')) throw new ViError("E488: 레지스터 이름이 올바르지 않습니다: " + arg);
            selectedRegister = arg[0];
        }
        try { action(); } finally { selectedRegister = saveRegister; }
    }

    private int? Address(string s, ref int i, int current)
    {
        if (i >= s.Length) return null;
        int? value = null; char c = s[i];
        if (c == '.') { i++; value = current; }
        else if (c == '$') { i++; value = lines.Count - 1; }
        else if (char.IsAsciiDigit(c)) { int start = i; while (i < s.Length && char.IsAsciiDigit(s[i])) i++; value = int.TryParse(s[start..i], out int number) ? Math.Min(number, 1_000_000) - 1 : lines.Count - 1; }
        else if (c == '\'' && i + 1 < s.Length) { char m = s[i + 1]; i += 2; value = MarkRow(m); }
        else if (c is '+' or '-') value = current;
        if (value == null) return null;
        while (i < s.Length && s[i] is '+' or '-')
        {
            char sign = s[i++]; int start = i; while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            int amount = i > start && int.TryParse(s[start..i], out int parsed) ? Math.Min(parsed, 1_000_000) : 1;
            value += sign == '+' ? amount : -amount;
        }
        return value;
    }

    private int MarkRow(char name)
    {
        if (name == '`') name = '\'';
        if (marks.TryGetValue(name, out var mark)) return Math.Clamp(mark.Row, 0, lines.Count - 1);
        if (name == '<') return VisualRows().First;
        if (name == '>') return VisualRows().Last;
        throw new ViError($"E20: 마크 {name} 가 없습니다");
    }

    private void Write(string arg)
    {
        string bytes = Encoding.UTF8.GetByteCount(Text).ToString("N0");
        if (Embedded)
        {
            string name = arg.Length > 0 ? arg : FileName;
            string? error = SaveHandler?.Invoke(name, Text);
            if (error != null) throw new ViError(error);
            if (arg.Length == 0 || arg == FileName) saved = [.. lines];
            SaveCount++; Info($"\"{name}\" {lines.Count}줄, {bytes}바이트 저장됨"); return;
        }
        if (arg.Length > 0) FileName = arg;
        saved = [.. lines]; SaveCount++;
        Info($"\"{FileName}\" {lines.Count}줄, {bytes}바이트 저장됨 (연습용 · 실제 파일은 만들지 않습니다)");
    }

    private bool? GetBool(string key) => key switch
    {
        "nu" or "number" => ShowNumbers, "rnu" or "relativenumber" => RelativeNumbers, "hls" or "hlsearch" => hlsearch,
        "ic" or "ignorecase" => IgnoreCase, "scs" or "smartcase" => SmartCase, "list" => ListChars, "cul" or "cursorline" => CursorLine,
        _ => null
    };

    private void SetBool(string key, bool value)
    {
        switch (key)
        {
            case "nu": case "number": ShowNumbers = value; break;
            case "rnu": case "relativenumber": RelativeNumbers = value; break;
            case "hls": case "hlsearch": hlsearch = value; break;
            case "ic": case "ignorecase": IgnoreCase = value; break;
            case "scs": case "smartcase": SmartCase = value; break;
            case "list": ListChars = value; break;
            case "cul": case "cursorline": CursorLine = value; break;
        }
        OptionChanges++;
    }

    private static readonly HashSet<string> QuietOptions =
    [
        "nocompatible", "nocp", "compatible", "cp", "ai", "autoindent", "noai", "noautoindent", "si", "smartindent", "nosi", "nosmartindent", "cin", "cindent", "nocin", "nocindent",
        "is", "incsearch", "nois", "noincsearch", "ru", "ruler", "noru", "noruler", "sc", "showcmd", "nosc", "noshowcmd", "smd", "showmode", "nosmd", "noshowmode", "wrap", "nowrap",
        "paste", "nopaste", "ws", "wrapscan", "nows", "nowrapscan", "sm", "showmatch", "nosm", "noshowmatch", "et", "expandtab", "noet", "noexpandtab", "sta", "smarttab", "nosta", "nosmarttab",
        "title", "notitle", "wmnu", "wildmenu", "nowildmenu", "lz", "lazyredraw", "vb", "visualbell", "novb", "eb", "errorbells", "noeb", "noerrorbells", "hidden", "nohidden", "confirm",
        "autoread", "ar", "backup", "nobackup", "swapfile", "noswapfile", "wb", "writebackup", "nowb", "nowritebackup", "magic", "nomagic", "spell", "nospell", "tf", "ttyfast", "termguicolors", "tgc"
    ];
    private static readonly HashSet<string> QuietValueOptions =
    [
        "bg", "background", "mouse", "enc", "encoding", "fenc", "fileencoding", "fencs", "fileencodings", "ls", "laststatus", "so", "scrolloff", "bs", "backspace", "hi", "history",
        "ul", "undolevels", "tw", "textwidth", "cc", "colorcolumn", "t_Co", "cb", "clipboard", "mps", "matchpairs", "ff", "fileformat", "ffs", "fileformats", "nf", "nrformats",
        "wim", "wildmode", "stl", "statusline", "ch", "cmdheight", "ss", "sidescroll", "siso", "sidescrolloff", "shm", "shortmess", "lcs", "listchars", "fo", "formatoptions",
        "cino", "cinoptions", "sts", "softtabstop", "ve", "virtualedit", "wig", "wildignore", "tm", "timeoutlen", "ttm", "ttimeoutlen", "gfn", "guifont", "shell", "sh", "lines", "columns"
    ];

    private void SetOptions(string arg)
    {
        if (arg.Trim().Length == 0)
        {
            var changed = new List<string>();
            if (ShowNumbers) changed.Add("number"); if (RelativeNumbers) changed.Add("relativenumber"); if (!hlsearch) changed.Add("nohlsearch");
            if (IgnoreCase) changed.Add("ignorecase"); if (SmartCase) changed.Add("smartcase"); if (ListChars) changed.Add("list"); if (CursorLine) changed.Add("cursorline");
            changed.Add($"shiftwidth={ShiftWidth}"); changed.Add($"tabstop={TabStop}"); changed.Add($"filetype={FileType}");
            Info("--- 설정 ---  " + string.Join("  ", changed)); return;
        }
        var output = new List<string>();
        foreach (var raw in arg.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('"')) break;
            string item = raw; bool query = item.EndsWith('?'); if (query) item = item[..^1];
            int eq = item.IndexOf('=');
            string name = (eq >= 0 ? item[..eq] : item).TrimEnd('+', '-', '^'), value = eq >= 0 ? item[(eq + 1)..] : "";
            if (eq >= 0) { if (!SetValue(name, value)) throw new ViError($"E518: 알 수 없는 설정입니다: {raw}"); continue; }
            if (query) { output.Add(Describe(name)); continue; }
            if (name.EndsWith('&')) continue;
            bool toggle = name.EndsWith('!'); if (toggle) name = name[..^1];
            string key = name; bool state = true;
            if (name.StartsWith("inv") && GetBool(name[3..]) != null) { toggle = true; key = name[3..]; }
            else if (name.StartsWith("no") && GetBool(name[2..]) != null) { key = name[2..]; state = false; }
            if (GetBool(key) is bool current) { SetBool(key, toggle ? !current : state); continue; }
            if (key is "sw" or "shiftwidth" or "ts" or "tabstop" or "ft" or "filetype") { output.Add(Describe(key)); continue; }
            if (QuietOptions.Contains(name) || QuietValueOptions.Contains(name)) continue;
            throw new ViError($"E518: 알 수 없는 설정입니다: {raw} · 연습창 지원: nu rnu hls ic scs list cul sw= ts= ft=");
        }
        if (output.Count > 0) Info(string.Join("  ", output));
    }

    private bool SetValue(string name, string value)
    {
        int Size() => int.TryParse(value, out int number) && number >= 0 && number <= 16 ? number : throw new ViError($"E521: 0~16 사이의 숫자를 입력하세요: {name}={value}");
        switch (name)
        {
            case "sw": case "shiftwidth": { int size = Size(); ShiftWidth = size == 0 ? TabStop : size; OptionChanges++; return true; }
            case "ts": case "tabstop": { int size = Size(); TabStop = Math.Max(1, size); OptionChanges++; return true; }
            case "ft": case "filetype": fileTypeOverride = value.Length == 0 ? null : value.ToLowerInvariant(); OptionChanges++; return true;
            case "syn": case "syntax":
                if (value is "on" or "off") SyntaxOn = value == "on"; else fileTypeOverride = value.ToLowerInvariant();
                SyntaxChanges++; return true;
        }
        return QuietValueOptions.Contains(name);
    }

    private string Describe(string name) => name switch
    {
        "sw" or "shiftwidth" => $"shiftwidth={ShiftWidth}",
        "ts" or "tabstop" => $"tabstop={TabStop}",
        "ft" or "filetype" => $"filetype={FileType}",
        _ => GetBool(name) is bool value ? (value ? "" : "no") + name : throw new ViError($"E518: 알 수 없는 설정입니다: {name}")
    };

    private void Source(string arg)
    {
        if (arg != "%") throw new ViError("연습창에서는 :so % (지금 편집 중인 파일)만 실행할 수 있습니다");
        int ran = 0; string? error = null; int errorLine = 0; var script = lines.ToList();
        for (int r = 0; r < script.Count; r++)
        {
            string line = script[r].Trim();
            if (line.Length == 0 || line.StartsWith('"')) continue;
            Message = ""; MessageIsError = false; RunCommand(line); ran++;
            if (MessageIsError && error == null) { error = Message; errorLine = r + 1; }
        }
        Sources++;
        if (error != null) Fail($"{errorLine}번째 줄: {error}");
        else Info($"\"{FileName}\" 의 설정 {ran}줄을 적용했습니다 (:so %)");
    }

    private void Print(int first, int last, bool numbers)
    {
        var rows = Enumerable.Range(first, last - first + 1).Select(r => numbers ? $"{r + 1,4} {lines[r]}" : lines[r]).ToList();
        if (printed != null) { printed.AddRange(rows); return; }
        Current.Row = last; Current.Col = FirstNonBlank(last);
        Info(string.Join("\n", rows.TakeLast(20)));
    }

    private void ShowRegisters(string names)
    {
        var rows = new List<string> { "--- 레지스터 ---   종류: c 글자 · l 줄 · b 블록" };
        foreach (char c in "\"0123456789-abcdefghijklmnopqrstuvwxyz")
        {
            if (names.Length > 0 && !names.Contains(c)) continue;
            if (!registers.TryGetValue(c, out var reg)) continue;
            rows.Add($"  {(reg.Block ? 'b' : reg.Linewise ? 'l' : 'c')}  \"{c}   {Visible(reg.Text, 60)}");
        }
        if (lastEx.Length > 0 && (names.Length == 0 || names.Contains(':'))) rows.Add($"  c  \":   {Visible(lastEx, 60)}");
        if (lastSearch.Length > 0 && (names.Length == 0 || names.Contains('/'))) rows.Add($"  c  \"/   {Visible(lastSearch, 60)}");
        if (rows.Count == 1) rows.Add("  (비어 있음)");
        RegisterViews++; Info(string.Join("\n", rows.Take(22)));
    }

    private void ShowMarks()
    {
        var rows = new List<string> { "마크  줄  칸  내용" };
        foreach (var (name, mark) in marks.Where(m => m.Key != '<' && m.Key != '>').OrderBy(m => m.Key == '\'' ? ' ' : m.Key))
        {
            int r = Math.Clamp(mark.Row, 0, lines.Count - 1);
            rows.Add($" {name}  {r + 1,4} {mark.Col,3}  {(lines[r].Length > 40 ? lines[r][..40] + "…" : lines[r])}");
        }
        if (rows.Count == 1) rows.Add(" (표시한 마크가 없습니다 · m 다음에 a~z)");
        Info(string.Join("\n", rows.Take(22)));
    }

    private void MoveLines(int first, int last, int target)
    {
        if (target >= first && target < last) throw new ViError("E134: 범위 안으로는 옮길 수 없습니다");
        var w = Current;
        if (target == first - 1 || target == last) { w.Row = last; w.Col = FirstNonBlank(last); return; }
        BeginUndo();
        var block = lines.GetRange(first, last - first + 1); lines.RemoveRange(first, block.Count);
        int at = target >= last ? target - block.Count + 1 : target + 1;
        lines.InsertRange(at, block); w.Row = at + block.Count - 1; w.Col = FirstNonBlank(w.Row);
        if (block.Count > 1 && printed == null) Info($"{block.Count}줄 옮김");
    }

    private void CopyLines(int first, int last, int target)
    {
        var w = Current; BeginUndo();
        var block = lines.GetRange(first, last - first + 1); lines.InsertRange(target + 1, block);
        w.Row = target + block.Count; w.Col = FirstNonBlank(w.Row);
    }

    private void Join(int first, int last, bool spaces)
    {
        if (last <= first) throw new ViError("합칠 다음 줄이 없습니다");
        BeginUndo();
        for (int r = first + 1; r <= last; r++)
        {
            string left = spaces ? lines[first].TrimEnd() : lines[first], right = spaces ? lines[first + 1].TrimStart() : lines[first + 1];
            lines[first] = spaces && left.Length > 0 && right.Length > 0 ? left + " " + right : left + right; lines.RemoveAt(first + 1);
        }
        Current.Row = first; Current.Col = FirstNonBlank(first);
    }

    private void Sort(int first, int last, bool reverse, string options)
    {
        bool numeric = options.Contains('n'), unique = options.Contains('u'), ignore = options.Contains('i');
        var block = lines.GetRange(first, last - first + 1);
        var comparer = ignore ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        List<string> sorted = numeric
            ? block.Select((line, index) => (line, index, number: FirstNumber(line))).OrderBy(x => x.number.HasValue ? 1 : 0).ThenBy(x => x.number ?? 0).ThenBy(x => x.index).Select(x => x.line).ToList()
            : block.OrderBy(line => line, comparer).ToList();
        if (reverse) sorted.Reverse();
        bool Same(string x, string y) => numeric && FirstNumber(x) is long a && FirstNumber(y) is long b ? a == b : comparer.Equals(x, y);
        if (unique) { var kept = new List<string>(); foreach (var line in sorted) if (kept.Count == 0 || !Same(line, kept[^1])) kept.Add(line); sorted = kept; }
        BeginUndo(); lines.RemoveRange(first, block.Count); lines.InsertRange(first, sorted); SortCount++;
        Current.Row = first; Current.Col = 0;
        Info($"{block.Count}줄 정렬{(unique && sorted.Count < block.Count ? $" · 중복 {block.Count - sorted.Count}줄 제거" : "")}");
    }

    private static long? FirstNumber(string line) { var m = Regex.Match(line, @"-?\d{1,17}"); return m.Success ? long.Parse(m.Value) : null; }

    private void Normal(string keys, bool ranged, int first, int last)
    {
        if (keys.Length == 0) throw new ViError("E471: 실행할 키를 입력하세요 (예: :%norm A;)");
        NormalCount++; var w = Current;
        var rows = ranged ? Enumerable.Range(first, last - first + 1).ToList() : new List<int> { w.Row };
        BeginUndo(); undoHold++;
        try
        {
            for (int k = 0; k < rows.Count; k++)
            {
                int row = rows[k]; if (row < 0 || row >= lines.Count) break;
                int before = lines.Count;
                if (ranged) { w.Row = row; w.Col = 0; }
                Mode = ViMode.Normal; pending.Clear(); macroDepth++;
                try
                {
                    foreach (var token in Decode(keys)) { if (macroAbort) break; FeedCore(token); }
                    if (Mode == ViMode.Insert) FeedCore("<Esc>");
                    if (Mode == ViMode.Command) FeedCore("<Esc>");
                    if (IsVisual) Mode = ViMode.Normal;
                    pending.Clear();
                }
                finally { macroDepth--; macroAbort = false; }
                int delta = lines.Count - before;
                for (int x = k + 1; x < rows.Count; x++) rows[x] += delta;
            }
        }
        finally { undoHold--; }
        if (!MessageIsError && printed == null) Info($"{rows.Count}줄에 :normal {keys} 실행");
    }

    private bool TryGlobal(string body, bool ranged, int lo, int hi)
    {
        var m = Regex.Match(body, @"^(global|vglobal|g|v)(!?)(.)");
        if (!m.Success || !IsDelimiter(m.Groups[3].Value[0])) return false;
        bool invert = m.Groups[1].Value.StartsWith('v') || m.Groups[2].Value == "!";
        char d = m.Groups[3].Value[0]; string spec = body[m.Length..];
        var pattern = new StringBuilder(); int i = 0; bool closed = false;
        for (; i < spec.Length; i++)
        {
            if (spec[i] == '\\' && i + 1 < spec.Length && spec[i + 1] == d) { pattern.Append(d); i++; }
            else if (spec[i] == d) { i++; closed = true; break; }
            else pattern.Append(spec[i]);
        }
        string pat = pattern.Length == 0 ? lastSearch : pattern.ToString();
        if (pat.Length == 0) throw new ViError("E35: 찾을 말이 없습니다");
        string sub = closed ? spec[i..].TrimStart() : ""; if (sub.Length == 0) sub = "p";
        if (printed != null) throw new ViError("E147: :g 안에서 :g 를 다시 쓸 수 없습니다");
        var regex = Compile(pat); lastSearch = pat; hlsearch = true;
        if (!ranged) { lo = 0; hi = lines.Count - 1; }
        var rows = new List<int>();
        for (int r = lo; r <= hi; r++) if (regex.IsMatch(lines[r]) != invert) rows.Add(r);
        if (rows.Count == 0) { Fail($"패턴과 맞는 줄이 없습니다: {pat}"); return true; }
        string subName = new(sub.TakeWhile(char.IsAsciiLetter).ToArray());
        bool move = subName is "m" or "mo" or "move", copy = subName is "t" or "co" or "copy";
        var collected = new List<string>(); printed = collected; GlobalCount++; BeginUndo(); undoHold++; string? error = null;
        try
        {
            for (int k = 0; k < rows.Count; k++)
            {
                int row = rows[k]; if (row < 0 || row >= lines.Count) continue;
                int before = lines.Count; int? target = null;
                if (move || copy) { int p = 0; target = Address(sub[subName.Length..].Trim(), ref p, row); }
                Current.Row = row; Current.Col = 0;
                Message = ""; MessageIsError = false;
                Ex((row + 1) + sub);
                if (MessageIsError) error ??= Message;
                int delta = lines.Count - before;
                for (int x = k + 1; x < rows.Count; x++)
                {
                    if (move && target is int t) { if (t >= row && rows[x] > row && rows[x] <= t) rows[x]--; }
                    else if (copy && target is int t2) { if (rows[x] > t2) rows[x] += delta; }
                    else if (rows[x] > row) rows[x] += delta;
                }
            }
        }
        finally
        {
            undoHold--; printed = null;
            if (collected.Count > 0) Info(string.Join("\n", collected.TakeLast(20)));
            else if (error != null) Fail(error);
            else Info($"{rows.Count}줄에 :{sub} 실행");
        }
        return true;
    }

    private void Substitute(string spec, int first, int last)
    {
        char delimiter = spec[0]; var parts = new List<string>(); var current = new StringBuilder();
        for (int i = 1; i < spec.Length; i++)
        {
            if (spec[i] == '\\' && i + 1 < spec.Length && spec[i + 1] == delimiter) { current.Append(delimiter); i++; }
            else if (spec[i] == '\\' && i + 1 < spec.Length) { current.Append(spec[i]).Append(spec[i + 1]); i++; }
            else if (spec[i] == delimiter) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(spec[i]);
        }
        parts.Add(current.ToString());
        if (parts.Count < 2) throw new ViError("E486: 바꿀 내용을 /찾을말/바꿀말/ 형식으로 입력하세요");
        string pattern = parts[0].Length == 0 ? lastSearch : parts[0];
        string flags = parts.Count > 2 ? parts[2].Trim() : "";
        SubstituteWith(pattern, parts[1], flags, first, last);
    }

    private void SubstituteWith(string pattern, string replacement, string flags, int first, int last)
    {
        if (pattern.Length == 0) throw new ViError("E35: 찾을 말이 없습니다");
        lastSubstitute = (pattern, replacement, flags);
        bool global = flags.Contains('g'), countOnly = flags.Contains('n'), quiet = flags.Contains('e');
        bool? ignore = flags.Contains('i') ? true : flags.Contains('I') ? false : null;
        var regex = Compile(pattern, ignore);
        lastSearch = pattern; hlsearch = true;
        int changes = 0, changedLines = 0, lastLine = -1; bool began = false;
        if (pattern.Contains("\\n"))
        {
            string text = string.Join("\n", lines.GetRange(first, last - first + 1)) + "\n";
            int hits = 0; string result = regex.Replace(text, m => { hits++; return Expand(replacement, m); }, global ? -1 : last - first + 1);
            if (hits > 0 && !countOnly)
            {
                BeginUndo();
                if (result.EndsWith('\n')) result = result[..^1];
                else if (last < lines.Count - 1) { result += lines[last + 1]; lines.RemoveAt(last + 1); }
                lines.RemoveRange(first, last - first + 1); var pieces = result.Split('\n'); lines.InsertRange(first, pieces);
                lastLine = first + pieces.Length - 1;
            }
            changes = hits; changedLines = hits > 0 ? 1 : 0;
        }
        else
        {
            for (int r = first; r <= last && r < lines.Count; r++)
            {
                string line = lines[r]; int hits = 0;
                string result = regex.Replace(line, m => { hits++; return Expand(replacement, m); }, global ? -1 : 1);
                if (hits == 0) continue;
                changes += hits; changedLines++;
                if (countOnly) continue;
                if (!began) { BeginUndo(); began = true; }
                var pieces = result.Split('\n'); lines[r] = pieces[0];
                if (pieces.Length > 1) { lines.InsertRange(r + 1, pieces.Skip(1)); r += pieces.Length - 1; last += pieces.Length - 1; }
                lastLine = r;
            }
        }
        if (changes == 0) { if (!quiet) Fail($"E486: 찾을 수 없습니다: {pattern}"); return; }
        if (countOnly) { CountOnlySubstitutes++; Info($"{changes}개 일치 ({changedLines}줄) · n 플래그는 세기만 하고 바꾸지 않습니다"); return; }
        if (lastLine >= 0) { Current.Row = Math.Min(lastLine, lines.Count - 1); Current.Col = FirstNonBlank(Current.Row); }
        if (printed == null) Info((changedLines > 1 || changes > 1 ? $"{changes}곳 바꿈 ({changedLines}줄)" : "1곳 바꿈") + (flags.Contains('c') ? " · 연습창은 c(확인) 없이 바꿉니다" : ""));
    }

    /// <summary>Vim replacement text: &amp; is the match, \1…\9 groups, \r a line break, \u \U \l \L \E change case.</summary>
    private static string Expand(string replacement, Match m)
    {
        var sb = new StringBuilder(); char once = '\0', mode = '\0';
        void Add(string text)
        {
            foreach (char c in text)
            {
                char ch = mode == 'U' ? char.ToUpperInvariant(c) : mode == 'L' ? char.ToLowerInvariant(c) : c;
                if (once == 'u') { ch = char.ToUpperInvariant(ch); once = '\0'; } else if (once == 'l') { ch = char.ToLowerInvariant(ch); once = '\0'; }
                sb.Append(ch);
            }
        }
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c == '&') { Add(m.Value); continue; }
            if (c != '\\' || i + 1 >= replacement.Length) { Add(c.ToString()); continue; }
            char d = replacement[++i];
            switch (d)
            {
                case >= '0' and <= '9': Add(m.Groups[d - '0'].Value); break;
                case 'n': case 'r': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'u': case 'l': once = d; break;
                case 'U': case 'L': mode = d; break;
                case 'E': case 'e': mode = '\0'; break;
                default: Add(d.ToString()); break;
            }
        }
        return sb.ToString();
    }

    private void Quit(bool force, bool closeOnly, bool saved = false)
    {
        if (Windows.Count > 1) { CloseWindow(Current); return; }
        if (closeOnly) { Fail("E444: 마지막 창은 닫을 수 없습니다"); return; }
        if (Modified && !force) { QuitBlockedCount++; Fail("E37: 저장하지 않은 변경이 있습니다 (:w 로 저장하거나 :q! 로 버리고 종료)"); return; }
        QuitCount++;
        if (Embedded) { ExitHandler?.Invoke(); return; }
        if (force && Modified) { BeginUndo(); lines.Clear(); lines.AddRange(this.saved); DiscardCount++; Info("변경을 버리고 종료했습니다 · 연습창은 마지막 저장 상태로 돌아갑니다"); return; }
        Info(saved ? $"\"{FileName}\" 저장 후 종료했습니다 (연습창은 그대로 남습니다)" : "종료했습니다 · 실제 vi라면 여기서 셸로 돌아갑니다 (연습창은 그대로 남습니다)");
    }

    // ───────────────────────── Windows ─────────────────────────

    private static IEnumerable<ViWindow> Leaves(ViLayout node) => node switch
    {
        ViLeaf leaf => [leaf.Window],
        ViSplit split => split.Children.SelectMany(Leaves),
        _ => []
    };

    private Result WindowCommand(string key)
    {
        var all = Windows; int index = all.ToList().IndexOf(Current);
        switch (key)
        {
            case "w": case "<C-w>": Switch(all[(index + 1) % all.Count]); return Result.Done;
            case "W": case "p": Switch(all[(index - 1 + all.Count) % all.Count]); return Result.Done;
            case "h": case "j": case "k": case "l": case "<Left>": case "<Down>": case "<Up>": case "<Right>": case "<C-h>": case "<C-j>": case "<C-k>": case "<C-l>":
            {
                char direction = key switch { "<Left>" or "<C-h>" => 'h', "<Down>" or "<C-j>" => 'j', "<Up>" or "<C-k>" => 'k', "<Right>" or "<C-l>" => 'l', _ => key[0] };
                var target = Neighbor(direction); if (target != null) Switch(target); return Result.Done;
            }
            case "s": case "S": case "<C-s>": Split(false); return Result.Done;
            case "v": case "<C-v>": Split(true); return Result.Done;
            case "q": case "c": Quit(false, key == "c"); return Result.Done;
            case "o": Only(); return Result.Done;
            case "<Esc>": return Result.Done;
        }
        return Result.Invalid;
    }

    private void Switch(ViWindow window) { if (window == Current) return; Current = window; WindowSwitches++; }

    private void Split(bool vertical)
    {
        if (Windows.Count >= 6) { Fail("E36: 공간이 부족합니다 · 연습창은 창을 6개까지 나눌 수 있습니다"); return; }
        var created = new ViWindow(nextWindowId++) { Row = Current.Row, Col = Current.Col, Top = Current.Top, Height = Current.Height, Want = Current.Want };
        var (parent, slot) = ParentOf(Root, Current);
        if (parent != null && parent.Vertical == vertical) parent.Children.Insert(slot, new ViLeaf(created));
        else
        {
            var split = new ViSplit(vertical); split.Children.Add(new ViLeaf(created)); split.Children.Add(new ViLeaf(Current));
            if (parent == null) Root = split; else parent.Children[slot] = split;
        }
        Current = created; LayoutVersion++;
    }

    private void CloseWindow(ViWindow window)
    {
        var all = Windows.ToList(); int index = all.IndexOf(window);
        var (parent, slot) = ParentOf(Root, window); if (parent == null) return;
        parent.Children.RemoveAt(slot);
        Root = Collapse(Root);
        var remaining = Windows; Current = remaining[Math.Clamp(index - 1, 0, remaining.Count - 1)]; LayoutVersion++;
    }

    private void Only(bool silent = false)
    {
        if (Windows.Count == 1) { if (!silent) Info("이미 창이 하나입니다"); return; }
        Root = new ViLeaf(Current); LayoutVersion++;
    }

    private static ViLayout Collapse(ViLayout node)
    {
        if (node is not ViSplit split) return node;
        for (int i = 0; i < split.Children.Count; i++) split.Children[i] = Collapse(split.Children[i]);
        // Flatten same-direction children and single-child splits.
        for (int i = split.Children.Count - 1; i >= 0; i--)
            if (split.Children[i] is ViSplit inner && inner.Vertical == split.Vertical) { split.Children.RemoveAt(i); split.Children.InsertRange(i, inner.Children); }
        return split.Children.Count == 1 ? split.Children[0] : split;
    }

    private static (ViSplit? Parent, int Slot) ParentOf(ViLayout node, ViWindow window)
    {
        if (node is not ViSplit split) return (null, -1);
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (split.Children[i] is ViLeaf leaf && leaf.Window == window) return (split, i);
            var found = ParentOf(split.Children[i], window); if (found.Parent != null) return found;
        }
        return (null, -1);
    }

    /// <summary>Window rectangles in a unit square, matching the view's equal split sizes.</summary>
    public Dictionary<ViWindow, (double X, double Y, double W, double H)> Geometry()
    {
        var result = new Dictionary<ViWindow, (double, double, double, double)>();
        void Walk(ViLayout node, double x, double y, double width, double height)
        {
            if (node is ViLeaf leaf) { result[leaf.Window] = (x, y, width, height); return; }
            var split = (ViSplit)node; int n = split.Children.Count;
            for (int i = 0; i < n; i++)
                if (split.Vertical) Walk(split.Children[i], x + width * i / n, y, width / n, height);
                else Walk(split.Children[i], x, y + height * i / n, width, height / n);
        }
        Walk(Root, 0, 0, 1, 1); return result;
    }

    private ViWindow? Neighbor(char direction)
    {
        var rects = Geometry(); var c = rects[Current]; const double e = 1e-6;
        bool Overlap(double a1, double a2, double b1, double b2) => Math.Min(a2, b2) - Math.Max(a1, b1) > e;
        var candidates = rects.Where(p => p.Key != Current).Select(p => (Window: p.Key, R: p.Value)).Where(p => direction switch
        {
            'l' => p.R.X >= c.X + c.W - e && Overlap(p.R.Y, p.R.Y + p.R.H, c.Y, c.Y + c.H),
            'h' => p.R.X + p.R.W <= c.X + e && Overlap(p.R.Y, p.R.Y + p.R.H, c.Y, c.Y + c.H),
            'j' => p.R.Y >= c.Y + c.H - e && Overlap(p.R.X, p.R.X + p.R.W, c.X, c.X + c.W),
            _ => p.R.Y + p.R.H <= c.Y + e && Overlap(p.R.X, p.R.X + p.R.W, c.X, c.X + c.W),
        });
        return candidates.OrderBy(p => direction switch { 'l' => p.R.X, 'h' => -(p.R.X + p.R.W), 'j' => p.R.Y, _ => -(p.R.Y + p.R.H) })
            .ThenBy(p => Math.Abs((direction is 'l' or 'h' ? p.R.Y : p.R.X) - (direction is 'l' or 'h' ? c.Y : c.X))).Select(p => p.Window).FirstOrDefault();
    }

    // ───────────────────────── Undo ─────────────────────────

    private void BeginUndo()
    {
        if (undoOpen) return;
        undo.Push((lines.ToArray(), Current.Row, Current.Col)); redo.Clear(); undoOpen = true;
    }
    private void EndUndo()
    {
        if (undoHold > 0) return;
        undoOpen = false;
        if (undo.Count > 0 && undo.Peek().Lines.SequenceEqual(lines)) undo.Pop();
    }
    private void Undo(int n)
    {
        if (undo.Count == 0) { Fail("이미 가장 처음 상태입니다 (더 되돌릴 변경이 없습니다)"); return; }
        int done = 0;
        for (; done < n && undo.Count > 0; done++)
        {
            var state = undo.Pop(); redo.Push((lines.ToArray(), Current.Row, Current.Col));
            lines.Clear(); lines.AddRange(state.Lines); Current.Row = state.Row; Current.Col = state.Col;
        }
        UndoCount += done; Info($"{done}개 변경 되돌림 · 남은 되돌리기 {undo.Count}개");
    }
    private void Redo(int n)
    {
        if (redo.Count == 0) { Fail("다시 실행할 변경이 없습니다"); return; }
        int done = 0;
        for (; done < n && redo.Count > 0; done++)
        {
            var state = redo.Pop(); undo.Push((lines.ToArray(), Current.Row, Current.Col));
            lines.Clear(); lines.AddRange(state.Lines); Current.Row = state.Row; Current.Col = state.Col;
        }
        RedoCount += done; Info($"{done}개 변경 다시 실행");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private void Clamp(ViWindow w)
    {
        if (lines.Count == 0) lines.Add("");
        w.Row = Math.Clamp(w.Row, 0, lines.Count - 1);
        int length = lines[w.Row].Length;
        int max = w == Current && Mode == ViMode.Insert ? length : Math.Max(0, length - 1);
        w.Col = Math.Clamp(w.Col, 0, max);
        visualRow = Math.Clamp(visualRow, 0, lines.Count - 1);
    }
    private void Scroll(ViWindow w)
    {
        if (w.Row < w.Top) w.Top = w.Row;
        else if (w.Row >= w.Top + w.Height) w.Top = w.Row - w.Height + 1;
        w.Top = Math.Clamp(w.Top, 0, Math.Max(0, lines.Count - 1));
    }
    private int FirstNonBlank(int row) { string line = lines[row]; int i = 0; while (i < line.Length && char.IsWhiteSpace(line[i])) i++; return i >= line.Length ? Math.Max(0, line.Length - 1) : i; }
    private static string Indent(string line) => new(line.TakeWhile(c => c is ' ' or '\t').ToArray());
    private static string Toggle(string text) => new(text.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c).ToArray());

    public static string DetectFileType(string name)
    {
        string lower = Path.GetFileName(name).ToLowerInvariant(), ext = Path.GetExtension(lower);
        if (lower is ".vimrc" or "_vimrc" or ".exrc" or ".gvimrc" || ext == ".vim") return "vim";
        if (lower is ".bashrc" or ".profile" or ".bash_profile" or ".bash_aliases") return "sh";
        return ext switch
        {
            ".c" or ".h" => "c", ".cpp" or ".cc" or ".cxx" or ".hpp" => "cpp", ".java" => "java", ".cs" => "cs", ".js" => "javascript",
            ".py" => "python", ".sh" or ".bash" => "sh", ".conf" or ".ini" or ".cfg" or ".rcp" or ".properties" or ".bak" => "conf",
            ".log" => "log", ".csv" => "csv", _ => "text"
        };
    }

    public static string Display(string key) => key switch
    {
        "<Esc>" => "Esc", "<CR>" => "Enter", "<BS>" => "⌫", "<Del>" => "Del", "<Tab>" => "Tab", " " => "␣",
        "<Left>" => "←", "<Right>" => "→", "<Up>" => "↑", "<Down>" => "↓",
        _ when key.StartsWith("<C-") && key.Length == 5 => "Ctrl+" + char.ToUpperInvariant(key[3]),
        _ => key
    };
}

/// <summary>Converts vim "magic" patterns (\+ \? \| \( \) \&lt; \&gt; \{n,m} \c …) to .NET regular expressions.</summary>
public static class VimRegex
{
    public static Regex? Convert(string pattern, bool ignoreCase)
    {
        var sb = new StringBuilder(); bool veryMagic = false, atStart = true;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i]; bool start = atStart; atStart = false;
            if (c == '\\' && i + 1 < pattern.Length)
            {
                char d = pattern[++i];
                switch (d)
                {
                    case 'c': ignoreCase = true; atStart = start; break;
                    case 'C': ignoreCase = false; atStart = start; break;
                    case 'v': veryMagic = true; atStart = start; break;
                    case 'm': case 'M': case 'V': veryMagic = false; atStart = start; break;
                    case '<': case '>': sb.Append(@"\b"); break;
                    case '+': case '?': case '=': case '|': case '(': case ')':
                        if (veryMagic) sb.Append('\\').Append(d);
                        else { sb.Append(d == '=' ? '?' : d); atStart = d is '|' or '('; }
                        break;
                    case '{':
                        if (veryMagic) { sb.Append(@"\{"); break; }
                        i = Quantifier(pattern, i + 1, sb);
                        break;
                    case 'd': case 'D': case 'w': case 'W': case 's': case 'S': case 'n': case 't': sb.Append('\\').Append(d); break;
                    case 'r': sb.Append(@"\r"); break;
                    case 'e': sb.Append(@"\x1b"); break;
                    case 'a': sb.Append("[A-Za-z]"); break;
                    case 'A': sb.Append("[^A-Za-z]"); break;
                    case 'l': sb.Append("[a-z]"); break;
                    case 'L': sb.Append("[^a-z]"); break;
                    case 'u': sb.Append("[A-Z]"); break;
                    case 'U': sb.Append("[^A-Z]"); break;
                    case 'x': sb.Append("[0-9A-Fa-f]"); break;
                    case 'X': sb.Append("[^0-9A-Fa-f]"); break;
                    case 'h': sb.Append("[A-Za-z_]"); break;
                    case 'H': sb.Append("[^A-Za-z_]"); break;
                    case 'o': sb.Append("[0-7]"); break;
                    case >= '1' and <= '9': sb.Append('\\').Append(d); break;
                    default: sb.Append(Regex.Escape(d.ToString())); break;
                }
                continue;
            }
            if (veryMagic)
            {
                switch (c)
                {
                    case '<': case '>': sb.Append(@"\b"); continue;
                    case '=': sb.Append('?'); continue;
                    case '[': i = CharClass(pattern, i, sb); continue;
                    case '{': i = Quantifier(pattern, i + 1, sb); continue;
                    case '(': case '|': sb.Append(c); atStart = true; continue;
                    case '.': case '*': case '+': case '?': case ')': case '^': case '$': sb.Append(c); continue;
                    default: sb.Append(Regex.Escape(c.ToString())); continue;
                }
            }
            switch (c)
            {
                case '.': case '*': sb.Append(c); break;
                case '^': sb.Append(start ? "^" : @"\^"); break;
                case '$':
                {
                    bool end = i == pattern.Length - 1 || i + 2 < pattern.Length && pattern[i + 1] == '\\' && pattern[i + 2] is '|' or ')';
                    sb.Append(end ? "$" : @"\$"); break;
                }
                case '[': i = CharClass(pattern, i, sb); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        try { return new Regex(sb.ToString(), RegexOptions.Multiline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException) { return null; }
    }

    private static int Quantifier(string pattern, int i, StringBuilder sb)
    {
        int close = pattern.IndexOf('}', i); if (close < 0) { sb.Append(@"\{"); return i - 1; }
        string body = pattern[i..close].TrimEnd('\\'); bool lazy = body.StartsWith('-'); if (lazy) body = body[1..];
        if (body.Length == 0) sb.Append(lazy ? "*?" : "*");
        else if (Regex.IsMatch(body, @"^\d*(,\d*)?$")) sb.Append('{').Append(body.StartsWith(',') ? "0" + body : body).Append('}').Append(lazy ? "?" : "");
        else sb.Append(Regex.Escape("{" + body + "}"));
        return close;
    }

    private static int CharClass(string pattern, int i, StringBuilder sb)
    {
        int j = i + 1; if (j < pattern.Length && pattern[j] == '^') j++; if (j < pattern.Length && pattern[j] == ']') j++;
        while (j < pattern.Length && pattern[j] != ']') { if (pattern[j] == '[' && j + 1 < pattern.Length && pattern[j + 1] == ':') { int end = pattern.IndexOf(":]", j + 2, StringComparison.Ordinal); if (end > 0) { j = end + 2; continue; } } if (pattern[j] == '\\') j++; j++; }
        if (j >= pattern.Length) { sb.Append(@"\["); return i; }
        string body = pattern[(i + 1)..j]
            .Replace("[:alpha:]", "a-zA-Z").Replace("[:digit:]", "0-9").Replace("[:alnum:]", "a-zA-Z0-9").Replace("[:space:]", @"\s")
            .Replace("[:upper:]", "A-Z").Replace("[:lower:]", "a-z").Replace("[:punct:]", @"!-/:-@\[-`{-~").Replace("[:xdigit:]", "0-9A-Fa-f");
        body = body.Replace("[", @"\[");
        sb.Append('[').Append(body).Append(']');
        return j;
    }
}
