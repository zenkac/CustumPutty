using System.Text.RegularExpressions;
using System.Windows.Media;

namespace EquipLink;

public enum ViGroup { Normal, Comment, Constant, Identifier, Statement, PreProc, Type, Special, Error, Todo }

/// <summary>
/// Colors for one vim color scheme. "default" follows the app theme; the others are
/// approximations of the schemes that ship with vim, so ":colorscheme ron" looks familiar.
/// </summary>
public sealed class ViScheme
{
    public string Name { get; }
    public bool Light { get; }
    private readonly string[] colors;
    private readonly Dictionary<string, SolidColorBrush> cache = new(StringComparer.OrdinalIgnoreCase);

    internal ViScheme(string name, bool light, params string[] colors) { Name = name; Light = light; this.colors = colors; }
    public bool FollowsTheme => Name == "default";

    private Brush Get(int index)
    {
        string hex = colors[index];
        if (FollowsTheme) return Ui.Brush(hex);
        if (!cache.TryGetValue(hex, out var brush)) { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); cache[hex] = brush; }
        return brush;
    }

    public Brush Background => Get(0);
    public Brush Foreground => Get(1);
    public Brush NonText => Get(2);
    public Brush LineNr => Get(3);
    public Brush CursorLineNr => Get(4);
    public Brush CursorLine => Get(5);
    public Brush Visual => Get(6);
    public Brush Search => Get(7);
    public Brush SearchText => Get(8);
    public Brush Status => Get(9);
    public Brush StatusText => Get(10);
    public Brush StatusInactive => Get(11);
    public Brush StatusInactiveText => Get(12);
    public Brush Cursor => Get(13);
    public Brush CursorText => Get(14);
    public Brush ModeText => Get(15);
    public Brush ErrorText => Get(16);

    public Brush Group(ViGroup group) => group switch
    {
        ViGroup.Comment => Get(17), ViGroup.Constant => Get(18), ViGroup.Identifier => Get(19), ViGroup.Statement => Get(20),
        ViGroup.PreProc => Get(21), ViGroup.Type => Get(22), ViGroup.Special => Get(23), ViGroup.Error => Get(24), ViGroup.Todo => Get(25),
        _ => Foreground
    };
}

