using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

/// <summary>One entry stored inside a practice tar archive.</summary>
public sealed record ShellArchiveEntry(string Path, bool IsDir, string Content, string Mode, DateTime Modified, long? FixedSize);

/// <summary>A file or directory in the in-memory practice file system.</summary>
public sealed class ShellNode
{
    public ShellNode(string name, bool isDir, DateTime modified, string? mode = null)
    {
        Name = name; IsDir = isDir; Modified = modified; Mode = mode ?? (isDir ? "rwxr-xr-x" : "rw-r--r--");
    }
    public string Name { get; internal set; }
    public bool IsDir { get; }
    public ShellNode? Parent { get; internal set; }
    public SortedDictionary<string, ShellNode> Children { get; } = new(StringComparer.Ordinal);
    public string Content { get; set; } = "";
    /// <summary>Size for binary files whose bytes are not modelled (programs, core dumps, archives).</summary>
    public long? FixedSize { get; set; }
    public DateTime Modified { get; set; }
    public string Mode { get; set; }
    public string Owner { get; set; } = "user";
    public List<ShellArchiveEntry>? Archive { get; set; }
    public bool Gzip { get; set; }
    /// <summary>Set when a binary file was transferred in FTP ASCII mode.</summary>
    public bool Corrupt { get; set; }
    /// <summary>gzip keeps the original text here.</summary>
    public string? Compressed { get; set; }
    public bool Binary => FixedSize != null || Archive != null || Compressed != null;
    public long Size => IsDir ? 4096 : FixedSize ?? Encoding.UTF8.GetByteCount(Content);
    public bool Executable => Mode.Length >= 3 && Mode[2] == 'x';
    public string Path
    {
        get
        {
            if (Parent == null) return "/";
            var parts = new List<string>(); for (var node = this; node.Parent != null; node = node.Parent) parts.Add(node.Name);
            parts.Reverse(); return "/" + string.Join("/", parts);
        }
    }
    public ShellNode Clone(string? name = null)
    {
        var copy = new ShellNode(name ?? Name, IsDir, Modified, Mode) { Content = Content, FixedSize = FixedSize, Owner = Owner, Archive = Archive?.ToList(), Gzip = Gzip, Corrupt = Corrupt, Compressed = Compressed };
        foreach (var child in Children.Values) copy.Add(child.Clone());
        return copy;
    }
    public ShellNode Add(ShellNode child) { child.Parent = this; Children[child.Name] = child; return child; }
    public IEnumerable<ShellNode> Walk() { yield return this; foreach (var child in Children.Values) foreach (var node in child.Walk()) yield return node; }
}

/// <summary>In-memory Unix-like file tree. Nothing here touches the real disk.</summary>
public sealed class ShellFs
{
    public ShellFs(DateTime created) { Root = new ShellNode("", true, created); }
    public ShellNode Root { get; }

