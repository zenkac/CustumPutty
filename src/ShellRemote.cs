using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

/// <summary>Simulated ftp, sftp, scp, ssh and ping against the practice FTP server. No network is used.</summary>
public sealed partial class ShellEngine
{
    public const string FtpHost = "ftp-server", FtpAddress = "192.168.0.50", FtpUser = "guest", FtpPassword = "guest";

    private sealed class Remote
    {
        public string Kind = "ftp";
        public string Host = FtpHost;
        public bool Connected, LoggedIn;
        public string User = "";
        public string Cwd = "/";
        public bool Prompting = true, Binary = true, Hash;
        public int Transfers;
    }

    private Remote? remote;
    /// <summary>Local paths received from the FTP server (get, mget, scp).</summary>
    public HashSet<string> Downloaded { get; } = new(StringComparer.Ordinal);
    /// <summary>Remote paths sent to the FTP server (put, mput, scp).</summary>
    public HashSet<string> Uploaded { get; } = new(StringComparer.Ordinal);
    public bool RemoteActive => remote != null;
    public bool RemoteLoggedIn => remote?.LoggedIn == true;
    public string? RemoteKind => remote?.Kind;
    public string? RemoteCwd => remote?.Cwd;
    public bool? RemotePrompting => remote?.Prompting;
    public bool? RemoteBinary => remote?.Binary;
    public int RemoteSessions { get; private set; }
    public int MgetPrompts { get; private set; }

    private static readonly string[] FtpCommands = ["ascii", "bin", "binary", "bye", "cd", "cdup", "close", "delete", "dir", "disconnect", "exit", "get", "hash", "help", "lcd", "ls", "mdelete", "mget", "mkdir", "mput", "open", "prompt", "put", "pwd", "quit", "recv", "rename", "rmdir", "send", "size", "status", "user", "?", "!"];
    private static readonly string[] SftpCommands = ["bye", "cd", "chmod", "exit", "get", "help", "lcd", "lls", "lmkdir", "ln", "lpwd", "ls", "mget", "mkdir", "mput", "put", "pwd", "quit", "rename", "rm", "rmdir", "?", "!"];
    private IEnumerable<string> RemoteCommandNames => remote?.Kind == "sftp" ? SftpCommands : FtpCommands;

    private void RegisterRemote()
    {
        commands["ftp"] = Ftp; commands["sftp"] = Sftp; commands["scp"] = Scp; commands["ping"] = Ping;
        commands["ssh"] = c => { c.Note("연습 터미널은 다른 서버에 SSH 로 접속하지 않습니다 · 파일 주고받기는 sftp / scp / ftp 로 연습하세요"); return 255; };
        commands["telnet"] = c => { c.Note("연습 터미널은 telnet 접속을 하지 않습니다 · 실제 설비 접속은 EquipLink [터미널] 에서 하세요"); return 1; };
        commands["wget"] = commands["curl"] = c => { c.Note("연습 서버에는 인터넷이 연결되어 있지 않습니다"); return 1; };
        commands["ifconfig"] = commands["ip"] = c => { c.Line("eth0: flags=4163<UP,BROADCAST,RUNNING,MULTICAST>  mtu 1500\n        inet 192.168.0.21  netmask 255.255.255.0  broadcast 192.168.0.255"); return 0; };
    }

    private static bool KnownHost(string host) => host is FtpHost or FtpAddress;

    private static (string? User, string Host) SplitTarget(string target)
    {
        int at = target.LastIndexOf('@');
        return at < 0 ? (null, target) : (target[..at], target[(at + 1)..]);
    }

    // ───────── ftp ─────────

    private int Ftp(Ctx c)
    {
        if (c.Piped) return c.Fail("연습 터미널에서는 ftp 를 파이프(|)와 함께 쓸 수 없습니다");
        var o = Options(c.Args, "invdgp");
        var session = new Remote { Kind = "ftp", Prompting = !o.Has('i') };
        remote = session; RemoteSessions++;
        if (o.Rest.Count > 0) OpenFtp(session, o.Rest[0], !o.Has('n'));
        return 0;
    }

