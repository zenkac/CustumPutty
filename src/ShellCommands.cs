using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

public sealed partial class ShellEngine
{
    private readonly List<ShellProc> processes = [];
    private readonly HashSet<int> stubborn = [3101];
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private const string Blue = "\u001b[01;34m", Green = "\u001b[01;32m", Red = "\u001b[01;31m", Plain = "\u001b[0m";

    /// <summary>Line to start the embedded editor at (from "vi +N file").</summary>
    public int EditStartLine { get; private set; }

    private void Register()
    {
        void On(Func<Ctx, int> handler, params string[] names) { foreach (var name in names) commands[name] = handler; }
        On(Pwd, "pwd"); On(Cd, "cd"); On(Ls, "ls", "dir"); On(Mkdir, "mkdir"); On(Rmdir, "rmdir"); On(Touch, "touch"); On(Cp, "cp"); On(Mv, "mv"); On(Rm, "rm");
        On(Chmod, "chmod"); On(Chown, "chown", "chgrp"); On(Cat, "cat"); On(Less, "less", "more"); On(Head, "head"); On(Tail, "tail"); On(Wc, "wc");
        On(Echo, "echo"); On(Printf, "printf"); On(Clear, "clear"); On(HistoryCommand, "history"); On(Alias, "alias"); On(Unalias, "unalias");
        On(Export, "export"); On(Unset, "unset"); On(Env, "env", "printenv");
        On(c => { c.Line(User); return 0; }, "whoami"); On(c => { c.Line(Host); return 0; }, "hostname");
        On(c => { c.Line("uid=1000(user) gid=1000(user) groups=1000(user),10(wheel)"); return 0; }, "id");
        On(Date, "date"); On(Uname, "uname"); On(Uptime, "uptime"); On(Free, "free"); On(Df, "df"); On(Du, "du"); On(Tree, "tree");
        On(Which, "which"); On(TypeCommand, "type"); On(Man, "man"); On(FileCommand, "file"); On(Basename, "basename"); On(Dirname, "dirname"); On(Seq, "seq");
        On(_ => 0, "sleep", "true", ":", "sync"); On(_ => 1, "false");
        On(c => { c.Note("연습 터미널은 닫히지 않습니다 · 실제 서버에서는 exit 로 접속이 끊깁니다"); return 0; }, "exit", "logout");
        On(Sudo, "sudo"); On(Ps, "ps"); On(Kill, "kill"); On(Pkill, "pkill", "killall"); On(Pgrep, "pgrep"); On(Top, "top", "htop");
        On(Vi, "vi", "vim", "view");
        On(c => c.Fail("연습 터미널은 vi 편집기만 지원합니다 · vi " + string.Join(" ", c.Args) + " 로 열어 보세요"), "nano", "emacs", "gedit", "pico");
        On(SourceCommand, "source", "."); On(Bash, "bash", "sh");
        RegisterText(); RegisterRemote();
    }

    // ───────── Option parsing ─────────

    private sealed class Opts
    {
        public HashSet<char> Flags { get; } = [];
        public Dictionary<char, string> Values { get; } = [];
        public List<string> Long { get; } = [];
        public List<string> Rest { get; } = [];
        public string? Error { get; set; }
        public bool Has(char c) => Flags.Contains(c);
        public string? Value(char c) => Values.GetValueOrDefault(c);
        public string? LongValue(string name) { var item = Long.FirstOrDefault(l => l == name || l.StartsWith(name + "=")); return item == null ? null : item.Contains('=') ? item[(item.IndexOf('=') + 1)..] : ""; }
    }