public static class ViColorSchemes
{
    // Order: bg fg nontext linenr cursorlinenr cursorline visual search searchfg status statusfg statusNC statusNCfg cursor cursorfg mode errormsg
    //        comment constant identifier statement preproc type special error todo
    private static readonly ViScheme[] all =
    [
        new("default", false, "#111923", "#DCE5F0", "#687B91", "#687B91", "#E9C786", "#17212D", "#35685F", "#4A3D2A", "#DCE5F0", "#25413F", "#DCE5F0", "#1B2936", "#8797AA", "#5DD6BE", "#102D29", "#5DD6BE", "#E78494",
            "#687B91", "#E9C786", "#5DD6BE", "#B8A6EE", "#E78494", "#69DDC0", "#D2B479", "#E78494", "#E9C786"),
        new("blue", false, "#000087", "#FFFFFF", "#00AFFF", "#FFD700", "#FFD700", "#00005F", "#008787", "#FFD700", "#000087", "#D0D0D0", "#000087", "#5F5F87", "#D0D0D0", "#FFFFFF", "#000087", "#FFD700", "#FF5F5F",
            "#87AFD7", "#FFAFAF", "#87FFFF", "#FFFF87", "#FF87FF", "#87FF87", "#FFAF5F", "#FF5F5F", "#FFFF00"),
        new("darkblue", false, "#000040", "#C0C0C0", "#0030FF", "#90F020", "#90F020", "#00006F", "#3A3A8A", "#2050D0", "#FFFFFF", "#C0C0C0", "#000040", "#2A2A6A", "#A0A0A0", "#C0C0C0", "#000040", "#90F020", "#FF6060",
            "#80A0FF", "#FFA0A0", "#40FFFF", "#FFFF60", "#FF80FF", "#60FF60", "#FFA500", "#FF6060", "#FFFF00"),
        new("desert", false, "#333333", "#FFFFFF", "#87CEEB", "#FFFF00", "#FFFF00", "#4D4D4D", "#6B8E23", "#CD853F", "#F5DEB3", "#C2BFA5", "#000000", "#C2BFA5", "#7F7F7F", "#F0E68C", "#333333", "#DAA520", "#FF6060",
            "#87CEEB", "#FFA0A0", "#98FB98", "#F0E68C", "#CD5C5C", "#BDB76B", "#FFDEAD", "#FF6060", "#FF4500"),
        new("elflord", false, "#000000", "#00FFFF", "#4169E1", "#FFFF00", "#FFFF00", "#262626", "#4D4D4D", "#FFFF00", "#000000", "#FFFFFF", "#000000", "#4D4D4D", "#FFFFFF", "#00FFFF", "#000000", "#FFFFFF", "#FF4040",
            "#80A0FF", "#FF00FF", "#40FFFF", "#FFFF60", "#FF80FF", "#60FF60", "#FFA500", "#FF4040", "#FFFF00"),
        new("evening", false, "#333333", "#FFFFFF", "#4169E1", "#FFFF00", "#FFFF00", "#4D4D4D", "#808080", "#FFFF00", "#000000", "#E5E5E5", "#000000", "#4D4D4D", "#E5E5E5", "#00FF00", "#000000", "#FFFFFF", "#FF6060",
            "#80A0FF", "#FFA0A0", "#40FFFF", "#FFFF60", "#FF80FF", "#60FF60", "#FFA500", "#FF6060", "#FFFF00"),
        new("habamax", false, "#1C1C1C", "#BCBCBC", "#585858", "#585858", "#AFAF87", "#303030", "#5F87AF", "#5FAF5F", "#1C1C1C", "#9E9E9E", "#1C1C1C", "#3A3A3A", "#9E9E9E", "#BCBCBC", "#1C1C1C", "#BCBCBC", "#D75F5F",
            "#767676", "#AFAF87", "#87AFAF", "#D7AF87", "#AF8787", "#87AFD7", "#5F8787", "#D75F5F", "#AFAF87"),
        new("industry", false, "#000000", "#FFFFFF", "#5F5FFF", "#FFFF00", "#FFFF00", "#303030", "#808080", "#FFFF00", "#000000", "#FFFFFF", "#000000", "#808080", "#000000", "#FFFFFF", "#000000", "#FFFFFF", "#FF0000",
            "#808080", "#FF00FF", "#00FFFF", "#FFFF00", "#00FF00", "#00D700", "#FFA500", "#FF0000", "#FFFF00"),
        new("koehler", false, "#000000", "#FFFFFF", "#4169E1", "#FFFF00", "#FFFF00", "#333333", "#5F5F87", "#FFFF00", "#000000", "#C0C0C0", "#000000", "#4D4D4D", "#C0C0C0", "#FFFFFF", "#000000", "#FFFFFF", "#FF6060",
            "#80A0FF", "#FFA0A0", "#40FFFF", "#FFFF60", "#FF80FF", "#60FF60", "#FFA500", "#FF6060", "#FFFF00"),
        new("morning", true, "#E5E5E5", "#000000", "#0000FF", "#A52A2A", "#A52A2A", "#D0D0D0", "#BFBFBF", "#FFFF00", "#000000", "#000000", "#FFFFFF", "#BEBEBE", "#000000", "#000000", "#FFFFFF", "#A52A2A", "#FF0000",
            "#0000FF", "#FF00FF", "#008B8B", "#A52A2A", "#A020F0", "#2E8B57", "#6A5ACD", "#FF0000", "#0000FF"),
        new("murphy", false, "#000000", "#87FF87", "#4169E1", "#FFFF87", "#FFFF87", "#303030", "#3A3A6A", "#FFFF87", "#000000", "#87AFFF", "#000000", "#4E4E4E", "#C0C0C0", "#87FF87", "#000000", "#FFFF87", "#FF5F5F",
            "#FFA500", "#FFFFFF", "#87CEFA", "#87CEEB", "#8787FF", "#FFFF87", "#FF87FF", "#FF5F5F", "#FFFF00"),
        new("pablo", false, "#000000", "#FFFFFF", "#4169E1", "#808000", "#C0C000", "#262626", "#808080", "#C0C000", "#000000", "#C0C0C0", "#000000", "#4D4D4D", "#C0C0C0", "#00FF00", "#000000", "#FFFFFF", "#FF0000",
            "#808080", "#00FFFF", "#00C0C0", "#C0C000", "#00FF00", "#00C000", "#5F87FF", "#FF0000", "#FFFF00"),
        new("peachpuff", true, "#FFDAB9", "#000000", "#0000FF", "#A52A2A", "#A52A2A", "#F5C195", "#E0B896", "#FFFF00", "#000000", "#000000", "#FFFFFF", "#C0A08A", "#000000", "#000000", "#FFFFFF", "#000000", "#FF0000",
            "#406090", "#C00058", "#008B8B", "#CD3333", "#CD00CD", "#2E8B57", "#6A5ACD", "#FF0000", "#0000FF"),
        new("ron", false, "#000000", "#00FFFF", "#4682B4", "#FFFF00", "#FFFF00", "#262626", "#4682B4", "#FFFF00", "#000000", "#87CEEB", "#000000", "#4D4D4D", "#FFFFFF", "#00FFFF", "#000000", "#FFFFFF", "#FF6060",
            "#00CD00", "#FF8C00", "#40FFFF", "#87CEEB", "#FF80FF", "#FFD700", "#FFA500", "#FF4040", "#FFFF00"),
        new("slate", false, "#262626", "#FFFFFF", "#87CEEB", "#BEBEBE", "#FFD700", "#3A3A3A", "#5F5F5F", "#FFD700", "#000000", "#C2BFA5", "#000000", "#C2BFA5", "#666666", "#FFFFFF", "#262626", "#DAA520", "#FF6060",
            "#808080", "#FFA0A0", "#BDB76B", "#87CEEB", "#CD5C5C", "#DAA520", "#FFDEAD", "#FF6060", "#FF4500"),
        new("torte", false, "#000000", "#CCCCCC", "#4169E1", "#FFFF00", "#FFFF00", "#262626", "#555555", "#FFFF00", "#000000", "#BEBEBE", "#000000", "#4D4D4D", "#BEBEBE", "#CCCCCC", "#000000", "#FFFFFF", "#FF6060",
            "#80A0FF", "#FFA0A0", "#40FFFF", "#FFFF60", "#FF80FF", "#60FF60", "#FFA500", "#FF6060", "#FFFF00"),
        new("zellner", true, "#FFFFFF", "#000000", "#0000FF", "#A52A2A", "#A52A2A", "#E5E5E5", "#D0D0FF", "#FFFF00", "#000000", "#1C1C1C", "#FFFFFF", "#BEBEBE", "#000000", "#000000", "#FFFFFF", "#000000", "#FF0000",
            "#FF0000", "#FF00FF", "#0000FF", "#A52A2A", "#A020F0", "#2E8B57", "#6A5ACD", "#FF0000", "#0000FF"),
    ];

