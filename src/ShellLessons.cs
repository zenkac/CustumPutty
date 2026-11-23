namespace EquipLink;

public sealed record ShellStep(string Text, Func<ShellEngine, bool> Done);

/// <summary>A terminal lesson. <see cref="Solution"/> is a key string (with &lt;CR&gt;, &lt;Tab&gt; …) that completes every step.</summary>
public sealed record ShellLesson(PracticeLevel Level, string Title, string Keys, string Explain, ShellStep[] Steps, string Solution, string Start = "~");

public static class ShellLessons
{
    private const PracticeLevel B = PracticeLevel.Basic, I = PracticeLevel.Intermediate, A = PracticeLevel.Advanced;
    private const string H = ShellEngine.Home;

    private static bool Flags(ShellRun run, string letters) => letters.All(l => run.Args.Any(a => a.StartsWith('-') && !a.StartsWith("--") && a.Contains(l)));
    private static int Lines(ShellRun run) => run.Output.Split('\n').Count(l => l.Length > 0);

    private static long WaferTotal(ShellEngine e) =>
        (e.ReadFile("~/data/wafer_count.csv") ?? "").Split('\n').Skip(1).Where(l => l.Length > 0).Sum(l => long.TryParse(l.Split(',').Last(), out long n) ? n : 0);