    private static Opts Options(List<string> args, string flags, string valued = "", bool numeric = false, bool stopAtOperand = false)
    {
        var o = new Opts(); bool end = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (end || a.Length < 2 || a[0] != '-') { o.Rest.Add(a); if (stopAtOperand) end = true; continue; }
            if (a == "--") { end = true; continue; }
            if (a.StartsWith("--")) { o.Long.Add(a[2..]); continue; }
            if (numeric && a[1..].All(char.IsAsciiDigit)) { o.Values['n'] = a[1..]; continue; }
            for (int j = 1; j < a.Length; j++)
            {
                char c = a[j];
                if (valued.Contains(c))
                {
                    if (j + 1 < a.Length) o.Values[c] = a[(j + 1)..];
                    else if (i + 1 < args.Count) o.Values[c] = args[++i];
                    else o.Error ??= $"option requires an argument -- '{c}'";
                    break;
                }
                if (flags.Contains(c)) o.Flags.Add(c); else o.Error ??= $"invalid option -- '{c}'";
            }
        }
        return o;
    }

    private static int Usage(Ctx c, string error, int code = 2) { c.Err.Append($"{c.Name}: {error}\nTry '{c.Name} --help' for more information.\n"); return code; }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Split('\n').ToList(); if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Human(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(C);
        double value = bytes; string[] units = ["K", "M", "G", "T"]; int unit = -1;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value < 10 ? (Math.Ceiling(value * 10) / 10).ToString("0.0", C) + units[unit] : Math.Ceiling(value).ToString("0", C) + units[unit];
    }

    private string Stamp(DateTime time) =>
        (Now - time).TotalDays < 180 && time <= Now.AddHours(1)
            ? $"{time.ToString("MMM", C)} {time.Day,2} {time:HH:mm}"
            : $"{time.ToString("MMM", C)} {time.Day,2}  {time.Year}";

    private static bool IsArchiveName(string name) => name.EndsWith(".tar.gz") || name.EndsWith(".tgz") || name.EndsWith(".gz") || name.EndsWith(".zip") || name.EndsWith(".tar");

    private static string Colored(ShellNode node, string shown, bool tty)
    {
        if (!tty) return shown;
        if (node.IsDir) return Blue + shown + Plain;
        if (node.Executable) return Green + shown + Plain;
        if (IsArchiveName(node.Name)) return Red + shown + Plain;
        return shown;
    }

    private string LongRow(ShellNode node, string shown, bool human, int linkWidth, int ownerWidth, int sizeWidth, bool tty)
    {
        int links = node.IsDir ? 2 + node.Children.Values.Count(x => x.IsDir) : 1;
        string size = human ? Human(node.Size) : node.Size.ToString(C);
        return $"{(node.IsDir ? 'd' : '-')}{node.Mode} {links.ToString(C).PadLeft(linkWidth)} {node.Owner.PadRight(ownerWidth)} {node.Owner.PadRight(ownerWidth)} {size.PadLeft(sizeWidth)} {Stamp(node.Modified)} {Colored(node, shown, tty)}";
    }

    /// <summary>Runs another command with the same output streams (sudo, xargs, find -exec).</summary>
    private int RunNested(Ctx parent, List<string> argv, string? stdin = null)
    {
        if (argv.Count == 0) return 0;
        if (parent.Depth > 6) return parent.Fail("너무 깊게 중첩되었습니다");
        var child = new Ctx(this, argv[0], argv.Skip(1).ToList(), stdin, parent.Tty, parent.Depth + 1, parent.Piped);
        int status = Dispatch(child);
        parent.Out.Append(child.Out); parent.Err.Append(child.Err); parent.Notes.AddRange(child.Notes);
        runs.Add(new ShellRun(string.Join(" ", argv), child.Name, child.Args, StripAnsi(child.Out.ToString()), child.Err.ToString(), status, "shell"));
        return status;
    }

    // ───────── Navigation and listing ─────────

    private int Pwd(Ctx c) { c.Line(Cwd); return 0; }

    private int Cd(Ctx c)
    {
        if (c.Args.Count > 1) { c.Err.Append("bash: cd: too many arguments\n"); return 1; }
        string target = c.Args.Count == 0 ? Home : c.Args[0]; bool back = target == "-";
        if (back) target = previousDir;
        var node = Fs.Find(Resolve(target));
        if (node == null) { c.Err.Append($"bash: cd: {target}: No such file or directory\n"); return 1; }
        if (!node.IsDir) { c.Err.Append($"bash: cd: {target}: Not a directory\n"); return 1; }
        previousDir = Cwd; Cwd = node.Path;
        if (back) c.Line(Cwd);
        return 0;
    }

    private int Ls(Ctx c)
    {
        var o = Options(c.Args, "laAhtrR1dFSi");
        if (o.Error != null) return Usage(c, o.Error);
        bool longFormat = o.Has('l'), all = o.Has('a') || o.Long.Contains("all"), almost = o.Has('A'), human = o.Has('h') || o.Long.Contains("human-readable");
        bool recursive = o.Has('R'), one = o.Has('1') || !c.Tty, itself = o.Has('d'), classify = o.Has('F'), color = c.Tty && o.LongValue("color") != "never";
        var targets = o.Rest.Count == 0 ? ["."] : o.Rest;
        var files = new List<(string Shown, ShellNode Node)>(); var dirs = new List<(string Shown, ShellNode Node)>(); int status = 0;
        foreach (var target in targets)
        {
            var node = Fs.Find(Resolve(target));
            if (node == null) { c.Err.Append($"ls: cannot access '{target}': No such file or directory\n"); status = 2; continue; }
            if (node.IsDir && !itself) dirs.Add((target, node)); else files.Add((target, node));
        }
        IEnumerable<(string Shown, ShellNode Node)> Sorted(IEnumerable<(string Shown, ShellNode Node)> items)
        {
            var list = o.Has('t') ? items.OrderByDescending(x => x.Node.Modified).ThenBy(x => x.Shown, StringComparer.Ordinal)
                : o.Has('S') ? items.OrderByDescending(x => x.Node.Size).ThenBy(x => x.Shown, StringComparer.Ordinal)
                : items.OrderBy(x => x.Shown.TrimStart('.').Length == 0 ? x.Shown : x.Shown, StringComparer.Ordinal);
            var result = list.ToList(); if (o.Has('r')) result.Reverse(); return result;
        }
        string Name(ShellNode node, string shown) => Colored(node, shown, color) + (classify ? node.IsDir ? "/" : node.Executable ? "*" : "" : "");
        void Write(List<(string Shown, ShellNode Node)> items, bool total)
        {
            if (longFormat)
            {
                if (total) c.Line("total " + items.Where(x => x.Shown is not ("." or "..")).Sum(x => x.Node.IsDir ? 4 : (x.Node.Size + 4095) / 4096 * 4));
                if (items.Count == 0) return;
                int linkWidth = items.Max(x => (x.Node.IsDir ? 2 + x.Node.Children.Values.Count(y => y.IsDir) : 1).ToString(C).Length);
                int ownerWidth = items.Max(x => x.Node.Owner.Length), sizeWidth = items.Max(x => (human ? Human(x.Node.Size) : x.Node.Size.ToString(C)).Length);
                foreach (var (shown, node) in items) c.Line(LongRow(node, shown, human, linkWidth, ownerWidth, sizeWidth, color) + (classify ? node.IsDir ? "/" : node.Executable ? "*" : "" : ""));
                return;
            }
            if (one) { foreach (var (shown, node) in items) c.Line(Name(node, shown)); return; }
            var names = items.Select(x => x.Shown + (classify ? x.Node.IsDir ? "/" : x.Node.Executable ? "*" : "" : "")).ToList();
            c.Out.Append(Columnize(names, names.Select((name, i) => Colored(items[i].Node, name, color)).ToList()));
        }
        if (files.Count > 0) Write(Sorted(files).ToList(), false);
        bool headers = targets.Count > 1 || recursive; bool firstBlock = files.Count == 0;
        void ListDir(string shown, ShellNode dir)
        {
            if (headers) { if (!firstBlock) c.Out.Append('\n'); c.Line(shown + ":"); }
            firstBlock = false;
            var entries = dir.Children.Values.Where(x => all || almost || !x.Name.StartsWith('.')).Select(x => (x.Name, x)).ToList();
            var sorted = Sorted(entries).ToList();
            if (all) sorted.InsertRange(0, [(".", dir), ("..", dir.Parent ?? dir)]);
            Write(sorted, true);
            if (recursive) foreach (var (name, child) in Sorted(entries)) if (child.IsDir) ListDir((shown.EndsWith('/') ? shown : shown + "/") + name, child);
        }
        foreach (var (shown, node) in Sorted(dirs)) ListDir(shown, node);
        return status;
    }

    private int Tree(Ctx c)
    {
        var o = Options(c.Args, "adf", "L");
        if (o.Error != null) return Usage(c, o.Error);
        int depthLimit = int.TryParse(o.Value('L'), out int limit) ? limit : int.MaxValue; bool dirsOnly = o.Has('d'), all = o.Has('a');
        string target = o.Rest.FirstOrDefault() ?? "."; var root = Fs.Find(Resolve(target));
        if (root is not { IsDir: true }) { c.Line($"{target} [error opening dir]"); return 2; }
        int dirCount = 0, fileCount = 0;
        c.Line(Colored(root, target, c.Tty));
        void Walk(ShellNode node, string prefix, int depth)
        {
            if (depth > depthLimit) return;
            var children = node.Children.Values.Where(x => (all || !x.Name.StartsWith('.')) && (!dirsOnly || x.IsDir)).ToList();
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i]; bool last = i == children.Count - 1;
                c.Line(prefix + (last ? "└── " : "├── ") + Colored(child, child.Name, c.Tty));
                if (child.IsDir) { dirCount++; Walk(child, prefix + (last ? "    " : "│   "), depth + 1); } else fileCount++;
            }
        }
        Walk(root, "", 1);
        c.Line(""); c.Line(dirsOnly ? $"{dirCount} directories" : $"{dirCount} directories, {fileCount} files");
        return 0;
    }

    // ───────── Files ─────────

    private int Mkdir(Ctx c)
    {
        var o = Options(c.Args, "pv", "m");
        if (o.Error != null) return Usage(c, o.Error);
        if (o.Rest.Count == 0) return Usage(c, "missing operand", 1);
        int status = 0;
        foreach (var arg in o.Rest)
        {
            string path = Resolve(arg); var existing = Fs.Find(path);
            if (existing != null) { if (o.Has('p') && existing.IsDir) continue; c.Err.Append($"mkdir: cannot create directory ‘{arg}’: File exists\n"); status = 1; continue; }
            var parent = Fs.Find(ShellFs.Parent(path));
            if (parent is not { IsDir: true } && !o.Has('p')) { c.Err.Append($"mkdir: cannot create directory ‘{arg}’: No such file or directory\n"); status = 1; continue; }
            if (o.Has('p') && FirstFileOnPath(path) != null) { c.Err.Append($"mkdir: cannot create directory ‘{arg}’: Not a directory\n"); status = 1; continue; }
            Fs.MakeDirs(path, Now);
            if (o.Has('v')) c.Line($"mkdir: created directory '{arg}'");
        }
        return status;
    }

    private string? FirstFileOnPath(string path)
    {
        var node = Fs.Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out var child)) return null;
            if (!child.IsDir) return child.Path;
            node = child;
        }
        return null;
    }

    private int Rmdir(Ctx c)
    {
        var o = Options(c.Args, "pv");
        if (o.Rest.Count == 0) return Usage(c, "missing operand", 1);
        int status = 0;
        foreach (var arg in o.Rest)
        {
            var node = Fs.Find(Resolve(arg));
            string? problem = node == null ? "No such file or directory" : !node.IsDir ? "Not a directory" : node.Children.Count > 0 ? "Directory not empty" : node.Parent == null ? "Device or resource busy" : null;
            if (problem != null) { c.Err.Append($"rmdir: failed to remove '{arg}': {problem}\n"); status = 1; continue; }
            ShellFs.Remove(node!); FixCwd(c);
        }
        return status;
    }

    private void FixCwd(Ctx c)
    {
        if (Fs.Find(Cwd) != null) return;
        string path = Cwd; while (Fs.Find(path) == null) path = ShellFs.Parent(path);
        Cwd = path; c.Note($"현재 폴더가 지워져서 {DisplayPath(path)} 로 옮겼습니다 (실제 bash 는 지워진 폴더에 그대로 남습니다)");
    }

    private int Touch(Ctx c)
    {
        var o = Options(c.Args, "acm");
        if (o.Rest.Count == 0) return Usage(c, "missing file operand", 1);
        int status = 0;
        foreach (var arg in o.Rest)
        {
            string path = Resolve(arg); var node = Fs.Find(path);
            if (node != null) { node.Modified = Now; continue; }
            if (Fs.Find(ShellFs.Parent(path)) is not { IsDir: true }) { c.Err.Append($"touch: cannot touch '{arg}': No such file or directory\n"); status = 1; continue; }
            if (!o.Has('c')) Fs.WriteFile(path, "", Now);
        }
        return status;
    }

    private bool Protected(ShellNode node, Ctx c, string verb)
    {
        if (node.Owner == "user") return false;
        c.Err.Append($"{c.Name}: cannot {verb} '{node.Path}': Permission denied\n");
        return true;
    }

    private int Cp(Ctx c)
    {
        var o = Options(c.Args, "rRivpfan");
        if (o.Error != null) return Usage(c, o.Error);
        if (o.Rest.Count < 2) return Usage(c, o.Rest.Count == 0 ? "missing file operand" : $"missing destination file operand after '{o.Rest[0]}'", 1);
        bool recursive = o.Has('r') || o.Has('R') || o.Has('a'), keep = o.Has('p') || o.Has('a');
        string dest = o.Rest[^1]; var sources = o.Rest.Take(o.Rest.Count - 1).ToList();
        string destPath = Resolve(dest); var destNode = Fs.Find(destPath);
        if (sources.Count > 1 && destNode is not { IsDir: true }) { c.Err.Append($"cp: target '{dest}' is not a directory\n"); return 1; }
        int status = 0;
        foreach (var src in sources)
        {
            string srcPath = Resolve(src); var node = Fs.Find(srcPath);
            if (node == null) { c.Err.Append($"cp: cannot stat '{src}': No such file or directory\n"); status = 1; continue; }
            if (node.IsDir && !recursive) { c.Err.Append($"cp: -r not specified; omitting directory '{src}'\n"); status = 1; continue; }
            string target = destNode is { IsDir: true } ? ShellFs.Combine(destNode.Path, node.Name) : destPath;
            if (node.IsDir && (target == srcPath || target.StartsWith(srcPath + "/"))) { c.Err.Append($"cp: cannot copy a directory, '{src}', into itself, '{dest}'\n"); status = 1; continue; }
            if (target == srcPath) { c.Err.Append($"cp: '{src}' and '{dest}' are the same file\n"); status = 1; continue; }
            var parent = Fs.Find(ShellFs.Parent(target));
            if (parent is not { IsDir: true }) { c.Err.Append($"cp: cannot create regular file '{dest}': No such file or directory\n"); status = 1; continue; }
            if (Protected(parent, c, "create")) { status = 1; continue; }
            if (o.Has('n') && Fs.Find(target) != null) continue;
            if (!CopyInto(node, parent, ShellFs.BaseName(target), keep, c, src)) status = 1;
            else if (o.Has('v')) c.Line($"'{src}' -> '{dest}{(destNode is { IsDir: true } ? "/" + node.Name : "")}'");
        }
        return status;
    }

    private bool CopyInto(ShellNode source, ShellNode parent, string name, bool keep, Ctx c, string shown)
    {
        parent.Children.TryGetValue(name, out var existing);
        if (existing != null && existing.IsDir && source.IsDir)
        {
            foreach (var child in source.Children.Values.ToList()) CopyInto(child, existing, child.Name, keep, c, shown + "/" + child.Name);
            return true;
        }
        if (existing is { IsDir: true }) { c.Err.Append($"cp: cannot overwrite directory '{existing.Path}' with non-directory\n"); return false; }
        if (existing is { IsDir: false } && source.IsDir) { c.Err.Append($"cp: cannot overwrite non-directory '{existing.Path}' with directory '{shown}'\n"); return false; }
        var copy = source.Clone(name);
        if (!keep) foreach (var node in copy.Walk()) node.Modified = Now;
        foreach (var node in copy.Walk()) node.Owner = "user";
        parent.Add(copy);
        return true;
    }

    private int Mv(Ctx c)
    {
        var o = Options(c.Args, "ifvn");
        if (o.Error != null) return Usage(c, o.Error);
        if (o.Rest.Count < 2) return Usage(c, o.Rest.Count == 0 ? "missing file operand" : $"missing destination file operand after '{o.Rest[0]}'", 1);
        string dest = o.Rest[^1]; var sources = o.Rest.Take(o.Rest.Count - 1).ToList();
        string destPath = Resolve(dest); var destNode = Fs.Find(destPath);
        if (sources.Count > 1 && destNode is not { IsDir: true }) { c.Err.Append($"mv: target '{dest}' is not a directory\n"); return 1; }
        int status = 0;
        foreach (var src in sources)
        {
            string srcPath = Resolve(src); var node = Fs.Find(srcPath);
            if (node == null) { c.Err.Append($"mv: cannot stat '{src}': No such file or directory\n"); status = 1; continue; }
            if (Protected(node, c, "move")) { status = 1; continue; }
            string target = destNode is { IsDir: true } ? ShellFs.Combine(destNode.Path, node.Name) : destPath;
            if (target == srcPath) { c.Err.Append($"mv: '{src}' and '{dest}' are the same file\n"); status = 1; continue; }
            if (node.IsDir && target.StartsWith(srcPath + "/")) { c.Err.Append($"mv: cannot move '{src}' to a subdirectory of itself, '{dest}'\n"); status = 1; continue; }
            var parent = Fs.Find(ShellFs.Parent(target));
            if (parent is not { IsDir: true }) { c.Err.Append($"mv: cannot move '{src}' to '{dest}': No such file or directory\n"); status = 1; continue; }
            if (parent.Children.TryGetValue(ShellFs.BaseName(target), out var existing))
            {
                if (o.Has('n')) continue;
                if (existing.IsDir && !node.IsDir) { c.Err.Append($"mv: cannot overwrite directory '{dest}' with non-directory\n"); status = 1; continue; }
                if (existing.IsDir && existing.Children.Count > 0) { c.Err.Append($"mv: cannot move '{src}' to '{dest}': Directory not empty\n"); status = 1; continue; }
                ShellFs.Remove(existing);
            }
            ShellFs.Remove(node); node.Name = ShellFs.BaseName(target); parent.Add(node);
            if (o.Has('v')) c.Line($"renamed '{src}' -> '{dest}'");
            if (Cwd == srcPath || Cwd.StartsWith(srcPath + "/")) Cwd = node.Path + Cwd[srcPath.Length..];
        }
        return status;
    }

    private int Rm(Ctx c)
    {
        var o = Options(c.Args, "rRfivd");
        if (o.Error != null) return Usage(c, o.Error);
        if (o.Rest.Count == 0) { if (o.Has('f')) return 0; return Usage(c, "missing operand", 1); }
        bool recursive = o.Has('r') || o.Has('R'); int status = 0;
        foreach (var arg in o.Rest)
        {
            string path = Resolve(arg); var node = Fs.Find(path);
            if (path == "/") { c.Err.Append("rm: it is dangerous to operate recursively on '/'\nrm: use --no-preserve-root to override this failsafe\n"); status = 1; continue; }
            if (node == null) { if (!o.Has('f')) { c.Err.Append($"rm: cannot remove '{arg}': No such file or directory\n"); status = 1; } continue; }
            if (node.IsDir && !recursive && !(o.Has('d') && node.Children.Count == 0)) { c.Err.Append($"rm: cannot remove '{arg}': Is a directory\n"); status = 1; continue; }
            if (Protected(node.Parent ?? node, c, "remove")) { status = 1; continue; }
            if (o.Has('i'))
            {
                var target = node; string kind = node.IsDir ? "directory" : node.Size == 0 ? "regular empty file" : "regular file";
                Ask($"rm: remove {kind} '{arg}'? ", false, answer =>
                {
                    if (answer.Trim().StartsWith('y') || answer.Trim().StartsWith('Y')) { ShellFs.Remove(target); if (o.Has('v')) Print($"removed '{arg}'"); RmConfirmed++; }
                });
                continue;
            }
            ShellFs.Remove(node);
            if (o.Has('v')) c.Line(node.IsDir ? $"removed directory '{arg}'" : $"removed '{arg}'");
        }
        FixCwd(c);
        return status;
    }

    /// <summary>Files removed after answering y to rm -i.</summary>
    public int RmConfirmed { get; private set; }

    private int Chmod(Ctx c)
    {
        var o = Options(c.Args.Where(a => !Regex.IsMatch(a, @"^-[rwxXst]+$")).ToList(), "Rvf");
        var modeArg = c.Args.FirstOrDefault(a => Regex.IsMatch(a, @"^-[rwxXst]+$"));
        var rest = o.Rest.ToList(); if (modeArg != null) rest.Insert(0, modeArg);
        if (rest.Count < 2) return Usage(c, rest.Count == 0 ? "missing operand" : $"missing operand after ‘{rest[0]}’", 1);
        string mode = rest[0]; int status = 0;
        if (!Regex.IsMatch(mode, @"^[0-7]{3,4}$") && !Regex.IsMatch(mode, @"^([ugoa]*[+\-=][rwxXst]*)(,[ugoa]*[+\-=][rwxXst]*)*$")) { c.Err.Append($"chmod: invalid mode: ‘{mode}’\nTry 'chmod --help' for more information.\n"); return 1; }
        foreach (var arg in rest.Skip(1))
        {
            var node = Fs.Find(Resolve(arg));
            if (node == null) { c.Err.Append($"chmod: cannot access '{arg}': No such file or directory\n"); status = 1; continue; }
            foreach (var target in o.Has('R') ? node.Walk() : [node])
            {
                if (target.Owner != "user") { c.Err.Append($"chmod: changing permissions of '{target.Path}': Operation not permitted\n"); status = 1; continue; }
                target.Mode = ApplyMode(target.Mode, mode, target.IsDir);
            }
        }
        return status;
    }

    private static string ApplyMode(string current, string mode, bool dir)
    {
        if (Regex.IsMatch(mode, @"^[0-7]{3,4}$"))
        {
            var digits = mode[^3..]; var sb = new StringBuilder();
            foreach (char d in digits) { int v = d - '0'; sb.Append((v & 4) != 0 ? 'r' : '-').Append((v & 2) != 0 ? 'w' : '-').Append((v & 1) != 0 ? 'x' : '-'); }
            return sb.ToString();
        }
        var bits = current.ToCharArray();
        foreach (var clause in mode.Split(','))
        {
            var m = Regex.Match(clause, @"^([ugoa]*)([+\-=])([rwxXst]*)$"); string who = m.Groups[1].Value.Length == 0 ? "a" : m.Groups[1].Value;
            foreach (char w in who.Contains('a') ? "ugo" : who)
            {
                int offset = w == 'u' ? 0 : w == 'g' ? 3 : 6;
                if (m.Groups[2].Value == "=") { bits[offset] = bits[offset + 1] = bits[offset + 2] = '-'; }
                foreach (char p in m.Groups[3].Value)
                {
                    int index = p switch { 'r' => 0, 'w' => 1, 'x' or 'X' => 2, _ => -1 };
                    if (index < 0 || p == 'X' && !dir && !current.Contains('x')) continue;
                    bits[offset + index] = m.Groups[2].Value == "-" ? '-' : "rwx"[index];
                }
            }
        }
        return new string(bits);
    }

    private int Chown(Ctx c)
    {
        var o = Options(c.Args, "Rvf");
        if (o.Rest.Count < 2) return Usage(c, "missing operand", 1);
        foreach (var arg in o.Rest.Skip(1))
        {
            if (Fs.Find(Resolve(arg)) == null) { c.Err.Append($"{c.Name}: cannot access '{arg}': No such file or directory\n"); continue; }
            c.Err.Append($"{c.Name}: changing ownership of '{arg}': Operation not permitted\n");
        }
        c.Note("일반 사용자는 파일 소유자를 바꿀 수 없습니다 (실제 서버에서는 sudo 가 필요합니다)");
        return 1;
    }

    // ───────── Reading files ─────────

    /// <summary>Reads operands (or stdin) as text. Missing files are reported on c.Err.</summary>
    private List<(string Name, string Text)> Inputs(Ctx c, List<string> operands, out int status, bool allowDirs = false)
    {
        status = 0; var result = new List<(string, string)>();
        if (operands.Count == 0) { result.Add(("-", c.Stdin ?? "")); return result; }
        foreach (var arg in operands)
        {
            if (arg == "-") { result.Add(("-", c.Stdin ?? "")); continue; }
            var node = Fs.Find(Resolve(arg));
            if (node == null) { c.Err.Append($"{c.Name}: {arg}: No such file or directory\n"); status = 1; continue; }
            if (node.IsDir) { if (!allowDirs) { c.Err.Append($"{c.Name}: {arg}: Is a directory\n"); status = 1; } continue; }
            if (node.Binary) { c.Note($"{arg} 은(는) 바이너리 파일이라 내용을 표시하지 않습니다"); continue; }
            result.Add((arg, node.Content));
        }
        return result;
    }

    private int Cat(Ctx c)
    {
        var o = Options(c.Args, "nbAEs");
        if (o.Error != null) return Usage(c, o.Error, 1);
        var inputs = Inputs(c, o.Rest, out int status); int number = 1;
        foreach (var (_, text) in inputs)
        {
            if (!o.Has('n') && !o.Has('b') && !o.Has('E')) { c.Out.Append(text); continue; }
            foreach (var line in SplitLines(text))
            {
                string body = o.Has('E') ? line + "$" : line;
                if (o.Has('b') && line.Length == 0) c.Line(body); else if (o.Has('n') || o.Has('b')) c.Line($"{number++,6}\t{body}"); else c.Line(body);
            }
        }
        return status;
    }

    private int Less(Ctx c)
    {
        var o = Options(c.Args, "NSiR");
        var inputs = Inputs(c, o.Rest, out int status); int n = 1;
        foreach (var (_, text) in inputs) foreach (var line in SplitLines(text)) c.Line(o.Has('N') ? $"{n++,7} {line}" : line);
        if (c.Tty && inputs.Count > 0) c.Note($"연습 터미널은 {c.Name} 화면을 열지 않고 내용을 바로 보여 줍니다 · 실제 {c.Name} 에서는 Space 다음 쪽, b 이전 쪽, /단어 검색, q 종료");
        return status;
    }

    private int Head(Ctx c)
    {
        var o = Options(c.Args, "qv", "nc", numeric: true);
        if (o.Error != null) return Usage(c, o.Error, 1);
        string count = o.Value('n') ?? "10"; bool negative = count.StartsWith('-');
        if (!int.TryParse(count.TrimStart('-', '+'), out int n)) return c.Fail($"invalid number of lines: ‘{count}’");
        var inputs = Inputs(c, o.Rest, out int status); bool headers = inputs.Count > 1 && !o.Has('q') || o.Has('v');
        for (int k = 0; k < inputs.Count; k++)
        {
            var (name, text) = inputs[k];
            if (headers) { if (k > 0) c.Line(""); c.Line($"==> {(name == "-" ? "standard input" : name)} <=="); }
            var lines = SplitLines(text);
            foreach (var line in negative ? lines.Take(Math.Max(0, lines.Count - n)) : lines.Take(n)) c.Line(line);
        }
        return status;
    }

    private int Tail(Ctx c)
    {
        var o = Options(c.Args, "fFqv", "nc", numeric: true);
        if (o.Error != null) return Usage(c, o.Error, 1);
        string count = o.Value('n') ?? "10"; bool fromStart = count.StartsWith('+');
        if (!int.TryParse(count.TrimStart('-', '+'), out int n)) return c.Fail($"invalid number of lines: ‘{count}’");
        var inputs = Inputs(c, o.Rest, out int status); bool headers = inputs.Count > 1 && !o.Has('q') || o.Has('v');
        for (int k = 0; k < inputs.Count; k++)
        {
            var (name, text) = inputs[k];
            if (headers) { if (k > 0) c.Line(""); c.Line($"==> {(name == "-" ? "standard input" : name)} <=="); }
            var lines = SplitLines(text);
            foreach (var line in fromStart ? lines.Skip(Math.Max(0, n - 1)) : lines.Skip(Math.Max(0, lines.Count - n))) c.Line(line);
        }
        if (o.Has('f') || o.Has('F')) c.Note("tail -f 는 실제로는 새로 쌓이는 줄을 계속 보여 줍니다 · 멈출 때는 Ctrl+C (연습 터미널은 지금까지의 내용만 보여 줍니다)");
        return status;
    }

    private int Wc(Ctx c)
    {
        var o = Options(c.Args, "lwcmL");
        if (o.Error != null) return Usage(c, o.Error, 1);
        bool any = o.Has('l') || o.Has('w') || o.Has('c') || o.Has('m') || o.Has('L');
        bool lines = !any || o.Has('l'), words = !any || o.Has('w'), bytes = !any || o.Has('c'), chars = o.Has('m'), longest = o.Has('L');
        var inputs = Inputs(c, o.Rest, out int status);
        var rows = new List<(long[] Values, string Name)>(); var totals = new long[5];
        foreach (var (name, text) in inputs)
        {
            long[] values = [text.Count(ch => ch == '\n'), Regex.Matches(text, @"\S+").Count, Encoding.UTF8.GetByteCount(text), text.Length, SplitLines(text).Select(l => (long)DisplayWidth(l)).DefaultIfEmpty(0).Max()];
            for (int i = 0; i < 4; i++) totals[i] += values[i]; totals[4] = Math.Max(totals[4], values[4]);
            rows.Add((values, name == "-" ? "" : name));
        }
        if (rows.Count > 1) rows.Add((totals, "total"));
        bool[] show = [lines, words, bytes, chars, longest];
        int shown = show.Count(x => x);
        int width = shown == 1 && rows.Count == 1 ? 1 : Math.Max(rows.Count > 1 || rows.Any(r => r.Name.Length > 0) ? 1 : 7, rows.SelectMany(r => r.Values.Where((_, i) => show[i])).Select(v => v.ToString(C).Length).DefaultIfEmpty(1).Max());
        foreach (var (values, name) in rows)
        {
            var parts = new List<string>(); int[] order = [0, 1, 3, 2, 4];
            foreach (int i in order) if (show[i]) parts.Add(values[i].ToString(C).PadLeft(width));
            c.Line(string.Join(" ", parts) + (name.Length > 0 ? " " + name : ""));
        }
        return status;
    }

    // ───────── Shell builtins ─────────

    private int Echo(Ctx c)
    {
        bool newline = true, escapes = false; int start = 0;
        while (start < c.Args.Count && Regex.IsMatch(c.Args[start], "^-[neE]+$")) { if (c.Args[start].Contains('n')) newline = false; if (c.Args[start].Contains('e')) escapes = true; start++; }
        string text = string.Join(" ", c.Args.Skip(start));
        if (escapes) text = Unescape(text);
        c.Out.Append(text); if (newline) c.Out.Append('\n');
        return 0;
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\([ntr\\aeb0]|x[0-9A-Fa-f]{1,2})", m => m.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", "\\" => "\\", "a" => "", "e" => "\u001b", "b" => "\b", "0" => "",
        var hex => ((char)Convert.ToInt32(hex[1..], 16)).ToString()
    });

    private int Printf(Ctx c)
    {
        if (c.Args.Count == 0) return Usage(c, "usage: printf format [arguments]", 2);
        c.Out.Append(Format(c.Args[0], c.Args.Skip(1).ToList(), repeat: true));
        return 0;
    }

    /// <summary>printf-style formatting shared by printf and awk.</summary>
    internal static string Format(string format, IReadOnlyList<string> args, bool repeat)
    {
        var sb = new StringBuilder(); int used = 0;
        do
        {
            int before = used;
            for (int i = 0; i < format.Length; i++)
            {
                char ch = format[i];
                if (ch == '\\' && i + 1 < format.Length) { sb.Append(Unescape(format.Substring(i, 2))); i++; continue; }
                if (ch != '%') { sb.Append(ch); continue; }
                var m = Regex.Match(format[i..], @"^%([-+ 0#]*)(\d*)(?:\.(\d+))?([sdifxXoc%])");
                if (!m.Success) { sb.Append(ch); continue; }
                i += m.Length - 1; char kind = m.Groups[4].Value[0];
                if (kind == '%') { sb.Append('%'); continue; }
                string arg = used < args.Count ? args[used++] : ""; string flags = m.Groups[1].Value; int width = int.TryParse(m.Groups[2].Value, out int w) ? w : 0; int? precision = int.TryParse(m.Groups[3].Value, out int p) ? p : null;
                double.TryParse(arg, NumberStyles.Float, C, out double number);
                string text = kind switch
                {
                    's' => precision is int limit && arg.Length > limit ? arg[..limit] : arg,
                    'c' => arg.Length > 0 ? arg[..1] : "",
                    'd' or 'i' => ((long)Math.Truncate(number)).ToString(C),
                    'x' => ((long)number).ToString("x", C), 'X' => ((long)number).ToString("X", C), 'o' => Convert.ToString((long)number, 8),
                    _ => number.ToString("F" + (precision ?? 6), C)
                };
                if (flags.Contains('+') && kind is 'd' or 'i' or 'f' && number >= 0) text = "+" + text;
                if (text.Length < width) text = flags.Contains('-') ? text.PadRight(width) : flags.Contains('0') && kind != 's' ? text.PadLeft(width, '0') : text.PadLeft(width);
                sb.Append(text);
            }
            if (!repeat || used == before || used >= args.Count) break;
        } while (true);
        return sb.ToString();
    }

    private int Clear(Ctx c) { output.Clear(); ClearCount++; return 0; }

    private int HistoryCommand(Ctx c)
    {
        if (c.Args.Contains("-c")) { history.Clear(); return 0; }
        int skip = c.Args.Count > 0 && int.TryParse(c.Args[0], out int last) ? Math.Max(0, history.Count - last) : 0;
        for (int i = skip; i < history.Count; i++) c.Line($"{i + 1,5}  {history[i]}");
        return 0;
    }

    private int Alias(Ctx c)
    {
        if (c.Args.Count == 0) { foreach (var (name, value) in aliases.OrderBy(a => a.Key, StringComparer.Ordinal)) c.Line($"alias {name}='{value}'"); return 0; }
        int status = 0;
        foreach (var arg in c.Args)
        {
            int eq = arg.IndexOf('=');
            if (eq < 0) { if (aliases.TryGetValue(arg, out var value)) c.Line($"alias {arg}='{value}'"); else { c.Err.Append($"bash: alias: {arg}: not found\n"); status = 1; } continue; }
            aliases[arg[..eq]] = arg[(eq + 1)..];
        }
        return status;
    }

    private int Unalias(Ctx c)
    {
        if (c.Args.Contains("-a")) { aliases.Clear(); return 0; }
        int status = 0;
        foreach (var arg in c.Args) if (!aliases.Remove(arg)) { c.Err.Append($"bash: unalias: {arg}: not found\n"); status = 1; }
        return status;
    }

    private int Export(Ctx c)
    {
        if (c.Args.Count == 0 || c.Args[0] == "-p") { foreach (var (name, value) in variables.OrderBy(v => v.Key, StringComparer.Ordinal)) c.Line($"declare -x {name}=\"{value}\""); return 0; }
        foreach (var arg in c.Args)
        {
            int eq = arg.IndexOf('=');
            if (eq < 0) { variables.TryAdd(arg, ""); continue; }
            if (!Regex.IsMatch(arg[..eq], @"^[A-Za-z_]\w*$")) { c.Err.Append($"bash: export: `{arg}': not a valid identifier\n"); return 1; }
            variables[arg[..eq]] = arg[(eq + 1)..];
        }
        return 0;
    }

    private int Unset(Ctx c) { foreach (var arg in c.Args) variables.Remove(arg); return 0; }

    private int Env(Ctx c)
    {
        if (c.Name == "printenv" && c.Args.Count > 0) { int status = 0; foreach (var arg in c.Args) { if (variables.TryGetValue(arg, out var value)) c.Line(value); else status = 1; } return status; }
        foreach (var (name, value) in variables.Concat([new("PWD", Cwd), new("OLDPWD", previousDir)]).OrderBy(v => v.Key, StringComparer.Ordinal)) c.Line($"{name}={value}");
        return 0;
    }

    private int Date(Ctx c)
    {
        var now = Now; var format = c.Args.FirstOrDefault(a => a.StartsWith('+'));
        if (format == null) { c.Line($"{now.ToString("ddd MMM", C)} {now.Day,2} {now:HH:mm:ss} KST {now.Year}"); return 0; }
        string text = Regex.Replace(format[1..], "%[YmdHMSFTabejyABsDRpn%]", m => m.Value[1] switch
        {
            'Y' => now.ToString("yyyy", C), 'm' => now.ToString("MM", C), 'd' => now.ToString("dd", C), 'H' => now.ToString("HH", C), 'M' => now.ToString("mm", C), 'S' => now.ToString("ss", C),
            'F' => now.ToString("yyyy-MM-dd", C), 'T' => now.ToString("HH:mm:ss", C), 'a' => now.ToString("ddd", C), 'A' => now.ToString("dddd", C), 'b' => now.ToString("MMM", C), 'B' => now.ToString("MMMM", C),
            'e' => now.Day.ToString(C).PadLeft(2), 'j' => now.DayOfYear.ToString("000", C), 'y' => now.ToString("yy", C), 's' => new DateTimeOffset(now, TimeSpan.FromHours(9)).ToUnixTimeSeconds().ToString(C),
            'D' => now.ToString("MM/dd/yy", C), 'R' => now.ToString("HH:mm", C), 'p' => now.Hour < 12 ? "AM" : "PM", 'n' => "\n", _ => "%"
        });
        c.Line(text); return 0;
    }

    private int Uname(Ctx c)
    {
        var o = Options(c.Args, "asnrvmpio");
        if (o.Has('a')) c.Line("Linux eqp-server 5.14.0-practice #1 SMP PREEMPT_DYNAMIC x86_64 x86_64 x86_64 GNU/Linux");
        else c.Line(string.Join(" ", new[] { o.Has('s') || o.Flags.Count == 0 ? "Linux" : null, o.Has('n') ? Host : null, o.Has('r') ? "5.14.0-practice" : null, o.Has('m') ? "x86_64" : null }.Where(x => x != null)));
        return 0;
    }

    private int Uptime(Ctx c) { c.Line($" {Now:HH:mm:ss} up 3 days,  1:12,  1 user,  load average: 1.05, 1.02, 0.98"); return 0; }

    private int Free(Ctx c)
    {
        bool human = c.Args.Any(a => a.Contains('h'));
        c.Line("               total        used        free      shared  buff/cache   available");
        c.Line(human ? "Mem:           7.6Gi       3.1Gi       1.2Gi       120Mi       3.3Gi       4.2Gi" : "Mem:         7965212     3250084     1258736      122880     3456392     4402312");
        c.Line(human ? "Swap:          2.0Gi       128Mi       1.9Gi" : "Swap:        2097148      131072     1966076");
        return 0;
    }

    private int Df(Ctx c)
    {
        bool human = c.Args.Any(a => a.StartsWith('-') && a.Contains('h'));
        if (human)
        {
            c.Line("Filesystem      Size  Used Avail Use% Mounted on");
            c.Line("/dev/sda1        50G   31G   17G  65% /"); c.Line("tmpfs           3.8G     0  3.8G   0% /dev/shm"); c.Line("/dev/sdb1       200G  182G   18G  92% /data");
        }
        else
        {
            c.Line("Filesystem     1K-blocks      Used Available Use% Mounted on");
            c.Line("/dev/sda1       52403200  32505856  17825792  65% /"); c.Line("tmpfs            3982604         0   3982604   0% /dev/shm"); c.Line("/dev/sdb1      209612800 190840832  18771968  92% /data");
        }
        return 0;
    }

    private static long DiskUsage(ShellNode node) => node.IsDir ? 4 + node.Children.Values.Sum(DiskUsage) : node.Size == 0 ? 0 : (node.Size + 4095) / 4096 * 4;

    private int Du(Ctx c)
    {
        var o = Options(c.Args, "shacSk", "d");
        if (o.Error != null) return Usage(c, o.Error);
        int maxDepth = int.TryParse(o.Value('d') ?? o.LongValue("max-depth"), out int d) ? d : o.Has('s') ? 0 : int.MaxValue;
        bool human = o.Has('h') || o.Long.Contains("human-readable");
        string Size(long kb) => human ? (kb == 0 ? "0" : Human(kb * 1024)) : kb.ToString(C);
        var targets = o.Rest.Count == 0 ? ["."] : o.Rest; int status = 0; long total = 0;
        foreach (var target in targets)
        {
            var node = Fs.Find(Resolve(target));
            if (node == null) { c.Err.Append($"du: cannot access '{target}': No such file or directory\n"); status = 1; continue; }
            void Walk(ShellNode current, string shown, int depth)
            {
                if (current.IsDir) foreach (var child in current.Children.Values) if (child.IsDir || o.Has('a')) Walk(child, (shown.EndsWith('/') ? shown : shown + "/") + child.Name, depth + 1);
                if (depth <= maxDepth && (current.IsDir || o.Has('a') || depth == 0)) c.Line($"{Size(DiskUsage(current))}\t{shown}");
            }
            Walk(node, target, 0); total += DiskUsage(node);
        }
        if (o.Has('c')) c.Line($"{Size(total)}\ttotal");
        return status;
    }

    private int Which(Ctx c)
    {
        int status = 0;
        foreach (var arg in c.Args.Where(a => !a.StartsWith('-')))
        {
            if (aliases.TryGetValue(arg, out var alias) && c.Args.Contains("-a")) c.Line($"alias {arg}='{alias}'");
            if (commands.ContainsKey(arg) && arg is not ("cd" or "alias" or "unalias" or "export" or "history" or "source" or "." or "unset" or "type" or "exit" or "logout")) c.Line("/usr/bin/" + arg);
            else { c.Err.Append($"which: no {arg} in ({variables.GetValueOrDefault("PATH")})\n"); status = 1; }
        }
        return status;
    }

    private int TypeCommand(Ctx c)
    {
        int status = 0;
        foreach (var arg in c.Args)
        {
            if (aliases.TryGetValue(arg, out var alias)) c.Line($"{arg} is aliased to `{alias}'");
            else if (arg is "cd" or "alias" or "unalias" or "export" or "history" or "source" or "." or "unset" or "type" or "exit" or "echo" or "pwd" or "printf" or "kill" or "true" or "false") c.Line($"{arg} is a shell builtin");
            else if (commands.ContainsKey(arg)) c.Line($"{arg} is /usr/bin/{arg}");
            else { c.Err.Append($"bash: type: {arg}: not found\n"); status = 1; }
        }
        return status;
    }

    private int Man(Ctx c)
    {
        if (c.Args.Count == 0) { c.Line("What manual page do you want?\nFor example, try 'man man'."); return 1; }
        string name = c.Args[^1];
        if (!Manual.TryGetValue(name, out var page)) { c.Err.Append($"No manual entry for {name}\n"); return 16; }
        c.Line(page);
        if (c.Tty) c.Note("연습용 요약 설명입니다 · 실제 man 은 화면 단위로 보여 주며 q 로 나옵니다");
        return 0;
    }

    private int FileCommand(Ctx c)
    {
        foreach (var arg in c.Args.Where(a => !a.StartsWith('-')))
        {
            var node = Fs.Find(Resolve(arg));
            string kind = node == null ? "cannot open `" + arg + "' (No such file or directory)"
                : node.IsDir ? "directory"
                : node.Gzip || node.Compressed != null ? "gzip compressed data, from Unix"
                : node.Archive != null ? "POSIX tar archive (GNU)"
                : node.Name.StartsWith("core.") ? "ELF 64-bit LSB core file, x86-64, version 1 (SYSV)"
                : node.FixedSize != null ? "ELF 64-bit LSB executable, x86-64, version 1 (SYSV), dynamically linked, stripped"
                : node.Content.Length == 0 ? "empty"
                : node.Content.StartsWith("#!/bin/bash") ? "Bourne-Again shell script, " + (node.Content.Any(ch => ch > 127) ? "Unicode text, UTF-8 text" : "ASCII text") + " executable"
                : node.Content.StartsWith("#!/usr/bin/env python") ? "Python script, " + (node.Content.Any(ch => ch > 127) ? "Unicode text, UTF-8 text" : "ASCII text") + " executable"
                : node.Name.EndsWith(".csv") ? "CSV text"
                : node.Content.Any(ch => ch > 127) ? "Unicode text, UTF-8 text" : "ASCII text";
            c.Line($"{arg}: {kind}");
        }
        return 0;
    }

    private int Basename(Ctx c)
    {
        if (c.Args.Count == 0) return Usage(c, "missing operand", 1);
        string name = ShellFs.BaseName(c.Args[0]); if (c.Args.Count > 1 && name.EndsWith(c.Args[1]) && name != c.Args[1]) name = name[..^c.Args[1].Length];
        c.Line(name.Length == 0 ? "/" : name); return 0;
    }

    private int Dirname(Ctx c)
    {
        if (c.Args.Count == 0) return Usage(c, "missing operand", 1);
        string arg = c.Args[0].TrimEnd('/'); int cut = arg.LastIndexOf('/');
        c.Line(cut < 0 ? "." : cut == 0 ? "/" : arg[..cut]); return 0;
    }

    private int Seq(Ctx c)
    {
        var numbers = c.Args.Where(a => double.TryParse(a, NumberStyles.Float, C, out _)).Select(a => double.Parse(a, C)).ToList();
        if (numbers.Count == 0) return Usage(c, "missing operand", 1);
        double first = numbers.Count > 1 ? numbers[0] : 1, step = numbers.Count > 2 ? numbers[1] : 1, last = numbers[^1];
        if (step == 0) return c.Fail("invalid Zero increment value: '0'");
        int guard = 0;
        for (double v = first; step > 0 ? v <= last + 1e-9 : v >= last - 1e-9; v += step) { c.Line(v.ToString(C)); if (++guard > 10000) break; }
        return 0;
    }

    private int Sudo(Ctx c)
    {
        if (c.Args.Count == 0) { c.Err.Append("usage: sudo command\n"); return 1; }
        c.Note("연습 터미널은 sudo 비밀번호를 묻지 않고 일반 사용자 권한으로 실행합니다");
        return RunNested(c, c.Args);
    }

    // ───────── Processes ─────────

    private int Ps(Ctx c)
    {
        bool full = c.Args.Any(a => a.StartsWith('-') && (a.Contains('e') || a.Contains('f') || a.Contains('A')));
        bool bsd = c.Args.Any(a => !a.StartsWith('-') && a.Contains('a') && a.Contains('u'));
        int pid = 3400 + runs.Count % 500;
        if (bsd)
        {
            c.Line("USER         PID %CPU %MEM    VSZ   RSS TTY      STAT START   TIME COMMAND");
            foreach (var p in processes) c.Line($"{p.User,-8} {p.Pid,7} {p.Cpu,4:0.0} {p.Mem,4:0.0} {120000 + p.Pid * 17,6} {8000 + p.Pid * 3,5} {p.Tty,-8} {(p.Cpu > 50 ? "R" : "S"),-4} {p.Started,5} {p.Time[3..],6} {p.Command}");
            c.Line($"{"user",-8} {pid,7} {0.0,4:0.0} {0.0,4:0.0} {10088,6} {3400,5} {"pts/0",-8} {"R+",-4} {Now:HH:mm} {"0:00",6} ps {string.Join(" ", c.Args)}");
            return 0;
        }
        if (full)
        {
            c.Line("UID          PID    PPID  C STIME TTY          TIME CMD");
            foreach (var p in processes) c.Line($"{p.User,-8} {p.Pid,7} {p.Ppid,7} {Math.Min(p.Cpu, 99),2} {p.Started,-5} {p.Tty,-8} {p.Time} {p.Command}");
            c.Line($"{"user",-8} {pid,7} {3355,7} {0,2} {Now:HH:mm} {"pts/0",-8} 00:00:00 ps {string.Join(" ", c.Args)}");
            return 0;
        }
        c.Line("    PID TTY          TIME CMD"); c.Line("   3355 pts/0    00:00:00 bash"); c.Line($"{pid,7} pts/0    00:00:00 ps");
        return 0;
    }

    private int Kill(Ctx c)
    {
        if (c.Args.Count == 0) { c.Err.Append("kill: usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]\n"); return 2; }
        if (c.Args[0] == "-l") { c.Line(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM\t18) SIGCONT\t19) SIGSTOP"); return 0; }
        string signal = "TERM"; var targets = new List<string>();
        for (int i = 0; i < c.Args.Count; i++)
        {
            string a = c.Args[i];
            if (a == "-s" && i + 1 < c.Args.Count) { signal = c.Args[++i].ToUpperInvariant().Replace("SIG", ""); continue; }
            if (a.StartsWith('-') && a.Length > 1) { signal = a[1..].ToUpperInvariant().Replace("SIG", ""); continue; }
            targets.Add(a);
        }
        if (signal == "9") signal = "KILL"; if (signal == "15") signal = "TERM"; if (signal == "1") signal = "HUP"; if (signal == "2") signal = "INT";
        int status = 0;
        foreach (var target in targets)
        {
            if (!int.TryParse(target, out int pid)) { c.Err.Append($"bash: kill: {target}: arguments must be process or job IDs\n"); status = 1; continue; }
            status |= Signal(c, pid, signal, $"bash: kill: ({pid})");
        }
        return status;
    }

    private int Signal(Ctx c, int pid, string signal, string prefix)
    {
        var process = processes.FirstOrDefault(p => p.Pid == pid);
        if (process == null) { c.Err.Append($"{prefix} - No such process\n"); return 1; }
        if (process.User != User) { c.Err.Append($"{prefix} - Operation not permitted\n"); return 1; }
        if (pid == 3355 || pid == 3300) { c.Note("지금 쓰고 있는 셸이라 연습 터미널에서는 끝내지 않습니다"); return 0; }
        if (signal is "TERM" or "INT" or "HUP" && stubborn.Contains(pid)) { stubborn.Remove(pid); KillIgnored++; return 0; }
        if (signal is "STOP" or "CONT" or "0") return 0;
        processes.Remove(process); foreach (var child in processes.Where(p => p.Ppid == pid).ToList()) processes[processes.IndexOf(child)] = child with { Ppid = 1 };
        return 0;
    }

    /// <summary>How often a process ignored a polite kill (cvd_monitor needs kill -9).</summary>
    public int KillIgnored { get; private set; }
    public bool ProcessAlive(int pid) => processes.Any(p => p.Pid == pid);

    private int Pkill(Ctx c)
    {
        string signal = "TERM"; var names = new List<string>(); bool full = false;
        foreach (var a in c.Args) { if (a == "-f") full = true; else if (a.StartsWith('-')) signal = a[1..].ToUpperInvariant().Replace("SIG", "") switch { "9" => "KILL", "15" => "TERM", var s => s }; else names.Add(a); }
        if (names.Count == 0) return Usage(c, "no matching criteria specified", 2);
        var matches = processes.Where(p => names.Any(n => full ? p.Command.Contains(n) : ShellFs.BaseName(p.Command.Split(' ')[0]) == n || c.Name == "pkill" && ShellFs.BaseName(p.Command.Split(' ')[0]).Contains(n))).ToList();
        if (matches.Count == 0) { if (c.Name == "killall") c.Err.Append($"{names[0]}: no process found\n"); return 1; }
        int status = 0; foreach (var p in matches) status |= Signal(c, p.Pid, signal, $"{c.Name}: killing pid {p.Pid} failed:");
        return status;
    }

    private int Pgrep(Ctx c)
    {
        bool full = c.Args.Contains("-f"), list = c.Args.Contains("-l") || c.Args.Contains("-a"); var names = c.Args.Where(a => !a.StartsWith('-')).ToList();
        if (names.Count == 0) return Usage(c, "no matching criteria specified", 2);
        var matches = processes.Where(p => names.Any(n => full ? p.Command.Contains(n) : ShellFs.BaseName(p.Command.Split(' ')[0]).Contains(n))).ToList();
        foreach (var p in matches) c.Line(list ? $"{p.Pid} {(c.Args.Contains("-a") ? p.Command : ShellFs.BaseName(p.Command.Split(' ')[0]))}" : p.Pid.ToString(C));
        return matches.Count > 0 ? 0 : 1;
    }

    private int Top(Ctx c)
    {
        double load = processes.Any(p => p.Pid == 3101) ? 1.05 : 0.12;
        c.Line($"top - {Now:HH:mm:ss} up 3 days,  1:12,  1 user,  load average: {load:0.00}, 1.02, 0.98");
        c.Line($"Tasks: {processes.Count + 120} total,   {(processes.Any(p => p.Cpu > 50) ? 2 : 1)} running, {processes.Count + 119} sleeping,   0 stopped,   0 zombie");
        c.Line($"%Cpu(s): {processes.Sum(p => p.Cpu) / 4.0,4:0.0} us,  1.2 sy,  0.0 ni, {100 - processes.Sum(p => p.Cpu) / 4.0,4:0.0} id,  0.0 wa,  0.0 hi,  0.1 si,  0.0 st");
        c.Line("MiB Mem :   7778.5 total,   1229.2 free,   3173.9 used,   3375.4 buff/cache"); c.Line("");
        c.Line("    PID USER      PR  NI    VIRT    RES    SHR S  %CPU  %MEM     TIME+ COMMAND");
        foreach (var p in processes.OrderByDescending(p => p.Cpu).ThenBy(p => p.Pid)) c.Line($"{p.Pid,7} {p.User,-8}  20   0 {120000 + p.Pid * 17,7} {8000 + p.Pid * 3,6} {4000 + p.Pid,6} {(p.Cpu > 50 ? 'R' : 'S')} {p.Cpu,5:0.0} {p.Mem,5:0.0} {p.Time[1..] + ".00",9} {ShellFs.BaseName(p.Command.Split(' ')[0])}");
        if (c.Tty) c.Note("top 은 실제로는 화면을 계속 새로 고칩니다 · 정렬 P(CPU) M(메모리), 종료 q");
        return 0;
    }

    // ───────── Editor and scripts ─────────

    private int Vi(Ctx c)
    {
        var o = Options(c.Args.Where(a => !a.StartsWith('+')).ToList(), "RnN");
        string? start = c.Args.FirstOrDefault(a => a.StartsWith('+'));
        if (c.Piped) return c.Fail("연습 터미널에서는 vi 를 파이프(|)와 함께 쓸 수 없습니다");
        if (o.Rest.Count == 0) { c.Note("연습 터미널에서는 파일 이름을 붙여서 여세요 · 예: vi notes.txt"); return 1; }
        string path = Resolve(o.Rest[0]); var node = Fs.Find(path);
        if (node is { IsDir: true }) return c.Fail($"\"{o.Rest[0]}\" 은 디렉터리입니다 · 파일 이름을 지정하세요");
        if (node?.Binary == true) return c.Fail($"\"{o.Rest[0]}\" 은 바이너리 파일이라 연습 편집기로 열 수 없습니다");
        EditStartLine = start == null ? 0 : start == "+" ? int.MaxValue : int.TryParse(start[1..], out int line) ? line : 0;
        OpenEditor(path, node?.Content ?? "");
        return 0;
    }

    private int SourceCommand(Ctx c)
    {
        if (c.Args.Count == 0) { c.Err.Append($"bash: {c.Name}: filename argument required\n"); return 2; }
        var inner = new Ctx(this, c.Args[0], c.Args.Skip(1).ToList(), c.Stdin, c.Tty, c.Depth, c.Piped);
        int status = RunScript(inner, false, true);
        c.Out.Append(inner.Out); c.Err.Append(inner.Err.ToString().Replace("bash: ", $"bash: ")); c.Notes.AddRange(inner.Notes);
        return status;
    }

    private int Bash(Ctx c)
    {
        if (c.Args.Count >= 2 && c.Args[0] == "-c") { c.Out.Append(Capture(c.Args[1], c.Depth)); return lastExit; }
        var operands = c.Args.Where(a => !a.StartsWith('-')).ToList();
        if (operands.Count == 0) { c.Note("연습 터미널 안에서 새 셸을 열지 않습니다 · bash 스크립트.sh 처럼 파일을 붙여 실행하세요"); return 0; }
        var inner = new Ctx(this, operands[0], operands.Skip(1).ToList(), c.Stdin, c.Tty, c.Depth, c.Piped);
        int status = RunScript(inner, false);
        c.Out.Append(inner.Out); c.Err.Append(inner.Err); c.Notes.AddRange(inner.Notes);
        return status;
    }

    // ───────── Manual pages ─────────

    private static readonly Dictionary<string, string> Manual = new()
    {
        ["ls"] = "ls - 폴더 안의 파일 목록\n  ls [옵션] [경로]\n  -l  자세히(권한·크기·날짜)   -a  숨김 파일(.으로 시작)까지   -h  크기를 K·M·G 로\n  -t  수정 시간 순   -r  거꾸로   -R  하위 폴더까지   -d  폴더 자체만",
        ["cd"] = "cd - 현재 폴더 바꾸기\n  cd 경로   cd ..(위로)   cd ~ 또는 cd(홈)   cd -(바로 전 폴더)",
        ["pwd"] = "pwd - 현재 폴더의 전체 경로 출력",
        ["cat"] = "cat - 파일 내용 출력\n  cat 파일...   -n  줄 번호   (여러 파일을 이어서 출력)",
        ["less"] = "less - 긴 파일을 쪽 단위로 보기\n  Space 다음 쪽  b 이전 쪽  /단어 검색  n 다음 결과  G 끝  g 처음  q 종료",
        ["head"] = "head - 파일 앞부분 출력 (기본 10줄)\n  head -n 5 파일   head -5 파일",
        ["tail"] = "tail - 파일 끝부분 출력 (기본 10줄)\n  tail -n 20 파일   tail -f 로그(새 줄을 계속 보기, Ctrl+C 로 멈춤)   tail -n +5(5번째 줄부터)",
        ["grep"] = "grep - 파일 내용에서 글자 찾기\n  grep [옵션] 패턴 [파일...]\n  -i 대소문자 무시  -n 줄 번호  -v 일치하지 않는 줄  -c 개수  -l 파일 이름만\n  -r 하위 폴더까지  -w 단어 단위  -E 확장 정규식(a|b)  -A/-B/-C N 앞뒤 N줄\n  --include=\"*.log\" (-r 과 함께) 해당 파일만",
        ["find"] = "find - 조건에 맞는 파일 찾기\n  find [경로] [조건] [동작]\n  -name \"*.log\"  이름(따옴표로 감싸기)  -iname 대소문자 무시  -type f|d  파일|폴더\n  -mtime -1 하루 안에 수정  -mtime +7 7일 넘게 지난  -size +100M  -maxdepth N\n  -exec 명령 {} \\;  찾은 파일마다 실행   -delete  찾은 파일 삭제(주의)",
        ["cp"] = "cp - 복사\n  cp 원본 대상   cp -r 폴더 대상(폴더 복사)   -i 덮어쓰기 전 확인  -p 날짜 유지  -v 진행 표시",
        ["mv"] = "mv - 이동 또는 이름 바꾸기\n  mv 원본 대상   mv 파일 폴더/",
        ["rm"] = "rm - 삭제 (휴지통 없이 바로 지워집니다)\n  rm 파일   rm -r 폴더   rm -i 하나씩 확인   rm -f 확인·오류 없이",
        ["mkdir"] = "mkdir - 폴더 만들기\n  mkdir 이름   mkdir -p a/b/c (중간 폴더까지)",
        ["chmod"] = "chmod - 권한 바꾸기\n  chmod +x 파일(실행 권한)   chmod 755 파일   chmod u+w,g-w 파일\n  r=4 w=2 x=1  (소유자·그룹·기타 순서, 예: 644 = rw-r--r--)",
        ["tar"] = "tar - 묶기/풀기\n  tar czf 묶음.tar.gz 폴더   (c 만들기 z gzip f 파일 이름)\n  tar xzf 묶음.tar.gz        (x 풀기)   tar tzf 묶음.tar.gz (t 목록)   -C 폴더 (그 폴더에서)",
        ["gzip"] = "gzip - 압축 (파일이 파일.gz 로 바뀝니다)\n  gzip 파일   gunzip 파일.gz   zcat 파일.gz(풀지 않고 보기)   -k 원본 유지",
        ["ftp"] = "ftp - FTP 서버 접속\n  ftp 서버   ftp -i 서버 (mget/mput 할 때 파일마다 묻지 않음)\n  ls  cd  pwd  lcd(내 PC 폴더)  get 파일  mget *.log  put 파일  mput *.txt\n  bin(바이너리 모드)  ascii  hash(진행 표시)  prompt(묻기 켜기/끄기)  bye(종료)",
        ["sftp"] = "sftp - SSH 로 파일 주고받기\n  sftp 사용자@서버\n  ls  cd  lcd  lls  get 파일  get *.log  put 파일  bye",
        ["scp"] = "scp - SSH 로 파일 복사\n  scp 사용자@서버:/경로/파일 .        (받기)\n  scp 파일 사용자@서버:/경로/          (보내기)   -r 폴더",
        ["ps"] = "ps - 실행 중인 프로세스\n  ps -ef   ps aux   (보통 ps -ef | grep 이름 으로 찾습니다)",
        ["kill"] = "kill - 프로세스에 신호 보내기\n  kill PID (정상 종료 요청, TERM)   kill -9 PID (강제 종료, KILL)",
        ["df"] = "df - 디스크 사용량\n  df -h",
        ["du"] = "du - 폴더 크기\n  du -sh 폴더   du -sh *   du -h --max-depth=1",
        ["wc"] = "wc - 줄·단어·바이트 수\n  wc -l 파일 (줄 수)   grep ERROR 파일 | wc -l",
        ["sort"] = "sort - 정렬\n  -n 숫자  -r 거꾸로  -u 중복 제거  -k 2 두 번째 칸 기준  -t , 구분자  -h 사람이 읽는 크기(1K 2M)",
        ["uniq"] = "uniq - 연속된 중복 줄 정리 (보통 sort 뒤에 씁니다)\n  -c 개수 표시  -d 중복만",
        ["cut"] = "cut - 칸 잘라내기\n  cut -d , -f 2 파일   cut -d ' ' -f 1,3",
        ["awk"] = "awk - 칸 단위로 처리\n  awk '{print $1, $3}' 파일   awk -F , '{print $2}' 파일\n  awk '/ERROR/ {print $4}'   awk '{s+=$4} END {print s}'   awk '$4 > 22'",
        ["sed"] = "sed - 줄 단위 편집\n  sed 's/찾을/바꿀/g' 파일   sed -n '5,10p' 파일   sed '/패턴/d' 파일   sed -i (파일 직접 수정)",
        ["xargs"] = "xargs - 입력을 명령의 인자로\n  find . -name \"*.log\" | xargs grep -l ERROR   ... | xargs -I {} cp {} 폴더/",
        ["vi"] = "vi - 편집기\n  i 입력  Esc 명령 모드  :w 저장  :q 종료  :wq 저장 후 종료  :q! 저장 안 하고 종료\n  자세한 연습은 [vi 편집기] 과제에서 할 수 있습니다",
        ["history"] = "history - 입력한 명령 기록\n  history   !번호(다시 실행)  !!(직전 명령)  !grep(grep 으로 시작한 마지막 명령)  Ctrl+R 검색",
        ["alias"] = "alias - 명령 별명\n  alias ll='ls -alF'   alias (목록)   unalias ll",
        ["tree"] = "tree - 폴더 구조를 나무 모양으로\n  tree   tree -L 2 (2단계까지)   tree -d (폴더만)",
        ["diff"] = "diff - 두 파일 비교\n  diff 원본 수정본   (< 원본에만 있는 줄, > 수정본에만 있는 줄)   -u 통합 형식",
        ["echo"] = "echo - 글자 출력\n  echo 글자   echo $HOME   echo \"내용\" > 파일 (덮어쓰기)   >> (뒤에 추가)",
        ["touch"] = "touch - 빈 파일 만들기 또는 수정 시간 갱신",
        ["top"] = "top - CPU·메모리를 많이 쓰는 프로세스 보기 (q 종료)",
        ["ping"] = "ping - 네트워크 연결 확인\n  ping -c 4 서버",
        ["man"] = "man - 명령 설명서\n  man 명령   (명령 --help 도 짧은 도움말을 보여 줍니다)",
        ["file"] = "file - 파일 종류 확인",
        ["tee"] = "tee - 화면에 보여 주면서 파일로도 저장\n  명령 | tee 결과.txt   -a 뒤에 추가",
    };
}