    private void OpenFtp(Remote session, string host, bool autoLogin)
    {
        if (!KnownHost(host))
        {
            Print($"ftp: {host}: Name or service not known");
            if (Regex.IsMatch(host, @"^\d+\.\d+\.\d+\.\d+$")) { Print($"ftp: connect: Connection timed out"); }
            Note($"연습 FTP 서버 주소는 {FtpHost} (또는 {FtpAddress}) 입니다 · /etc/hosts 에 적혀 있습니다");
            return;
        }
        session.Host = host; session.Connected = true;
        Print($"Connected to {host} ({FtpAddress}).");
        Print("220 (vsFTPd 3.0.3)");
        if (autoLogin) AskFtpUser(session);
    }

    private void AskFtpUser(Remote session)
    {
        Ask($"Name ({session.Host}:{User}): ", false, name =>
        {
            string user = name.Trim().Length == 0 ? User : name.Trim();
            if (remote != session) return;
            Print("331 Please specify the password.");
            Ask("Password: ", true, password =>
            {
                if (remote != session) return;
                if (user != FtpUser || password != FtpPassword)
                {
                    Print("530 Login incorrect.");
                    Print("Login failed.");
                    Note($"연습 FTP 계정은 {FtpUser} / {FtpPassword} 입니다 (~/notes.txt 에도 적혀 있습니다) · user 명령으로 다시 로그인하세요");
                    return;
                }
                session.LoggedIn = true; session.User = user; session.Cwd = "/";
                Print("230 Login successful.");
                Print("Remote system type is UNIX.");
                Print("Using binary mode to transfer files.");
            });
        });
    }

