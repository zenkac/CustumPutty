using System.Text;

namespace EquipLink;

public sealed record ShellProc(string User, int Pid, int Ppid, int Cpu, double Mem, string Started, string Tty, string Time, string Command);

/// <summary>Sample files for the practice shell: an equipment server and a small FTP server. All text is made up.</summary>
public static class ShellWorld
{
    private static readonly string[] Errors = ["ALM-1203 chamber pressure high", "ALM-2101 door open", "ALM-3307 RF reflected power over limit", "ALM-1408 He leak detected"];
    private static readonly string[] Warnings = ["fan speed low (rpm=820)", "temp drift +1.8C", "pump current high (12.4A)", "gas flow unstable"];

    public static string Log(string eqp, DateTime day, int lines, int seed, Dictionary<int, (string Level, string Text)>? fixedLines = null)
    {
        var random = new Random(seed); var sb = new StringBuilder(); int wafer = 1;
        for (int i = 0; i < lines; i++)
        {
            var time = day.Date.AddMinutes(i * (day.Date == ShellEngine.Start.Date ? 38 : 40) + random.Next(0, 30)).AddSeconds(random.Next(60));
            int roll = random.Next(100); string level, text; string channel = "CH" + (1 + random.Next(3));
            if (fixedLines != null && fixedLines.TryGetValue(i, out var line)) { level = line.Level; text = line.Text; }
            else if (roll < 9) { level = "ERROR"; text = Errors[random.Next(Errors.Length)]; }
            else if (roll < 22) { level = "WARN"; text = Warnings[random.Next(Warnings.Length)]; }
            else
            {
                level = "INFO";
                text = (i % 6) switch
                {
                    0 => $"wafer W{wafer:000} loaded",
                    1 => $"step=pump_down pressure=0.{random.Next(10, 30)}",
                    2 => $"step=heat_up temp={60 + random.Next(0, 60) / 10.0:0.0}",
                    3 => $"step=process temp={64 + random.Next(0, 30) / 10.0:0.0} pressure={110 + random.Next(0, 15)}",
                    4 => "step=vent",
                    _ => $"wafer W{wafer++:000} unloaded",
                };
            }
            sb.Append($"{time:yyyy-MM-dd HH:mm:ss} [{level}] {eqp} {channel} {text}\n");
        }
        return sb.ToString();
    }

    private static string Recipe(string name, int chamber, int temp, int pressure, int gas, bool comment = true) =>
        (comment ? $"# {name} recipe (연습용)\n" : "") +
        $"[recipe]\nname = {name}\nchamber = {chamber}\ntemp = {temp}\npressure = {pressure}\ngas_flow = {gas}\n\n[steps]\nstep1 = pump_down\nstep2 = heat_up\nstep3 = process\nstep4 = vent\n\n[limits]\ntemp_max = 80\npressure_max = 150\nalarm = on\n";