    public static IReadOnlyList<ShellLesson> All { get; } =
    [
        // ───────────── 기초 ─────────────
        new(B, "현재 위치와 목록", "pwd  ls  ls -l  ls -a",
            "접속하면 홈 폴더(~ = /home/user)에서 시작합니다. pwd 는 지금 있는 폴더의 전체 경로, ls 는 폴더 안 목록입니다. ls -l 은 권한·크기·날짜까지 자세히, ls -a 는 .으로 시작하는 숨김 파일까지 보여 줍니다. 옵션은 ls -la 처럼 합쳐 쓸 수 있습니다. 파란색은 폴더, 초록색은 실행 파일, 빨간색은 압축 파일입니다.",
            [new("pwd 로 지금 위치 확인", e => e.Ran("pwd")),
             new("ls 로 목록 보기", e => e.Ran("ls")),
             new("ls -l 로 자세히 보기", e => e.Ran("ls", r => Flags(r, "l"))),
             new("ls -a 로 숨김 파일(.bashrc)까지 보기", e => e.Ran("ls", r => Flags(r, "a") && r.Output.Contains(".bashrc")))],
            "pwd<CR>ls<CR>ls -l<CR>ls -a<CR>"),

        new(B, "폴더 이동", "cd  cd ..  cd ~  cd -",
            "cd 폴더 로 들어가고 cd .. 로 한 단계 위로 나옵니다. cd 만 치거나 cd ~ 는 홈으로, cd - 는 바로 전 폴더로 돌아갑니다. /로 시작하면 절대 경로(/var/log), 아니면 지금 위치 기준 상대 경로(logs/old)입니다. 프롬프트의 ~/logs 부분이 현재 위치입니다.",
            [new("cd logs 로 logs 폴더에 들어가기", e => e.Cwd == H + "/logs" || e.Ran("cd", r => r.Args.FirstOrDefault()?.TrimEnd('/') == "logs")),
             new("cd old 로 들어간 뒤 cd .. 로 나오기", e => e.Ran("cd", r => r.Args.FirstOrDefault()?.TrimEnd('/') is "old" or "logs/old") && e.Ran("cd", r => r.Args.FirstOrDefault() == "..")),
             new("cd /var/log 로 절대 경로 이동", e => e.Ran("cd", r => r.Args.FirstOrDefault()?.TrimEnd('/') == "/var/log")),
             new("cd - 로 바로 전 폴더, 그다음 cd 로 홈으로 돌아오기", e => e.Ran("cd", r => r.Args.FirstOrDefault() == "-") && e.Cwd == H)],
            "cd logs<CR>cd old<CR>cd ..<CR>cd /var/log<CR>cd -<CR>cd<CR>"),

        new(B, "파일 내용 보기", "cat  head  tail  less",
            "cat 은 파일 전체를 출력합니다. 긴 로그는 head(앞 10줄), tail(끝 10줄)로 보고 -n 숫자로 줄 수를 정합니다. tail -f 는 새로 쌓이는 로그를 계속 보여 줘서 실시간 확인에 씁니다 (Ctrl+C 로 멈춤). less 는 화면 단위로 넘겨 봅니다 (Space 다음, q 종료).",
            [new("cat notes.txt 로 메모 보기", e => e.Ran("cat", r => r.Args.Any(a => a.EndsWith("notes.txt")) && r.Exit == 0)),
             new("head -n 3 logs/etch_20261122.log 로 앞 3줄 보기", e => e.Ran("head", r => Lines(r) == 3)),
             new("tail -n 5 로 같은 로그의 마지막 5줄 보기", e => e.Ran("tail", r => Lines(r) == 5)),
             new("tail -f logs/etch_20261123.log 로 실시간 보기 흉내", e => e.Ran("tail", r => Flags(r, "f")))],
            "cat notes.txt<CR>head -n 3 logs/etch_20261122.log<CR>tail -n 5 logs/etch_20261122.log<CR>tail -f logs/etch_20261123.log<CR>"),

        new(B, "만들기·복사·이동·삭제", "mkdir  cp  mv  rm -i",
            "mkdir 로 폴더를 만들고 cp 원본 대상 으로 복사, mv 원본 대상 으로 옮기거나 이름을 바꿉니다. 폴더를 복사할 때는 cp -r 입니다. rm 은 휴지통 없이 바로 지우므로 rm -i 로 하나씩 확인하는 습관을 들이세요 (y 지우기, n 취소). 폴더는 rm -r 로 지웁니다.",
            [new("mkdir work 로 작업 폴더 만들기", e => e.Fs.Find(H + "/work") is { IsDir: true }),
             new("cp recipes/ETCH_A01.rcp work/ 로 레시피 복사", e => e.Exists("~/work/ETCH_A01.rcp") || e.Exists("~/work/ETCH_A01_new.rcp")),
             new("mv work/ETCH_A01.rcp work/ETCH_A01_new.rcp 로 이름 바꾸기", e => e.Exists("~/work/ETCH_A01_new.rcp") && !e.Exists("~/work/ETCH_A01.rcp")),
             new("rm -i tmp/a.tmp 후 y 로 확인하며 지우기", e => !e.Exists("~/tmp/a.tmp") && e.RmConfirmed >= 1)],
            "mkdir work<CR>cp recipes/ETCH_A01.rcp work/<CR>mv work/ETCH_A01.rcp work/ETCH_A01_new.rcp<CR>rm -i tmp/a.tmp<CR>y<CR>"),

        new(B, "자동 완성과 명령 기록", "Tab  ↑  history  Ctrl+L",
            "Tab 을 누르면 명령과 파일 이름을 자동으로 채웁니다. 후보가 여러 개면 Tab 을 두 번 눌러 목록을 봅니다. ↑ ↓ 는 전에 입력한 명령을 다시 불러오고, history 는 기록 전체, !! 는 바로 전 명령, !번호 는 그 번호 명령을 다시 실행합니다. Ctrl+R 은 기록을 글자로 검색합니다. 화면이 지저분하면 clear 또는 Ctrl+L 입니다.",
            [new("cat no 까지 치고 Tab 으로 notes.txt 를 완성한 뒤 Enter", e => e.TabCompletions >= 1 && e.Ran("cat", r => r.Args.Contains("notes.txt"))),
             new("↑ 로 방금 명령을 불러와 다시 실행", e => e.HistoryRecalls >= 1),
             new("history 로 지금까지 입력한 명령 보기", e => e.Ran("history")),
             new("Ctrl+L 로 화면 지우기", e => e.ClearCount >= 1)],
            "cat no<Tab><CR><Up><CR>history<CR><C-l>"),

        // ───────────── 중급 ─────────────
        new(I, "find 로 파일 이름 찾기", "find . -name \"*.log\"",
            "find 경로 -name \"패턴\" 은 이름이 맞는 파일을 하위 폴더까지 모두 찾습니다. * 는 아무 글자 여러 개, ? 는 한 글자입니다. 패턴은 꼭 따옴표로 감싸세요 (감싸지 않으면 셸이 먼저 현재 폴더 이름으로 바꿔 버려 엉뚱한 결과가 납니다). -iname 은 대소문자 무시, -type f 는 파일만, -type d 는 폴더만입니다.",
            [new("find . -name \"*.log\" 로 모든 로그 찾기", e => e.Ran("find", r => r.Args.Contains("-name") && r.Output.Contains("logs/old/etch_20261001.log"))),
             new("find . -iname \"etch_a01*\" 로 대소문자 상관없이 레시피 찾기", e => e.Ran("find", r => r.Args.Contains("-iname") && r.Output.Contains("ETCH_A01.rcp"))),
             new("find ~ -type d -name \"*backup*\" 으로 백업 폴더 찾기", e => e.Ran("find", r => r.Args.Contains("-type") && r.Output.Contains("recipes/backup")))],
            "find . -name \"*.log\"<CR>find . -iname \"etch_a01*\"<CR>find ~ -type d -name \"*backup*\"<CR>"),

        new(I, "find 조건과 실행", "-mtime  -size  -exec",
            "-mtime -1 은 하루 안에 바뀐 파일, -mtime +30 은 30일 넘게 지난 파일입니다. -size +100M 은 100MB 보다 큰 파일. -exec 명령 {} \\; 는 찾은 파일마다 명령을 실행합니다 ({} 자리에 파일 이름, 끝의 \\; 는 꼭 필요). -delete 도 있지만 먼저 -exec ls -l {} \\; 처럼 확인부터 하세요.",
            [new("find . -name \"*.log\" -mtime -1 로 최근 하루 로그 찾기", e => e.Ran("find", r => r.Args.Contains("-mtime") && r.Output.Contains("etch_20261123.log") && !r.Output.Contains("etch_20261118"))),
             new("find . -size +100M 으로 큰 파일(코어 덤프) 찾기", e => e.Ran("find", r => r.Args.Contains("-size") && r.Output.Contains("core.3101"))),
             new("find tmp -name \"*.tmp\" -mtime +30 -exec rm {} \\; 로 오래된 임시 파일만 지우기", e => !e.Exists("~/tmp/cache_old.tmp") && e.Exists("~/tmp/b.tmp"))],
            "find . -name \"*.log\" -mtime -1<CR>find . -size +100M<CR>find tmp -name \"*.tmp\" -mtime +30 -exec rm {} \\;<CR>"),

        new(I, "grep 으로 내용 찾기", "grep  -i  -n  -c  -v",
            "grep 찾을말 파일 은 그 글자가 들어 있는 줄만 보여 줍니다. -i 대소문자 무시, -n 줄 번호, -c 줄 개수, -v 는 반대로 '없는' 줄입니다. 여러 파일에서 찾으면 줄 앞에 파일 이름이 붙고, 찾은 부분은 빨간색으로 표시됩니다.",
            [new("grep ERROR logs/etch_20261121.log 로 오류 줄 보기", e => e.Ran("grep", r => r.Args.Contains("ERROR") && r.Exit == 0)),
             new("grep -n ERROR … 로 줄 번호와 함께 보기", e => e.Ran("grep", r => Flags(r, "n") && r.Exit == 0)),
             new("grep -i error … 로 소문자 error 까지 찾기", e => e.Ran("grep", r => Flags(r, "i") && r.Output.Contains("recovered from error"))),
             new("grep -c ERROR logs/*.log 로 파일별 오류 개수 세기", e => e.Ran("grep", r => Flags(r, "c") && r.Output.Contains("logs/etch_20261121.log:")))],
            "grep ERROR logs/etch_20261121.log<CR>grep -n ERROR logs/etch_20261121.log<CR>grep -i error logs/etch_20261121.log<CR>grep -c ERROR logs/*.log<CR>"),

        new(I, "폴더 전체에서 찾기", "grep -r  -l  --include  -A -B",
            "grep -r 은 폴더 안 모든 파일을 뒤집니다. -l 은 찾은 파일 이름만, -w 는 단어가 정확히 일치할 때만, --include=\"*.rcp\" 는 그 종류 파일만 봅니다. -A 2 · -B 2 는 찾은 줄의 뒤 · 앞 2줄도 함께 보여 줘서 알람 앞뒤 상황을 볼 때 씁니다.",
            [new("grep -r ALM-1203 . 로 홈 전체에서 알람 코드 찾기", e => e.Ran("grep", r => Flags(r, "r") && r.Output.Contains("ALM-1203"))),
             new("grep -rl ERROR logs 로 오류가 있는 파일 이름만 보기", e => e.Ran("grep", r => Flags(r, "rl") && r.Output.Contains("logs/"))),
             new("grep -r --include=\"*.rcp\" temp recipes 로 레시피 파일만 찾기", e => e.Ran("grep", r => r.Args.Any(a => a.StartsWith("--include")) && r.Output.Contains(".rcp:"))),
             new("grep -B 1 -A 1 ALM-1203 logs/etch_20261121.log 로 앞뒤 줄 같이 보기", e => e.Ran("grep", r => r.Args.Any(a => a.StartsWith("-A") || a.StartsWith("-B") || a.StartsWith("-C")) && r.Exit == 0))],
            "grep -r ALM-1203 .<CR>grep -rl ERROR logs<CR>grep -r --include=\"*.rcp\" temp recipes<CR>grep -B 1 -A 1 ALM-1203 logs/etch_20261121.log<CR>"),

        new(I, "파이프와 리다이렉션", "|  >  >>  wc  sort  uniq",
            "| (파이프)는 앞 명령의 결과를 뒤 명령의 입력으로 넘깁니다. grep ERROR 로그 | wc -l 은 오류 줄 수, … | sort | uniq -c 는 종류별 개수입니다. cut -d ' ' -f 6 은 공백으로 나눈 6번째 칸만 뽑습니다. > 파일 은 결과를 파일로 저장(덮어쓰기), >> 는 파일 끝에 추가, 2>/dev/null 은 오류 메시지를 버립니다.",
            [new("grep ERROR logs/etch_20261123.log | wc -l 로 오늘 오류 개수 세기", e => e.Ran("wc", r => Flags(r, "l") && r.Line.Contains('|'))),
             new("grep -h ERROR logs/*.log | cut -d ' ' -f 6 | sort | uniq -c 로 알람 코드별 개수", e => e.Ran("uniq", r => Flags(r, "c") && r.Output.Contains("ALM-"))),
             new("grep ERROR logs/etch_20261123.log > today_errors.txt 로 파일에 저장", e => e.ReadFile("~/today_errors.txt")?.Contains("ERROR") == true),
             new("echo \"checked by user\" >> today_errors.txt 후 cat 으로 확인", e => e.ReadFile("~/today_errors.txt")?.EndsWith("checked by user\n") == true && e.Ran("cat", r => r.Args.Any(a => a.EndsWith("today_errors.txt"))))],
            "grep ERROR logs/etch_20261123.log | wc -l<CR>grep -h ERROR logs/*.log | cut -d ' ' -f 6 | sort | uniq -c<CR>grep ERROR logs/etch_20261123.log > today_errors.txt<CR>echo \"checked by user\" >> today_errors.txt<CR>cat today_errors.txt<CR>"),

        new(I, "권한과 스크립트 실행", "ls -l  chmod +x  ./파일",
            "ls -l 맨 앞의 rwxr-xr-x 는 소유자 · 그룹 · 다른 사람의 읽기(r) · 쓰기(w) · 실행(x) 권한입니다. 스크립트는 실행 권한(x)이 있어야 ./파일 로 실행됩니다 (./ 는 '지금 폴더의' 라는 뜻). chmod +x 파일 로 실행 권한을 주고, chmod 755 처럼 숫자(r=4 w=2 x=1)로도 정합니다.",
            [new("cd scripts 후 ls -l 로 권한 확인", e => e.Cwd == H + "/scripts" && e.Ran("ls", r => Flags(r, "l"))),
             new("./backup.sh 실행 → Permission denied 확인", e => e.Ran("./backup.sh", r => r.Exit == 126)),
             new("chmod +x backup.sh 로 실행 권한 주기", e => e.IsExecutable("~/scripts/backup.sh")),
             new("./backup.sh 로 실행해 백업 만들기", e => e.Exists("~/backup/recipes_20261123.tar.gz"))],
            "cd scripts<CR>ls -l<CR>./backup.sh<CR>chmod +x backup.sh<CR>./backup.sh<CR>"),

        new(I, "압축 묶기와 풀기", "tar czf  tzf  xzf  -C",
            "tar 는 여러 파일을 하나로 묶습니다. c 만들기, x 풀기, t 목록, z gzip 압축, v 진행 표시, f 파일 이름(바로 뒤에 이름). tar czf 묶음.tar.gz 폴더 로 묶고, tar tzf 로 안을 보고, tar xzf 로 풉니다. -C 폴더 를 붙이면 그 폴더에 풉니다. 로그를 보내기 전에 압축할 때 자주 씁니다.",
            [new("tar czvf logs_old.tar.gz logs/old 로 오래된 로그 묶기", e => e.Fs.Find(H + "/logs_old.tar.gz")?.Archive != null),
             new("tar tzf logs_old.tar.gz 로 안의 목록 확인", e => e.Ran("tar", r => r.Output.Contains("logs/old/etch_20261001.log") && r.Args.Any(a => a.TrimStart('-').StartsWith('t')))),
             new("mkdir restore && tar xzf logs_old.tar.gz -C restore 로 다른 곳에 풀기", e => e.Exists("~/restore/logs/old/etch_20261001.log"))],
            "tar czvf logs_old.tar.gz logs/old<CR>tar tzf logs_old.tar.gz<CR>mkdir restore && tar xzf logs_old.tar.gz -C restore<CR>"),

        new(I, "별명과 환경 변수", "alias  echo $HOME  ~/.bashrc",
            "alias 이름='명령' 으로 긴 명령에 별명을 붙입니다 (ll 은 이미 ls -alF 의 별명). $이름 은 환경 변수 값을 꺼냅니다 (echo $HOME). export 이름=값 으로 변수를 만들고, 다음 접속에도 쓰려면 ~/.bashrc 에 적은 뒤 source ~/.bashrc 로 바로 적용합니다.",
            [new("alias 로 지금 별명 목록 보기", e => e.Ran("alias", r => r.Args.Count == 0)),
             new("alias lg='ls -lt logs' 로 별명을 만들고 lg 실행", e => e.Ran("ls", r => r.Line.TrimStart().StartsWith("lg"))),
             new("echo $HOME $EQP_HOME 으로 변수 값 보기", e => e.Ran("echo", r => r.Output.Contains("/opt/eqp"))),
             new("echo \"alias lg='ls -lt logs'\" >> ~/.bashrc 후 source ~/.bashrc", e => e.ReadFile("~/.bashrc")?.Contains("alias lg=") == true && (e.Ran("source") || e.Ran(".")))],
            "alias<CR>alias lg='ls -lt logs'<CR>lg<CR>echo $HOME $EQP_HOME<CR>echo \"alias lg='ls -lt logs'\" >> ~/.bashrc<CR>source ~/.bashrc<CR>"),

        // ───────────── 고급 ─────────────
        new(A, "ftp -i 로 파일 받기", "ftp -i  lcd  cd  mget  bye",
            "ftp -i 서버 로 접속하면 이름과 비밀번호를 묻습니다 (연습 서버: guest / guest, 비밀번호는 화면에 보이지 않습니다). ls 로 서버 목록, cd 로 서버 폴더 이동, lcd 로 내 쪽(받을 곳) 폴더를 정합니다. get 파일 은 하나, mget *.log 는 여러 개를 받습니다. -i 는 mget 할 때 파일마다 y/n 을 묻지 않게 하고, bye 는 종료입니다.",
            [new("ftp -i ftp-server 로 접속하고 guest / guest 로 로그인", e => e.RemoteLoggedIn && e.RemoteKind == "ftp" && e.RemotePrompting == false),
             new("lcd download 로 받을 폴더를 정하고 cd log/2026-11 로 서버 폴더 이동", e => e.Cwd == H + "/download" && e.RemoteCwd == "/log/2026-11"),
             new("mget *.log 로 로그를 한 번에 받기", e => e.Downloaded.Count(p => p.StartsWith(H + "/download/") && p.EndsWith(".log")) >= 5),
             new("bye 로 끝내고 ls 로 받은 파일 확인", e => !e.RemoteActive && e.Ran("ls", r => r.Where == "shell" && r.Output.Contains("EQ01_20261123.log")))],
            "ftp -i ftp-server<CR>guest<CR>guest<CR>lcd download<CR>cd log/2026-11<CR>mget *.log<CR>bye<CR>ls<CR>"),

        new(A, "묻는 mget · 올리기 · bin", "prompt  mget  put  bin",
            "-i 없이 접속하면 mget 이 파일마다 'mget 파일?' 하고 묻습니다 (y 받기, n 건너뛰기, a 모두). 접속 후 prompt 를 입력하면 묻기를 끄고 켭니다. put 파일 은 내 파일을 서버로 올립니다 (연습 서버는 /upload 에만). 압축 파일은 꼭 bin(바이너리) 모드로 받으세요 — ascii 모드로 받으면 파일이 깨집니다.",
            [new("ftp ftp-server 로 (-i 없이) 접속해 로그인", e => e.RemoteLoggedIn && e.RemoteKind == "ftp"),
             new("cd log/2026-10 후 mget *.log 를 하고 y 로 답하기", e => e.MgetPrompts >= 1 && e.Downloaded.Any(p => p.EndsWith("EQ01_20261031.log"))),
             new("cd /upload 후 put notes.txt 로 올리기", e => e.Uploaded.Contains("/upload/notes.txt")),
             new("cd /backup → bin → get recipe_20261101.tar.gz 받고 bye", e => !e.RemoteActive && e.Fs.Find(H + "/recipe_20261101.tar.gz") is { Corrupt: false, Archive: not null })],
            "ftp ftp-server<CR>guest<CR>guest<CR>cd log/2026-10<CR>mget *.log<CR>y<CR>cd /upload<CR>put notes.txt<CR>cd /backup<CR>bin<CR>get recipe_20261101.tar.gz<CR>bye<CR>"),

        new(A, "sftp 와 scp", "sftp user@host  get  scp",
            "요즘 서버는 보안 때문에 FTP 대신 SSH 기반의 sftp · scp 를 많이 씁니다. sftp 사용자@서버 로 접속해 get · put 을 쓰고, 내 쪽 명령은 lls · lcd 처럼 앞에 l 을 붙입니다. scp 는 접속하지 않고 한 줄로 복사합니다: scp 사용자@서버:/경로/파일 . (끝의 점은 '여기로')",
            [new("sftp guest@ftp-server 로 접속 (비밀번호 guest)", e => e.RemoteKind == "sftp" && e.RemoteLoggedIn),
             new("cd recipe 후 get ETCH_B02.rcp 로 받고 bye", e => e.Downloaded.Any(p => p.EndsWith("ETCH_B02.rcp")) && !e.RemoteActive),
             new("scp guest@ftp-server:/readme.txt . 로 한 줄 복사", e => e.Exists("~/readme.txt"))],
            "sftp guest@ftp-server<CR>guest<CR>cd recipe<CR>get ETCH_B02.rcp<CR>bye<CR>scp guest@ftp-server:/readme.txt .<CR>guest<CR>"),

        new(A, "find 와 xargs 조합", "| xargs  -I {}  -exec … +",
            "find 결과를 | xargs 명령 으로 넘기면 찾은 파일들을 한꺼번에 명령의 인자로 씁니다. find logs -name \"*.log\" | xargs grep -l ALM-1203 은 그 알람이 있는 로그 목록입니다. xargs -I {} 는 {} 자리에 하나씩 넣어 실행하고, find … -exec 명령 {} + 는 xargs 처럼 한 번에 넘깁니다.",
            [new("find logs -name \"*.log\" | xargs grep -l ALM-1203 로 알람이 있는 로그 찾기", e => e.Ran("xargs") && e.Ran("grep", r => Flags(r, "l") && r.Output.Contains("etch_20261121.log"))),
             new("find logs -name \"*.log\" -mtime -2 | xargs -I {} cp {} download/ 로 최근 로그 복사", e => e.Exists("~/download/etch_20261123.log")),
             new("find logs -name \"*.log\" -exec wc -l {} + 로 파일별 줄 수 보기", e => e.Ran("wc", r => r.Output.Contains("total")))],
            "find logs -name \"*.log\" | xargs grep -l ALM-1203<CR>find logs -name \"*.log\" -mtime -2 | xargs -I {} cp {} download/<CR>find logs -name \"*.log\" -exec wc -l {} +<CR>"),

        new(A, "awk 로 칸 가공", "awk '{print $2}'  -F ,  END",
            "awk 는 줄을 칸(기본은 공백)으로 나눠 $1, $2 … 로 씁니다. -F , 는 쉼표로 나눕니다. 조건을 앞에 쓰면 그 줄만 처리합니다: awk -F , '$4 > 22'. {s += $4} END {print s} 는 합계, NR 은 줄 번호입니다. sort -t , -k 4 -n 은 4번째 칸 숫자 순 정렬입니다.",
            [new("awk -F , '{print $2, $4}' data/wafer_count.csv 로 설비와 수량만 보기", e => e.Ran("awk", r => r.Output.Contains("EQ01 ") && r.Exit == 0)),
             new("awk -F , 'NR > 1 {s += $4} END {print s}' … 로 전체 수량 합계", e => e.Ran("awk", r => r.Output.Trim() == WaferTotal(e).ToString())),
             new("awk -F , '$2 == \"EQ02\" && $4 >= 23' … 로 조건에 맞는 줄만", e => e.Ran("awk", r => r.Args.Any(a => a.Contains("EQ02")) && r.Exit == 0 && r.Output.Split('\n').Where(l => l.Length > 0).All(l => l.Contains("EQ02")))),
             new("sort -t , -k 4 -n data/wafer_count.csv | tail -3 로 수량이 많은 3개", e => e.Ran("tail", r => r.Line.Contains("sort") && Lines(r) == 3))],
            "awk -F , '{print $2, $4}' data/wafer_count.csv<CR>awk -F , 'NR > 1 {s += $4} END {print s}' data/wafer_count.csv<CR>awk -F , '$2 == \"EQ02\" && $4 >= 23' data/wafer_count.csv<CR>sort -t , -k 4 -n data/wafer_count.csv | tail -3<CR>"),

        new(A, "sed 로 바꾸기", "sed 's///'  -n p  -i.bak  diff",
            "sed 's/찾을/바꿀/g' 파일 은 바꾼 결과를 화면에만 보여 줍니다 (원본은 그대로). sed -n '5,10p' 는 5~10번째 줄만, sed '/^#/d' 는 #으로 시작하는 줄을 빼고 보여 줍니다. -i 를 붙이면 파일 자체를 고치는데, -i.bak 처럼 쓰면 원본을 .bak 으로 남겨서 안전합니다. diff 로 바뀐 줄을 확인합니다.",
            [new("sed 's/temp = 65/temp = 70/' recipes/ETCH_A01.rcp 로 결과 미리 보기", e => e.Ran("sed", r => !r.Args.Any(a => a.StartsWith("-i")) && r.Output.Contains("temp = 70"))),
             new("sed -n '2,5p' recipes/ETCH_A01.rcp 로 2~5번째 줄만 보기", e => e.Ran("sed", r => r.Args.Contains("-n") && Lines(r) == 4)),
             new("sed -i.bak 's/temp = 65/temp = 70/' recipes/ETCH_A01.rcp 로 파일 직접 고치기", e => e.ReadFile("~/recipes/ETCH_A01.rcp")?.Contains("temp = 70") == true && e.Exists("~/recipes/ETCH_A01.rcp.bak")),
             new("diff recipes/ETCH_A01.rcp.bak recipes/ETCH_A01.rcp 로 바뀐 줄 확인", e => e.Ran("diff", r => r.Output.Contains("> temp = 70")))],
            "sed 's/temp = 65/temp = 70/' recipes/ETCH_A01.rcp<CR>sed -n '2,5p' recipes/ETCH_A01.rcp<CR>sed -i.bak 's/temp = 65/temp = 70/' recipes/ETCH_A01.rcp<CR>diff recipes/ETCH_A01.rcp.bak recipes/ETCH_A01.rcp<CR>"),

        new(A, "프로세스 확인과 종료", "ps -ef | grep  kill  kill -9",
            "ps -ef 는 실행 중인 모든 프로세스, ps -ef | grep 이름 은 특정 프로그램만 보여 줍니다 (두 번째 칸이 PID 번호). top 은 CPU 를 많이 쓰는 순서입니다. kill PID 는 정상 종료를 요청하고, 그래도 안 끝나면 kill -9 PID 로 강제 종료합니다. 다른 사용자(root)의 프로세스는 끌 수 없습니다.",
            [new("top (또는 ps aux) 로 CPU 를 많이 쓰는 프로그램 찾기", e => e.Ran("top") || e.Ran("ps", r => r.Args.Any(a => a.Contains("aux")))),
             new("ps -ef | grep cvd_monitor 로 PID 확인", e => e.Ran("grep", r => r.Args.Contains("cvd_monitor") && r.Output.Contains("3101"))),
             new("kill 3101 후 ps -ef | grep cvd 로 아직 살아 있는지 확인", e => e.KillIgnored >= 1 && e.Runs.SkipWhile(r => !(r.Name == "kill" && !r.Args.Contains("-9"))).Any(r => r.Name == "grep" && r.Output.Contains("3101"))),
             new("kill -9 3101 로 강제 종료", e => !e.ProcessAlive(3101))],
            "top<CR>ps -ef | grep cvd_monitor<CR>kill 3101<CR>ps -ef | grep cvd<CR>kill -9 3101<CR>"),

        new(A, "디스크와 폴더 용량", "df -h  du -sh *  sort -h",
            "df -h 는 디스크 전체 사용량(Use%)을 보여 줍니다. 90% 가 넘으면 정리가 필요합니다. du -sh 폴더 는 그 폴더의 크기, du -sh * | sort -h 는 크기 순서로 보여 줘서 무엇이 공간을 차지하는지 찾을 때 씁니다. 큰 코어 덤프(core.숫자)는 확인 후 지웁니다.",
            [new("df -h 로 디스크 사용량 확인", e => e.Ran("df")),
             new("du -sh * | sort -h 로 홈에서 큰 폴더 찾기", e => e.Ran("sort", r => Flags(r, "h") && r.Output.Contains("tmp"))),
             new("du -sh tmp/* 로 안을 보고 rm tmp/core.3101 로 코어 파일 지우기", e => !e.Exists("~/tmp/core.3101"))],
            "df -h<CR>du -sh * | sort -h<CR>du -sh tmp/*<CR>rm tmp/core.3101<CR>"),

        new(A, "찾아서 vi 로 고치기", "grep -l → vi → :wq",
            "실제 작업 흐름을 이어서 연습합니다. grep -l 로 고칠 파일을 찾고, vi 파일 로 열어 고친 뒤 :wq 로 저장합니다. 연습 터미널에서 vi 를 실행하면 아래 화면이 vi 편집기로 바뀌고, 종료하면 다시 터미널로 돌아옵니다 (vi 키는 [vi 편집기] 과제에서 연습할 수 있습니다).",
            [new("grep -l \"temp = 65\" recipes/* 로 temp 가 65 인 레시피 찾기", e => e.Ran("grep", r => Flags(r, "l") && r.Output.Contains("ETCH_A01.rcp"))),
             new("vi recipes/ETCH_A01.rcp 로 파일 열기", e => e.EditorOpens >= 1),
             new("/65 로 찾아가 cw 70 Esc 로 고치고 :wq", e => e.ReadFile("~/recipes/ETCH_A01.rcp")?.Contains("temp = 70") == true && !e.Editing),
             new("grep temp recipes/ETCH_A01.rcp 로 바뀐 값 확인", e => e.Ran("grep", r => r.Output.Contains("temp = 70")))],
            "grep -l \"temp = 65\" recipes/*<CR>vi recipes/ETCH_A01.rcp<CR>/65<CR>cw70<Esc>:wq<CR>grep temp recipes/ETCH_A01.rcp<CR>"),

        new(A, "자유 연습", "모든 명령",
            "배운 명령을 자유롭게 써 보세요. 예: find . -name \"*.log\" -mtime -1 | xargs grep -c ERROR,  ftp -i ftp-server 로 로그 받기,  awk 로 수량 합계,  vi notes.txt 로 메모 고치기. [처음부터]를 누르면 연습 서버가 처음 상태로 돌아갑니다. man 명령 · 명령 --help 로 짧은 설명을 볼 수 있습니다.",
            [], ""),
    ];

