using System.Text.RegularExpressions;

namespace EquipLink;

public enum PracticeLevel { Basic, Intermediate, Advanced }

public static class PracticeLevels
{
    public static string Name(PracticeLevel level) => level switch { PracticeLevel.Basic => "기초", PracticeLevel.Intermediate => "중급", _ => "고급" };
}

public sealed record ViStep(string Text, Func<ViEngine, bool> Done);

/// <summary>A practice lesson. <see cref="Solution"/> is one key sequence that completes every step.</summary>
public sealed record ViLesson(PracticeLevel Level, string Title, string Keys, string Explain, string Text, int Row, int Col, ViStep[] Steps, string Solution, string File = "");

public static class ViLessons
{
    private const string Log = "[INFO] boot start\n[WARN] fan speed low\n[INFO] load recipe\n[ERROR] chamber pressure\n[INFO] retry\n[ERROR] door open\n[INFO] done";
    private const string Recipe = """
        # 설비 레시피 예시 (연습용)
        [recipe]
        name = ETCH_A01
        chamber = 2
        temp = 65
        pressure = 120
        gas_flow = 30

        [steps]
        step1 = pump_down
        step2 = heat_up
        step3 = process
        step4 = vent

        [limits]
        temp_max = 80
        pressure_max = 150
        alarm = on

        [log]
        path = /var/log/etch
        level = info
        """;

    private const string Memo = "memo: 점검 일정\n- 09:00 장비 A\n- 13:00 장비 B";
    private const PracticeLevel B = PracticeLevel.Basic, I = PracticeLevel.Intermediate, A = PracticeLevel.Advanced;