    public static string Combine(string cwd, string path)
    {
        if (path.StartsWith('/')) return Normalize(path);
        return Normalize((cwd.EndsWith('/') ? cwd : cwd + "/") + path);
    }

    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join("/", parts);
    }

    public ShellNode? Find(string absolute)
    {
        var node = Root;
        foreach (var part in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.IsDir || !node.Children.TryGetValue(part, out var child)) return null;
            node = child;
        }
        return node;
    }

    /// <summary>Why a path cannot be reached: "No such file or directory" or "Not a directory".</summary>
    public string Problem(string absolute)
    {
        var node = Root;
        foreach (var part in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.IsDir) return "Not a directory";
            if (!node.Children.TryGetValue(part, out var child)) return "No such file or directory";
            node = child;
        }
        return node.IsDir ? "Is a directory" : "";
    }

    public static string Parent(string absolute) { int cut = absolute.TrimEnd('/').LastIndexOf('/'); return cut <= 0 ? "/" : absolute[..cut]; }
    public static string BaseName(string absolute) { var trimmed = absolute.TrimEnd('/'); int cut = trimmed.LastIndexOf('/'); return cut < 0 ? trimmed : trimmed[(cut + 1)..]; }

    public ShellNode MakeDirs(string absolute, DateTime when)
    {
        var node = Root;
        foreach (var part in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out var child)) child = node.Add(new ShellNode(part, true, when));
            node = child;
        }
        return node;
    }

    public ShellNode WriteFile(string absolute, string content, DateTime when, string? mode = null)
    {
        var dir = MakeDirs(Parent(absolute), when); string name = BaseName(absolute);
        if (dir.Children.TryGetValue(name, out var existing) && !existing.IsDir)
        {
            existing.Content = content; existing.Modified = when; existing.FixedSize = null; existing.Archive = null; existing.Compressed = null; existing.Corrupt = false; existing.Gzip = false;
            if (mode != null) existing.Mode = mode; return existing;
        }
        return dir.Add(new ShellNode(name, false, when, mode) { Content = content });
    }

    public static void Remove(ShellNode node) { node.Parent?.Children.Remove(node.Name); node.Parent = null; }

    // ───────── Glob patterns (*, ?, [..]) ─────────

    public static bool HasGlob(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++) { if (pattern[i] == '\\') { i++; continue; } if (pattern[i] is '*' or '?' or '[') return true; }
        return false;
    }

    public static string Unescape(string pattern)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++) { if (pattern[i] == '\\' && i + 1 < pattern.Length) i++; sb.Append(pattern[i]); }
        return sb.ToString();
    }

    public static Regex GlobRegex(string pattern, bool ignoreCase = false)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { sb.Append(Regex.Escape(pattern[++i].ToString())); continue; }
            if (c == '*') { sb.Append(".*"); continue; }
            if (c == '?') { sb.Append('.'); continue; }
            if (c == '[')
            {
                int end = pattern.IndexOf(']', i + 2 < pattern.Length ? i + 2 : i + 1);
                if (end > i)
                {
                    string body = pattern[(i + 1)..end];
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    body = body.Replace("[:digit:]", "0-9").Replace("[:alpha:]", "a-zA-Z").Replace("[:upper:]", "A-Z").Replace("[:lower:]", "a-z").Replace("\\", "\\\\");
                    sb.Append('[').Append(body).Append(']'); i = end; continue;
                }
            }
            sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
    }

    /// <summary>Expands a shell glob relative to cwd. Results keep the typed prefix, sorted like ls.</summary>
    public List<string> Glob(string pattern, string cwd, string home)
    {
        string typed = pattern; bool absolute = typed.StartsWith('/');
        var segments = typed.Split('/').ToList();
        var results = new List<(ShellNode Node, string Shown)>();
        var start = absolute ? Root : Find(cwd);
        if (start == null) return [];
        results.Add((start, absolute ? "" : ""));
        for (int s = 0; s < segments.Count; s++)
        {
            string segment = segments[s];
            if (segment.Length == 0) { if (s == 0 && absolute) continue; if (s == segments.Count - 1) { results = results.Select(r => (r.Node, r.Shown + "/")).Where(r => r.Node.IsDir).ToList(); } continue; }
            var next = new List<(ShellNode, string)>();
            foreach (var (node, shown) in results)
            {
                if (!node.IsDir) continue;
                string prefix = shown.Length == 0 ? (absolute ? "/" : "") : shown.EndsWith('/') ? shown : shown + "/";
                if (!HasGlob(segment))
                {
                    string name = Unescape(segment);
                    var child = name == "." ? node : name == ".." ? node.Parent ?? node : node.Children.GetValueOrDefault(name);
                    if (child != null) next.Add((child, prefix + name));
                    continue;
                }
                var regex = GlobRegex(segment);
                foreach (var child in node.Children.Values)
                    if ((!child.Name.StartsWith('.') || segment.StartsWith('.')) && regex.IsMatch(child.Name)) next.Add((child, prefix + child.Name));
            }
            results = next;
            if (results.Count == 0) return [];
        }
        return results.Select(r => r.Shown).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