    private static List<string> SplitRemoteArgs(string line)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(line, "\"([^\"]*)\"|'([^']*)'|(\\S+)")) result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return result;
    }

    private void RemoteCommand(string line)
    {
        var session = remote!; string trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.StartsWith('!'))
        {
            string local = trimmed[1..].Trim();
            if (local.Length == 0) { Note("연습 터미널에서는 ! 만으로 셸을 열지 않습니다 · !ls 처럼 명령을 붙이세요"); return; }
            var keep = remote; remote = null;
            try { ExecuteLine(local); } finally { if (remote == null) remote = keep; }
            return;
        }
        var args = SplitRemoteArgs(trimmed); string name = args[0]; args.RemoveAt(0);
        int status = session.Kind == "sftp" ? SftpCommand(session, name, args) : FtpCommand(session, name, args);
        runs.Add(new ShellRun(trimmed, name, args, "", "", status, session.Kind));
    }

    private bool NeedLogin(Remote session)
    {
        if (!session.Connected) { Print("Not connected."); return false; }
        if (!session.LoggedIn) { Print("530 Please login with USER and PASS."); return false; }
        return true;
    }

    private string RemotePath(Remote session, string path) => ShellFs.Combine(session.Cwd, path);

    private static string RemoteRow(ShellNode node, string name) =>
        $"{(node.IsDir ? 'd' : '-')}{node.Mode}    {(node.IsDir ? 2 : 1)} {node.Owner,-8} {node.Owner,-8} {node.Size,12} {node.Modified.ToString("MMM", C)} {node.Modified.Day,2} {node.Modified:HH:mm} {name}";

    private int FtpCommand(Remote session, string name, List<string> args)
    {
        switch (name)
        {
            case "bye": case "quit": case "exit":
                if (session.Connected) Print("221 Goodbye.");
                remote = null; return 0;
            case "close": case "disconnect":
                if (!session.Connected) { Print("Not connected."); return 1; }
                Print("221 Goodbye."); session.Connected = session.LoggedIn = false; return 0;
            case "open":
                if (session.Connected) { Print($"Already connected to {session.Host}, use close first."); return 1; }
                if (args.Count == 0) { Ask("(to) ", false, host => { if (host.Trim().Length > 0) OpenFtp(session, host.Trim(), true); }); return 0; }
                OpenFtp(session, args[0], true); return 0;
            case "user":
                if (!session.Connected) { Print("Not connected."); return 1; }
                session.LoggedIn = false; AskFtpUser(session); return 0;
            case "help": case "?":
                Print("Commands may be abbreviated.  Commands are:\n");
                Print(Columnize(FtpCommands.Where(c => c is not ("!" or "?")).ToList()).TrimEnd('\n'));
                Note("자주 쓰는 명령: ls  cd  pwd  lcd  get  mget  put  mput  bin  hash  prompt  bye");
                return 0;
            case "prompt":
                session.Prompting = !session.Prompting; Print($"Interactive mode {(session.Prompting ? "on" : "off")}."); return 0;
            case "hash":
                session.Hash = !session.Hash; Print(session.Hash ? "Hash mark printing on (1024 bytes/hash mark)." : "Hash mark printing off."); return 0;
            case "bin": case "binary": case "image":
                if (!NeedLogin(session)) return 1;
                session.Binary = true; Print("200 Switching to Binary mode."); return 0;
            case "ascii":
                if (!NeedLogin(session)) return 1;
                session.Binary = false; Print("200 Switching to ASCII mode."); return 0;
            case "status":
                Print(session.Connected ? $"Connected to {session.Host}." : "Not connected.");
                Print($"Mode: stream; Type: {(session.Binary ? "binary" : "ascii")}; Form: non-print; Structure: file");
                Print($"Interactive: {(session.Prompting ? "on" : "off")}; Hashing: {(session.Hash ? "on" : "off")}");
                return 0;
            case "lcd":
            {
                string target = args.Count == 0 ? Home : args[0];
                var node = Fs.Find(Resolve(target));
                if (node is not { IsDir: true }) { Print($"local: {target}: No such file or directory"); return 1; }
                previousDir = Cwd; Cwd = node.Path; Print($"Local directory now {Cwd}"); return 0;
            }
        }
        if (!NeedLogin(session)) return 1;
        switch (name)
        {
            case "pwd": Print($"257 \"{session.Cwd}\" is the current directory"); return 0;
            case "cd": case "cdup":
            {
                string target = name == "cdup" ? ".." : args.Count == 0 ? "/" : args[0];
                var node = RemoteFs.Find(RemotePath(session, target));
                if (node is not { IsDir: true }) { Print("550 Failed to change directory."); return 1; }
                session.Cwd = node.Path; Print("250 Directory successfully changed."); return 0;
            }
            case "ls": case "dir": case "nlist":
            {
                string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
                var matches = RemoteMatches(session, target);
                Print("227 Entering Passive Mode (192,168,0,50,195,149).");
                if (matches.Count == 0) { Print("150 Here comes the directory listing."); Print("226 Transfer done (but failed to open directory)."); return 1; }
                Print("150 Here comes the directory listing.");
                foreach (var node in matches)
                {
                    if (node.IsDir && (matches.Count == 1 || !ShellFs.HasGlob(target))) { foreach (var child in node.Children.Values) Print(name == "nlist" ? child.Name : RemoteRow(child, child.Name)); }
                    else Print(name == "nlist" ? node.Name : RemoteRow(node, node.Name));
                }
                Print("226 Directory send OK.");
                return 0;
            }
            case "get": case "recv":
            {
                if (args.Count == 0) { Note("받을 파일 이름을 붙이세요 · 예: get readme.txt"); return 1; }
                var node = RemoteFs.Find(RemotePath(session, args[0]));
                Download(session, node, args[0], args.Count > 1 ? args[1] : ShellFs.BaseName(args[0]));
                return node is { IsDir: false } ? 0 : 1;
            }
            case "mget":
            {
                if (args.Count == 0) { Note("받을 파일 패턴을 붙이세요 · 예: mget *.log"); return 1; }
                var files = args.SelectMany(a => RemoteMatches(session, a)).Where(n => !n.IsDir).Distinct().ToList();
                if (files.Count == 0) { Print($"No such file or directory: {string.Join(" ", args)}"); return 1; }
                if (!session.Prompting) { foreach (var file in files) Download(session, file, file.Name, file.Name); return 0; }
                QueueMget(session, files, 0);
                return 0;
            }
            case "put": case "send":
            {
                if (args.Count == 0) { Note("보낼 파일 이름을 붙이세요 · 예: put notes.txt"); return 1; }
                var node = Fs.Find(Resolve(args[0]));
                return Upload(session, node, args[0], args.Count > 1 ? args[1] : ShellFs.BaseName(args[0])) ? 0 : 1;
            }
            case "mput":
            {
                var files = args.SelectMany(a => { var g = Fs.Glob(a, Cwd, Home); return g.Count > 0 ? g : [a]; }).Select(p => (Path: p, Node: Fs.Find(Resolve(p)))).Where(x => x.Node is { IsDir: false }).ToList();
                if (files.Count == 0) { Print($"local: {string.Join(" ", args)}: No such file or directory"); return 1; }
                if (!session.Prompting) { foreach (var (path, node) in files) Upload(session, node, path, ShellFs.BaseName(path)); return 0; }
                void Next(int i)
                {
                    if (i >= files.Count) return;
                    var (path, node) = files[i];
                    Ask($"mput {path}? ", false, answer =>
                    {
                        if (Yes(answer, out bool all))
                        {
                            Upload(session, node, path, ShellFs.BaseName(path));
                            if (all) { session.Prompting = false; foreach (var rest in files.Skip(i + 1)) Upload(session, rest.Node, rest.Path, ShellFs.BaseName(rest.Path)); return; }
                        }
                        Next(i + 1);
                    });
                }
                Next(0); return 0;
            }
            case "delete": case "mdelete":
            {
                var targets = args.SelectMany(a => RemoteMatches(session, a)).Where(n => !n.IsDir).ToList();
                if (targets.Count == 0) { Print("550 Delete operation failed."); return 1; }
                foreach (var node in targets) { if (node.Path.StartsWith("/upload")) { ShellFs.Remove(node); Print("250 Delete operation successful."); } else Print("550 Permission denied."); }
                return 0;
            }
            case "mkdir":
            {
                if (args.Count == 0) return 1;
                string path = RemotePath(session, args[0]);
                if (!path.StartsWith("/upload")) { Print("550 Permission denied."); return 1; }
                if (RemoteFs.Find(path) != null) { Print("550 Create directory operation failed."); return 1; }
                RemoteFs.MakeDirs(path, Now); Print($"257 \"{path}\" created"); return 0;
            }
            case "size":
            {
                var node = args.Count > 0 ? RemoteFs.Find(RemotePath(session, args[0])) : null;
                Print(node is { IsDir: false } ? $"213 {node.Size}" : "550 Could not get file size."); return node != null ? 0 : 1;
            }
        }
        Print("?Invalid command.");
        if (name is "lls" or "lpwd") Note("ftp 에서는 내 PC(로컬) 목록을 !ls 로 봅니다 (lls 는 sftp 명령입니다)");
        else if (commands.ContainsKey(name)) Note($"지금은 ftp> 안입니다 · 셸 명령은 !{name} 처럼 앞에 ! 를 붙이거나 bye 로 나가서 실행하세요");
        return 1;
    }

    private static bool Yes(string answer, out bool all)
    {
        string a = answer.Trim().ToLowerInvariant(); all = a is "a" or "all";
        return a.Length == 0 || a.StartsWith('y') || all;
    }

    private void QueueMget(Remote session, List<ShellNode> files, int index)
    {
        if (index >= files.Count) return;
        var file = files[index]; MgetPrompts++;
        Ask($"mget {file.Name}? ", false, answer =>
        {
            if (Yes(answer, out bool all))
            {
                Download(session, file, file.Name, file.Name);
                if (all) { session.Prompting = false; foreach (var rest in files.Skip(index + 1)) Download(session, rest, rest.Name, rest.Name); return; }
            }
            QueueMget(session, files, index + 1);
        });
    }

    private List<ShellNode> RemoteMatches(Remote session, string pattern)
    {
        if (!ShellFs.HasGlob(pattern)) { var node = RemoteFs.Find(RemotePath(session, pattern)); return node == null ? [] : [node]; }
        return RemoteFs.Glob(pattern, session.Cwd, "/").Select(p => RemoteFs.Find(ShellFs.Combine(session.Cwd, p))).Where(n => n != null).Select(n => n!).ToList();
    }

    private void Download(Remote session, ShellNode? node, string shown, string localName)
    {
        if (node == null) { Print($"local: {localName} remote: {shown}"); Print("227 Entering Passive Mode (192,168,0,50,195,151)."); Print("550 Failed to open file."); return; }
        if (node.IsDir) { Print($"local: {localName} remote: {shown}"); Print("550 Failed to open file."); Note("폴더는 get 으로 받을 수 없습니다 · cd 로 들어가서 mget * 로 받으세요"); return; }
        string localPath = Resolve(localName);
        if (Fs.Find(ShellFs.Parent(localPath)) is not { IsDir: true }) { Print($"local: {localName}: No such file or directory"); return; }
        Print($"local: {localName} remote: {shown}");
        Print("227 Entering Passive Mode (192,168,0,50,195,150).");
        Print($"150 Opening {(session.Binary ? "BINARY" : "ASCII")} mode data connection for {node.Name} ({node.Size} bytes).");
        if (session.Hash) Print(new string('#', (int)Math.Max(1, Math.Min(60, node.Size / 1024))));
        Print("226 Transfer complete.");
        Print($"{node.Size} bytes received in 0.00 secs ({Math.Max(0.1, node.Size / 1_000_000.0 * 8.4):0.0} MB/s)");
        var copy = Fs.WriteFile(localPath, node.Content, Now); copy.FixedSize = node.FixedSize; copy.Archive = node.Archive?.ToList(); copy.Gzip = node.Gzip; copy.Compressed = node.Compressed; copy.Owner = "user";
        if (!session.Binary && node.Binary)
        {
            copy.Corrupt = true;
            Note($"{node.Name} 은(는) 바이너리 파일인데 ASCII 모드로 받았습니다 · 파일이 깨질 수 있으니 bin 으로 바꾼 뒤 다시 받으세요");
        }
        Downloaded.Add(localPath); session.Transfers++;
    }

    private bool Upload(Remote session, ShellNode? node, string shown, string remoteName)
    {
        if (node == null || node.IsDir) { Print($"local: {shown}: {(node == null ? "No such file or directory" : "not a plain file.")}"); return false; }
        string target = RemotePath(session, remoteName);
        Print($"local: {shown} remote: {remoteName}");
        Print("227 Entering Passive Mode (192,168,0,50,195,152).");
        if (!target.StartsWith("/upload/")) { Print("553 Could not create file."); Note("연습 FTP 서버는 /upload 폴더에만 올릴 수 있습니다 · cd /upload 후 다시 put 하세요"); return false; }
        Print("150 Ok to send data.");
        if (session.Hash) Print(new string('#', (int)Math.Max(1, Math.Min(60, node.Size / 1024))));
        Print("226 Transfer complete.");
        Print($"{node.Size} bytes sent in 0.00 secs ({Math.Max(0.1, node.Size / 1_000_000.0 * 7.9):0.0} MB/s)");
        var copy = RemoteFs.WriteFile(target, node.Content, Now); copy.FixedSize = node.FixedSize; copy.Archive = node.Archive?.ToList(); copy.Gzip = node.Gzip; copy.Owner = "1001";
        Uploaded.Add(target); session.Transfers++;
        return true;
    }

    // ───────── sftp / scp ─────────

    private int Sftp(Ctx c)
    {
        if (c.Piped) return c.Fail("연습 터미널에서는 sftp 를 파이프(|)와 함께 쓸 수 없습니다");
        var operands = c.Args.Where(a => !a.StartsWith('-')).ToList();
        if (operands.Count == 0) { c.Err.Append("usage: sftp [-46AaCfNpqrv] [-B buffer_size] [-b batchfile] [-c cipher]\n          destination\n"); return 1; }
        var (user, host) = SplitTarget(operands[0]); user ??= User;
        if (!KnownHost(host)) { c.Err.Append($"ssh: Could not resolve hostname {host}: Name or service not known\nConnection closed.\n"); c.Note($"연습 서버 주소는 {FtpHost} 입니다 · 예: sftp {FtpUser}@{FtpHost}"); return 255; }
        var session = new Remote { Kind = "sftp", Host = host, Connected = true, User = user, Prompting = false };
        remote = session; RemoteSessions++;
        Ask($"{user}@{host}'s password: ", true, password =>
        {
            if (remote != session) return;
            if (user != FtpUser || password != FtpPassword)
            {
                Print("Permission denied, please try again.");
                Note($"연습 계정은 {FtpUser} / {FtpPassword} 입니다 · 다시 sftp {FtpUser}@{host} 로 접속하세요");
                remote = null; return;
            }
            session.LoggedIn = true; session.Cwd = "/";
            Print($"Connected to {host}.");
        });
        return 0;
    }

    private int SftpCommand(Remote session, string name, List<string> args)
    {
        switch (name)
        {
            case "bye": case "quit": case "exit": remote = null; return 0;
            case "help": case "?":
                Print("Available commands:\nbye                                Quit sftp\ncd path                            Change remote directory to 'path'\nget remote [local]                 Download file\nlcd path                           Change local directory to 'path'\nlls [ls-options [path]]            Display local directory listing\nlpwd                               Print local working directory\nls [-1afhlnrSt] [path]             Display remote directory listing\nmkdir path                         Create remote directory\nput local [remote]                 Upload file\npwd                                Display remote working directory\nrm path                            Delete remote file");
                return 0;
            case "pwd": Print($"Remote working directory: {session.Cwd}"); return 0;
            case "lpwd": Print($"Local working directory: {Cwd}"); return 0;
            case "cd":
            {
                var node = RemoteFs.Find(RemotePath(session, args.FirstOrDefault() ?? "/"));
                if (node == null) { Print($"Couldn't stat remote file: No such file or directory"); return 1; }
                if (!node.IsDir) { Print($"Can't change directory: \"{node.Path}\" is not a directory"); return 1; }
                session.Cwd = node.Path; return 0;
            }
            case "lcd":
            {
                var node = Fs.Find(Resolve(args.FirstOrDefault() ?? Home));
                if (node is not { IsDir: true }) { Print($"Couldn't change local directory to \"{args.FirstOrDefault()}\": No such file or directory"); return 1; }
                previousDir = Cwd; Cwd = node.Path; return 0;
            }
            case "lls": { var keep = remote; remote = null; try { ExecuteLine("ls " + string.Join(" ", args)); } finally { remote ??= keep; } return 0; }
            case "lmkdir": { var keep = remote; remote = null; try { ExecuteLine("mkdir " + string.Join(" ", args)); } finally { remote ??= keep; } return 0; }
            case "ls":
            {
                bool longFormat = args.Any(a => a.StartsWith('-') && a.Contains('l'));
                string target = args.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
                var matches = RemoteMatches(session, target);
                if (matches.Count == 0) { Print($"Can't ls: \"{RemotePath(session, target)}\" not found"); return 1; }
                var items = matches.Count == 1 && matches[0].IsDir ? matches[0].Children.Values.ToList() : matches;
                if (longFormat) foreach (var node in items) Print(RemoteRow(node, node.Name));
                else Print(Columnize(items.Select(n => n.Name).ToList()).TrimEnd('\n'));
                return 0;
            }
            case "get": case "mget":
            {
                if (args.Count == 0) { Print("You must specify at least one path after a get command."); return 1; }
                var files = RemoteMatches(session, args[0]);
                if (files.Count == 0) { Print($"File \"{RemotePath(session, args[0])}\" not found."); return 1; }
                foreach (var file in files)
                {
                    if (file.IsDir) { Print($"Cannot download non-regular file: {file.Path}"); continue; }
                    string local = files.Count == 1 && args.Count > 1 ? args[1] : file.Name; string localPath = Resolve(local);
                    if (Fs.Find(localPath) is { IsDir: true } dir) localPath = ShellFs.Combine(dir.Path, file.Name);
                    Print($"Fetching {file.Path} to {local}");
                    Print($"{file.Name,-40}100% {Human(file.Size),6}   {Math.Max(0.1, file.Size / 1_000_000.0 * 9):0.0}MB/s   00:00");
                    var copy = Fs.WriteFile(localPath, file.Content, Now); copy.FixedSize = file.FixedSize; copy.Archive = file.Archive?.ToList(); copy.Gzip = file.Gzip; copy.Compressed = file.Compressed;
                    Downloaded.Add(localPath); session.Transfers++;
                }
                return 0;
            }
            case "put": case "mput":
            {
                if (args.Count == 0) { Print("You must specify at least one path after a put command."); return 1; }
                var paths = Fs.Glob(args[0], Cwd, Home); if (paths.Count == 0) paths = [args[0]];
                foreach (var path in paths)
                {
                    var node = Fs.Find(Resolve(path));
                    if (node == null) { Print($"stat {path}: No such file or directory"); return 1; }
                    if (node.IsDir) { Print($"Skipping non-regular file {path}"); continue; }
                    string remoteName = paths.Count == 1 && args.Count > 1 ? args[1] : ShellFs.BaseName(path); string target = RemotePath(session, remoteName);
                    if (RemoteFs.Find(target) is { IsDir: true } dir) target = ShellFs.Combine(dir.Path, node.Name);
                    Print($"Uploading {path} to {target}");
                    if (!target.StartsWith("/upload/")) { Print($"remote open(\"{target}\"): Permission denied"); Note("연습 서버는 /upload 폴더에만 올릴 수 있습니다"); return 1; }
                    Print($"{node.Name,-40}100% {Human(node.Size),6}   {Math.Max(0.1, node.Size / 1_000_000.0 * 8):0.0}MB/s   00:00");
                    var copy = RemoteFs.WriteFile(target, node.Content, Now); copy.FixedSize = node.FixedSize; copy.Owner = "1001";
                    Uploaded.Add(target); session.Transfers++;
                }
                return 0;
            }
            case "rm":
            {
                var node = args.Count > 0 ? RemoteFs.Find(RemotePath(session, args[0])) : null;
                if (node == null) { Print("Couldn't delete file: No such file or directory"); return 1; }
                if (!node.Path.StartsWith("/upload/")) { Print("Couldn't delete file: Permission denied"); return 1; }
                Print($"Removing {node.Path}"); ShellFs.Remove(node); return 0;
            }
            case "mkdir":
            {
                if (args.Count == 0) return 1; string path = RemotePath(session, args[0]);
                if (!path.StartsWith("/upload/")) { Print("Couldn't create directory: Permission denied"); return 1; }
                RemoteFs.MakeDirs(path, Now); return 0;
            }
        }
        Print($"Invalid command.");
        if (commands.ContainsKey(name)) Note($"지금은 sftp> 안입니다 · 내 PC 쪽 명령은 lls, lcd, lpwd 또는 !{name} 처럼 씁니다");
        return 1;
    }

    private int Scp(Ctx c)
    {
        if (c.Piped) return c.Fail("연습 터미널에서는 scp 를 파이프(|)와 함께 쓸 수 없습니다");
        var operands = c.Args.Where(a => !a.StartsWith('-')).ToList(); bool recursive = c.Args.Any(a => a.StartsWith('-') && a.Contains('r'));
        if (operands.Count < 2) { c.Err.Append("usage: scp [-346ABCOpqRrsTv] [-c cipher] [-D sftp_server_path] [-F ssh_config]\n           [-i identity_file] [-J destination] [-l limit] [-o ssh_option]\n           [-P port] [-S program] [-X sftp_option] source ... target\n"); return 1; }
        string target = operands[^1]; var sources = operands.Take(operands.Count - 1).ToList();
        bool download = sources.Any(s => s.Contains(':')), upload = target.Contains(':');
        if (download == upload) return c.Fail(download ? "연습 터미널은 원격끼리 복사하지 않습니다" : "원격 주소가 없습니다 · 예: scp guest@ftp-server:/readme.txt .");
        string remoteSpec = download ? sources[0] : target; string userHost = remoteSpec[..remoteSpec.IndexOf(':')];
        var (user, host) = SplitTarget(userHost); user ??= User;
        if (!KnownHost(host)) { c.Err.Append($"ssh: Could not resolve hostname {host}: Name or service not known\nscp: Connection closed\n"); return 255; }
        Ask($"{user}@{host}'s password: ", true, password =>
        {
            if (user != FtpUser || password != FtpPassword) { Print("Permission denied, please try again."); Note($"연습 계정은 {FtpUser} / {FtpPassword} 입니다"); return; }
            if (download)
            {
                foreach (var source in sources)
                {
                    string path = source[(source.IndexOf(':') + 1)..]; if (path.Length == 0) path = "/";
                    var matches = ShellFs.HasGlob(path) ? RemoteFs.Glob(path, "/", "/").Select(p => RemoteFs.Find(ShellFs.Combine("/", p))!).ToList() : RemoteFs.Find(ShellFs.Combine("/", path)) is { } single ? [single] : [];
                    if (matches.Count == 0) { Print($"scp: {path}: No such file or directory"); continue; }
                    foreach (var node in matches)
                    {
                        if (node.IsDir && !recursive) { Print($"scp: {node.Path}: not a regular file"); continue; }
                        var localTarget = Fs.Find(Resolve(target));
                        string destination = localTarget is { IsDir: true } ? ShellFs.Combine(localTarget.Path, node.Name) : Resolve(target);
                        foreach (var item in node.Walk())
                        {
                            string local = item == node ? destination : destination + item.Path[node.Path.Length..];
                            if (item.IsDir) { Fs.MakeDirs(local, Now); continue; }
                            Print($"{item.Name,-40}100% {Human(item.Size),6}   {Math.Max(0.1, item.Size / 1_000_000.0 * 9):0.0}MB/s   00:00");
                            var copy = Fs.WriteFile(local, item.Content, Now); copy.FixedSize = item.FixedSize; copy.Archive = item.Archive?.ToList(); copy.Gzip = item.Gzip; copy.Compressed = item.Compressed;
                            Downloaded.Add(local);
                        }
                    }
                }
                return;
            }
            string remotePath = target[(target.IndexOf(':') + 1)..]; if (remotePath.Length == 0) remotePath = "/";
            foreach (var source in sources)
            {
                var node = Fs.Find(Resolve(source));
                if (node == null) { Print($"scp: {source}: No such file or directory"); continue; }
                if (node.IsDir && !recursive) { Print($"scp: {source}: not a regular file"); continue; }
                var remoteTarget = RemoteFs.Find(ShellFs.Combine("/", remotePath));
                string destination = remoteTarget is { IsDir: true } ? ShellFs.Combine(remoteTarget.Path, node.Name) : ShellFs.Combine("/", remotePath);
                if (!destination.StartsWith("/upload/")) { Print($"scp: {destination}: Permission denied"); Note("연습 서버는 /upload 폴더에만 올릴 수 있습니다 · 예: scp notes.txt guest@ftp-server:/upload/"); continue; }
                foreach (var item in node.Walk())
                {
                    string dest = item == node ? destination : destination + item.Path[node.Path.Length..];
                    if (item.IsDir) { RemoteFs.MakeDirs(dest, Now); continue; }
                    Print($"{item.Name,-40}100% {Human(item.Size),6}   {Math.Max(0.1, item.Size / 1_000_000.0 * 8):0.0}MB/s   00:00");
                    RemoteFs.WriteFile(dest, item.Content, Now).Owner = "1001"; Uploaded.Add(dest);
                }
            }
        });
        return 0;
    }

    private int Ping(Ctx c)
    {
        var o = Options(c.Args, "nq", "cWi");
        if (o.Rest.Count == 0) { c.Err.Append("ping: usage error: Destination address required\n"); return 2; }
        string host = o.Rest[0]; int count = int.TryParse(o.Value('c'), out int n) ? Math.Clamp(n, 1, 20) : 4;
        string? address = host switch { FtpHost or FtpAddress => FtpAddress, "eqp-server" or "192.168.0.21" or "localhost" or "127.0.0.1" => host is "localhost" or "127.0.0.1" ? "127.0.0.1" : "192.168.0.21", _ => null };
        if (address == null) { c.Err.Append($"ping: {host}: Name or service not known\n"); return 2; }
        c.Line($"PING {host} ({address}) 56(84) bytes of data.");
        var random = new Random(host.Length * 31 + count);
        var times = Enumerable.Range(0, count).Select(_ => 0.2 + random.NextDouble() * 0.5).ToList();
        for (int i = 0; i < count; i++) c.Line($"64 bytes from {host} ({address}): icmp_seq={i + 1} ttl=64 time={times[i]:0.000} ms");
        c.Line(""); c.Line($"--- {host} ping statistics ---");
        c.Line($"{count} packets transmitted, {count} received, 0% packet loss, time {count * 1000 - 999}ms");
        c.Line($"rtt min/avg/max/mdev = {times.Min():0.000}/{times.Average():0.000}/{times.Max():0.000}/0.084 ms");
        if (o.Value('c') == null) c.Note("실제 ping 은 Ctrl+C 를 누를 때까지 계속됩니다 · 횟수는 ping -c 4 서버 처럼 정합니다");
        return 0;
    }
}