    public static IReadOnlyList<string> Names { get; } = all.Select(s => s.Name).ToArray();
    public static string? Find(string name) => all.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;
    public static ViScheme Get(string name) => all.FirstOrDefault(s => s.Name == name) ?? all[0];
}

/// <summary>A small line-based syntax highlighter for the file types used in the lessons.</summary>
public static class ViSyntax
{
    private static readonly HashSet<string> CStatements = ["if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue", "goto", "try", "catch", "finally", "throw", "new", "delete", "sizeof", "typedef", "namespace", "using", "import", "package", "function", "var", "let"];
    private static readonly HashSet<string> CTypes = ["int", "char", "void", "float", "double", "long", "short", "unsigned", "signed", "struct", "enum", "union", "const", "static", "extern", "volatile", "bool", "string", "class", "public", "private", "protected", "virtual", "override", "final", "auto", "inline", "size_t", "uint8_t", "uint16_t", "uint32_t", "int32_t", "FILE", "byte", "boolean", "String"];
    private static readonly HashSet<string> CConstants = ["true", "false", "NULL", "null", "nullptr", "this", "TRUE", "FALSE", "EOF"];
    private static readonly HashSet<string> ShStatements = ["if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case", "esac", "in", "function", "return", "exit", "break", "continue", "echo", "cd", "export", "local", "read", "source", "printf", "test", "set", "unset", "shift", "exec", "eval", "trap", "alias"];
    private static readonly HashSet<string> PyStatements = ["def", "class", "if", "elif", "else", "for", "while", "return", "import", "from", "as", "with", "try", "except", "finally", "pass", "break", "continue", "in", "and", "or", "not", "is", "lambda", "yield", "raise", "global", "print"];
    private static readonly HashSet<string> VimStatements = ["set", "se", "syntax", "syn", "colorscheme", "colo", "filetype", "let", "map", "nmap", "nnoremap", "inoremap", "vnoremap", "noremap", "imap", "autocmd", "au", "if", "endif", "else", "function", "endfunction", "call", "source", "so", "highlight", "hi"];
    private static readonly Regex LogStamp = new(@"^\s*\[?\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d+)?\]?", RegexOptions.Compiled);
    private static readonly Regex LogWords = new(@"\b(ERROR|FATAL|CRITICAL|ALARM|FAIL(ED)?|ALM-\d+)\b|\b(WARN(ING)?)\b|\b(INFO|NOTICE)\b|\b(DEBUG|TRACE)\b|\b\d+(\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex Todo = new(@"\b(TODO|FIXME|XXX|NOTE)\b", RegexOptions.Compiled);

    public static List<(int Start, int Length, ViGroup Group)> Spans(string line, string type, ref bool block)
    {
        var spans = new List<(int, int, ViGroup)>();
        if (line.Length == 0) return spans;
        switch (type)
        {
            case "c": case "cpp": case "java": case "cs": case "javascript": CLike(line, type, ref block, spans); break;
            case "sh": Shell(line, spans); break;
            case "python": Python(line, spans); break;
            case "conf": Conf(line, spans); break;
            case "log": Log(line, spans); break;
            case "vim": Vim(line, spans); break;
            case "csv": foreach (Match m in Regex.Matches(line, @"(?<![\w.])-?\d+(\.\d+)?(?![\w.])")) spans.Add((m.Index, m.Length, ViGroup.Constant)); break;
        }
        return spans;
    }

    private static void Comment(string line, int start, int end, List<(int, int, ViGroup)> spans)
    {
        spans.Add((start, end - start, ViGroup.Comment));
        foreach (Match m in Todo.Matches(line[start..end])) spans.Add((start + m.Index, m.Length, ViGroup.Todo));
    }

    private static int Quoted(string line, int i)
    {
        char quote = line[i]; int j = i + 1;
        while (j < line.Length && line[j] != quote) { if (line[j] == '\\') j++; j++; }
        return Math.Min(line.Length, j + 1);
    }

    private static int Word(string line, int i, string extra = "")
    {
        int j = i; while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_' || extra.Contains(line[j]))) j++;
        return j;
    }

    private static void CLike(string line, string type, ref bool block, List<(int, int, ViGroup)> spans)
    {
        int i = 0;
        if (block)
        {
            int end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0) { Comment(line, 0, line.Length, spans); return; }
            Comment(line, 0, end + 2, spans); i = end + 2; block = false;
        }
        string trimmed = line.TrimStart();
        if (i == 0 && type is "c" or "cpp" && trimmed.StartsWith('#'))
        {
            int start = line.Length - trimmed.Length, word = Word(line, start + 1);
            spans.Add((start, word - start, ViGroup.PreProc));
            var m = Regex.Match(line[word..], "<[^>]*>|\"[^\"]*\"");
            if (m.Success) spans.Add((word + m.Index, m.Length, ViGroup.Constant));
            int comment = line.IndexOf("//", word, StringComparison.Ordinal); if (comment >= 0) Comment(line, comment, line.Length, spans);
            return;
        }
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { Comment(line, i, line.Length, spans); return; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { Comment(line, i, line.Length, spans); block = true; return; }
                Comment(line, i, end + 2, spans); i = end + 2; continue;
            }
            if (c is '"' or '\'' || c == '`' && type == "javascript") { int end = Quoted(line, i); spans.Add((i, end - i, ViGroup.Constant)); i = end; continue; }
            if (char.IsAsciiDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_'))
            {
                int end = Word(line, i, "."); spans.Add((i, end - i, ViGroup.Constant)); i = end; continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int end = Word(line, i); string word = line[i..end];
                if (CStatements.Contains(word)) spans.Add((i, end - i, ViGroup.Statement));
                else if (CTypes.Contains(word)) spans.Add((i, end - i, ViGroup.Type));
                else if (CConstants.Contains(word)) spans.Add((i, end - i, ViGroup.Constant));
                i = end; continue;
            }
            i++;
        }
    }