    public static IReadOnlyList<ViLesson> All { get; } =
    [
        // ───────────── 기초 ─────────────
        new(B, "모드 이해", "i · Esc",
            "vi는 '명령 모드'에서 시작합니다. 이때 누르는 키는 글자가 아니라 명령입니다. i 를 누르면 입력 모드(화면 아래 -- INSERT --)가 되어 글자를 입력할 수 있고, Esc 를 누르면 다시 명령 모드로 돌아옵니다. 헷갈리면 일단 Esc 를 누르세요.",
            "vi는 모드가 있는 편집기입니다.\n커서가 있는 아래 빈 줄에 글자를 입력해 보세요.\n\n입력을 마치면 Esc 로 명령 모드로 돌아옵니다.", 2, 0,
            [new("i 를 눌러 입력 모드로 들어가기 (아래에 -- INSERT -- 표시)", e => e.Mode == ViMode.Insert),
             new("hello vi 를 입력하기", e => e.Lines.Count > 2 && e.Lines[2] == "hello vi"),
             new("Esc 를 눌러 명령 모드로 돌아오기", e => e.Mode == ViMode.Normal && e.Lines[2] == "hello vi")],
            "ihello vi<Esc>"),

        new(B, "커서 이동", "h j k l",
            "명령 모드에서 h ← · j ↓ · k ↑ · l → 로 커서를 움직입니다. 손을 키보드 가운데에 둔 채 이동할 수 있어 vi에서 가장 많이 쓰는 키입니다. 숫자를 먼저 누르면 그만큼 이동합니다 (예: 3j 는 세 줄 아래).",
            "h 왼쪽   j 아래   k 위   l 오른쪽\n..........\n..........\n.......*..\n..........", 1, 0,
            [new("j 로 * 가 있는 줄까지 내려가기", e => e.Current.Row == 3),
             new("l 로 오른쪽으로 가서 * 위에 커서 놓기", e => e.Current.Row == 3 && e.Current.Col == 7)],
            "jjlllllll"),

        new(B, "단어·줄 이동", "w b e 0 $",
            "w 는 다음 단어의 처음, b 는 이전 단어의 처음, e 는 단어의 끝으로 갑니다. 0 은 줄의 맨 앞, ^ 는 첫 글자, $ 는 줄의 맨 끝입니다. 긴 명령줄이나 설정 값을 고칠 때 l 을 여러 번 누르는 것보다 훨씬 빠릅니다.",
            "The quick brown fox jumps over the lazy dog", 0, 0,
            [new("w 를 여러 번 눌러 lazy 의 l 로 이동", e => e.Current.Col == 35),
             new("e 로 lazy 의 끝(y)으로 이동", e => e.Current.Col == 38),
             new("$ 로 줄 끝으로 이동", e => e.Current.Col == 42),
             new("0 으로 줄 맨 앞으로 이동", e => e.Current.Col == 0)],
            "wwwwwwwe$0"),

        new(B, "입력 위치 고르기", "a A o O I",
            "i 는 커서 앞, a 는 커서 뒤에 입력합니다. A 는 줄 끝, I 는 줄 맨 앞에서 입력을 시작합니다. o 는 아래에, O 는 위에 새 줄을 만들고 바로 입력 모드로 들어갑니다. 입력이 끝나면 항상 Esc 입니다.",
            "int count = 0\nreturn count;", 0, 0,
            [new("A 로 첫 줄 끝에 ; 붙이기 (A ; Esc)", e => e.Lines[0] == "int count = 0;" && e.Mode == ViMode.Normal),
             new("o 로 첫 줄 아래에 count++; 줄 추가", e => e.Lines.Count >= 3 && e.Lines.Contains("count++;") && e.Mode == ViMode.Normal),
             new("gg 로 첫 줄에 가서 O 로 맨 위에 // counter 줄 추가", e => e.Lines[0] == "// counter" && e.Mode == ViMode.Normal)],
            "A;<Esc>ocount++;<Esc>ggO// counter<Esc>"),

        new(B, "지우기", "x dd dw D",
            "x 는 커서 위 한 글자, dd 는 한 줄, dw 는 단어 하나, D 는 커서부터 줄 끝까지 지웁니다. d 는 '지우기' 명령이고 뒤에 이동 키를 붙여 범위를 정합니다 (d$, d0, dG …).",
            "이 문장에는 글자가가 하나 더 있습니다.\ndelete me\nremove extra extra word", 0, 0,
            [new("w · l 로 '가가' 로 가서 x 로 한 글자 지우기", e => e.Lines[0] == "이 문장에는 글자가 하나 더 있습니다."),
             new("j 로 내려가 dd 로 delete me 줄 지우기", e => !e.Lines.Contains("delete me") && e.Lines.Count == 2),
             new("w 로 extra 로 가서 dw 로 중복 단어 지우기", e => e.Lines.Contains("remove extra word"))],
            "wwllxjddwdw"),

        new(B, "복사·붙여넣기", "yy dd p P",
            "yy 는 줄 복사, yw 는 단어 복사입니다. dd 로 지운 내용도 보관되므로 잘라내기처럼 쓸 수 있습니다. p 는 커서 아래(뒤)에, P 는 위(앞)에 붙여넣습니다. 'dd p' 는 두 줄의 순서를 바꾸는 단골 조합입니다.",
            "B 줄\nA 줄\nC 줄", 0, 0,
            [new("dd 후 p 로 두 줄의 순서 바꾸기 (A, B, C 순서로)", e => e.Lines.Count >= 3 && e.Lines[0] == "A 줄" && e.Lines[1] == "B 줄"),
             new("C 줄로 가서 yy 후 p 로 한 줄 더 붙이기", e => e.Lines.Count == 4 && e.Lines[2] == "C 줄" && e.Lines[3] == "C 줄")],
            "ddpjyyp"),

        new(B, "고쳐 쓰기", "cw r ~",
            "cw 는 단어를 지우고 바로 입력 모드로 들어갑니다 (change word). r 다음에 글자를 누르면 커서 위 한 글자만 바꿉니다. ~ 는 대문자·소문자를 바꿉니다. f 다음에 글자를 누르면 그 글자로 바로 이동합니다.",
            "teh quick brovn fox", 0, 0,
            [new("cw 로 teh 를 the 로 바꾸기 (cw the Esc)", e => e.Lines[0].StartsWith("the ") && e.Mode == ViMode.Normal),
             new("f v 로 v 로 이동해 r w 로 한 글자 바꾸기", e => e.Lines[0].Contains("brown")),
             new("0 으로 줄 앞에 가서 ~ 로 첫 글자를 대문자로", e => e.Lines[0] == "The quick brown fox")],
            "cwthe<Esc>fvrw0~"),

        new(B, "되돌리기와 반복", "u Ctrl+r .",
            "u 는 마지막 변경을 되돌리고 Ctrl+r 은 되돌린 것을 다시 실행합니다. . (점)은 방금 한 변경을 그대로 반복합니다. 같은 수정을 여러 곳에 할 때 아주 유용합니다.",
            "line 1\nline 2\nline 3\nline 4\nline 5", 0, 0,
            [new("dd 로 한 줄 지우고 . 으로 한 번 더 지우기", e => e.Lines.Count == 3 && e.RepeatCount >= 1),
             new("u 를 두 번 눌러 모두 되돌리기", e => e.Lines.Count == 5 && e.UndoCount >= 2),
             new("Ctrl+r 로 한 번 다시 실행하기", e => e.Lines.Count == 4 && e.RedoCount >= 1)],
            "dd.uu<C-r>"),

        new(B, "저장과 종료", ":w :q :q! :wq",
            ":w 는 저장, :q 는 종료입니다. 저장하지 않은 변경이 있으면 :q 는 경고(E37)를 보여 줍니다. :q! 는 변경을 버리고 종료, :wq (또는 :x, ZZ) 는 저장 후 종료합니다. 연습창은 실제 파일을 만들거나 지우지 않습니다.",
            Memo, 0, 0,
            [new("x 등으로 아무 곳이나 고친 뒤 :w 로 저장", e => e.SaveCount >= 1 && !e.Modified && e.Text != Memo),
             new("다시 고친 뒤 :q 로 종료를 시도해 경고 확인", e => e.QuitBlockedCount >= 1),
             new(":q! 로 변경을 버리고 종료", e => e.DiscardCount >= 1),
             new(":wq 로 저장 후 종료", e => e.SaveCount >= 2)],
            "x:w<CR>x:q<CR>:q!<CR>:wq<CR>"),

        // ───────────── 중급 ─────────────
        new(I, "검색", "/ n N *",
            "/ 다음에 찾을 말을 입력하고 Enter 를 누르면 아래로 검색합니다 (? 는 위로). n 은 다음, N 은 이전 결과로 갑니다. * 는 커서 아래 단어를 바로 검색합니다. 로그 파일에서 오류를 찾을 때 자주 씁니다. :noh 로 강조를 끕니다.",
            Log, 0, 0,
            [new("/ERROR 입력 후 Enter 로 첫 오류 줄로 이동", e => e.Current.Row == 3 && e.SearchCount >= 1),
             new("n 으로 다음 ERROR 로 이동", e => e.Current.Row == 5),
             new("N 으로 이전 ERROR 로 돌아가기", e => e.Current.Row == 3)],
            "/ERROR<CR>nN"),

        new(I, "바꾸기 (치환)", ":s  :%s",
            ":s/찾을말/바꿀말/ 은 현재 줄에서 처음 하나를, 끝에 g 를 붙이면 그 줄 전체를 바꿉니다. 앞에 % 를 붙인 :%s/찾을말/바꿀말/g 는 파일 전체를 바꿉니다. 설정 파일의 값을 한 번에 바꿀 때 씁니다.",
            "set temp = 100\nset limit = 120\nprint temp\nlog temp", 0, 0,
            [new(":s/set/SET/ 으로 현재 줄(첫 줄)만 바꾸기", e => e.Lines[0].StartsWith("SET") && e.Lines[1].StartsWith("set")),
             new(":%s/temp/TEMP/g 로 모든 temp 를 바꾸기", e => !e.Text.Contains("temp") && e.Text.Contains("TEMP"))],
            ":s/set/SET/<CR>:%s/temp/TEMP/g<CR>"),

        new(I, "화면 분할", ":vsp  :sp  Ctrl+w",
            ":vsp 는 화면을 좌우로, :sp 는 위아래로 나눕니다. 같은 파일의 다른 부분을 나란히 볼 수 있습니다. Ctrl+w 다음에 w 를 누르면 다음 창으로, h j k l 을 누르면 그 방향 창으로 이동합니다. :q 는 현재 창을 닫고 :only 는 현재 창만 남깁니다.",
            Recipe, 0, 0,
            [new(":vsp 로 화면을 좌우로 나누기", e => e.Windows.Count >= 2 && e.Root is ViSplit { Vertical: true }),
             new("Ctrl+w w 로 옆 창으로 이동하기", e => e.WindowSwitches >= 1),
             new(":sp 로 현재 창을 위아래로 나누기", e => e.Windows.Count >= 3),
             new("Ctrl+w h (또는 j k l) 로 방향을 골라 이동하기", e => e.WindowSwitches >= 2),
             new(":only 로 창 하나만 남기기", e => e.Windows.Count == 1)],
            ":vsp<CR><C-w>w:sp<CR><C-w>h:only<CR>"),

        new(I, "선택해서 편집 (비주얼)", "v V d y >",
            "v 는 글자 단위, V 는 줄 단위로 선택을 시작합니다. 이동 키로 범위를 넓힌 뒤 d (지우기) · y (복사) · > (들여쓰기) · c (바꾸기)를 누르면 선택한 부분에 적용됩니다. >> 는 현재 줄을 들여씁니다.",
            "keep 1\nremove A\nremove B\nkeep 2\nindent me", 0, 0,
            [new("j 로 remove A 로 가서 V j d 로 두 줄 지우기", e => !e.Text.Contains("remove") && e.Lines.Count == 3),
             new("indent me 줄로 가서 >> 로 들여쓰기", e => e.Lines[^1] == "    indent me")],
            "jVjdj>>"),

        new(I, "숫자로 반복", "3j 3dd G gg",
            "대부분의 명령 앞에 숫자를 붙이면 그만큼 반복합니다. 3j 는 세 줄 아래, 3dd 는 세 줄 삭제, 5x 는 다섯 글자 삭제입니다. G 는 마지막 줄, gg 는 첫 줄, 숫자G 나 :숫자 는 그 줄로 갑니다. :set nu 로 줄 번호를 켜 보세요.",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"줄 {i}")), 0, 0,
            [new("3j 로 세 줄 아래(줄 4)로 이동", e => e.Current.Row == 3 && e.Lines.Count == 10),
             new("3dd 로 줄 4 ~ 6 을 한 번에 지우기", e => e.Lines.Count == 7 && e.Lines[3] == "줄 7"),
             new("G 로 마지막 줄로 이동", e => e.Current.Row == e.Lines.Count - 1),
             new("gg 로 첫 줄로 이동", e => e.Current.Row == 0)],
            "3j3ddGgg"),

        new(I, "텍스트 객체", "ciw  ci\"  di(  da{",
            "d · c · y 뒤에 i(안쪽) 또는 a(바깥까지)와 대상을 붙이면 커서가 그 안 어디에 있든 한 번에 고릅니다. ciw 는 커서 아래 단어 바꾸기, ci\" 는 따옴표 안 바꾸기, di( 는 괄호 안 지우기, da{ 는 중괄호까지 통째로 지우기입니다. viw · vi( 처럼 선택(v)에도 씁니다.",
            "name = \"ETCH_A01\"\nset_temp(65, unit)\ncall(legacy_mode)", 0, 0,
            [new("f\" 로 따옴표로 가서 ci\" 로 ETCH_A01 을 ETCH_B07 로 바꾸기", e => e.Lines[0] == "name = \"ETCH_B07\"" && e.Mode == ViMode.Normal),
             new("j 로 내려가 괄호 안 아무 데서나 di( 로 괄호 안 지우기", e => e.Lines[1] == "set_temp()"),
             new("j 로 내려가 legacy_mode 위에서 ciw 로 safe 로 바꾸기", e => e.Lines[2] == "call(safe)" && e.Mode == ViMode.Normal)],
            "f\"ci\"ETCH_B07<Esc>jdi(jciwsafe<Esc>"),

        new(I, "마크로 돌아오기", "ma  'a  `a  d'a",
            "m 다음에 a~z 를 누르면 지금 위치에 마크(책갈피)를 표시합니다. 멀리 이동한 뒤 'a 는 그 줄로, `a 는 정확한 칸으로 돌아옵니다. '' 는 바로 전에 뛰어오기 전 위치입니다. d'a 처럼 연산자와 함께 쓰면 '여기부터 마크까지'를 한 번에 지웁니다. :marks 로 목록을 봅니다.",
            "[recipe]\nname = ETCH_A01\ntemp = 65\n# --- 임시 디버그 설정 시작 ---\ndebug = on\ntrace = on\ndump = /tmp/etch.dmp\n# --- 임시 디버그 설정 끝 ---\npressure = 120\ngas_flow = 30\n[limits]\ntemp_max = 80", 0, 0,
            [new("3번째 줄(temp)로 가서 ma 로 마크 a 표시", e => e.Mark('a')?.Row == 2),
             new("G 로 맨 끝에 갔다가 'a 로 마크 줄로 돌아오기", e => e.MarkJumps >= 1 && e.Current.Row == 2),
             new("디버그 시작 줄에서 mb, 끝 줄로 가서 d'b 로 디버그 설정 지우기", e => !e.Text.Contains("debug = on") && e.Lines.Count == 7)],
            "jjmaG'ajmb4jd'b"),

        new(I, "보기 설정", ":set nu rnu list ic",
            ":set 으로 화면과 검색 방식을 바꿉니다. nu 줄 번호, rnu 상대 줄 번호(지금 줄에서 몇 줄 떨어졌는지라 5j 처럼 이동할 때 편합니다), cul 현재 줄 강조, list 줄 끝($)과 뒤쪽 공백(·) 보기, ic 검색할 때 대소문자 무시입니다. 끌 때는 :set nonu 처럼 no 를 붙이고, :set sw? 처럼 ? 를 붙이면 지금 값을 봅니다.",
            "2026-11-21 08:00 [INFO] boot start   \n2026-11-21 08:10 [WARN] fan speed low\n2026-11-21 08:20 [ERROR] sensor timeout\n2026-11-21 08:30 [INFO] recipe loaded  \n2026-11-21 08:40 [error] chamber pressure", 0, 0,
            [new(":set nu rnu 로 줄 번호와 상대 줄 번호 켜기", e => e.ShowNumbers && e.RelativeNumbers),
             new(":set list 로 줄 끝 공백(·) 확인", e => e.ListChars),
             new(":set ic 를 켜고 /error 로 대소문자 상관없이 찾기 (3번째 줄)", e => e.IgnoreCase && e.SearchCount >= 1 && e.Current.Row == 2),
             new(":set nonu nornu nolist noic 로 원래대로", e => !e.ShowNumbers && !e.RelativeNumbers && !e.ListChars && !e.IgnoreCase)],
            ":set nu rnu<CR>:set list<CR>:set ic<CR>/error<CR>:set nonu nornu nolist noic<CR>"),

        new(I, "줄 옮기기", ":m  :t  :2,4m $",
            ":m 숫자 는 현재 줄을 그 줄 아래로 옮기고(:m 0 은 맨 위로), :t 숫자 는 복사합니다(:t . 은 바로 아래에 복제). :2,4m $ 처럼 범위를 앞에 붙이면 여러 줄을 한 번에 옮깁니다. ddp · ddkP 로 한 줄씩 옮기는 것보다 정확합니다.",
            "step3 = process\nstep1 = pump_down\nstep2 = heat_up\nstep4 = vent", 0, 0,
            [new("2번째 줄(step1)에서 :m 0 으로 맨 위로 옮기기", e => e.Lines[0] == "step1 = pump_down"),
             new("step2 줄로 가서 :m 1 로 step1 아래로 옮기기 (step1~4 순서)", e => e.Lines.Take(4).SequenceEqual(["step1 = pump_down", "step2 = heat_up", "step3 = process", "step4 = vent"])),
             new(":t $ 로 지금 줄을 맨 아래에 복사하기", e => e.Lines.Count == 5)],
            "j:m 0<CR>jj:m 1<CR>:t $<CR>"),

        new(I, "입력 중 자동 완성", "Ctrl+n  Ctrl+p",
            "입력 모드에서 단어 앞부분만 치고 Ctrl+n 을 누르면 파일 안에 있는 단어로 자동 완성합니다. 다시 누르면 다음 후보, Ctrl+p 는 이전 후보입니다. 긴 변수 이름이나 설정 이름을 틀리지 않고 입력할 때 씁니다.",
            "chamber_pressure_limit = 150\nchamber_temperature_limit = 80\n", 2, 0,
            [new("i 로 들어가 cha 까지 치고 Ctrl+n 으로 단어 완성", e => e.CompletionCount >= 1 && e.Lines[^1].StartsWith("chamber_")),
             new("Ctrl+n 을 한 번 더 눌러 temperature 쪽으로 바꾸고 ' = 100' 입력 후 Esc", e => e.Lines[^1] == "chamber_temperature_limit = 100" && e.Mode == ViMode.Normal)],
            "icha<C-n><C-n> = 100<Esc>"),

        // ───────────── 고급 ─────────────
        new(A, "매크로 녹화", "qa … q  @a  @@  4@a",
            "q 다음에 a~z 를 누르면 그때부터 누르는 키를 녹화합니다 (화면 아래 recording @a). 다시 q 를 누르면 녹화가 끝나고 @a 로 그대로 다시 실행합니다. @@ 는 마지막 매크로를 한 번 더, 4@a 는 네 번 실행합니다. 끝에 j 처럼 다음 줄로 가는 키를 넣어 두면 여러 줄을 한 번에 처리하고, 마지막 줄에서 j 가 실패하면 자동으로 멈춥니다.",
            "EQ01 fan_speed\nEQ02 fan_speed\nEQ03 fan_speed\nEQ04 fan_speed\nEQ05 fan_speed\nEQ06 fan_speed", 0, 0,
            [new("qa → I set␣ Esc → A =on; Esc → j → q 로 첫 줄을 고치며 녹화", e => e.MacroRecords >= 1 && e.Lines[0] == "set EQ01 fan_speed=on;"),
             new("@a 로 둘째 줄에 한 번 실행", e => e.MacroRuns >= 1 && e.Lines[1] == "set EQ02 fan_speed=on;"),
             new("4@a (또는 @@ 반복)로 나머지 줄 모두 처리", e => e.Lines.All(l => Regex.IsMatch(l, @"^set EQ0\d fan_speed=on;$")))],
            "qaIset <Esc>A=on;<Esc>jq@a4@a"),

        new(A, "레지스터", "\"ayy  \"Ayy  \"ap  :reg",
            "복사·삭제한 내용은 레지스터(이름 붙은 클립보드)에 들어갑니다. 명령 앞에 \"와 이름을 붙이면 그 레지스터를 씁니다: \"ayy 는 a 에 복사, \"ap 는 a 에서 붙여넣기, 대문자 \"Ayy 는 a 뒤에 덧붙이기입니다. dd 를 해도 \"0 에는 마지막으로 y 한 내용이 남아 \"0p 로 붙여넣을 수 있습니다. :reg 로 내용을 봅니다.",
            "# 공통 설정\ngas = N2\npump = on\n# EQ01\ntemp = 65\n# EQ02\ntemp = 70", 0, 0,
            [new("2번째 줄(gas = N2)에서 \"ayy 로 레지스터 a 에 복사", e => e.RegisterText('a') == "gas = N2"),
             new("3번째 줄(pump = on)에서 \"Ayy 로 a 에 덧붙이기", e => e.RegisterText('a') == "gas = N2\npump = on"),
             new("# EQ01 줄로 가서 \"ap 로 두 줄 붙여넣기", e => e.Lines.Count(l => l == "gas = N2") == 2 && e.Lines.Count(l => l == "pump = on") == 2),
             new("# EQ02 줄에서도 \"ap 로 붙여넣고 :reg a 로 확인", e => e.Lines.Count(l => l == "gas = N2") == 3 && e.RegisterViews >= 1)],
            "j\"ayyj\"Ayyj\"apjjj\"ap:reg a<CR>"),

        new(A, "자동 정렬", "=  ==  gg=G  V=",
            "= 는 들여쓰기를 { } 괄호 깊이에 맞춰 자동으로 정리합니다. == 는 현재 줄, V 로 여러 줄을 고른 뒤 = 는 고른 줄, gg=G 는 파일 전체입니다. 한 단계 칸 수는 :set sw=4 (shiftwidth) 로 정합니다. 복사해 온 코드나 남이 짠 스크립트의 들여쓰기가 엉망일 때 씁니다.",
            "int check(int temp)\n{\nif (temp > 80) {\nprintf(\"over\\n\");\nreturn 1;\n}\n        return 0;\n}", 0, 0,
            [new("3번째 줄로 가서 V 와 jjj 로 4줄을 고르고 = 로 정렬", e => e.Lines[2] == "    if (temp > 80) {" && e.Lines[3] == "        printf(\"over\\n\");"),
             new("gg=G 로 파일 전체 정렬 (return 0; 줄도 맞춰짐)", e => e.Lines[6] == "    return 0;" && e.IndentOps >= 2),
             new(":set sw=2 후 다시 gg=G 로 2칸 들여쓰기", e => e.ShiftWidth == 2 && e.Lines[2] == "  if (temp > 80) {")],
            "jjVjjj=gg=G:set sw=2<CR>gg=G", "check.c"),

        new(A, "블록 선택", "Ctrl+v  I  A  $A  d",
            "Ctrl+v 는 네모(블록) 모양으로 고릅니다. 여러 줄의 같은 칸을 고르고 I 로 앞에, A 로 뒤에 글자를 넣은 뒤 Esc 를 누르면 고른 모든 줄에 한 번에 들어갑니다 (여러 줄 주석 처리). $ 를 누른 뒤 A 를 쓰면 길이가 다른 줄 끝마다 붙고, d 는 고른 네모만 지웁니다. PuTTY 에서도 Ctrl+v 는 vi 로 전달됩니다.",
            "debug = on\ntrace = on\ndump = on\nmode = auto\nspeed = 3", 0, 0,
            [new("Ctrl+v 와 jj 로 세 줄을 고르고 I #␣ Esc 로 주석 처리", e => e.Lines.Take(3).All(l => l.StartsWith("# ")) && e.Mode == ViMode.Normal),
             new("4번째 줄에서 Ctrl+v j $ 로 끝까지 고르고 A ; Esc 로 줄 끝에 ; 붙이기", e => e.Lines[3] == "mode = auto;" && e.Lines[4] == "speed = 3;"),
             new("gg 에서 Ctrl+v jj l 로 '# ' 두 칸을 고르고 d 로 주석 풀기", e => e.Lines[0] == "debug = on" && e.Lines[2] == "dump = on" && e.BlockInserts >= 2)],
            "<C-v>jjI# <Esc>jjj<C-v>j$A;<Esc>gg<C-v>jjld"),

        new(A, "색 구성표와 문법 강조", ":colo ron  :syntax off",
            ":colorscheme 이름 (줄여서 :colo) 으로 색 구성표를 바꿉니다. ron · desert · elflord · morning(밝은 바탕) · peachpuff 등이 있고, :colo 다음에 공백을 넣고 Tab 을 누르면 이름이 차례로 나옵니다. :syntax off 는 문법 색을 끄고 :syntax on 은 다시 켭니다. 실제 서버의 vim 도 같은 명령이며, 계속 쓰려면 ~/.vimrc 에 적습니다.",
            "#!/bin/bash\n# 알람 개수를 세는 스크립트 (연습용)\nLOG=/home/user/logs/etch_20261123.log\nCOUNT=$(grep -c ERROR \"$LOG\")\nif [ \"$COUNT\" -gt 3 ]; then\n    echo \"알람이 많습니다: $COUNT\"\nfi", 0, 0,
            [new(":colo ron 으로 색 구성표 바꾸기", e => e.ColorScheme == "ron" && e.ColorChanges >= 1),
             new(":colo␣ 다음 Tab 으로 다른 이름을 골라 바꾸기 (예: desert)", e => e.ColorChanges >= 2 && e.ColorScheme is not ("ron" or "default")),
             new(":syntax off 로 문법 색 끄기", e => !e.SyntaxOn),
             new(":syntax on 과 :colo default 로 원래대로", e => e.SyntaxOn && e.ColorScheme == "default" && e.SyntaxChanges >= 2)],
            ":colo ron<CR>:colo desert<CR>:syntax off<CR>:syntax on<CR>:colo default<CR>", "monitor.sh"),

        new(A, "정규식으로 바꾸기", ":%s/\\s\\+$//  \\(\\)  \\1",
            "찾을 말에 정규식을 쓸 수 있습니다. ^ 줄 맨 앞, $ 줄 끝, . 아무 글자, \\d 숫자, \\w 글자·숫자, \\s 공백, \\+ 1번 이상, * 0번 이상입니다. \\( \\) 로 묶은 부분은 바꿀 말에서 \\1, \\2 로 다시 쓰고 & 는 찾은 글자 전체입니다. :%s/^/# / 는 모든 줄 앞에 # 를 붙이고, 끝에 n 플래그(:%s/x//gn)를 붙이면 바꾸지 않고 개수만 셉니다.",
            "temp=65   \npressure=120\ngas_flow=30  \nrf_power=500", 0, 0,
            [new(":%s/\\s\\+$// 로 줄 끝 공백 지우기", e => !e.Lines.Any(l => l.EndsWith(' '))),
             new(":%s/=/ = / 로 = 양쪽에 공백 넣기", e => e.Lines[0] == "temp = 65"),
             new(":%s/^\\(\\w\\+\\) = \\(\\d\\+\\)$/set \\1 \\2/ 로 'set 이름 값' 형식 만들기", e => e.Lines[0] == "set temp 65" && e.Lines[3] == "set rf_power 500"),
             new(":%s/^/# / 로 모든 줄 주석 처리", e => e.Lines.All(l => l.StartsWith("# set ")))],
            ":%s/\\s\\+$//<CR>:%s/=/ = /<CR>:%s/^\\(\\w\\+\\) = \\(\\d\\+\\)$/set \\1 \\2/<CR>:%s/^/# /<CR>"),

        new(A, "전역 명령", ":g/패턴/d  :v  :g/^/m0  :%norm",
            ":g/패턴/명령 은 패턴이 있는 모든 줄에 명령을 실행합니다. :g/DEBUG/d 는 DEBUG 줄을 모두 지우고, :v/ERROR/d (= :g!) 는 ERROR 가 없는 줄을 지워 오류만 남깁니다. :g/^/m0 은 줄 순서를 거꾸로 뒤집습니다. :%norm A; 처럼 :normal 을 쓰면 모든 줄에서 일반 모드 키를 실행합니다.",
            "08:00 [INFO] boot\n08:01 [DEBUG] init ok\n08:05 [ERROR] ALM-1203 pressure\n08:06 [DEBUG] retry 1\n08:10 [INFO] recipe loaded\n08:12 [ERROR] ALM-2101 door open\n08:15 [DEBUG] retry 2\n08:20 [INFO] done", 0, 0,
            [new(":g/DEBUG/d 로 DEBUG 줄 모두 지우기", e => !e.Text.Contains("DEBUG") && e.Lines.Count == 5),
             new(":g/^/m0 으로 줄 순서 뒤집기 (최근 로그가 위로)", e => e.Lines[0] == "08:20 [INFO] done"),
             new(":v/ERROR/d 로 ERROR 줄만 남기기", e => e.Lines.Count == 2 && e.Lines.All(l => l.Contains("ERROR"))),
             new(":%norm A [checked] 로 모든 줄 끝에 표시 붙이기", e => e.NormalCount >= 1 && e.Lines.All(l => l.EndsWith(" [checked]")))],
            ":g/DEBUG/d<CR>:g/^/m0<CR>:v/ERROR/d<CR>:%norm A [checked]<CR>"),

        new(A, "숫자 늘리기", "Ctrl+a  Ctrl+x  g Ctrl+a",
            "Ctrl+a 는 커서 뒤 첫 숫자를 1 늘리고 Ctrl+x 는 1 줄입니다. 30 Ctrl+a 처럼 숫자를 앞에 붙이면 그만큼 바뀝니다. V 로 여러 줄을 고르고 g Ctrl+a 를 누르면 01, 02, 03 … 처럼 차례로 늘어난 번호가 됩니다. 매크로와 함께 쓰면 번호 붙은 설정 목록을 금방 만듭니다.",
            "timeout = 30\nretry = 3\nwafer_00\nwafer_00\nwafer_00\nwafer_00", 0, 0,
            [new("첫 줄에서 30 Ctrl+a 로 timeout 을 60 으로", e => e.Lines[0] == "timeout = 60"),
             new("둘째 줄에서 Ctrl+x 로 retry 를 2 로", e => e.Lines[1] == "retry = 2"),
             new("wafer 네 줄을 V 로 고르고 g Ctrl+a 로 01~04 번호 매기기", e => e.Lines.Skip(2).SequenceEqual(["wafer_01", "wafer_02", "wafer_03", "wafer_04"]))],
            "30<C-a>j<C-x>jVjjjg<C-a>"),

        new(A, "설정 파일 .vimrc", "~/.vimrc  :so %",
            "vim 을 켤 때마다 읽는 설정 파일이 ~/.vimrc 입니다. 자주 쓰는 :set 명령을 앞의 : 없이 한 줄에 하나씩 적어 두면 매번 입력하지 않아도 됩니다. \" 로 시작하는 줄은 주석입니다. 편집 중인 설정을 바로 적용하려면 :so % (source) 를 입력합니다. 실제 서버에서는 vi ~/.vimrc 로 이 파일을 엽니다.",
            "\" 나만의 vim 설정 (연습용)\nset number\nsyntax on", 2, 0,
            [new("o 로 아래에 set cursorline 줄 추가", e => e.Lines.Contains("set cursorline")),
             new("o 로 한 줄 더: colorscheme desert", e => e.Lines.Contains("colorscheme desert")),
             new(":w 로 저장하고 :so % 로 설정 적용 (줄 번호·현재 줄·색이 바뀜)", e => e.Sources >= 1 && e.SaveCount >= 1 && e.ShowNumbers && e.CursorLine && e.ColorScheme == "desert")],
            "oset cursorline<Esc>ocolorscheme desert<Esc>:w<CR>:so %<CR>", ".vimrc"),

        new(A, "자유 연습", "모든 키",
            "배운 키를 자유롭게 써 보세요. :vsp 로 나누고, /temp 로 검색하고, :%s 로 값을 바꾸고, qa 로 매크로를 녹화해 보세요. :e! 는 마지막 저장 상태로 되돌리고, [처음부터]는 연습 문서를 새로 엽니다. 색을 원래대로 돌리려면 :colo default 입니다.",
            Recipe, 0, 0, [], "", "recipe.conf"),
    ];

    /// <summary>Key reference shown beside the editor.</summary>
    public static IReadOnlyList<(string Section, (string Key, string Text)[] Items)> CheatSheet { get; } =
    [
        ("모드 바꾸기", [("Esc", "명령 모드로 (모를 땐 일단 Esc)"), ("i  /  a", "커서 앞 / 뒤에 입력"), ("I  /  A", "줄 맨 앞 / 줄 끝에 입력"), ("o  /  O", "아래 / 위에 새 줄을 만들고 입력"), ("v  /  V", "글자 / 줄 단위 선택"), ("Ctrl+v", "네모(블록) 선택"), (":", "명령줄 (저장·치환·분할 등)")]),
        ("이동", [("h j k l", "← ↓ ↑ →"), ("w  /  b", "다음 / 이전 단어 처음"), ("e", "단어 끝"), ("0  ^  $", "줄 맨 앞 / 첫 글자 / 줄 끝"), ("gg  /  G", "첫 줄 / 마지막 줄"), ("12G  /  :12", "12번째 줄로"), ("f{글자}", "줄에서 그 글자로 이동 (; 다음)"), ("%", "짝이 되는 괄호로"), ("Ctrl+d / Ctrl+u", "반 화면 아래 / 위"), ("zz", "지금 줄을 화면 가운데로")]),
        ("지우기·바꾸기", [("x", "글자 하나 지우기"), ("dd", "줄 지우기"), ("dw  /  D", "단어 / 줄 끝까지 지우기"), ("cw  /  cc", "단어 / 줄을 지우고 입력"), ("r{글자}", "한 글자 바꾸기"), ("~  /  gUiw", "대소문자 바꾸기 / 단어를 대문자로"), ("J", "아래 줄을 이어 붙이기"), (">>  /  <<", "들여쓰기 / 내어쓰기")]),
        ("텍스트 객체", [("ciw  /  daw", "단어 바꾸기 / 공백까지 지우기"), ("ci\"  /  ci'", "따옴표 안 바꾸기"), ("di(  /  da(", "괄호 안 / 괄호까지 지우기"), ("ci{  /  yi[", "중괄호 안 바꾸기 / 대괄호 안 복사"), ("dip", "문단(빈 줄로 나뉜 덩어리) 지우기")]),
        ("복사·붙여넣기", [("yy  /  yw", "줄 / 단어 복사"), ("p  /  P", "아래(뒤) / 위(앞)에 붙여넣기"), ("v → y", "선택한 부분 복사"), ("\"ayy  /  \"ap", "레지스터 a 에 복사 / 붙여넣기"), ("\"Ayy", "레지스터 a 에 덧붙이기"), ("\"0p", "마지막으로 복사(y)한 것"), (":reg", "레지스터 목록")]),
        ("되돌리기·반복·매크로", [("u", "되돌리기"), ("Ctrl+r", "다시 실행"), (".", "마지막 변경 반복"), ("3dd · 5x", "숫자를 붙여 반복"), ("qa … q", "매크로 a 녹화 시작 / 끝"), ("@a  /  @@", "매크로 실행 / 마지막 매크로 다시"), ("10@a", "매크로 10번 실행")]),
        ("마크·자동 완성", [("ma", "지금 위치에 마크 a"), ("'a  /  `a", "마크 줄 / 정확한 위치로"), ("''", "바로 전 위치로 돌아가기"), ("Ctrl+n / Ctrl+p", "(입력 중) 단어 자동 완성"), ("Ctrl+a / Ctrl+x", "숫자 1 늘리기 / 줄이기")]),
        ("정렬·블록", [("==  /  gg=G", "현재 줄 / 파일 전체 자동 정렬"), ("V → =", "고른 줄 정렬"), ("Ctrl+v → I", "여러 줄 앞에 한 번에 입력"), ("Ctrl+v → $A", "여러 줄 끝에 한 번에 입력"), ("V → g Ctrl+a", "고른 줄에 1, 2, 3… 번호")]),
        ("검색·치환", [("/단어", "아래로 검색 (Enter)"), ("?단어", "위로 검색"), ("n  /  N", "다음 / 이전 결과"), ("*", "커서 아래 단어 검색"), (":noh", "검색 강조 끄기"), (":s/a/b/", "현재 줄의 a 를 b 로"), (":%s/a/b/g", "파일 전체의 a 를 모두 b 로"), (":%s/\\s\\+$//", "줄 끝 공백 지우기"), (":%s/x//gn", "x 개수만 세기")]),
        ("여러 줄 명령", [(":g/a/d", "a 가 있는 줄 모두 지우기"), (":v/a/d", "a 가 없는 줄 모두 지우기"), (":g/^/m0", "줄 순서 뒤집기"), (":%norm A;", "모든 줄 끝에 ; 붙이기"), (":m 0  /  :t .", "줄을 맨 위로 / 아래에 복제"), (":sort  /  :sort u", "정렬 / 중복 제거 정렬")]),
        ("화면 분할", [(":vsp", "좌우로 나누기"), (":sp", "위아래로 나누기"), ("Ctrl+w w", "다음 창으로"), ("Ctrl+w h j k l", "그 방향 창으로"), ("Ctrl+w q  /  :q", "현재 창 닫기"), (":only", "현재 창만 남기기")]),
        ("저장·종료", [(":w", "저장"), (":q", "종료 (변경 있으면 경고)"), (":wq  :x  ZZ", "저장 후 종료"), (":q!", "저장하지 않고 종료"), (":e!", "마지막 저장 상태로 되돌리기")]),
        ("설정·색", [(":set nu / rnu", "줄 번호 / 상대 줄 번호"), (":set list", "줄 끝($)·뒤쪽 공백(·) 보기"), (":set ic / cul", "검색 대소문자 무시 / 현재 줄 강조"), (":set sw=4", "들여쓰기 한 단계 칸 수"), (":colo ron", "색 구성표 바꾸기 (Tab 으로 고르기)"), (":syntax off", "문법 색 끄기 (on 켜기)"), (":so %", "편집 중인 .vimrc 적용"), ("Ctrl+g", "파일 이름과 현재 줄 보기")]),
    ];
}