    public static ShellFs Local(DateTime now)
    {
        var fs = new ShellFs(now.AddDays(-400));
        DateTime Day(int day, int hour = 23, int minute = 59) => new DateTime(2026, 11, day, hour, minute, 0);
        foreach (var dir in new[] { "/bin", "/usr/bin", "/tmp", "/var/log", "/etc", "/opt/eqp/bin", "/opt/eqp/etc", ShellEngine.Home + "/download" }) fs.MakeDirs(dir, now.AddDays(-200));
        foreach (var path in new[] { "/bin", "/usr/bin", "/tmp", "/var/log", "/etc", "/opt" }) fs.Find(path)!.Owner = "root";
        fs.Find("/home")!.Owner = "root";
        fs.WriteFile("/etc/hosts", "127.0.0.1       localhost\n192.168.0.21    eqp-server\n192.168.0.50    ftp-server\n", now.AddDays(-120)).Owner = "root";
        fs.WriteFile("/etc/hostname", "eqp-server\n", now.AddDays(-300)).Owner = "root";
        fs.WriteFile("/var/log/messages", "Nov 23 08:00:01 eqp-server systemd[1]: Started etch daemon.\nNov 23 08:00:02 eqp-server etch_daemon[2314]: config loaded: /opt/eqp/etc/etch.conf\nNov 23 09:02:11 eqp-server cvd_monitor[3101]: debug mode enabled\nNov 23 09:12:44 eqp-server sshd[3300]: Accepted password for user from practice\n", now.AddMinutes(-18)).Owner = "root";
        foreach (var (name, size) in new[] { ("etch_daemon", 2_412_544L), ("etch_logger", 512_880L), ("cvd_monitor", 1_204_224L) })
        {
            var program = fs.WriteFile("/opt/eqp/bin/" + name, "", now.AddDays(-90), "rwxr-xr-x"); program.FixedSize = size; program.Owner = "root";
        }
        fs.WriteFile("/opt/eqp/etc/etch.conf", "# etch daemon settings (연습용)\nlog_dir = /home/user/logs\nrecipe_dir = /home/user/recipes\nport = 7010\ndebug = off\n", now.AddDays(-30)).Owner = "root";

        string home = ShellEngine.Home;
        fs.WriteFile(home + "/.bashrc", "# ~/.bashrc (연습용)\nalias ll='ls -alF'\nalias la='ls -A'\nalias grep='grep --color=auto'\nexport EQP_HOME=/opt/eqp\n", now.AddDays(-60));
        fs.WriteFile(home + "/.bash_history", "", now.AddDays(-1));
        fs.WriteFile(home + "/notes.txt", "점검 메모 (연습용)\n- FTP 서버: ftp-server (192.168.0.50)\n- 계정: guest / 비밀번호 guest\n- 받을 로그: /log/2026-11 폴더의 *.log\n- 레시피 원본: /recipe\n- 할 일: ETCH_A01 의 temp 65 -> 70 으로 수정 (vi 사용)\n", now.AddHours(-20));

        for (int day = 18; day <= 23; day++)
        {
            bool today = day == 23;
            var fixedLines = day switch
            {
                21 => new Dictionary<int, (string, string)> { [7] = ("INFO", "recovered from error state"), [12] = ("ERROR", "ALM-1203 chamber pressure high"), [13] = ("WARN", "pump current high (12.4A)") },
                23 => new Dictionary<int, (string, string)> { [4] = ("ERROR", "ALM-2101 door open"), [9] = ("ERROR", "ALM-1203 chamber pressure high") },
                _ => null
            };
            fs.WriteFile($"{home}/logs/etch_202611{day}.log", Log("EQ01", Day(day), today ? 15 : 36, 100 + day, fixedLines), today ? now.AddMinutes(-5) : Day(day));
        }
        fs.WriteFile($"{home}/logs/cvd_20261122.log", Log("CVD01", Day(22), 30, 222), Day(22));
        fs.WriteFile($"{home}/logs/cvd_20261123.log", Log("CVD01", Day(23), 14, 223), now.AddMinutes(-3));
        foreach (var day in new[] { 1, 2, 15 }) fs.WriteFile($"{home}/logs/old/etch_202610{day:00}.log", Log("EQ01", new DateTime(2026, 10, day), 30, 1000 + day), new DateTime(2026, 10, day, 23, 59, 0));

        fs.WriteFile($"{home}/recipes/ETCH_A01.rcp", Recipe("ETCH_A01", 2, 65, 120, 30), Day(20, 14, 2));
        fs.WriteFile($"{home}/recipes/ETCH_A02.rcp", Recipe("ETCH_A02", 1, 70, 110, 25), Day(19, 10, 40));
        fs.WriteFile($"{home}/recipes/CVD_B01.rcp", Recipe("CVD_B01", 3, 420, 5, 120), Day(12, 16, 25));
        fs.WriteFile($"{home}/recipes/backup/ETCH_A01.rcp.bak", Recipe("ETCH_A01", 2, 60, 120, 28), Day(5, 9, 0));

        fs.WriteFile($"{home}/scripts/backup.sh", "#!/bin/bash\n# 레시피 폴더를 압축해서 ~/backup 에 보관합니다 (연습용)\nmkdir -p ~/backup\ntar czf ~/backup/recipes_$(date +%Y%m%d).tar.gz -C ~ recipes\necho \"백업 완료: ~/backup\"\nls -l ~/backup\n", Day(10, 11, 0), "rw-r--r--");
        fs.WriteFile($"{home}/scripts/check_alarm.sh", "#!/bin/bash\n# 오늘 로그의 알람(ERROR) 개수를 셉니다 (연습용)\nLOG=~/logs/etch_20261123.log\necho \"검사할 로그: $LOG\"\ngrep -c ERROR $LOG\n", Day(10, 11, 5), "rwxr-xr-x");
        fs.WriteFile($"{home}/scripts/cleanup.py", "#!/usr/bin/env python3\n# 오래된 임시 파일 목록을 출력합니다 (연습용)\nimport os\nfor name in sorted(os.listdir('tmp')):\n    if name.endswith('.tmp'):\n        print(name)\n", Day(8, 17, 30));

        var counts = new StringBuilder("date,eqp,lot,count\n"); var random = new Random(77); int lot = 1001;
        for (int day = 18; day <= 22; day++) foreach (var eqp in new[] { "EQ01", "EQ02", "EQ03" }) counts.Append($"2026-11-{day},{eqp},L{lot++},{20 + random.Next(6)}\n");
        fs.WriteFile($"{home}/data/wafer_count.csv", counts.ToString(), Day(22, 23, 0));
        var temps = new StringBuilder("time,ch1,ch2,ch3\n");
        for (int i = 0; i < 12; i++) temps.Append($"08:{i * 5:00},{64 + random.Next(0, 30) / 10.0:0.0},{65 + random.Next(0, 30) / 10.0:0.0},{63 + random.Next(0, 40) / 10.0:0.0}\n");
        fs.WriteFile($"{home}/data/sensor_temp.csv", temps.ToString(), now.AddMinutes(-40));

        fs.WriteFile($"{home}/tmp/a.tmp", "temp data a\n", Day(22, 18, 0));
        fs.WriteFile($"{home}/tmp/b.tmp", "temp data b\n", Day(23, 8, 50));
        fs.WriteFile($"{home}/tmp/cache_old.tmp", "old cache\n", new DateTime(2026, 10, 2, 12, 0, 0));
        var core = fs.WriteFile($"{home}/tmp/core.3101", "", now.AddMinutes(-20)); core.FixedSize = 536_870_912;
        foreach (var node in fs.Find(home)!.Walk()) node.Owner = "user";
        return fs;
    }

