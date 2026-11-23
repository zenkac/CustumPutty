namespace EquipLink;

public sealed record ViStep(string Text, Func<ViEngine, bool> Done);

/// <summary>A practice lesson. <see cref="Solution"/> is one key sequence that completes every step.</summary>
public sealed record ViLesson(string Title, string Keys, string Explain, string Text, int Row, int Col, ViStep[] Steps, string Solution);

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

    public static IReadOnlyList<ViLesson> All { get; } =
    [
        new("모드 이해", "i · Esc",
            "vi는 '명령 모드'에서 시작합니다. 이때 누르는 키는 글자가 아니라 명령입니다. i 를 누르면 입력 모드(화면 아래 -- INSERT --)가 되어 글자를 입력할 수 있고, Esc 를 누르면 다시 명령 모드로 돌아옵니다. 헷갈리면 일단 Esc 를 누르세요.",
            "vi는 모드가 있는 편집기입니다.\n커서가 있는 아래 빈 줄에 글자를 입력해 보세요.\n\n입력을 마치면 Esc 로 명령 모드로 돌아옵니다.", 2, 0,
            [new("i 를 눌러 입력 모드로 들어가기 (아래에 -- INSERT -- 표시)", e => e.Mode == ViMode.Insert),
             new("hello vi 를 입력하기", e => e.Lines.Count > 2 && e.Lines[2] == "hello vi"),
             new("Esc 를 눌러 명령 모드로 돌아오기", e => e.Mode == ViMode.Normal && e.Lines[2] == "hello vi")],
            "ihello vi<Esc>"),

        new("커서 이동", "h j k l",
            "명령 모드에서 h ← · j ↓ · k ↑ · l → 로 커서를 움직입니다. 손을 키보드 가운데에 둔 채 이동할 수 있어 vi에서 가장 많이 쓰는 키입니다. 숫자를 먼저 누르면 그만큼 이동합니다 (예: 3j 는 세 줄 아래).",
            "h 왼쪽   j 아래   k 위   l 오른쪽\n..........\n..........\n.......*..\n..........", 1, 0,
            [new("j 로 * 가 있는 줄까지 내려가기", e => e.Current.Row == 3),
             new("l 로 오른쪽으로 가서 * 위에 커서 놓기", e => e.Current.Row == 3 && e.Current.Col == 7)],
            "jjlllllll"),

        new("단어·줄 이동", "w b e 0 $",
            "w 는 다음 단어의 처음, b 는 이전 단어의 처음, e 는 단어의 끝으로 갑니다. 0 은 줄의 맨 앞, ^ 는 첫 글자, $ 는 줄의 맨 끝입니다. 긴 명령줄이나 설정 값을 고칠 때 l 을 여러 번 누르는 것보다 훨씬 빠릅니다.",
            "The quick brown fox jumps over the lazy dog", 0, 0,
            [new("w 를 여러 번 눌러 lazy 의 l 로 이동", e => e.Current.Col == 35),
             new("e 로 lazy 의 끝(y)으로 이동", e => e.Current.Col == 38),
             new("$ 로 줄 끝으로 이동", e => e.Current.Col == 42),
             new("0 으로 줄 맨 앞으로 이동", e => e.Current.Col == 0)],
            "wwwwwwwe$0"),

        new("입력 위치 고르기", "a A o O I",
            "i 는 커서 앞, a 는 커서 뒤에 입력합니다. A 는 줄 끝, I 는 줄 맨 앞에서 입력을 시작합니다. o 는 아래에, O 는 위에 새 줄을 만들고 바로 입력 모드로 들어갑니다. 입력이 끝나면 항상 Esc 입니다.",
            "int count = 0\nreturn count;", 0, 0,
            [new("A 로 첫 줄 끝에 ; 붙이기 (A ; Esc)", e => e.Lines[0] == "int count = 0;" && e.Mode == ViMode.Normal),
             new("o 로 첫 줄 아래에 count++; 줄 추가", e => e.Lines.Count >= 3 && e.Lines.Contains("count++;") && e.Mode == ViMode.Normal),
             new("gg 로 첫 줄에 가서 O 로 맨 위에 // counter 줄 추가", e => e.Lines[0] == "// counter" && e.Mode == ViMode.Normal)],
            "A;<Esc>ocount++;<Esc>ggO// counter<Esc>"),

        new("지우기", "x dd dw D",
            "x 는 커서 위 한 글자, dd 는 한 줄, dw 는 단어 하나, D 는 커서부터 줄 끝까지 지웁니다. d 는 '지우기' 명령이고 뒤에 이동 키를 붙여 범위를 정합니다 (d$, d0, dG …).",
            "이 문장에는 글자가가 하나 더 있습니다.\ndelete me\nremove extra extra word", 0, 0,
            [new("w · l 로 '가가' 로 가서 x 로 한 글자 지우기", e => e.Lines[0] == "이 문장에는 글자가 하나 더 있습니다."),
             new("j 로 내려가 dd 로 delete me 줄 지우기", e => !e.Lines.Contains("delete me") && e.Lines.Count == 2),
             new("w 로 extra 로 가서 dw 로 중복 단어 지우기", e => e.Lines.Contains("remove extra word"))],
            "wwllxjddwdw"),

        new("복사·붙여넣기", "yy dd p P",
            "yy 는 줄 복사, yw 는 단어 복사입니다. dd 로 지운 내용도 보관되므로 잘라내기처럼 쓸 수 있습니다. p 는 커서 아래(뒤)에, P 는 위(앞)에 붙여넣습니다. 'dd p' 는 두 줄의 순서를 바꾸는 단골 조합입니다.",
            "B 줄\nA 줄\nC 줄", 0, 0,
            [new("dd 후 p 로 두 줄의 순서 바꾸기 (A, B, C 순서로)", e => e.Lines.Count >= 3 && e.Lines[0] == "A 줄" && e.Lines[1] == "B 줄"),
             new("C 줄로 가서 yy 후 p 로 한 줄 더 붙이기", e => e.Lines.Count == 4 && e.Lines[2] == "C 줄" && e.Lines[3] == "C 줄")],
            "ddpjyyp"),

        new("고쳐 쓰기", "cw r ~",
            "cw 는 단어를 지우고 바로 입력 모드로 들어갑니다 (change word). r 다음에 글자를 누르면 커서 위 한 글자만 바꿉니다. ~ 는 대문자·소문자를 바꿉니다. f 다음에 글자를 누르면 그 글자로 바로 이동합니다.",
            "teh quick brovn fox", 0, 0,
            [new("cw 로 teh 를 the 로 바꾸기 (cw the Esc)", e => e.Lines[0].StartsWith("the ") && e.Mode == ViMode.Normal),
             new("f v 로 v 로 이동해 r w 로 한 글자 바꾸기", e => e.Lines[0].Contains("brown")),
             new("0 으로 줄 앞에 가서 ~ 로 첫 글자를 대문자로", e => e.Lines[0] == "The quick brown fox")],
            "cwthe<Esc>fvrw0~"),

        new("되돌리기와 반복", "u Ctrl+r .",
            "u 는 마지막 변경을 되돌리고 Ctrl+r 은 되돌린 것을 다시 실행합니다. . (점)은 방금 한 변경을 그대로 반복합니다. 같은 수정을 여러 곳에 할 때 아주 유용합니다.",
            "line 1\nline 2\nline 3\nline 4\nline 5", 0, 0,
            [new("dd 로 한 줄 지우고 . 으로 한 번 더 지우기", e => e.Lines.Count == 3 && e.RepeatCount >= 1),
             new("u 를 두 번 눌러 모두 되돌리기", e => e.Lines.Count == 5 && e.UndoCount >= 2),
             new("Ctrl+r 로 한 번 다시 실행하기", e => e.Lines.Count == 4 && e.RedoCount >= 1)],
            "dd.uu<C-r>"),

        new("검색", "/ n N *",
            "/ 다음에 찾을 말을 입력하고 Enter 를 누르면 아래로 검색합니다 (? 는 위로). n 은 다음, N 은 이전 결과로 갑니다. * 는 커서 아래 단어를 바로 검색합니다. 로그 파일에서 오류를 찾을 때 자주 씁니다. :noh 로 강조를 끕니다.",
            Log, 0, 0,
            [new("/ERROR 입력 후 Enter 로 첫 오류 줄로 이동", e => e.Current.Row == 3 && e.SearchCount >= 1),
             new("n 으로 다음 ERROR 로 이동", e => e.Current.Row == 5),
             new("N 으로 이전 ERROR 로 돌아가기", e => e.Current.Row == 3)],
            "/ERROR<CR>nN"),

        new("바꾸기 (치환)", ":s  :%s",
            ":s/찾을말/바꿀말/ 은 현재 줄에서 처음 하나를, 끝에 g 를 붙이면 그 줄 전체를 바꿉니다. 앞에 % 를 붙인 :%s/찾을말/바꿀말/g 는 파일 전체를 바꿉니다. 설정 파일의 값을 한 번에 바꿀 때 씁니다.",
            "set temp = 100\nset limit = 120\nprint temp\nlog temp", 0, 0,
            [new(":s/set/SET/ 으로 현재 줄(첫 줄)만 바꾸기", e => e.Lines[0].StartsWith("SET") && e.Lines[1].StartsWith("set")),
             new(":%s/temp/TEMP/g 로 모든 temp 를 바꾸기", e => !e.Text.Contains("temp") && e.Text.Contains("TEMP"))],
            ":s/set/SET/<CR>:%s/temp/TEMP/g<CR>"),

        new("화면 분할", ":vsp  :sp  Ctrl+w",
            ":vsp 는 화면을 좌우로, :sp 는 위아래로 나눕니다. 같은 파일의 다른 부분을 나란히 볼 수 있습니다. Ctrl+w 다음에 w 를 누르면 다음 창으로, h j k l 을 누르면 그 방향 창으로 이동합니다. :q 는 현재 창을 닫고 :only 는 현재 창만 남깁니다.",
            Recipe, 0, 0,
            [new(":vsp 로 화면을 좌우로 나누기", e => e.Windows.Count >= 2 && e.Root is ViSplit { Vertical: true }),
             new("Ctrl+w w 로 옆 창으로 이동하기", e => e.WindowSwitches >= 1),
             new(":sp 로 현재 창을 위아래로 나누기", e => e.Windows.Count >= 3),
             new("Ctrl+w h (또는 j k l) 로 방향을 골라 이동하기", e => e.WindowSwitches >= 2),
             new(":only 로 창 하나만 남기기", e => e.Windows.Count == 1)],
            ":vsp<CR><C-w>w:sp<CR><C-w>h:only<CR>"),

        new("선택해서 편집 (비주얼)", "v V d y >",
            "v 는 글자 단위, V 는 줄 단위로 선택을 시작합니다. 이동 키로 범위를 넓힌 뒤 d (지우기) · y (복사) · > (들여쓰기) · c (바꾸기)를 누르면 선택한 부분에 적용됩니다. >> 는 현재 줄을 들여씁니다.",
            "keep 1\nremove A\nremove B\nkeep 2\nindent me", 0, 0,
            [new("j 로 remove A 로 가서 V j d 로 두 줄 지우기", e => !e.Text.Contains("remove") && e.Lines.Count == 3),
             new("indent me 줄로 가서 >> 로 들여쓰기", e => e.Lines[^1] == "    indent me")],
            "jVjdj>>"),

        new("숫자로 반복", "3j 3dd G gg",
            "대부분의 명령 앞에 숫자를 붙이면 그만큼 반복합니다. 3j 는 세 줄 아래, 3dd 는 세 줄 삭제, 5x 는 다섯 글자 삭제입니다. G 는 마지막 줄, gg 는 첫 줄, 숫자G 나 :숫자 는 그 줄로 갑니다. :set nu 로 줄 번호를 켜 보세요.",
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"줄 {i}")), 0, 0,
            [new("3j 로 세 줄 아래(줄 4)로 이동", e => e.Current.Row == 3 && e.Lines.Count == 10),
             new("3dd 로 줄 4 ~ 6 을 한 번에 지우기", e => e.Lines.Count == 7 && e.Lines[3] == "줄 7"),
             new("G 로 마지막 줄로 이동", e => e.Current.Row == e.Lines.Count - 1),
             new("gg 로 첫 줄로 이동", e => e.Current.Row == 0)],
            "3j3ddGgg"),

        new("저장과 종료", ":w :q :q! :wq",
            ":w 는 저장, :q 는 종료입니다. 저장하지 않은 변경이 있으면 :q 는 경고(E37)를 보여 줍니다. :q! 는 변경을 버리고 종료, :wq (또는 :x, ZZ) 는 저장 후 종료합니다. 연습창은 실제 파일을 만들거나 지우지 않습니다.",
            Memo, 0, 0,
            [new("x 등으로 아무 곳이나 고친 뒤 :w 로 저장", e => e.SaveCount >= 1 && !e.Modified && e.Text != Memo),
             new("다시 고친 뒤 :q 로 종료를 시도해 경고 확인", e => e.QuitBlockedCount >= 1),
             new(":q! 로 변경을 버리고 종료", e => e.DiscardCount >= 1),
             new(":wq 로 저장 후 종료", e => e.SaveCount >= 2)],
            "x:w<CR>x:q<CR>:q!<CR>:wq<CR>"),

        new("자유 연습", "모든 키",
            "배운 키를 자유롭게 써 보세요. :vsp 로 나눠 보고, /temp 로 검색하고, :%s 로 값을 바꿔 보세요. :e! 는 마지막 저장 상태로 되돌리고, [처음부터]는 연습 문서를 새로 엽니다.",
            Recipe, 0, 0, [], ""),
    ];

    /// <summary>Key reference shown beside the editor.</summary>
    public static IReadOnlyList<(string Section, (string Key, string Text)[] Items)> CheatSheet { get; } =
    [
        ("모드 바꾸기", [("Esc", "명령 모드로 (모를 땐 일단 Esc)"), ("i  /  a", "커서 앞 / 뒤에 입력"), ("I  /  A", "줄 맨 앞 / 줄 끝에 입력"), ("o  /  O", "아래 / 위에 새 줄을 만들고 입력"), ("v  /  V", "글자 / 줄 단위 선택"), (":", "명령줄 (저장·치환·분할 등)")]),
        ("이동", [("h j k l", "← ↓ ↑ →"), ("w  /  b", "다음 / 이전 단어 처음"), ("e", "단어 끝"), ("0  ^  $", "줄 맨 앞 / 첫 글자 / 줄 끝"), ("gg  /  G", "첫 줄 / 마지막 줄"), ("12G  /  :12", "12번째 줄로"), ("f{글자}", "줄에서 그 글자로 이동 (; 다음)"), ("%", "짝이 되는 괄호로"), ("Ctrl+d / Ctrl+u", "반 화면 아래 / 위")]),
        ("지우기·바꾸기", [("x", "글자 하나 지우기"), ("dd", "줄 지우기"), ("dw  /  D", "단어 / 줄 끝까지 지우기"), ("cw  /  cc", "단어 / 줄을 지우고 입력"), ("r{글자}", "한 글자 바꾸기"), ("~", "대소문자 바꾸기"), ("J", "아래 줄을 이어 붙이기"), (">>  /  <<", "들여쓰기 / 내어쓰기")]),
        ("복사·붙여넣기", [("yy  /  yw", "줄 / 단어 복사"), ("p  /  P", "아래(뒤) / 위(앞)에 붙여넣기"), ("v → y", "선택한 부분 복사")]),
        ("되돌리기·반복", [("u", "되돌리기"), ("Ctrl+r", "다시 실행"), (".", "마지막 변경 반복"), ("3dd · 5x", "숫자를 붙여 반복")]),
        ("검색·치환", [("/단어", "아래로 검색 (Enter)"), ("?단어", "위로 검색"), ("n  /  N", "다음 / 이전 결과"), ("*", "커서 아래 단어 검색"), (":noh", "검색 강조 끄기"), (":s/a/b/", "현재 줄의 a 를 b 로"), (":%s/a/b/g", "파일 전체의 a 를 모두 b 로")]),
        ("화면 분할", [(":vsp", "좌우로 나누기"), (":sp", "위아래로 나누기"), ("Ctrl+w w", "다음 창으로"), ("Ctrl+w h j k l", "그 방향 창으로"), ("Ctrl+w q  /  :q", "현재 창 닫기"), (":only", "현재 창만 남기기")]),
        ("저장·종료", [(":w", "저장"), (":q", "종료 (변경 있으면 경고)"), (":wq  :x  ZZ", "저장 후 종료"), (":q!", "저장하지 않고 종료"), (":e!", "마지막 저장 상태로 되돌리기")]),
        ("설정", [(":set nu", "줄 번호 켜기"), (":set nonu", "줄 번호 끄기"), ("Ctrl+g", "파일 이름과 현재 줄 보기")]),
    ];
}
