using System.IO;
using System.Text;

namespace EquipLink;

/// <summary>Checks the practice shell, its simulated FTP server and the terminal lessons. Everything runs in memory.</summary>
public static class ShellTests
{
    public static Task RunAsync()
    {
        if (!App.IsTesting) throw new InvalidOperationException("Shell tests run only in an explicit test run.");
        Directory.CreateDirectory(AppPaths.Root);
        var report = new StringBuilder();
        var failures = new List<string>(); void Check(string label, bool ok) { if (!ok) failures.Add(label); report.AppendLine((ok ? "PASS: " : "FAIL: ") + label); }
        ShellEngine Sh(params string[] lines) { var e = new ShellEngine(); foreach (var line in lines) e.Run(line); return e; }
        string Last(ShellEngine e) => e.Runs.Count == 0 ? "" : e.Runs[^1].Output;
        string Screen(ShellEngine e) => e.Screen;
        try
        {
            // Basics and navigation
            var e = Sh("pwd"); Check("pwd starts in the home folder", Last(e) == "/home/user\n" && e.Prompt.Contains("~"));
            e = Sh("cd logs", "pwd"); Check("cd moves and the prompt shows ~/logs", Last(e) == "/home/user/logs\n" && ShellEngine.StripAnsi(e.Prompt).Contains("~/logs$"));
            e = Sh("cd logs/old", "cd ..", "cd -", "pwd"); Check("cd .. and cd - work", Last(e) == "/home/user/logs/old\n");
            e = Sh("cd /nowhere"); Check("cd to a missing folder reports bash's error", Screen(e).Contains("bash: cd: /nowhere: No such file or directory") && e.LastExit == 1);
            e = Sh("cd notes.txt"); Check("cd to a file says Not a directory", Screen(e).Contains("Not a directory"));
            e = Sh("ls"); Check("ls hides dotfiles", Last(e).Contains("notes.txt") && !Last(e).Contains(".bashrc"));
            e = Sh("ls -a"); Check("ls -a shows dotfiles, . and ..", Last(e).Contains(".bashrc") && Last(e).Contains(".."));
            e = Sh("ls -l"); Check("ls -l shows total, permissions and owners", Last(e).StartsWith("total ") && Last(e).Contains("drwxr-xr-x") && Last(e).Contains(" user user ") && Last(e).Contains("notes.txt"));
            e = Sh("ls -lh tmp"); Check("ls -lh prints human sizes", Last(e).Contains("512M") && Last(e).Contains("core.3101"));
            e = Sh("ls -lt logs"); Check("ls -lt sorts newest first", Last(e).Split('\n')[1].Contains("cvd_20261123.log"));
            e = Sh("ls nothing"); Check("ls on a missing path reports it", Screen(e).Contains("ls: cannot access 'nothing': No such file or directory") && e.LastExit == 2);
            e = Sh("ll"); Check("The ll alias from .bashrc runs ls -alF", e.Runs[^1].Name == "ls" && Last(e).Contains("logs/"));
            e = Sh("ls -z"); Check("An invalid option reports it", Screen(e).Contains("invalid option -- 'z'") && e.LastExit == 2);
            e = Sh("tree -L 1 recipes"); Check("tree draws the folder structure", Last(e).Contains("├── ") && Last(e).Contains("directories"));

            // Quoting, globbing, variables, substitution
            e = Sh("echo logs/etch_2026112*.log"); Check("Unquoted globs expand to matching files", Last(e).Split(' ').Length == 4);
            e = Sh("echo \"logs/*.log\" 'a  b'"); Check("Quoted globs and spaces stay literal", Last(e) == "logs/*.log a  b\n");
            e = Sh("echo *.nothing"); Check("A glob without matches stays literal", Last(e) == "*.nothing\n");
            e = Sh("X=5", "echo $X ${X} '$X' \"$X\""); Check("Variables expand except in single quotes", Last(e) == "5 5 $X 5\n");
            e = Sh("echo $(date +%Y%m%d) `whoami`"); Check("Command substitution with $( ) and backticks", Last(e) == "20261123 user\n");
            e = Sh("echo ~ ~/logs"); Check("~ expands to the home folder", Last(e) == "/home/user /home/user/logs\n");
            e = Sh("echo \"unterminated"); Check("A missing quote reports a syntax error", Screen(e).Contains("unexpected EOF"));
            e = Sh("false && echo no; true || echo no; false || echo yes"); Check("&& and || chain on exit status", Screen(e).Contains("yes") && !Screen(e).Contains("\nno"));
            e = Sh("ls nothing; echo $?"); Check("$? holds the last exit status", Last(e) == "2\n");

            // Pipes and redirection
            string today = e.ReadFile("~/logs/etch_20261123.log")!;
            int errors = today.Split('\n').Count(l => l.Contains("ERROR"));
            e = Sh("cat logs/etch_20261123.log | grep ERROR | wc -l"); Check("Pipes connect commands", Last(e).Trim() == errors.ToString() && errors >= 2);
            e = Sh("echo hi > a.txt", "echo there >> a.txt", "cat a.txt"); Check("> writes and >> appends", Last(e) == "hi\nthere\n");
            e = Sh("ls nothing 2>/dev/null"); Check("2>/dev/null hides error messages", !Screen(e).Contains("cannot access"));
            e = Sh("ls nothing 2> err.txt", "cat err.txt"); Check("2> writes errors to a file", Last(e).Contains("cannot access"));
            e = Sh("wc -l < notes.txt"); Check("< feeds a file to stdin", Last(e).Trim() == "6");
            e = Sh("grep ERROR logs/etch_20261123.log | tee saved.txt | wc -l", "cat saved.txt"); Check("tee writes the stream and passes it on", Last(e).Split('\n').Count(l => l.Contains("ERROR")) == errors);
            e = Sh("echo x > logs"); Check("Redirecting into a folder fails", Screen(e).Contains("Is a directory"));
            e = Sh("| wc"); Check("A leading pipe is a syntax error", Screen(e).Contains("syntax error near unexpected token `|'"));

            // Files
            e = Sh("mkdir -p a/b/c", "touch a/b/c/x", "ls a/b/c"); Check("mkdir -p and touch create paths", Last(e).Trim() == "x");
            e = Sh("mkdir logs"); Check("mkdir on an existing folder reports File exists", Screen(e).Contains("cannot create directory ‘logs’: File exists"));
            e = Sh("cp recipes/ETCH_A01.rcp x.rcp", "cat x.rcp"); Check("cp copies a file", Last(e).Contains("temp = 65"));
            e = Sh("cp recipes y"); Check("cp of a folder without -r explains", Screen(e).Contains("-r not specified; omitting directory 'recipes'"));
            e = Sh("cp -r recipes y", "ls y/backup"); Check("cp -r copies folders", Last(e).Contains("ETCH_A01.rcp.bak"));
            e = Sh("mv notes.txt memo.txt", "ls"); Check("mv renames", Last(e).Contains("memo.txt") && !Last(e).Contains("notes.txt"));
            e = Sh("mv recipes/ETCH_A02.rcp logs/", "ls logs"); Check("mv into a folder keeps the name", Last(e).Contains("ETCH_A02.rcp"));
            e = Sh("rm logs"); Check("rm on a folder needs -r", Screen(e).Contains("rm: cannot remove 'logs': Is a directory") && e.Exists("logs"));
            e = Sh("rm -r logs/old"); Check("rm -r removes a folder", !e.Exists("logs/old"));
            e = Sh("rm -i tmp/a.tmp", "n"); Check("rm -i keeps the file on n", e.Exists("tmp/a.tmp") && Screen(e).Contains("rm: remove regular file 'tmp/a.tmp'?"));
            e = Sh("rm -i tmp/a.tmp", "y"); Check("rm -i removes on y", !e.Exists("tmp/a.tmp") && e.RmConfirmed == 1);
            e = Sh("rm /etc/hosts"); Check("Root's files cannot be removed", e.Exists("/etc/hosts") && Screen(e).Contains("Permission denied"));
            e = Sh("rm -rf /"); Check("rm -rf / is refused", Screen(e).Contains("dangerous") && e.Exists("/home/user"));
            e = Sh("cd tmp", "rm -r ../tmp", "pwd"); Check("Removing the current folder moves to its parent", Last(e) == "/home/user\n");
            e = Sh("rmdir logs"); Check("rmdir refuses a non-empty folder", Screen(e).Contains("Directory not empty"));

            // Reading
            e = Sh("head -n 3 logs/etch_20261122.log"); Check("head -n 3 prints three lines", Last(e).Split('\n').Count(l => l.Length > 0) == 3);
            e = Sh("head -2 notes.txt"); Check("head -2 shorthand works", Last(e).Split('\n').Count(l => l.Length > 0) == 2);
            e = Sh("tail -n 5 logs/etch_20261122.log"); Check("tail -n 5 prints the last lines", Last(e).Split('\n').Count(l => l.Length > 0) == 5 && today.Length > 0);
            e = Sh("tail -n +5 notes.txt"); Check("tail -n +5 starts at line 5", Last(e).Split('\n').Count(l => l.Length > 0) == 2);
            e = Sh("tail -f logs/etch_20261123.log"); Check("tail -f explains that it would keep following", Screen(e).Contains("Ctrl+C"));
            e = Sh("cat -n notes.txt"); Check("cat -n numbers lines", Last(e).StartsWith("     1\t"));
            e = Sh("cat tmp/core.3101"); Check("cat on a binary file shows a note", Screen(e).Contains("바이너리"));
            e = Sh("wc -l notes.txt logs/etch_20261121.log"); Check("wc prints a total line for several files", Last(e).Contains("total") && Last(e).Contains("notes.txt"));

            // find
            e = Sh("find . -name \"*.rcp\""); Check("find -name with a quoted pattern", Last(e).Contains("./recipes/ETCH_A01.rcp") && !Last(e).Contains(".bak"));
            e = Sh("cd logs", "find . -name *.log"); Check("An unquoted pattern that the shell expands is explained", Screen(e).Contains("paths must precede expression") && Screen(e).Contains("possible unquoted pattern"));
            e = Sh("find . -iname \"etch_a01*\""); Check("find -iname ignores case", Last(e).Contains("ETCH_A01.rcp"));
            e = Sh("find . -type d -name \"*backup*\""); Check("find -type d", Last(e).Trim() == "./recipes/backup");
            e = Sh("find logs -mtime +30"); Check("find -mtime +30 finds old logs", Last(e).Contains("etch_20261001.log") && !Last(e).Contains("etch_20261123"));
            e = Sh("find . -size +100M"); Check("find -size +100M finds the core dump", Last(e).Trim() == "./tmp/core.3101");
            e = Sh("find . -maxdepth 1 -type d"); Check("find -maxdepth limits depth", Last(e).Contains("./logs") && !Last(e).Contains("./logs/old"));
            e = Sh("find tmp -name \"*.tmp\" -mtime +30 -exec rm {} \\;"); Check("find -exec rm {} \\; runs per file", !e.Exists("tmp/cache_old.tmp") && e.Exists("tmp/b.tmp"));
            e = Sh("find tmp -name \"*.tmp\" -exec rm {} ;"); Check("A missing \\; reports missing argument to -exec", Screen(e).Contains("missing argument to `-exec'"));
            e = Sh("find tmp -name \"*.tmp\" -delete", "ls tmp"); Check("find -delete removes matches", !Last(e).Contains(".tmp") && Last(e).Contains("core.3101"));
            e = Sh("find logs -name \"*.log\" -exec wc -l {} +"); Check("find -exec … {} + runs once", Last(e).Contains("total"));
            e = Sh("find . -nmae x"); Check("An unknown predicate is reported", Screen(e).Contains("unknown predicate `-nmae'"));
            e = Sh("find . -name \"*.sh\" -o -name \"*.py\""); Check("find -o combines tests", Last(e).Contains("backup.sh") && Last(e).Contains("cleanup.py"));
            e = Sh("find . -not -name \"*.log\" -type f -path \"*logs*\""); Check("find -not and -path", Last(e).Trim().Length == 0);

            // grep
            e = Sh("grep ERROR logs/etch_20261123.log"); Check("grep prints matching lines", Last(e).Split('\n').Count(l => l.Length > 0) == errors);
            e = Sh("grep -c ERROR logs/etch_20261123.log"); Check("grep -c counts", Last(e).Trim() == errors.ToString());
            e = Sh("grep -i error logs/etch_20261121.log"); Check("grep -i finds lowercase too", Last(e).Contains("recovered from error"));
            e = Sh("grep -n ALM-1203 logs/etch_20261121.log"); Check("grep -n adds line numbers", Last(e).StartsWith("13:"));
            e = Sh("grep -v INFO logs/etch_20261123.log"); Check("grep -v inverts", !Last(e).Contains("[INFO]"));
            e = Sh("grep -rl ALM-1203 ."); Check("grep -rl lists files recursively", Last(e).Contains("./logs/etch_20261121.log") && !Last(e).Contains(":"));
            e = Sh("grep -r --include=\"*.rcp\" temp recipes"); Check("grep --include filters files", Last(e).Contains("recipes/ETCH_A01.rcp:temp = 65") && !Last(e).Contains(".bak"));
            e = Sh("grep -E \"ALM-(1203|2101)\" logs/etch_20261123.log"); Check("grep -E supports alternation", Last(e).Split('\n').Count(l => l.Length > 0) >= 2);
            e = Sh("grep -o \"ALM-[0-9]*\" logs/etch_20261123.log"); Check("grep -o prints only the match", Last(e).Split('\n').Where(l => l.Length > 0).All(l => l.StartsWith("ALM-")));
            e = Sh("grep -A 1 ALM-1203 logs/etch_20261121.log"); Check("grep -A adds trailing context", Last(e).Contains("pump current high"));
            e = Sh("grep -w temp recipes/ETCH_A01.rcp"); Check("grep -w matches whole words", Last(e).Trim() == "temp = 65");
            e = Sh("grep nothing-here notes.txt"); Check("grep exits 1 when nothing matches", e.LastExit == 1);
            e = Sh("grep ERROR logs"); Check("grep on a folder without -r says Is a directory", Screen(e).Contains("grep: logs: Is a directory"));
            e = Sh("grep -c ERROR logs/*.log"); Check("grep on several files prefixes names", Last(e).Contains("logs/etch_20261121.log:"));
            e = Sh("grpe x"); Check("A typo suggests the right command", Screen(e).Contains("command not found") && Screen(e).Contains("'grep'"));

            // Text tools
            e = Sh("cut -d , -f 2 data/wafer_count.csv | sort | uniq -c"); Check("cut | sort | uniq -c counts values", Last(e).Contains("      5 EQ01"));
            e = Sh("sort -t , -k 4 -n data/wafer_count.csv | tail -1"); Check("sort -t -k -n sorts by a numeric column", Last(e).Split(',').Length == 4);
            e = Sh("printf \"%s=%03d\\n\" a 7 b 12"); Check("printf formats and repeats", Last(e) == "a=007\nb=012\n");
            e = Sh("echo hello | tr a-z A-Z"); Check("tr translates ranges", Last(e) == "HELLO\n");
            e = Sh("sed 's/temp = 65/temp = 70/' recipes/ETCH_A01.rcp"); Check("sed s/// prints the change only", Last(e).Contains("temp = 70") && e.ReadFile("recipes/ETCH_A01.rcp")!.Contains("temp = 65"));
            e = Sh("sed -n '2,5p' recipes/ETCH_A01.rcp"); Check("sed -n with a range and p", Last(e).Split('\n').Count(l => l.Length > 0) == 4);
            e = Sh("sed '/^#/d' recipes/ETCH_A01.rcp"); Check("sed /re/d deletes lines", !Last(e).Contains("#"));
            e = Sh("sed -i.bak 's/65/70/' recipes/ETCH_A01.rcp"); Check("sed -i.bak edits in place and keeps a backup", e.ReadFile("recipes/ETCH_A01.rcp")!.Contains("temp = 70") && e.ReadFile("recipes/ETCH_A01.rcp.bak")!.Contains("temp = 65"));
            e = Sh("echo aaa | sed 's/a/b/2'"); Check("sed replaces the Nth match", Last(e) == "aba\n");
            e = Sh("diff recipes/backup/ETCH_A01.rcp.bak recipes/ETCH_A01.rcp"); Check("diff prints changed lines", Last(e).Contains("< temp = 60") && Last(e).Contains("> temp = 65") && e.LastExit == 1);
            e = Sh("diff -u recipes/backup/ETCH_A01.rcp.bak recipes/ETCH_A01.rcp"); Check("diff -u prints a unified diff", Last(e).Contains("@@ -") && Last(e).Contains("-temp = 60") && Last(e).Contains("+temp = 65"));
            e = Sh("diff notes.txt notes.txt"); Check("diff of identical files prints nothing", Last(e) == "" && e.LastExit == 0);
            e = Sh("find logs -name \"*.log\" | xargs grep -l ALM-1203"); Check("find | xargs grep -l", Last(e).Contains("logs/etch_20261121.log"));
            e = Sh("ls logs/*.log | xargs -I {} cp {} download/", "ls download"); Check("xargs -I {} runs once per line", Last(e).Contains("etch_20261118.log"));

            // awk
            long total = e.ReadFile("data/wafer_count.csv")!.Split('\n').Skip(1).Where(l => l.Length > 0).Sum(l => long.Parse(l.Split(',')[3]));
            e = Sh("awk -F , 'NR > 1 {s += $4} END {print s}' data/wafer_count.csv"); Check("awk sums a column", Last(e).Trim() == total.ToString());
            e = Sh("awk -F , '{print $2, $4}' data/wafer_count.csv | head -2"); Check("awk prints fields with OFS", Last(e) == "eqp count\nEQ01 " + e.ReadFile("data/wafer_count.csv")!.Split('\n')[1].Split(',')[3] + "\n");
            e = Sh("awk -F , '$2 == \"EQ02\" && $4 >= 23' data/wafer_count.csv"); Check("awk patterns filter lines", Last(e).Split('\n').Where(l => l.Length > 0).All(l => l.Contains("EQ02")));
            e = Sh("awk -F , 'NR > 1 {c[$2] += $4} END {for (k in c) print k, c[k]}' data/wafer_count.csv | sort"); Check("awk arrays and for-in", Last(e).Split('\n').Count(l => l.Length > 0) == 3 && Last(e).StartsWith("EQ01 "));
            e = Sh("awk '/ERROR/ {n++} END {print n}' logs/etch_20261123.log"); Check("awk /regex/ patterns", Last(e).Trim() == errors.ToString());
            e = Sh("echo 'a b c' | awk '{print NF, $NF, toupper($1), length($0)}'"); Check("awk NF, $NF and string functions", Last(e) == "3 c A 5\n");
            e = Sh("echo 3 4 | awk '{printf \"%.2f\\n\", $1 / $2}'"); Check("awk printf formats numbers", Last(e) == "0.75\n");
            e = Sh("echo x | awk -v n=5 '{print n * 2}'"); Check("awk -v sets variables", Last(e) == "10\n");
            e = Sh("echo 'a-b-c' | awk '{gsub(/-/, \"+\"); print}'"); Check("awk gsub", Last(e) == "a+b+c\n");
            e = Sh("echo 1 | awk '{print $1 +}'"); Check("awk syntax errors are reported", Screen(e).Contains("awk: syntax error"));
            e = Sh("awk 'BEGIN {while (1) x++}'"); Check("An endless awk loop is stopped", Screen(e).Contains("awk:"));

            // Permissions and scripts
            e = Sh("cd scripts", "./backup.sh"); Check("A script without x permission is denied", Screen(e).Contains("bash: ./backup.sh: Permission denied") && e.LastExit == 126);
            e.Run("chmod +x backup.sh"); Check("chmod +x adds the execute bit", e.IsExecutable("backup.sh"));
            e.Run("./backup.sh"); Check("Running backup.sh creates the dated archive", e.Exists("~/backup/recipes_20261123.tar.gz") && Screen(e).Contains("백업 완료"));
            e = Sh("bash scripts/check_alarm.sh"); Check("bash script.sh runs without the x bit and expands variables", Screen(e).Contains("검사할 로그: /home/user/logs/etch_20261123.log") && e.Runs.Any(r => r.Name == "grep" && r.Output.Trim() == errors.ToString()));
            e = Sh("chmod 644 scripts/check_alarm.sh", "ls -l scripts/check_alarm.sh"); Check("chmod 644 sets rw-r--r--", Last(e).StartsWith("-rw-r--r--"));
            e = Sh("chmod u+x,g-r notes.txt", "ls -l notes.txt"); Check("Symbolic chmod clauses", Last(e).StartsWith("-rwx---r--"));
            e = Sh("chmod +x /etc/hosts"); Check("chmod on root's file is not permitted", Screen(e).Contains("Operation not permitted"));
            e = Sh("echo 'cd /tmp' > go.sh", "bash go.sh", "pwd"); Check("cd inside a script does not move the prompt", Last(e) == "/home/user\n");
            e = Sh("echo 'cd /tmp' > go.sh", "source go.sh", "pwd"); Check("source runs in the current shell", Last(e) == "/tmp\n");

            // Archives
            e = Sh("tar czvf old.tar.gz logs/old", "tar tzf old.tar.gz"); Check("tar czf and tzf list members", Last(e).Contains("logs/old/etch_20261001.log"));
            e.Run("mkdir r && tar xzf old.tar.gz -C r"); Check("tar xzf -C extracts elsewhere", e.ReadFile("r/logs/old/etch_20261001.log") == e.ReadFile("logs/old/etch_20261001.log"));
            e = Sh("tar xzf notes.txt"); Check("tar on a text file explains it is not gzip", Screen(e).Contains("not in gzip format"));
            e = Sh("tar czf x.tar.gz"); Check("tar refuses an empty archive", Screen(e).Contains("Cowardly refusing"));
            e = Sh("gzip notes.txt", "ls", "zcat notes.txt.gz"); Check("gzip replaces the file and zcat reads it", !e.Runs[^2].Output.Contains("notes.txt\n") && Last(e).Contains("점검 메모"));
            e.Run("gunzip notes.txt.gz"); Check("gunzip restores the file", e.ReadFile("notes.txt")!.Contains("점검 메모") && !e.Exists("notes.txt.gz"));

            // Processes and disks
            e = Sh("ps -ef | grep cvd_monitor"); Check("ps -ef | grep finds the process", Last(e).Contains("3101"));
            e = Sh("kill 3101", "ps -ef"); Check("cvd_monitor ignores a plain kill", e.ProcessAlive(3101) && e.KillIgnored == 1);
            e.Run("kill -9 3101"); Check("kill -9 ends it", !e.ProcessAlive(3101));
            e = Sh("kill 845"); Check("Killing root's process is not permitted", Screen(e).Contains("Operation not permitted") && e.ProcessAlive(845));
            e = Sh("kill 9999"); Check("Killing a missing PID reports No such process", Screen(e).Contains("No such process"));
            e = Sh("top"); Check("top lists processes by CPU", Last(e).Split('\n').First(l => l.Contains("PID") ? false : char.IsDigit(l.TrimStart().FirstOrDefault()) && l.Contains("user")).Contains("cvd_monitor"));
            e = Sh("df -h"); Check("df -h shows disk use", Last(e).Contains("Use%") && Last(e).Contains("92%"));
            e = Sh("du -sh * | sort -h | tail -1"); Check("du -sh * | sort -h finds the largest folder", Last(e).Contains("tmp"));
            e = Sh("pgrep etch"); Check("pgrep lists matching PIDs", Last(e).Contains("2314"));

            // Interactive keys
            e = new ShellEngine(); e.Type("cat no"); e.Key("<Tab>"); Check("Tab completes a unique file name", e.Input == "cat notes.txt ");
            e = new ShellEngine(); e.Type("cd lo"); e.Key("<Tab>"); Check("Tab completes folders with a slash", e.Input == "cd logs/");
            e = new ShellEngine(); e.Type("ls logs/etch_"); e.Key("<Tab>"); e.Key("<Tab>"); Check("Tab completes the common prefix and lists choices on the second press", e.Input == "ls logs/etch_202611" && e.TabListings == 0); e.Key("<Tab>"); e.Key("<Tab>"); Check("A double Tab lists candidates", e.TabListings == 1 && e.Screen.Contains("etch_20261118.log"));
            e = new ShellEngine(); e.Type("gr"); e.Key("<Tab>"); Check("Tab completes command names", e.Input == "grep ");
            e = Sh("echo one", "echo two"); e.Key("<Up>"); Check("Up recalls the last command", e.Input == "echo two"); e.Key("<Up>"); Check("Up again goes further back", e.Input == "echo one"); e.Key("<Down>"); e.Key("<Down>"); Check("Down returns to an empty line", e.Input == "");
            e = Sh("echo first", "!!"); Check("!! repeats the last command", Screen(e).Split('\n').Count(l => l == "first") == 2);
            e = Sh("echo alpha", "ls", "!ec"); Check("!prefix repeats the last matching command", Screen(e).Split('\n').Count(l => l == "alpha") == 2);
            e = Sh("!zzz"); Check("A missing history event is reported", Screen(e).Contains("event not found"));
            e = Sh("grep ERROR notes.txt", "ls"); e.Key("<C-r>"); e.Type("gre"); Check("Ctrl+R finds a command in history", e.Input == "grep ERROR notes.txt" && e.Prompt.Contains("reverse-i-search")); e.Key("<CR>"); Check("Enter runs the found command", e.Runs[^1].Name == "grep");
            e = new ShellEngine(); e.Type("rm -rf logs"); e.Key("<C-c>"); Check("Ctrl+C cancels the line", e.Input == "" && e.Screen.Contains("rm -rf logs^C") && e.Exists("logs"));
            e = Sh("ls"); e.Key("<C-l>"); Check("Ctrl+L clears the screen", e.Output.Count == 0 && e.ClearCount == 1);
            e = new ShellEngine(); e.Type("abc"); e.Key("<C-a>"); e.Type("x"); e.Key("<C-e>"); e.Type("y"); Check("Ctrl+A and Ctrl+E move to the line ends", e.Input == "xabcy");
            e.Key("<C-w>"); Check("Ctrl+W deletes the last word", e.Input == ""); e.Type("one two"); e.Key("<C-u>"); Check("Ctrl+U deletes the line before the cursor", e.Input == "");
            e = Sh("history"); Check("history numbers commands", Last(e).Contains("1  history"));
            e = Sh("alias lg='ls -lt logs'", "lg"); Check("alias defines a shortcut", e.Runs[^1].Name == "ls" && e.Runs[^1].Line == "lg");
            e = Sh("man grep"); Check("man shows a short Korean page", Last(e).Contains("grep - "));
            e = Sh("ls --help"); Check("--help shows the same page", Last(e).Contains("ls - "));
            e = Sh("exit"); Check("exit keeps the practice terminal open", Screen(e).Contains("닫히지 않습니다"));

            // FTP
            e = Sh("ftp -i ftp-server", "guest", "wrong"); Check("A wrong FTP password fails with 530", Screen(e).Contains("530 Login incorrect.") && !e.RemoteLoggedIn && e.RemoteActive);
            e.Run("user"); e.Run("guest"); e.Run("guest"); Check("user logs in again", e.RemoteLoggedIn && Screen(e).Contains("230 Login successful."));
            Check("The password is never echoed", !Screen(e).Contains("Password: guest") && !Screen(e).Contains("Password: wrong"));
            e.Run("ls"); Check("ftp ls lists the server", Screen(e).Contains("150 Here comes the directory listing.") && Screen(e).Contains("readme.txt"));
            e.Run("cd log/2026-11"); e.Run("pwd"); Check("ftp cd and pwd", Screen(e).Contains("257 \"/log/2026-11\" is the current directory"));
            e.Run("lcd download"); Check("lcd changes the local folder", e.Cwd == "/home/user/download" && Screen(e).Contains("Local directory now /home/user/download"));
            e.Run("mget *.log"); Check("mget with -i downloads without asking", e.Downloaded.Count == 5 && e.Exists("/home/user/download/EQ02_20261123.log"));
            e.Run("get nothing.log"); Check("get of a missing file reports 550", Screen(e).Contains("550 Failed to open file."));
            e.Run("put /home/user/notes.txt notes.txt"); Check("Uploading outside /upload is refused", Screen(e).Contains("553 Could not create file.") && e.Uploaded.Count == 0);
            e.Run("!ls"); Check("!ls runs a local command inside ftp", e.Runs.Any(r => r.Name == "ls" && r.Output.Contains("EQ01_20261120.log")));
            e.Run("grep x"); Check("A shell command inside ftp explains !", Screen(e).Contains("?Invalid command.") && Screen(e).Contains("!grep"));
            e.Run("bye"); Check("bye closes the session", !e.RemoteActive && Screen(e).Contains("221 Goodbye."));
            e = Sh("ftp ftp-server", "guest", "guest", "cd log/2026-11", "mget EQ01_2026112*.log", "y", "n", "a"); Check("mget without -i asks per file and a answers all", e.Downloaded.Count == 3 && e.MgetPrompts >= 3);
            Check("Answering a turns prompting off for the rest", e.RemotePrompting == false);
            e.Run("prompt"); Check("prompt toggles interactive mode back on", e.RemotePrompting == true);
            e.Run("cd /upload"); e.Run("put notes.txt"); Check("put uploads into /upload", e.Uploaded.Contains("/upload/notes.txt") && Screen(e).Contains("226 Transfer complete."));
            e.Run("cd /backup"); e.Run("ascii"); e.Run("get recipe_20261101.tar.gz"); Check("An archive fetched in ASCII mode is marked corrupt", e.Fs.Find("/home/user/recipe_20261101.tar.gz")!.Corrupt && Screen(e).Contains("bin"));
            e.Run("bin"); e.Run("get recipe_20261101.tar.gz"); e.Run("bye"); e.Run("tar tzf recipe_20261101.tar.gz"); Check("Fetched again in binary mode the archive opens", Last(e).Contains("recipe/ETCH_A01.rcp"));
            e = Sh("ftp nowhere"); Check("An unknown FTP host is reported", Screen(e).Contains("Name or service not known") && e.RemoteActive);
            e.Run("open ftp-server"); e.Run("guest"); e.Run("guest"); Check("open connects from inside ftp", e.RemoteLoggedIn); e.Run("quit");
            e = Sh("ftp -i ftp-server"); e.Key("<C-c>"); Check("Ctrl+C at the login prompt returns to ftp>", e.RemoteActive && e.Prompt == "ftp> ");
            e.Key("<C-d>"); Check("Ctrl+D leaves ftp", !e.RemoteActive);

            // sftp, scp, ping
            e = Sh("sftp guest@ftp-server", "guest", "cd recipe", "get ETCH_B02.rcp", "lls", "bye"); Check("sftp get downloads and lls lists locally", e.Exists("ETCH_B02.rcp") && Screen(e).Contains("Fetching /recipe/ETCH_B02.rcp") && !e.RemoteActive);
            e = Sh("sftp guest@ftp-server", "bad"); Check("A wrong sftp password closes the session", !e.RemoteActive && Screen(e).Contains("Permission denied"));
            e = Sh("scp guest@ftp-server:/readme.txt .", "guest"); Check("scp copies a remote file here", e.ReadFile("readme.txt")!.Contains("연습용 FTP"));
            e = Sh("scp notes.txt guest@ftp-server:/upload/", "guest"); Check("scp uploads into /upload", e.Uploaded.Contains("/upload/notes.txt"));
            e = Sh("ping -c 2 ftp-server"); Check("ping -c prints replies and statistics", Last(e).Contains("2 packets transmitted, 2 received"));
            e = Sh("ssh guest@ftp-server"); Check("ssh explains that practice stays local", Screen(e).Contains("SSH"));
            e = Sh("ls | ftp ftp-server"); Check("ftp in a pipe is refused", Screen(e).Contains("파이프") && !e.RemoteActive);

            // vi bridge
            e = new ShellEngine(); string? opened = null, openedText = null; e.EditRequested += (p, t) => { opened = p; openedText = t; };
            e.Run("vi notes.txt"); Check("vi raises an editor request with the file", opened == "/home/user/notes.txt" && openedText!.Contains("점검 메모") && e.Editing);
            e.Run("ls"); Check("Keys are ignored while the editor is open", e.Runs.Count(r => r.Name == "ls") == 0);
            Check("The editor saves through the shell", e.SaveFromEditor("notes.txt", "changed") == null && e.ReadFile("notes.txt") == "changed\n" && e.EditorSaves == 1);
            Check("Saving into a missing folder fails", e.SaveFromEditor("/no/such/x.txt", "x") != null);
            e.EditorClosed(); Check("Closing the editor returns to the prompt", !e.Editing);
            e.Run("vi logs"); Check("vi on a folder is refused", Screen(e).Contains("디렉터리"));
            e.Run("vi new.txt"); Check("vi on a new file opens it empty", openedText == "" && e.Editing); e.SaveFromEditor("new.txt", "hello"); e.EditorClosed(); Check("Saving a new file creates it", e.ReadFile("new.txt") == "hello\n");
            e.Run("vi +3 notes.txt"); Check("vi +N passes the start line", e.EditStartLine == 3); e.EditorClosed();

            // Lessons: each one is completed by its example input.
            for (int i = 0; i < ShellLessons.All.Count; i++)
            {
                var lesson = ShellLessons.All[i]; if (lesson.Steps.Length == 0) continue;
                var shell = new ShellEngine(); shell.Reset(lesson.Start);
                ViEngine? vi = null;
                shell.EditRequested += (path, text) =>
                {
                    var editor = new ViEngine { Embedded = true, FileName = Path.GetFileName(path), SaveHandler = shell.SaveFromEditor };
                    editor.ExitHandler = () => { shell.EditorClosed(); vi = null; };
                    editor.Load(text.EndsWith('\n') ? text[..^1] : text); vi = editor;
                };
                Check($"Shell lesson {i + 1} starts incomplete", !lesson.Steps[0].Done(shell));
                int step = 0;
                foreach (var key in ViEngine.Tokens(lesson.Solution))
                {
                    if (shell.Editing && vi != null) vi.Feed(key); else shell.Key(key);
                    while (step < lesson.Steps.Length && lesson.Steps[step].Done(shell)) step++;
                }
                if (step < lesson.Steps.Length) File.WriteAllText(Path.Combine(AppPaths.Root, $"shell-lesson-{i + 1}-screen.txt"), shell.Screen);
                Check($"Shell lesson {i + 1} '{lesson.Title}' is completed by its example input (step {step}/{lesson.Steps.Length})", step == lesson.Steps.Length);
            }
            Check("Terminal lessons cover three levels", ShellLessons.All.Select(l => l.Level).Distinct().Count() == 3 && ShellLessons.All.Count >= 20);

            // Robustness: random command lines never throw and the shell keeps working.
            var random = new Random(97);
            string[] words = ["ls", "-l", "-a", "cd", "..", "logs", "grep", "ERROR", "-r", "find", ".", "-name", "\"*.log\"", "*", "|", "wc", "-l", ">", "x.txt", ">>", "&&", "||", ";", "cat", "notes.txt", "rm", "-rf", "tmp", "mkdir", "a", "cp", "mv", "awk", "'{print $1}'", "sed", "'s/a/b/'", "sort", "uniq", "-c", "tar", "czf", "t.tar.gz", "echo", "$HOME", "$(pwd)", "'", "\"", "\\", "~", "!!", "ftp", "bye", "kill", "3101", "chmod", "+x", "head", "-n", "2", "xargs", "-exec", "{}", "\\;", "diff", "du", "-sh"];
            e = new ShellEngine();
            for (int i = 0; i < 2500; i++)
            {
                int count = 1 + random.Next(6);
                string line = string.Join(" ", Enumerable.Range(0, count).Select(_ => words[random.Next(words.Length)]));
                if (line.StartsWith("rm") && line.Contains('/')) continue;
                e.Run(line);
                if (e.RemoteActive) e.Run("bye");
                if (e.Editing) e.EditorClosed();
                while (e.Prompt.EndsWith("? ") || e.Prompt.StartsWith("Name") || e.Prompt.StartsWith("Password") || e.Secret) e.Run("n");
            }
            e.Run("pwd"); Check("2,500 random command lines keep the shell working", e.Runs[^1].Name == "pwd" && !e.Editing && !e.RemoteActive);
            if (failures.Count > 0) throw new InvalidOperationException("FAIL: " + string.Join(" | ", failures));
        }
        finally { File.WriteAllText(Path.Combine(AppPaths.Root, "shell-test-report.txt"), report.ToString()); }
        return Task.CompletedTask;
    }
}
