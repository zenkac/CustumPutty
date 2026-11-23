using System.Text;

namespace EquipLink;

public enum ViMode { Normal, Insert, Visual, VisualLine, Command }

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
/// processes or the network: ":w" only marks the in-memory text as saved.
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
    private List<string> lastChange = [], changeKeys = [];
    private string commandLine = "", lastSearch = "", register = "";
    private char commandPrefix = ':';
    private bool registerLinewise, lastSearchForward = true, commandFromVisual;
    private (char Kind, string Ch) lastFind;
    private int nextWindowId = 1, visualRow, visualCol;
    private ViMode commandReturn = ViMode.Normal;

    public ViEngine() { Current = new ViWindow(nextWindowId++); Root = new ViLeaf(Current); }

    public event Action? Changed;
    public IReadOnlyList<string> Lines => lines;
    public string Text => string.Join("\n", lines);
    public ViMode Mode { get; private set; }
    public ViLayout Root { get; private set; }
    public ViWindow Current { get; private set; }
    public int LayoutVersion { get; private set; }
    public string FileName { get; set; } = "practice.txt";
    public bool Modified => !lines.SequenceEqual(saved);
    public bool ShowNumbers { get; private set; }
    public string Message { get; private set; } = "";
    public bool MessageIsError { get; private set; }
    public string CommandText => Mode == ViMode.Command ? commandPrefix + commandLine : "";
    public bool CommandIsSearch => Mode == ViMode.Command && commandPrefix != ':';
    public string PendingKeys => string.Concat(pending.Select(Display));
    public string? Highlight => hlsearch && lastSearch.Length > 0 ? lastSearch : null;
    public (int Row, int Col) VisualAnchor => (visualRow, visualCol);
    public IReadOnlyList<string> RecentKeys => recent;
    public IReadOnlyList<ViWindow> Windows => Leaves(Root).ToArray();

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

    public void Load(string text, int row = 0, int col = 0)
    {
        lines.Clear(); lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        saved = [.. lines];
        pending.Clear(); undo.Clear(); redo.Clear(); recent.Clear(); lastChange = []; changeKeys = [];
        undoOpen = recordingInsert = false; commandLine = ""; lastSearch = ""; register = ""; hlsearch = true;
        Mode = ViMode.Normal; Message = ""; MessageIsError = false;
        UndoCount = RedoCount = RepeatCount = WindowSwitches = SaveCount = QuitBlockedCount = DiscardCount = SearchCount = QuitCount = 0;
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
                if (end > i + 1 && IsKeyName(keys[(i + 1)..end])) { yield return keys[i..(end + 1)]; i = end; continue; }
            }
            yield return keys[i].ToString();
        }
    }

    private static readonly HashSet<string> KeyNames = ["Esc", "CR", "BS", "Del", "Tab", "Left", "Right", "Up", "Down", "Home", "End"];
    private static bool IsKeyName(string name) => KeyNames.Contains(name) || name.Length == 3 && name.StartsWith("C-") && char.IsAsciiLetterLower(name[2]);

    public void Feed(string key)
    {
        if (key is "<C-c>" or "<C-[>") key = "<Esc>";
        recent.Add(Display(key)); if (recent.Count > 40) recent.RemoveAt(0);
        if (Mode is ViMode.Normal or ViMode.Visual or ViMode.VisualLine && pending.Count == 0) { Message = ""; MessageIsError = false; }
        if (recordingInsert && !replaying) changeKeys.Add(key);
        switch (Mode)
        {
            case ViMode.Insert: InsertKey(key); break;
            case ViMode.Command: CommandKey(key); break;
            default: NormalKey(key); break;
        }
        if (undoOpen && Mode != ViMode.Insert) EndUndo();
        foreach (var window in Windows) Clamp(window);
        Scroll(Current);
        Changed?.Invoke();
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

    // ───────────────────────── Normal and visual mode ─────────────────────────

    private enum Result { Done, Incomplete, Invalid }

    private void NormalKey(string key)
    {
        if (key == "<Esc>")
        {
            if (pending.Count > 0) pending.Clear();
            else if (Mode is ViMode.Visual or ViMode.VisualLine) Mode = ViMode.Normal;
            return;
        }
        pending.Add(key);
        var tokens = pending.ToList();
        bool changing = false;
        Result result;
        try { result = Execute(tokens, ref changing); }
        catch (ViError error) { Fail(error.Message); result = Result.Invalid; }
        if (result == Result.Incomplete) return;
        pending.Clear();
        if (result == Result.Done && changing && !replaying)
        {
            if (Mode == ViMode.Insert) { recordingInsert = true; changeKeys = tokens; }
            else lastChange = tokens;
        }
    }

    private Result Execute(List<string> t, ref bool changing)
    {
        int i = 0, count = ReadCount(t, ref i);
        if (i >= t.Count) return Result.Incomplete;
        int n = Math.Max(1, count);
        var w = Current;
        string k = t[i];
        bool visual = Mode is ViMode.Visual or ViMode.VisualLine;

        if (k is "d" or "y" or "c" or ">" or "<")
        {
            if (visual) { changing = k != "y"; VisualOperator(k); return Result.Done; }
            if (i + 1 >= t.Count) return Result.Incomplete;
            changing = k != "y";
            if (t[i + 1] == k) { LineOperator(k, w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done; }
            int j = i + 1, inner = ReadCount(t, ref j);
            if (j >= t.Count) return Result.Incomplete;
            int total = n * Math.Max(1, inner);
            if (k == "c" && t[j] is "w" or "W" && w.Col < lines[w.Row].Length && !char.IsWhiteSpace(lines[w.Row][w.Col])) t[j] = t[j] == "w" ? "e" : "E";
            var motion = Motion(t, j, total, count > 0 || inner > 0, true);
            if (motion.State == Result.Incomplete) return Result.Incomplete;
            if (motion.State == Result.Invalid) return Result.Invalid;
            // Vim: "dw"/"cw" on the last word of a line stops at that line's end.
            if (t[j] is "w" or "W" && motion.Row > w.Row && !motion.Linewise) motion = motion with { Row = w.Row, Col = lines[w.Row].Length, Inclusive = false };
            Operator(k, motion);
            return Result.Done;
        }

        var move = Motion(t, i, n, count > 0, false);
        if (move.State == Result.Incomplete) return Result.Incomplete;
        if (move.Known)
        {
            if (move.State == Result.Invalid) return Result.Invalid;
            w.Row = move.Row; w.Col = move.Col;
            if (move.Linewise && !move.KeepColumn) { w.Col = FirstNonBlank(w.Row); }
            if (move.KeepColumn) w.Col = Math.Min(w.Want, Math.Max(0, lines[w.Row].Length - 1)); else w.Want = move.Col == int.MaxValue ? int.MaxValue : w.Col;
            if (move.EndOfLine) w.Want = int.MaxValue;
            return Result.Done;
        }

        if (visual)
        {
            switch (k)
            {
                case "o": (visualRow, w.Row) = (w.Row, visualRow); (visualCol, w.Col) = (w.Col, visualCol); return Result.Done;
                case "x": case "<Del>": changing = true; VisualOperator("d"); return Result.Done;
                case "s": changing = true; VisualOperator("c"); return Result.Done;
                case "~": changing = true; VisualOperator("~"); return Result.Done;
                case "D": case "X": changing = true; Mode = ViMode.VisualLine; VisualOperator("d"); return Result.Done;
                case "C": case "S": case "R": changing = true; Mode = ViMode.VisualLine; VisualOperator("c"); return Result.Done;
                case "Y": Mode = ViMode.VisualLine; VisualOperator("y"); return Result.Done;
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
                    if (register.Length == 0 && !registerLinewise) return Fail("붙여넣을 내용이 없습니다.");
                    string keep = register; bool linewise = registerLinewise; changing = true;
                    VisualOperator("d"); register = keep; registerLinewise = linewise; Put(false, 1); return Result.Done;
                }
                case "i": case "a": case "I": case "A": case "r": case ".": case "u": case "<C-r>":
                    return Fail("비주얼 모드에서는 지원하지 않는 키입니다. Esc 로 나온 뒤 사용하세요.");
            }
        }

        switch (k)
        {
            case "i": Begin(ref changing); Mode = ViMode.Insert; return Result.Done;
            case "a": Begin(ref changing); if (lines[w.Row].Length > 0) w.Col++; Mode = ViMode.Insert; return Result.Done;
            case "I": Begin(ref changing); w.Col = FirstNonBlank(w.Row); Mode = ViMode.Insert; return Result.Done;
            case "A": Begin(ref changing); w.Col = lines[w.Row].Length; Mode = ViMode.Insert; return Result.Done;
            case "o": case "O":
            {
                Begin(ref changing);
                string indent = Indent(lines[w.Row]); int at = k == "o" ? w.Row + 1 : w.Row;
                lines.Insert(at, indent); w.Row = at; w.Col = indent.Length; Mode = ViMode.Insert; return Result.Done;
            }
            case "x": case "<Del>":
                if (lines[w.Row].Length == 0) return Result.Invalid;
                Begin(ref changing); DeleteChars(w.Row, w.Col, Math.Min(n, lines[w.Row].Length - w.Col)); return Result.Done;
            case "X":
                if (w.Col == 0) return Result.Invalid;
                Begin(ref changing); { int from = Math.Max(0, w.Col - n); DeleteChars(w.Row, from, w.Col - from); w.Col = from; } return Result.Done;
            case "D": Begin(ref changing); YankChars(w.Row, w.Col, lines[w.Row].Length); lines[w.Row] = lines[w.Row][..Math.Min(w.Col, lines[w.Row].Length)]; return Result.Done;
            case "C": Begin(ref changing); YankChars(w.Row, w.Col, lines[w.Row].Length); lines[w.Row] = lines[w.Row][..Math.Min(w.Col, lines[w.Row].Length)]; w.Col = lines[w.Row].Length; Mode = ViMode.Insert; return Result.Done;
            case "s": Begin(ref changing); if (lines[w.Row].Length > 0) DeleteChars(w.Row, w.Col, Math.Min(n, lines[w.Row].Length - w.Col)); Mode = ViMode.Insert; return Result.Done;
            case "S": changing = true; LineOperator("c", w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done;
            case "Y": LineOperator("y", w.Row, Math.Min(lines.Count - 1, w.Row + n - 1)); return Result.Done;
            case "p": case "P": if (register.Length == 0 && !registerLinewise) return Fail("붙여넣을 내용이 없습니다. 먼저 yy · dd · yw 등으로 복사하세요.");
                Begin(ref changing); Put(k == "p", n); return Result.Done;
            case "r":
                if (i + 1 >= t.Count) return Result.Incomplete;
                {
                    string ch = t[i + 1]; string line = lines[w.Row];
                    if (ch == "<Esc>") return Result.Done;
                    if (ch.Length != 1 && ch != "<CR>") return Result.Invalid;
                    if (w.Col + n > line.Length) return Result.Invalid;
                    Begin(ref changing);
                    if (ch == "<CR>") { lines[w.Row] = line[..w.Col]; lines.Insert(w.Row + 1, line[(w.Col + n)..]); w.Row++; w.Col = 0; }
                    else { lines[w.Row] = line[..w.Col] + new string(ch[0], n) + line[(w.Col + n)..]; w.Col += n - 1; }
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
            case "v": case "V":
            {
                var target = k == "v" ? ViMode.Visual : ViMode.VisualLine;
                if (Mode == target) Mode = ViMode.Normal;
                else { if (!visual) { visualRow = w.Row; visualCol = w.Col; } Mode = target; }
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
            case "Z":
                if (i + 1 >= t.Count) return Result.Incomplete;
                if (t[i + 1] == "Z") { RunCommand("x"); return Result.Done; }
                if (t[i + 1] == "Q") { RunCommand("q!"); return Result.Done; }
                return Result.Invalid;
            case "g":
                if (i + 1 >= t.Count) return Result.Incomplete;
                return Result.Invalid;
        }
        return Result.Invalid;
    }

    private void Begin(ref bool changing) { changing = true; BeginUndo(); }

    private sealed class ViError(string message) : Exception(message);
    private Result Fail(string message) { Message = message; MessageIsError = true; return Result.Invalid; }
    private void Info(string message) { Message = message; MessageIsError = false; }

    private static int ReadCount(List<string> t, ref int i)
    {
        int value = 0; bool any = false;
        while (i < t.Count && t[i].Length == 1 && char.IsAsciiDigit(t[i][0]) && (any || t[i] != "0")) { value = Math.Min(9999, value * 10 + (t[i][0] - '0')); any = true; i++; }
        return value;
    }

    // ───────────────────────── Motions ─────────────────────────

    private readonly record struct Move(Result State, bool Known, int Row, int Col, bool Linewise, bool Inclusive, bool KeepColumn = false, bool EndOfLine = false);
    private static Move Unknown => new(Result.Done, false, 0, 0, false, false);
    private static Move Pending => new(Result.Incomplete, true, 0, 0, false, false);

    private Move Motion(List<string> t, int i, int n, bool hasCount, bool forOperator)
    {
        var w = Current; int row = w.Row, col = w.Col; string line = lines[row];
        Move Char(int r, int c, bool inclusive = false) => new(Result.Done, true, r, c, false, inclusive);
        Move Line(int r, bool keep = false) => new(Result.Done, true, Math.Clamp(r, 0, lines.Count - 1), keep ? w.Col : 0, true, false, keep);
        switch (t[i])
        {
            case "h": case "<Left>": case "<BS>": return Char(row, Math.Max(0, col - n));
            case "l": case "<Right>": case " ": return Char(row, Math.Min(col + n, forOperator ? line.Length : Math.Max(0, line.Length - 1)));
            case "j": case "<Down>": return Line(row + n, true);
            case "k": case "<Up>": return Line(row - n, true);
            case "+": case "<CR>": return Line(row + n);
            case "-": return Line(row - n);
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
                if (t[i + 1].Length != 1) return new(Result.Invalid, true, 0, 0, false, false);
                lastFind = (t[i][0], t[i + 1]);
                return Find(t[i][0], t[i + 1][0], n);
            }
            case ";": case ",":
            {
                if (lastFind.Ch is null) return new(Result.Invalid, true, 0, 0, false, false);
                char kind = lastFind.Kind; if (t[i] == ",") kind = kind switch { 'f' => 'F', 'F' => 'f', 't' => 'T', _ => 't' };
                return Find(kind, lastFind.Ch[0], n);
            }
            case "n": case "N":
            {
                if (lastSearch.Length == 0) { Fail("검색어가 없습니다. 먼저 /단어 로 검색하세요."); return new(Result.Invalid, true, 0, 0, false, false); }
                bool forward = t[i] == "n" ? lastSearchForward : !lastSearchForward;
                var found = Search(lastSearch, forward, n, false);
                return found is { } p ? Char(p.Row, p.Col) : new(Result.Invalid, true, 0, 0, false, false);
            }
            case "*": case "#":
            {
                string word = WordAt(row, col); if (word.Length == 0) { Fail("커서 아래에 단어가 없습니다."); return new(Result.Invalid, true, 0, 0, false, false); }
                lastSearch = word; lastSearchForward = t[i] == "*"; hlsearch = true; SearchCount++;
                var found = Search(word, lastSearchForward, n, true);
                return found is { } p ? Char(p.Row, p.Col) : new(Result.Invalid, true, 0, 0, false, false);
            }
            case "%":
            {
                var match = Bracket(row, col);
                return match is { } m ? Char(m.Row, m.Col, true) : new(Result.Invalid, true, 0, 0, false, false);
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
            if (found < 0) return new(Result.Invalid, true, 0, 0, false, false);
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
    private static int Class(string text, int i, bool big)
    {
        if (i < 0 || i >= text.Length) return -1;
        char c = text[i];
        if (c == '\n') return i == 0 || text[i - 1] == '\n' ? 3 : 0;
        if (char.IsWhiteSpace(c)) return 0;
        if (big) return 1;
        return char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;
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
        while (c < line.Length && !(char.IsLetterOrDigit(line[c]) || line[c] == '_')) c++;
        if (c >= line.Length) return "";
        int start = c, end = c;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_')) start--;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
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
    private (int Row, int Col)? Search(string pattern, bool forward, int n, bool whole)
    {
        string s = Flat; int at = Index(Current.Row, Current.Col); bool wrapped = false;
        for (int x = 0; x < n; x++)
        {
            int found = -1;
            if (forward)
            {
                found = IndexOf(s, pattern, at + 1, whole);
                if (found < 0) { found = IndexOf(s, pattern, 0, whole); wrapped = found >= 0; }
            }
            else
            {
                found = LastIndexOf(s, pattern, at - 1, whole);
                if (found < 0) { found = LastIndexOf(s, pattern, s.Length - 1, whole); wrapped = found >= 0; }
            }
            if (found < 0) { Fail($"찾을 수 없습니다: {pattern}"); return null; }
            at = found;
        }
        if (wrapped) Info(forward ? "끝까지 검색해 처음부터 다시 찾았습니다" : "처음까지 검색해 끝에서부터 다시 찾았습니다");
        else Info((forward ? "/" : "?") + pattern);
        return Position(at);
    }
    private static bool Boundary(string s, int start, int length) =>
        (start == 0 || !(char.IsLetterOrDigit(s[start - 1]) || s[start - 1] == '_')) && (start + length >= s.Length || !(char.IsLetterOrDigit(s[start + length]) || s[start + length] == '_'));
    private static int IndexOf(string s, string p, int from, bool whole)
    {
        for (int i = Math.Max(0, from); i <= s.Length - p.Length; i++) { i = s.IndexOf(p, i, StringComparison.Ordinal); if (i < 0) return -1; if (!whole || Boundary(s, i, p.Length)) return i; }
        return -1;
    }
    private static int LastIndexOf(string s, string p, int from, bool whole)
    {
        for (int i = Math.Min(from, s.Length - p.Length); i >= 0; i--) { if (string.CompareOrdinal(s, i, p, 0, p.Length) == 0 && (!whole || Boundary(s, i, p.Length))) return i; }
        return -1;
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
        if (op is ">" or "<") { LineOperator(op, sr, er); return; }
        a = Index(sr, sc); b = Index(er, Math.Min(ec, lines[er].Length));
        string text = Flat[a..Math.Max(a, b)];
        register = text; registerLinewise = false;
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
        if (op == "y") { register = string.Join("\n", lines.Skip(first).Take(last - first + 1)); registerLinewise = true; Info(last > first ? $"{last - first + 1}줄 복사됨" : "1줄 복사됨"); return; }
        BeginUndo();
        if (op is ">" or "<")
        {
            for (int r = first; r <= last; r++)
            {
                if (op == ">") { if (lines[r].Length > 0) lines[r] = "    " + lines[r]; }
                else { int remove = 0; while (remove < 4 && remove < lines[r].Length && lines[r][remove] == ' ') remove++; if (remove == 0 && lines[r].StartsWith('\t')) remove = 1; lines[r] = lines[r][remove..]; }
            }
            w.Row = first; w.Col = FirstNonBlank(first); if (last > first) Info($"{last - first + 1}줄 {(op == ">" ? "들여쓰기" : "내어쓰기")}"); return;
        }
        register = string.Join("\n", lines.Skip(first).Take(last - first + 1)); registerLinewise = true;
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

    private (int First, int Last) VisualRows() => (Math.Min(visualRow, Current.Row), Math.Max(visualRow, Current.Row));

    private void VisualOperator(string op)
    {
        var w = Current; var mode = Mode; Mode = ViMode.Normal;
        if (mode == ViMode.VisualLine || op is ">" or "<") { var (a, b) = VisualRows(); LineOperator(op, a, b); return; }
        int s = Index(visualRow, visualCol), e = Index(w.Row, w.Col); if (s > e) (s, e) = (e, s);
        var (sr, sc) = Position(s); var (er, ec) = Position(e);
        if (op == "~")
        {
            BeginUndo(); string flat = Flat; e = Math.Min(e + 1, flat.Length);
            string changed = flat[..s] + Toggle(flat[s..e]) + flat[e..];
            lines.Clear(); lines.AddRange(changed.Split('\n')); w.Row = sr; w.Col = sc; return;
        }
        w.Row = sr; w.Col = sc;
        Operator(op, new Move(Result.Done, true, er, Math.Min(ec, Math.Max(0, lines[er].Length - 1)), false, true));
        w.Row = sr; w.Col = sc;
    }

    private void DeleteChars(int row, int col, int count)
    {
        string line = lines[row]; count = Math.Min(count, line.Length - col); if (count <= 0) return;
        register = line.Substring(col, count); registerLinewise = false;
        lines[row] = line.Remove(col, count);
    }
    private void YankChars(int row, int from, int to) { string line = lines[row]; from = Math.Min(from, line.Length); register = line[from..Math.Max(from, Math.Min(to, line.Length))]; registerLinewise = false; }

    private void Put(bool after, int n)
    {
        var w = Current;
        if (registerLinewise)
        {
            var block = Enumerable.Repeat(register.Split('\n'), n).SelectMany(x => x).ToList();
            int at = after ? w.Row + 1 : w.Row; lines.InsertRange(at, block); w.Row = at; w.Col = FirstNonBlank(at);
            if (block.Count > 1) Info($"{block.Count}줄 붙여넣음"); return;
        }
        string text = string.Concat(Enumerable.Repeat(register, n)); string line = lines[w.Row];
        int col = after && line.Length > 0 ? Math.Min(w.Col + 1, line.Length) : Math.Min(w.Col, line.Length);
        string combined = line[..col] + text + line[col..]; var parts = combined.Split('\n');
        lines[w.Row] = parts[0]; lines.InsertRange(w.Row + 1, parts.Skip(1));
        if (parts.Length == 1) w.Col = col + text.Length - 1; else w.Col = col;
    }

    // ───────────────────────── Insert mode ─────────────────────────

    private void InsertKey(string key)
    {
        var w = Current; string line = lines[w.Row]; w.Col = Math.Min(w.Col, line.Length);
        switch (key)
        {
            case "<Esc>":
                Mode = ViMode.Normal; if (w.Col > 0) w.Col--; w.Want = w.Col;
                if (recordingInsert && !replaying) { lastChange = changeKeys.ToList(); recordingInsert = false; }
                return;
            case "<CR>":
            {
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
            case "<Tab>": lines[w.Row] = line.Insert(w.Col, "    "); w.Col += 4; return;
            case "<C-w>":
            {
                int c = w.Col; while (c > 0 && line[c - 1] == ' ') c--; while (c > 0 && line[c - 1] != ' ') c--;
                lines[w.Row] = line.Remove(c, w.Col - c); w.Col = c; return;
            }
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

    // ───────────────────────── Command line ─────────────────────────

    private void StartCommand(char prefix)
    {
        commandFromVisual = Mode is ViMode.Visual or ViMode.VisualLine;
        commandReturn = prefix == ':' ? ViMode.Normal : Mode;
        commandPrefix = prefix; commandLine = prefix == ':' && commandFromVisual ? "'<,'>" : ""; Mode = ViMode.Command;
    }

    private void CommandKey(string key)
    {
        switch (key)
        {
            case "<Esc>": case "<C-c>": Mode = commandPrefix == ':' && commandFromVisual ? ViMode.Normal : commandReturn; commandLine = ""; return;
            case "<BS>": if (commandLine.Length == 0) { Mode = commandReturn; return; } commandLine = commandLine[..^1]; return;
            case "<C-w>": { var trimmed = commandLine.TrimEnd(); int cut = trimmed.LastIndexOf(' ') + 1; commandLine = trimmed[..cut]; return; }
            case "<CR>":
            {
                string text = commandLine; char prefix = commandPrefix; Mode = commandReturn; commandLine = "";
                if (prefix == ':') { if (commandFromVisual) Mode = ViMode.Normal; RunCommand(text); }
                else
                {
                    if (text.Length == 0) text = lastSearch;
                    if (text.Length == 0) { Fail("검색어를 입력하세요."); return; }
                    lastSearch = text; lastSearchForward = prefix == '/'; hlsearch = true; SearchCount++;
                    if (Search(text, lastSearchForward, 1, false) is { } p) { Current.Row = p.Row; Current.Col = p.Col; Current.Want = p.Col; }
                }
                return;
            }
        }
        if (key.Length == 1 || key.Length == 2 && char.IsSurrogatePair(key, 0)) commandLine += key;
    }

    /// <summary>Runs an ex command (without the leading ':').</summary>
    public void RunCommand(string input)
    {
        var w = Current; string cmd = input.Trim();
        if (cmd.Length == 0) return;
        int first = w.Row, last = w.Row; bool ranged = false;
        if (cmd.StartsWith('%')) { first = 0; last = lines.Count - 1; cmd = cmd[1..]; ranged = true; }
        else if (cmd.StartsWith("'<,'>")) { (first, last) = VisualRows(); cmd = cmd[5..]; ranged = true; }
        else
        {
            int i = 0; int? a = Address(cmd, ref i, w.Row);
            if (a != null)
            {
                first = last = a.Value; ranged = true;
                if (i < cmd.Length && cmd[i] == ',') { i++; int? b = Address(cmd, ref i, w.Row); if (b == null) { Fail("E14: 범위가 올바르지 않습니다"); return; } last = b.Value; }
                cmd = cmd[i..];
            }
        }
        if (first > last) (first, last) = (last, first);
        first = Math.Clamp(first, 0, lines.Count - 1); last = Math.Clamp(last, 0, lines.Count - 1);
        cmd = cmd.Trim();
        if (cmd.Length == 0) { if (ranged) { w.Row = last; w.Col = FirstNonBlank(last); } return; }

        string name = new(cmd.TakeWhile(c => char.IsAsciiLetter(c) || c == '!').ToArray()), arg = cmd[name.Length..].Trim();
        if (cmd.StartsWith('s') && cmd.Length > 1 && !char.IsAsciiLetterOrDigit(cmd[1]) && cmd[1] != ' ') { Substitute(cmd[1..], ranged ? first : w.Row, ranged ? last : w.Row); return; }
        if (cmd[0] is '>' or '<') { LineOperator(cmd[0].ToString(), first, last); return; }
        switch (name)
        {
            case "w": case "write":
                if (arg.Length > 0) FileName = arg;
                saved = [.. lines]; SaveCount++;
                Info($"\"{FileName}\" {lines.Count}줄, {Encoding.UTF8.GetByteCount(Text)}바이트 저장됨 (연습용 · 실제 파일은 만들지 않습니다)"); return;
            case "q": case "quit": case "clo": case "close": Quit(false, name is "clo" or "close"); return;
            case "q!": case "quit!": Quit(true, false); return;
            case "wq": case "x": case "xit": case "exit": case "wq!": case "x!":
                saved = [.. lines]; SaveCount++; Quit(true, false); if (Windows.Count == 1) Info($"\"{FileName}\" 저장 후 종료했습니다 (연습창은 그대로 남습니다)"); return;
            case "e!": case "edit!":
                BeginUndo(); lines.Clear(); lines.AddRange(saved); DiscardCount++; Info("마지막으로 저장한 상태로 되돌렸습니다"); return;
            case "sp": case "split": case "new": Split(false); return;
            case "vs": case "vsp": case "vsplit": case "vnew": case "vne": Split(true); return;
            case "on": case "only": Only(); return;
            case "noh": case "nohlsearch": hlsearch = false; return;
            case "set": case "se": SetOption(arg); return;
            case "d": case "delete": LineOperator("d", first, last); return;
            case "y": case "yank": LineOperator("y", first, last); return;
            case "u": case "undo": Undo(1); return;
            case "red": case "redo": Redo(1); return;
            case "h": case "help": Info("오른쪽 [단축키 모음]에서 명령과 설명을 볼 수 있습니다."); return;
        }
        if (cmd == "$") { w.Row = lines.Count - 1; w.Col = FirstNonBlank(w.Row); return; }
        Fail($"E492: 알 수 없는 명령입니다: {input.Trim()}");
    }

    private int? Address(string s, ref int i, int current)
    {
        if (i >= s.Length) return null;
        if (s[i] == '.') { i++; return current; }
        if (s[i] == '$') { i++; return lines.Count - 1; }
        int start = i; while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i > start ? int.Parse(s[start..i]) - 1 : null;
    }

    private void SetOption(string arg)
    {
        switch (arg)
        {
            case "nu": case "number": ShowNumbers = true; return;
            case "nonu": case "nonumber": ShowNumbers = false; return;
            case "nu!": case "invnu": case "number!": ShowNumbers = !ShowNumbers; return;
            case "hls": case "hlsearch": hlsearch = true; return;
            case "nohls": case "nohlsearch": hlsearch = false; return;
            case "": Info(ShowNumbers ? "number" : "nonumber"); return;
        }
        Fail($"E518: 지원하지 않는 설정입니다: {arg} (연습창은 nu · nonu · hls · nohls 를 지원합니다)");
    }

    private void Substitute(string spec, int first, int last)
    {
        char delimiter = spec[0]; var parts = new List<string>(); var current = new StringBuilder();
        for (int i = 1; i < spec.Length; i++)
        {
            if (spec[i] == '\\' && i + 1 < spec.Length && spec[i + 1] == delimiter) { current.Append(delimiter); i++; }
            else if (spec[i] == delimiter) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(spec[i]);
        }
        parts.Add(current.ToString());
        if (parts.Count < 2) { Fail("E486: 바꿀 내용을 /찾을말/바꿀말/ 형식으로 입력하세요"); return; }
        string pattern = parts[0].Length == 0 ? lastSearch : parts[0], replacement = parts[1], flags = parts.Count > 2 ? parts[2] : "";
        if (pattern.Length == 0) { Fail("E35: 찾을 말이 없습니다"); return; }
        bool global = flags.Contains('g'), ignore = flags.Contains('i');
        var comparison = ignore ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int changes = 0, changedLines = 0, lastLine = -1;
        for (int r = first; r <= last; r++)
        {
            string line = lines[r]; var output = new StringBuilder(); int at = 0, hits = 0;
            while (at <= line.Length)
            {
                int found = line.IndexOf(pattern, at, comparison);
                if (found < 0 || (!global && hits > 0)) break;
                output.Append(line, at, found - at).Append(replacement); at = found + pattern.Length; hits++;
            }
            if (hits == 0) continue;
            if (changes == 0) BeginUndo();
            output.Append(line, at, line.Length - at); lines[r] = output.ToString(); changes += hits; changedLines++; lastLine = r;
        }
        lastSearch = pattern; hlsearch = true;
        if (changes == 0) { Fail($"E486: 찾을 수 없습니다: {pattern}"); return; }
        Current.Row = lastLine; Current.Col = FirstNonBlank(lastLine);
        Info(changedLines > 1 || changes > 1 ? $"{changes}곳 바꿈 ({changedLines}줄)" : "1곳 바꿈");
    }

    private void Quit(bool force, bool closeOnly)
    {
        if (Windows.Count > 1) { CloseWindow(Current); return; }
        if (closeOnly) { Fail("E444: 마지막 창은 닫을 수 없습니다"); return; }
        if (Modified && !force) { QuitBlockedCount++; Fail("E37: 저장하지 않은 변경이 있습니다 (:w 로 저장하거나 :q! 로 버리고 종료)"); return; }
        QuitCount++;
        if (force && Modified) { BeginUndo(); lines.Clear(); lines.AddRange(saved); DiscardCount++; Info("변경을 버리고 종료했습니다 · 연습창은 마지막 저장 상태로 돌아갑니다"); return; }
        Info("종료했습니다 · 실제 vi라면 여기서 셸로 돌아갑니다 (연습창은 그대로 남습니다)");
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
            case "h": case "j": case "k": case "l": case "<Left>": case "<Down>": case "<Up>": case "<Right>":
            {
                char direction = key switch { "<Left>" => 'h', "<Down>" => 'j', "<Up>" => 'k', "<Right>" => 'l', _ => key[0] };
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

    private void Only()
    {
        if (Windows.Count == 1) { Info("이미 창이 하나입니다"); return; }
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
        if (undoOpen || replaying && undoOpen) return;
        undo.Push((lines.ToArray(), Current.Row, Current.Col)); redo.Clear(); undoOpen = true;
    }
    private void EndUndo()
    {
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
        if (w == Current && Mode is ViMode.Visual or ViMode.VisualLine) max = Math.Max(0, length - 1);
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

    public static string Display(string key) => key switch
    {
        "<Esc>" => "Esc", "<CR>" => "Enter", "<BS>" => "⌫", "<Del>" => "Del", "<Tab>" => "Tab", " " => "␣",
        "<Left>" => "←", "<Right>" => "→", "<Up>" => "↑", "<Down>" => "↓",
        _ when key.StartsWith("<C-") && key.Length == 5 => "Ctrl+" + char.ToUpperInvariant(key[3]),
        _ => key
    };
}