    private static void Shell(string line, List<(int, int, ViGroup)> spans)
    {
        int i = 0; bool command = true;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] == ';')) { Comment(line, i, line.Length, spans); return; }
            if (c is '"' or '\'')
            {
                int end = Quoted(line, i); spans.Add((i, end - i, ViGroup.Constant));
                if (c == '"') foreach (Match m in Regex.Matches(line[i..end], @"\$(\{[^}]*\}|[A-Za-z_][A-Za-z0-9_]*|[0-9?#@*$!])")) spans.Add((i + m.Index, m.Length, ViGroup.Identifier));
                i = end; command = false; continue;
            }
            if (c == '$' && i + 1 < line.Length)
            {
                int end = line[i + 1] == '{' ? Math.Max(line.IndexOf('}', i + 1) + 1, i + 2) : line[i + 1] == '(' ? i + 2 : Math.Max(Word(line, i + 1), i + 2);
                spans.Add((i, Math.Min(end, line.Length) - i, line[i + 1] == '(' ? ViGroup.Special : ViGroup.Identifier)); i = Math.Min(end, line.Length); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int end = Word(line, i, "-"); string word = line[i..end];
                if (end < line.Length && line[end] == '=' && command) spans.Add((i, end - i, ViGroup.Identifier));
                else if (ShStatements.Contains(word) && command) spans.Add((i, end - i, ViGroup.Statement));
                command = false; i = end; continue;
            }
            if (char.IsAsciiDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]))) { int end = Word(line, i, "."); spans.Add((i, end - i, ViGroup.Constant)); i = end; continue; }
            if (c is ';' or '|' or '&' or '(' or ')') command = true;
            else if (!char.IsWhiteSpace(c)) command = false;
            i++;
        }
    }

    private static void Python(string line, List<(int, int, ViGroup)> spans)
    {
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#') { Comment(line, i, line.Length, spans); return; }
            if (c is '"' or '\'') { int end = Quoted(line, i); spans.Add((i, end - i, ViGroup.Constant)); i = end; continue; }
            if (c == '@' && line[..i].Trim().Length == 0) { int end = Word(line, i + 1, "."); spans.Add((i, end - i, ViGroup.PreProc)); i = end; continue; }
            if (char.IsAsciiDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_')) { int end = Word(line, i, "."); spans.Add((i, end - i, ViGroup.Constant)); i = end; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                int end = Word(line, i); string word = line[i..end];
                if (PyStatements.Contains(word)) spans.Add((i, end - i, ViGroup.Statement));
                else if (word is "True" or "False" or "None") spans.Add((i, end - i, ViGroup.Constant));
                else if (i >= 4 && Regex.IsMatch(line[..i], @"\b(def|class)\s+$")) spans.Add((i, end - i, ViGroup.Identifier));
                i = end; continue;
            }
            i++;
        }
    }

    private static void Conf(string line, List<(int, int, ViGroup)> spans)
    {
        string trimmed = line.TrimStart(); int start = line.Length - trimmed.Length;
        if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) { Comment(line, start, line.Length, spans); return; }
        if (trimmed.StartsWith('[')) { spans.Add((start, trimmed.TrimEnd().Length, ViGroup.Special)); return; }
        var m = Regex.Match(line, @"^(\s*)([^=:#;\s][^=:#;]*?)(\s*[=:]\s*)([^#;]*)(.*)$");
        if (!m.Success) return;
        spans.Add((m.Groups[2].Index, m.Groups[2].Length, ViGroup.Identifier));
        string value = m.Groups[4].Value.TrimEnd();
        if (Regex.IsMatch(value, @"^-?\d+(\.\d+)?$|^(on|off|true|false|yes|no)$", RegexOptions.IgnoreCase)) spans.Add((m.Groups[4].Index, value.Length, ViGroup.Constant));
        if (m.Groups[5].Length > 0) Comment(line, m.Groups[5].Index, line.Length, spans);
    }

    private static void Log(string line, List<(int, int, ViGroup)> spans)
    {
        int from = 0; var stamp = LogStamp.Match(line);
        if (stamp.Success) { spans.Add((stamp.Index, stamp.Length, ViGroup.Constant)); from = stamp.Length; }
        foreach (Match m in LogWords.Matches(line, from))
        {
            var group = m.Groups[1].Success ? ViGroup.Error : m.Groups[3].Success ? ViGroup.Todo : m.Groups[5].Success ? ViGroup.Identifier : m.Groups[6].Success ? ViGroup.Comment : ViGroup.Constant;
            spans.Add((m.Index, m.Length, group));
        }
    }

    private static void Vim(string line, List<(int, int, ViGroup)> spans)
    {
        string trimmed = line.TrimStart(); int start = line.Length - trimmed.Length;
        if (trimmed.StartsWith('"')) { Comment(line, start, line.Length, spans); return; }
        int end = Word(line, start); string word = line[start..end];
        if (VimStatements.Contains(word)) spans.Add((start, end - start, ViGroup.Statement));
        foreach (Match m in Regex.Matches(line[end..], @"(?<=\s)(no|inv)?[a-z]+(?==|\s|$|!|\?)|=[^\s""]+|(?<=\s)""[^""]*$"))
        {
            var group = m.Value.StartsWith('=') ? ViGroup.Constant : m.Value.StartsWith('"') ? ViGroup.Comment : ViGroup.Type;
            spans.Add((end + m.Index + (m.Value.StartsWith('=') ? 1 : 0), m.Length - (m.Value.StartsWith('=') ? 1 : 0), group));
        }
    }
}