    public static IReadOnlyList<(string Section, (string Key, string Text)[] Items)> CheatSheet { get; } =
    [
        ("이동·목록", [("pwd", "지금 폴더 경로"), ("ls -la", "숨김 포함 자세한 목록"), ("ls -lt", "최근 수정 순"), ("cd 폴더  /  cd ..", "들어가기 / 위로"), ("cd ~  /  cd -", "홈 / 바로 전 폴더"), ("tree -L 2", "폴더 구조 2단계")]),
        ("파일 다루기", [("mkdir -p a/b", "중간 폴더까지 만들기"), ("cp -r 원본 대상", "복사 (폴더는 -r)"), ("mv 원본 대상", "이동 · 이름 바꾸기"), ("rm -i 파일", "확인하며 지우기"), ("rm -r 폴더", "폴더 지우기 (되돌릴 수 없음)"), ("touch 파일", "빈 파일 만들기")]),
        ("보기", [("cat 파일", "전체 출력"), ("head -n 5", "앞 5줄"), ("tail -n 20", "끝 20줄"), ("tail -f 로그", "새 줄 계속 보기 (Ctrl+C)"), ("less 파일", "쪽 단위 보기 (q 종료)"), ("wc -l", "줄 수")]),
        ("찾기", [("find . -name \"*.log\"", "이름으로 찾기 (따옴표!)"), ("find . -mtime -1", "하루 안에 바뀐 파일"), ("find . -size +100M", "100MB 보다 큰 파일"), ("… -exec 명령 {} \\;", "찾은 파일마다 실행"), ("grep -rn 단어 .", "폴더 전체 내용 찾기"), ("grep -i / -v / -c", "대소문자 무시 / 반대 / 개수"), ("grep -l / -A 2", "파일 이름만 / 뒤 2줄 같이")]),
        ("파이프·가공", [("명령 | 명령", "결과를 다음 명령으로"), ("> 파일  /  >> 파일", "저장(덮어쓰기) / 추가"), ("2>/dev/null", "오류 메시지 버리기"), ("sort | uniq -c", "종류별 개수"), ("cut -d , -f 2", "2번째 칸"), ("awk '{print $1}'", "칸 뽑기"), ("sed 's/a/b/g'", "글자 바꾸기 (-i 파일 수정)"), ("| xargs 명령", "목록을 인자로")]),
        ("권한·압축", [("chmod +x 파일", "실행 권한 주기"), ("chmod 644 파일", "rw-r--r--"), ("./스크립트.sh", "지금 폴더의 스크립트 실행"), ("tar czf a.tar.gz 폴더", "묶고 압축"), ("tar xzf a.tar.gz", "풀기 (-C 폴더)"), ("tar tzf a.tar.gz", "안의 목록"), ("gzip / gunzip", "파일 하나 압축 / 풀기")]),
        ("프로세스·디스크", [("ps -ef | grep 이름", "프로세스와 PID"), ("top", "CPU 많이 쓰는 순"), ("kill PID", "종료 요청"), ("kill -9 PID", "강제 종료"), ("df -h", "디스크 사용량"), ("du -sh * | sort -h", "큰 폴더 찾기")]),
        ("FTP·SFTP", [("ftp -i 서버", "접속 (mget 묻지 않음)"), ("ls / cd / pwd", "서버 쪽 목록 / 이동"), ("lcd 폴더 / !ls", "내 쪽 폴더 / 목록"), ("get · mget *.log", "받기 / 여러 개 받기"), ("put · mput", "올리기"), ("bin / prompt", "바이너리 모드 / 묻기 켜고 끄기"), ("bye", "종료"), ("sftp 사용자@서버", "SSH 로 접속 (lls lcd)"), ("scp 사용자@서버:/파일 .", "한 줄 복사")]),
        ("편리한 키", [("Tab / Tab Tab", "자동 완성 / 후보 목록"), ("↑ ↓", "이전 · 다음 명령"), ("Ctrl+R", "명령 기록 검색"), ("!!  /  !번호", "직전 / 그 번호 명령 다시"), ("Ctrl+C", "입력 · 실행 취소"), ("Ctrl+L", "화면 지우기"), ("Ctrl+A / Ctrl+E", "줄 맨 앞 / 맨 끝"), ("Ctrl+U / Ctrl+W", "앞부분 / 단어 지우기")]),
    ];
}