    public static ShellFs Remote(DateTime now)
    {
        var fs = new ShellFs(now.AddDays(-500));
        fs.WriteFile("/readme.txt", "연습용 FTP 서버입니다.\nlog/     설비 로그 (월별 폴더)\nrecipe/  레시피 원본\nbackup/  백업 압축 파일 (bin 모드로 받으세요)\nupload/  올리는 곳\n", now.AddDays(-40));
        for (int day = 20; day <= 23; day++)
            fs.WriteFile($"/log/2026-11/EQ01_202611{day}.log", Log("EQ01", new DateTime(2026, 11, day), day == 23 ? 15 : 36, 500 + day), day == 23 ? now.AddMinutes(-4) : new DateTime(2026, 11, day, 23, 59, 30));
        fs.WriteFile("/log/2026-11/EQ02_20261123.log", Log("EQ02", new DateTime(2026, 11, 23), 14, 623), now.AddMinutes(-2));
        fs.WriteFile("/log/2026-10/EQ01_20261031.log", Log("EQ01", new DateTime(2026, 10, 31), 36, 531), new DateTime(2026, 10, 31, 23, 59, 30));
        fs.WriteFile("/recipe/ETCH_A01.rcp", Recipe("ETCH_A01", 2, 65, 120, 30), new DateTime(2026, 11, 20, 14, 2, 0));
        fs.WriteFile("/recipe/ETCH_B02.rcp", Recipe("ETCH_B02", 1, 72, 115, 26), new DateTime(2026, 11, 17, 9, 12, 0));
        fs.WriteFile("/recipe/CVD_C01.rcp", Recipe("CVD_C01", 3, 410, 6, 110), new DateTime(2026, 11, 3, 13, 45, 0));
        var archive = fs.WriteFile("/backup/recipe_20261101.tar.gz", "", new DateTime(2026, 11, 1, 2, 0, 0));
        archive.Gzip = true;
        archive.Archive =
        [
            new("recipe", true, "", "rwxr-xr-x", new DateTime(2026, 11, 1, 1, 59, 0), null),
            new("recipe/ETCH_A01.rcp", false, Recipe("ETCH_A01", 2, 62, 120, 30), "rw-r--r--", new DateTime(2026, 10, 28, 10, 0, 0), null),
            new("recipe/ETCH_B02.rcp", false, Recipe("ETCH_B02", 1, 72, 115, 26), "rw-r--r--", new DateTime(2026, 10, 20, 15, 30, 0), null),
        ];
        archive.FixedSize = 1_183;
        fs.MakeDirs("/upload", now.AddDays(-100));
        foreach (var node in fs.Root.Walk()) node.Owner = "1001";
        return fs;
    }

    public static IEnumerable<ShellProc> Processes() =>
    [
        new("root", 1, 0, 0, 0.1, "Nov20", "?", "00:00:05", "/sbin/init"),
        new("root", 812, 1, 0, 0.1, "Nov20", "?", "00:00:00", "/usr/sbin/sshd -D"),
        new("root", 845, 1, 0, 0.0, "Nov20", "?", "00:00:00", "/usr/sbin/vsftpd /etc/vsftpd.conf"),
        new("root", 901, 1, 0, 0.2, "Nov20", "?", "00:00:12", "/usr/sbin/crond -n"),
        new("user", 2314, 1, 2, 1.8, "08:00", "?", "00:12:31", "/opt/eqp/bin/etch_daemon -c /opt/eqp/etc/etch.conf"),
        new("user", 2315, 2314, 0, 0.4, "08:00", "?", "00:00:40", "/opt/eqp/bin/etch_logger --dir /home/user/logs"),
        new("user", 3101, 1, 98, 6.5, "09:02", "?", "00:27:12", "/opt/eqp/bin/cvd_monitor --debug"),
        new("user", 3300, 812, 0, 0.1, "09:12", "?", "00:00:00", "sshd: user@pts/0"),
        new("user", 3355, 3300, 0, 0.1, "09:12", "pts/0", "00:00:00", "-bash"),
    ];
}
