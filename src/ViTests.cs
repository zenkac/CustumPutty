using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

/// <summary>Checks the vi practice editor, its lessons and the practice workspace. No files, processes or network are used.</summary>
public static class ViTests
{
    public static async Task RunAsync()
    {
        if (!App.IsTesting) throw new InvalidOperationException("VI tests run only in an explicit test run.");
        Directory.CreateDirectory(AppPaths.Root);
        var report = new StringBuilder();
        var failures = new List<string>(); void Check(string label, bool ok) { if (!ok) failures.Add(label); report.AppendLine((ok ? "PASS: " : "FAIL: ") + label); }
        ViEngine Run(string text, string keys, int row = 0, int col = 0, string file = "practice.txt") { var e = new ViEngine { FileName = file }; e.Load(text, row, col); e.FeedKeys(keys); return e; }
        try
        {
            // Modes and insertion
            var e = Run("abc", "ix<Esc>");
            Check("i inserts before the cursor and Esc returns to normal mode", e.Text == "xabc" && e.Mode == ViMode.Normal && e.Current.Col == 0);
            Check("a, A, I and o/O insert at the expected places", Run("abc", "aX<Esc>").Text == "aXbc" && Run("abc", "AX<Esc>").Text == "abcX" && Run("  abc", "$IX<Esc>").Text == "  Xabc" && Run("  a\nb", "ox<Esc>").Text == "  a\n  x\nb" && Run("a", "Ox<Esc>").Text == "x\na");
            Check("Enter and Backspace split and join lines in insert mode", Run("abcd", "lli<CR><Esc>").Text == "ab\ncd" && Run("ab\ncd", "ji<BS><Esc>").Text == "abcd");
            Check("Typed Korean text is inserted as characters", Run("", "i한글 입력<Esc>").Text == "한글 입력");
            Check("A count repeats an insert (3ix, 10i-, 2o)", Run("", "3ix<Esc>").Text == "xxx" && Run("", "10i-<Esc>").Text == new string('-', 10) && Run("a", "2ob<Esc>").Text == "a\nb\nb");

            // Motions
            e = Run("The quick brown fox", "w"); Check("w moves to the next word", e.Current.Col == 4);
            e = Run("The quick brown fox", "$b"); Check("$ and b move to line end and previous word", e.Current.Col == 16);
            e = Run("The quick brown fox", "we"); Check("e moves to the word end", e.Current.Col == 8);
            e = Run("foo.bar baz", "w"); Check("w stops at punctuation like vim", e.Current.Col == 3);
            e = Run("foo.bar baz", "W"); Check("W treats punctuation as part of a WORD", e.Current.Col == 8);
            e = Run("a\nb\nc\nd", "G"); Check("G goes to the last line", e.Current.Row == 3);
            e = Run("a\nb\nc\nd", "Ggg"); Check("gg goes to the first line", e.Current.Row == 0);
            e = Run("a\nb\nc\nd", "3G"); Check("3G goes to line 3", e.Current.Row == 2);
            e = Run("a\nb\nc\nd", "2j"); Check("Counts repeat motions (2j)", e.Current.Row == 2);
            e = Run("long line\nx\nlong line", "$jj"); Check("j keeps the end-of-line column after $", e.Current.Row == 2 && e.Current.Col == 8);
            e = Run("key = value", "f="); Check("f jumps to a character", e.Current.Col == 4);
            e = Run("call(a, (b))", "f(%"); Check("% jumps to the matching bracket", e.Current.Col == 11);
            e = Run("    text", "0^"); Check("^ goes to the first non-blank", e.Current.Col == 4);
            e = Run("a\nb\nc", "Gj"); Check("j on the last line fails like vim", e.Current.Row == 2);
            e = Run(string.Join("\n", Enumerable.Range(1, 60)), "40Gzz"); e.SetHeight(e.Current, 10); e.FeedKeys("zz"); Check("zz centers the cursor line", e.Current.Top == 34);

            // Deleting, changing, copying
            Check("x and 3x delete characters", Run("abcdef", "x").Text == "bcdef" && Run("abcdef", "3x").Text == "def");
            Check("dd and 2dd delete lines", Run("a\nb\nc", "dd").Text == "b\nc" && Run("a\nb\nc", "2dd").Text == "c");
            Check("dw deletes a word including the following space", Run("one two three", "dw").Text == "two three");
            Check("dw on the last word stays on its line", Run("one two\nnext", "wdw").Text == "one \nnext");
            Check("d$ / D delete to the end of the line", Run("one two", "wD").Text == "one " && Run("one two", "wd$").Text == "one ");
            Check("dj and dG delete linewise ranges", Run("a\nb\nc\nd", "jdj").Text == "a\nd" && Run("a\nb\nc", "jdG").Text == "a");
            Check("cw changes a word without eating the space", Run("teh fox", "cwthe<Esc>").Text == "the fox");
            Check("cc and C change a line / to the end", Run("  old\nb", "ccnew<Esc>").Text == "  new\nb" && Run("keep drop", "wCgo<Esc>").Text == "keep go");
            Check("r, ~ and J edit in place", Run("cat", "rb").Text == "bat" && Run("abc", "~~").Text == "ABc" && Run("a\n  b", "J").Text == "a b");
            Check("yy p and dd p copy and move lines", Run("a\nb", "yyp").Text == "a\na\nb" && Run("B\nA\nC", "ddp").Text == "A\nB\nC" && Run("a\nb", "jyyP").Text == "a\nb\nb");
            Check("yw p pastes a word after the cursor", Run("ab cd", "ywP").Text == "ab ab cd");
            Check(">> and << indent by shiftwidth", Run("x", ">>").Text == "    x" && Run("      x", "<<").Text == "  x" && Run("x", ":set sw=2<CR>>>").Text == "  x");
            Check("An unknown motion after an operator does nothing", Run("abc", "dz").Text == "abc");
            Check("gU, gu and g~ change case by motion and line", Run("make big", "gUiw").Text == "MAKE big" && Run("ABC DEF", "guu").Text == "abc def" && Run("abc", "g~~").Text == "ABC" && Run("ab cd", "veU").Text == "AB cd");

            // Undo, redo, repeat
            e = Run("a\nb\nc", "dddd"); e.FeedKeys("u"); Check("u undoes the last change", e.Text == "b\nc");
            e.FeedKeys("<C-r>"); Check("Ctrl+r redoes it", e.Text == "c");
            e = Run("one", "ix<Esc>u"); Check("A whole insert is undone at once", e.Text == "one");
            e = Run("a\nb\nc\nd", "dd."); Check(". repeats a delete", e.Text == "c\nd" && e.RepeatCount == 1);
            e = Run("a\nb", "A;<Esc>j."); Check(". repeats an insert", e.Text == "a;\nb;");
            e = Run("x", "u"); Check("Undo with nothing to undo reports a message", e.MessageIsError && e.Text == "x");

            // Visual mode
            Check("v selects characters for d", Run("abcdef", "lvlld").Text == "aef");
            Check("V selects lines for d and y", Run("a\nb\nc", "Vjd").Text == "c" && Run("a\nb", "VyjP").Text == "a\na\nb");
            Check("Visual > indents selected lines", Run("a\nb", "Vj>").Text == "    a\n    b");
            e = Run("abc", "v"); e.FeedKeys("<Esc>"); Check("Esc leaves visual mode", e.Mode == ViMode.Normal);
            e = Run("abc def", "vee<Esc>gv"); Check("gv reselects the last visual area", e.Mode == ViMode.Visual && e.Current.Col == 6);
            Check("Visual r replaces every selected character", Run("abcd", "lvlrx").Text == "axxd");

            // Text objects
            Check("ciw changes the whole word under the cursor", Run("call(legacy_mode)", "fgciwsafe<Esc>").Text == "call(safe)");
            Check("ci\" and di( work from anywhere inside", Run("name = \"OLD\"", "ci\"NEW<Esc>").Text == "name = \"NEW\"" && Run("f(a, b)", "fbdi(").Text == "f()");
            Check("daw removes a word with its space and da( the brackets", Run("one two three", "wdaw").Text == "one three" && Run("x(1)y", "lda(").Text == "xy");
            Check("di{ on a braces block deletes the inner lines", Run("if (a) {\n  one\n  two\n}", "jdi{").Text == "if (a) {\n}");
            e = Run("f(abc)", "fbvi("); Check("vi( selects the bracket contents", e.Mode == ViMode.Visual && e.VisualAnchor.Col == 2 && e.Current.Col == 4);
            Check("dip deletes a paragraph", Run("a\nb\n\nc", "dip").Text == "\nc");

            // Search and substitution
            e = Run("x\nfoo\nbar foo", "/foo<CR>"); Check("/ searches forward", e.Current.Row == 1 && e.Highlight == "foo");
            e.FeedKeys("n"); Check("n finds the next match", e.Current.Row == 2 && e.Current.Col == 4);
            e.FeedKeys("n"); Check("n wraps around with a message", e.Current.Row == 1 && e.Message.Contains("처음부터"));
            e.FeedKeys("N"); Check("N searches backwards", e.Current.Row == 2);
            e = Run("foo\nfoobar\nfoo", "*"); Check("* searches the whole word under the cursor", e.Current.Row == 2);
            e = Run("x\ny", "/zzz<CR>"); Check("A missing search reports an error", e.MessageIsError && e.Current.Row == 0);
            Check(":s replaces the first match on the current line", Run("a a\na a", ":s/a/b/<CR>").Text == "b a\na a");
            Check(":s///g and :%s///g replace all matches", Run("a a\na a", ":s/a/b/g<CR>").Text == "b b\na a" && Run("a a\na a", ":%s/a/b/g<CR>").Text == "b b\nb b");
            Check(":2,3s and visual :'<,'>s honor ranges", Run("a\na\na", ":2,3s/a/b/<CR>").Text == "a\nb\nb" && Run("a\na\na", "Vj:s/a/b/<CR>").Text == "b\nb\na");
            Check(":noh clears the search highlight", Run("foo", "/foo<CR>:noh<CR>").Highlight == null);
            Check("Regular expressions: \\d\\+, \\s\\+$ and ^", Run("t=65 p=120", ":s/\\d\\+/N/g<CR>").Text == "t=N p=N" && Run("a  \nb ", ":%s/\\s\\+$//<CR>").Text == "a\nb" && Run("a\nb", ":%s/^/# /<CR>").Text == "# a\n# b");
            Check("Groups \\( \\) with \\1 \\2 and & in the replacement", Run("temp=65", ":s/\\(\\w\\+\\)=\\(\\d\\+\\)/\\2 \\1/<CR>").Text == "65 temp" && Run("ab", ":s/b/[&]/<CR>").Text == "a[b]");
            Check("\\< \\> match whole words and \\c ignores case", Run("cat concat Cat", ":s/\\<cat\\>/X/g<CR>").Text == "X concat Cat" && Run("Cat", ":s/\\ccat/X/<CR>").Text == "X");
            e = Run("a a\na", ":%s/a//gn<CR>"); Check("The n flag counts matches without changing text", e.Text == "a a\na" && e.Message.StartsWith("3개 일치") && e.CountOnlySubstitutes == 1);
            Check("\\r in the replacement splits lines and \\n in the pattern joins them", Run("a,b", ":s/,/\\r/<CR>").Text == "a\nb" && Run("a\nb\nc", ":%s/\\n/,/<CR>").Text == "a,b,c,");
            Check("& repeats the last substitution on another line", Run("a\na", ":s/a/b/<CR>j&").Text == "b\nb");
            e = Run("x\nERROR\nerror", ":set ic<CR>/error<CR>"); Check(":set ic makes searches ignore case", e.Current.Row == 1);
            e = Run("x\nERROR\nerror", ":set ic scs<CR>/ERROR<CR>n"); Check("smartcase respects an uppercase pattern", e.Current.Row == 1);

            // Ex commands
            e = Run("a\nb\nc", ":3<CR>"); Check(":3 goes to line 3", e.Current.Row == 2);
            e = Run("a", ":set nu<CR>"); Check(":set nu shows line numbers", e.ShowNumbers); e.FeedKeys(":set nonu<CR>"); Check(":set nonu hides them", !e.ShowNumbers);
            e = Run("a", ":set nu rnu list cul<CR>"); Check(":set accepts several options at once", e.ShowNumbers && e.RelativeNumbers && e.ListChars && e.CursorLine);
            e.FeedKeys(":set sw=2<CR>:set sw?<CR>"); Check(":set sw? shows the current value", e.Message == "shiftwidth=2");
            e.FeedKeys(":set bogus<CR>"); Check("Unknown options report E518", e.MessageIsError && e.Message.StartsWith("E518"));
            e.FeedKeys(":set nocompatible encoding=utf-8 background=dark<CR>"); Check("Common .vimrc options are accepted quietly", !e.MessageIsError);
            e = Run("a", "x:w<CR>"); Check(":w saves in memory only", !e.Modified && e.SaveCount == 1 && e.Message.Contains("연습용"));
            e = Run("a", "x:q<CR>"); Check(":q refuses to quit with unsaved changes", e.QuitBlockedCount == 1 && e.MessageIsError && e.Message.StartsWith("E37"));
            e.FeedKeys(":q!<CR>"); Check(":q! discards changes back to the last save", e.Text == "a" && e.DiscardCount == 1);
            e = Run("a", "x:w<CR>ix<Esc>:e!<CR>"); Check(":e! returns to the last saved text", e.Text == "");
            e = Run("a", ":bogus<CR>"); Check("Unknown commands report E492", e.MessageIsError && e.Message.StartsWith("E492"));
            e = Run("a", ":wq<CR>"); Check(":wq saves and reports a practice quit", e.SaveCount == 1 && !e.MessageIsError);
            Check(":m and :t move and copy lines", Run("c\na\nb", ":m $<CR>").Text == "a\nb\nc" && Run("a\nb", ":t .<CR>").Text == "a\na\nb" && Run("a\nb\nc", "G:m 0<CR>").Text == "c\na\nb" && Run("a\nb\nc\nd", ":2,3m 0<CR>").Text == "b\nc\na\nd");
            Check(":j joins lines and :.,+1 ranges work", Run("a\nb\nc", ":j<CR>").Text == "a b\nc" && Run("a\nb\nc", ":.,+1d<CR>").Text == "c");
            Check(":sort, :sort u, :sort n and :sort! order lines", Run("b\na\nb", ":sort<CR>").Text == "a\nb\nb" && Run("b\na\nb", ":sort u<CR>").Text == "a\nb" && Run("x10\nx9\nx100", ":sort n<CR>").Text == "x9\nx10\nx100" && Run("a\nc\nb", ":sort!<CR>").Text == "c\nb\na");
            Check(":g/pat/d, :v/pat/d and :g!/pat/d", Run("a1\nb\na2", ":g/a/d<CR>").Text == "b" && Run("a1\nb\na2", ":v/a/d<CR>").Text == "a1\na2" && Run("a1\nb\na2", ":g!/a/d<CR>").Text == "a1\na2");
            Check(":g/^/m0 reverses the file and :g/x/m$ moves matches to the end", Run("1\n2\n3", ":g/^/m0<CR>").Text == "3\n2\n1" && Run("x1\na\nx2\nb", ":g/x/m$<CR>").Text == "a\nb\nx1\nx2");
            Check(":g with :s and :normal", Run("a=1\nb\na=2", ":g/=/s/a/k/<CR>").Text == "k=1\nb\nk=2" && Run("a\nb", ":g/a/normal A;<CR>").Text == "a;\nb");
            e = Run("a\nb\nc", ":%norm A;<CR>"); Check(":%norm runs normal keys on every line as one undo step", e.Text == "a;\nb;\nc;"); e.FeedKeys("u"); Check("Undo removes the whole :%norm", e.Text == "a\nb\nc");
            Check(":'<,'>norm I# comments out a visual selection", Run("a\nb\nc", "Vj:norm I# <CR>").Text == "# a\n# b\nc");
            e = Run("x\ny", ":g/x<CR>"); Check(":g/pat prints matching lines", e.Message == "x");

            // Registers
            e = Run("one\ntwo\nthree", "\"ayyj\"byy\"apG\"bp"); Check("Named registers keep separate texts", e.Text == "one\ntwo\none\nthree\ntwo" && e.RegisterText('a') == "one" && e.RegisterText('b') == "two");
            e = Run("one\ntwo\nx", "\"ayyj\"Ayy"); Check("Uppercase registers append", e.RegisterText('a') == "one\ntwo");
            e = Run("keep\ndrop\nend", "yyjddG\"0p"); Check("\"0 keeps the last yank after a delete", e.Text == "keep\nend\nkeep");
            e = Run("keep\ndrop", "yyj\"_ddp"); Check("The black hole register \"_ leaves the unnamed register alone", e.Text == "keep\nkeep");
            e = Run("a\nb", "dd:reg<CR>"); Check(":reg lists registers", e.Message.Contains("--- 레지스터 ---") && e.Message.Contains("\"1") && e.RegisterViews == 1);
            e = Run("x", "\"zp"); Check("Pasting an empty named register reports it", e.MessageIsError && e.Message.Contains("\"z"));

            // Macros
            e = Run("1\n2\n3\n4", "qaA;<Esc>jq2@a"); Check("qa…q records and 2@a replays", e.Text == "1;\n2;\n3;\n4" && e.MacroRecords == 1 && e.MacroRuns == 1);
            e.FeedKeys("@@"); Check("@@ repeats the last macro", e.Text == "1;\n2;\n3;\n4;");
            e = Run("1\n2\n3\n4", "qaA!<Esc>j@aq@a"); Check("A recursive macro stops when j fails on the last line", e.Text == "1!\n2!\n3!\n4!");
            e = Run("1\n2\n3\n4\n5", "qaA,<Esc>jq100@a"); Check("A large count stops at the end of the file", e.Text == "1,\n2,\n3,\n4,\n5," && e.Mode == ViMode.Normal);
            e = Run("x", "qaq"); Check("An empty recording is allowed", e.RegisterText('a') == "" && e.Recording == null);
            e = Run("x", "qbix<Esc>"); Check("Recording shows its register until q", e.Recording == 'b'); e.FeedKeys("q"); Check("q ends the recording", e.Recording == null && e.RegisterText('b') == "ix\u001b");
            e = Run("a", ":s/a/b/<CR>u@:"); Check("@: repeats the last ex command", e.Text == "b");
            e = Run("x", "@a"); Check("Running an empty register explains how to record", e.MessageIsError && e.Message.Contains("qa"));

            // Marks
            e = Run("a\nb\nc\nd", "jmaG'a"); Check("ma and 'a return to the marked line", e.Current.Row == 1 && e.MarkJumps == 1);
            e = Run("abc\ndef", "lmbG`b"); Check("`b returns to the exact column", e.Current.Row == 0 && e.Current.Col == 1);
            e = Run("a\nb\nc\nd\ne", "jmajjd'a"); Check("d'a deletes from the cursor to the mark", e.Text == "a\ne");
            e = Run("a\nb\nc", "G''"); Check("'' jumps back to the previous position", e.Current.Row == 0);
            e = Run("a", "'q"); Check("A missing mark reports E20", e.MessageIsError && e.Message.StartsWith("E20"));
            e = Run("a\nb\nc\nd", "jmajmb:'a,'bd<CR>"); Check("Marks work as ex ranges", e.Text == "a\nd");

            // Auto indent (=)
            const string messy = "int f(int t)\n{\nif (t > 80) {\nprintf(\"hot\\n\");\nreturn 1;\n}\n        return 0;\n}";
            e = Run(messy, "gg=G", file: "f.c");
            Check("gg=G re-indents C code by brace depth", e.Text == "int f(int t)\n{\n    if (t > 80) {\n        printf(\"hot\\n\");\n        return 1;\n    }\n    return 0;\n}" && e.IndentOps == 1);
            Check("== fixes one line and V= the selection", Run(messy, "6G==", file: "f.c").Lines[5] == "    }" && Run(messy, "3GVjj=", file: "f.c").Lines[3] == "        printf(\"hot\\n\");");
            Check("= follows :set sw", Run(messy, ":set sw=2<CR>gg=G", file: "f.c").Lines[3] == "    printf(\"hot\\n\");");
            Check("= handles switch/case and braceless if", Run("switch (x) {\ncase 1:\nfoo();\nbreak;\n}\nif (a)\nb();\nc();", "gg=G", file: "s.c").Text == "switch (x) {\n    case 1:\n        foo();\n        break;\n}\nif (a)\n    b();\nc();");
            Check("= indents shell scripts by if/then/fi and for/do/done", Run("if [ -f x ]; then\necho a\nelse\necho b\nfi\nfor f in *; do\necho $f\ndone", "gg=G", file: "x.sh").Text == "if [ -f x ]; then\n    echo a\nelse\n    echo b\nfi\nfor f in *; do\n    echo $f\ndone");

            // Block visual
            Check("Ctrl+v I inserts on every selected line", Run("a\nb\nc", "<C-v>jjI# <Esc>").Text == "# a\n# b\n# c");
            Check("Ctrl+v $A appends to lines of different length", Run("ab\nabcd", "<C-v>j$A;<Esc>").Text == "ab;\nabcd;");
            Check("Ctrl+v A pads short lines to the block edge", Run("abc\na", "ll<C-v>jA|<Esc>").Text == "abc|\na  |");
            Check("Ctrl+v d deletes the rectangle", Run("abcd\nefgh", "l<C-v>jld").Text == "ad\neh");
            Check("Ctrl+v y and p paste the block", Run("ab\ncd\n", "<C-v>jy$p").Text == "aba\ncdc\n");
            Check("Ctrl+v c changes every line", Run("xx1\nxx2", "<C-v>jlcY<Esc>").Text == "Y1\nY2");
            Check("Ctrl+v r replaces the rectangle", Run("abc\ndef", "<C-v>jlr*").Text == "**c\n**f");

            // Numbers
            Check("Ctrl+a / Ctrl+x change the next number", Run("timeout = 30", "30<C-a>").Text == "timeout = 60" && Run("retry = 3", "<C-x>").Text == "retry = 2" && Run("v -3", "<C-a>").Text == "v -2" && Run("ETCH-01", "<C-a>").Text == "ETCH-02");
            Check("Ctrl+a keeps leading zeros", Run("w_009", "<C-a>").Text == "w_010");
            Check("Visual g Ctrl+a numbers lines progressively", Run("w0\nw0\nw0", "Vjjg<C-a>").Text == "w1\nw2\nw3" && Run("w0\nw0", "Vj<C-a>").Text == "w1\nw1");
            Check("Ctrl+a without a number reports it", Run("abc", "<C-a>").MessageIsError);

            // Completion
            e = Run("chamber_pressure\nchamber_temp\n", "Gicha<C-n>"); Check("Ctrl+n completes a word from the buffer", e.Lines[2] == "chamber_pressure" && e.CompletionCount == 1);
            e.FeedKeys("<C-n>"); Check("Ctrl+n again picks the next candidate", e.Lines[2] == "chamber_temp");
            e.FeedKeys("<C-p>"); Check("Ctrl+p goes back to the previous candidate", e.Lines[2] == "chamber_pressure");
            e.FeedKeys("<C-p>"); Check("Ctrl+p past the first candidate restores the typed prefix", e.Lines[2] == "cha");

            // Colors, syntax and .vimrc
            e = Run("a", ":colo ron<CR>"); Check(":colo ron changes the color scheme", e.ColorScheme == "ron" && e.ColorChanges == 1);
            e.FeedKeys(":colo nope<CR>"); Check("An unknown scheme reports E185", e.MessageIsError && e.Message.StartsWith("E185") && e.ColorScheme == "ron");
            e.FeedKeys(":colo de<Tab>"); Check("Tab completes color scheme names", e.CommandText == ":colo default"); e.FeedKeys("<Tab>"); Check("Tab cycles through matches", e.CommandText == ":colo delek" || e.CommandText == ":colo desert"); e.FeedKeys("<Esc>");
            e.FeedKeys(":syntax off<CR>"); Check(":syntax off turns highlighting off", !e.SyntaxOn); e.FeedKeys(":syntax on<CR>"); Check(":syntax on turns it back on", e.SyntaxOn);
            e = Run("x", ":colo desert<CR>"); e.Load("y"); Check("Color schemes carry over to the next lesson like a vim session", e.ColorScheme == "desert");
            e = Run("\" settings\nset number\nset sw=2\nsyntax off\ncolorscheme elflord", ":so %<CR>", file: ".vimrc"); Check(":so % applies a .vimrc", e.ShowNumbers && e.ShiftWidth == 2 && !e.SyntaxOn && e.ColorScheme == "elflord" && e.Sources == 1 && !e.MessageIsError);
            e = Run("set nu\nset bogus", ":so %<CR>", file: ".vimrc"); Check(":so % reports the failing line", e.MessageIsError && e.Message.StartsWith("2번째 줄"));
            Check("File types follow the file name", ViEngine.DetectFileType("a.c") == "c" && ViEngine.DetectFileType(".vimrc") == "vim" && ViEngine.DetectFileType("x.sh") == "sh" && ViEngine.DetectFileType("r.rcp") == "conf" && ViEngine.DetectFileType("a.log") == "log");
            bool block = false; var spans = ViSyntax.Spans("int x = 5; // TODO fix", "c", ref block);
            Check("C syntax marks types, numbers, comments and TODO", spans.Any(s => s.Group == ViGroup.Type && s.Start == 0) && spans.Any(s => s.Group == ViGroup.Constant) && spans.Any(s => s.Group == ViGroup.Comment) && spans.Any(s => s.Group == ViGroup.Todo));
            spans = ViSyntax.Spans("2026-11-21 08:00:00 [ERROR] EQ01 ALM-1203", "log", ref block); Check("Log syntax marks ERROR lines", spans.Any(s => s.Group == ViGroup.Error));
            ViSyntax.Spans("/* start", "c", ref block); Check("Block comments continue across lines", block && ViSyntax.Spans("still */ int", "c", ref block).First().Group == ViGroup.Comment && !block);
            Check("Every color scheme name resolves", ViColorSchemes.Names.Count >= 15 && ViColorSchemes.Names.All(n => ViColorSchemes.Get(n).Name == n));

            // Paste (right click)
            e = Run("ab", "a"); e.Paste("1\n2"); Check("Paste in insert mode inserts text and line breaks", e.Text == "a1\n2b" && e.Mode == ViMode.Insert && e.Current.Row == 1 && e.Current.Col == 1);
            e = Run("ab", ""); e.Paste("dd"); Check("Paste in normal mode is refused instead of running keys", e.Text == "ab" && e.Message.Contains("입력 모드"));

            // Command-line history
            e = Run("a", ":set nu<CR>:set nonu<CR>:<Up><Up><CR>"); Check("Up recalls older command lines", e.ShowNumbers);

            // Embedded mode (the practice shell's vi)
            string? savedName = null, savedText = null; bool exited = false;
            e = new ViEngine { Embedded = true, FileName = "notes.txt", SaveHandler = (n, t) => { savedName = n; savedText = t; return null; }, ExitHandler = () => exited = true };
            e.Load("hello"); e.FeedKeys("A!<Esc>:wq<CR>"); Check("Embedded :wq saves through the handler and exits", savedName == "notes.txt" && savedText == "hello!" && exited);
            e = new ViEngine { Embedded = true, SaveHandler = (_, _) => "E212: 저장할 수 없습니다" }; e.Load("x"); e.FeedKeys("x:w<CR>"); Check("A save error is shown and the buffer stays modified", e.MessageIsError && e.Modified);

            // Windows
            e = Run("a\nb\nc", ":vsp<CR>"); Check(":vsp splits side by side", e.Windows.Count == 2 && e.Root is ViSplit { Vertical: true });
            e.FeedKeys(":sp<CR>"); Check(":sp splits the current window top/bottom", e.Windows.Count == 3 && ((ViSplit)e.Root).Children.Any(c => c is ViSplit { Vertical: false }));
            var first = e.Current; e.FeedKeys("<C-w>w"); Check("Ctrl+w w moves to another window", e.Current != first && e.WindowSwitches == 1);
            e.FeedKeys("<C-w>l"); var geometry = e.Geometry(); Check("Ctrl+w l moves to the right-hand window", geometry[e.Current].X >= .5);
            e.FeedKeys("<C-w>h"); Check("Ctrl+w h moves back to the left", e.Geometry()[e.Current].X < .5);
            e.FeedKeys("Gdd"); Check("All windows share one buffer", e.Text == "a\nb" && e.Windows.All(w => w.Row <= 1));
            e.FeedKeys(":q<CR>"); Check(":q closes only the current window when split", e.Windows.Count == 2);
            e.FeedKeys(":only<CR>"); Check(":only keeps a single window", e.Windows.Count == 1 && e.Root is ViLeaf);
            e.FeedKeys(":q<CR>"); Check(":q on the last window never closes the practice editor", e.Windows.Count == 1);
            e = Run("a", ":vsp<CR>:vsp<CR>:vsp<CR>:vsp<CR>:vsp<CR>:vsp<CR>"); Check("Splitting stops at six windows", e.Windows.Count == 6 && e.MessageIsError);

            // Robustness: random input never corrupts state.
            var random = new Random(4321); string[] pool = ["h", "j", "k", "l", "w", "b", "e", "x", "d", "y", "p", "P", "u", "<C-r>", ".", "i", "a", "o", "O", "<Esc>", "v", "V", "<C-v>", ">", "<", "=", "c", "r", "J", "~", "G", "g", "U", "$", "0", "/", ":", "<CR>", "<BS>", "n", "N", "*", "%", "f", "t", "3", "2", "<C-w>", "s", "q", "a", "@", "m", "'", "`", "\"", "<C-a>", "<C-x>", "<C-n>", "I", "A", "z", "&", "{", "(", "가", "<Tab>"];
            e = new ViEngine(); e.Load("first line\n  second (line) {\nthird \"q\"\n\nfifth word word 12", 0, 0);
            for (int i = 0; i < 30000; i++)
            {
                e.Feed(pool[random.Next(pool.Length)]);
                if (e.Lines.Count == 0 || e.Lines.Count > 5000 || e.Windows.Count is < 1 or > 6 || e.Windows.Any(w => w.Row < 0 || w.Row >= e.Lines.Count || w.Col < 0 || w.Col > e.Lines[w.Row].Length))
                    throw new InvalidOperationException("FAIL: random input produced an invalid cursor or buffer at step " + i);
            }
            Check("30,000 random keys keep cursors and windows valid", true);

            // Every lesson can be completed with its example keys.
            for (int i = 0; i < ViLessons.All.Count; i++)
            {
                var lesson = ViLessons.All[i]; if (lesson.Steps.Length == 0) continue;
                var engine = new ViEngine { FileName = lesson.File.Length > 0 ? lesson.File : "lesson.txt" }; engine.Load(lesson.Text, lesson.Row, lesson.Col); int step = 0;
                Check($"VI lesson {i + 1} starts incomplete", !lesson.Steps[0].Done(engine));
                foreach (var key in ViEngine.Tokens(lesson.Solution)) { engine.Feed(key); while (step < lesson.Steps.Length && lesson.Steps[step].Done(engine)) step++; }
                Check($"VI lesson {i + 1} '{lesson.Title}' is completed by its example keys (step {step}/{lesson.Steps.Length})", step == lesson.Steps.Length);
            }
            Check("Lessons cover three levels", ViLessons.All.Select(l => l.Level).Distinct().Count() == 3 && ViLessons.All.Count >= 25);

            // Real workspace: mode switch, keyboard routing, tracks and lesson progress.
            ThemeService.Apply("graphite");
            var window = new MainWindow(AppStore.Demo(), true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.ShowViForTest(true); await Settle(window);
                var practice = window.ViWorkspaceForTest!;
                Check("[연습] opens its own workspace and hides terminals", practice.IsVisible && !window.TerminalModeForTest && !practice.ShellModeForTest);
                var editor = Find<ViEditorView>(practice, "ViEditor");
                Check("Normal mode turns off Korean IME so commands are Latin keys", !InputMethod.GetIsInputMethodEnabled(editor));
                Type(editor, "i"); await Settle(window);
                Check("Typed i enters insert mode through the real keyboard handlers", practice.EngineForTest.Mode == ViMode.Insert && InputMethod.GetIsInputMethodEnabled(editor));
                Type(editor, "hello vi"); Key(editor, System.Windows.Input.Key.Escape); await Settle(window);
                Check("Lesson 1 completes from real key events", practice.LessonCompleteForTest && Find<TextBlock>(practice, "PracticeState").Text.Contains("완료"));
                Check("The lesson list shows level headers", Descendants(Find<ListBox>(practice, "PracticeLessons")).OfType<TextBlock>().Any(t => t.Text == "고급"));
                Capture(window, "vi-lesson-complete.png");
                int split = ViLessons.All.ToList().FindIndex(l => l.Title == "화면 분할");
                practice.SelectLessonForTest(split); await Settle(window);
                Type(editor, ":vsp"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, ":sp"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, "/temp"); Key(editor, System.Windows.Input.Key.Enter); await Settle(window);
                Check("Split windows render as separate panes", Descendants(editor).OfType<ViWindowView>().Count() == 3);
                Capture(window, "vi-split-graphite.png");
                Key(editor, System.Windows.Input.Key.W, ModifierKeys.Control); Type(editor, "w"); await Settle(window);
                Check("Ctrl+W W from the keyboard switches windows", practice.EngineForTest.WindowSwitches >= 1);
                int colors = ViLessons.All.ToList().FindIndex(l => l.Title == "색 구성표와 문법 강조");
                practice.SelectLessonForTest(colors); await Settle(window);
                Type(editor, ":colo ron"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, ":set nu cul"); Key(editor, System.Windows.Input.Key.Enter); await Settle(window);
                Check("A color scheme repaints the editor", practice.EngineForTest.ColorScheme == "ron" && editor.Background is SolidColorBrush { Color: { R: 0, G: 0, B: 0 } });
                Capture(window, "vi-colorscheme-ron.png");
                Type(editor, ":colo default"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, ":set nonu nocul"); Key(editor, System.Windows.Input.Key.Enter);
                int blockLesson = ViLessons.All.ToList().FindIndex(l => l.Title == "블록 선택");
                practice.SelectLessonForTest(blockLesson); await Settle(window);
                Key(editor, System.Windows.Input.Key.V, ModifierKeys.Control); Type(editor, "jj"); await Settle(window);
                Check("Ctrl+V starts a block selection", practice.EngineForTest.Mode == ViMode.VisualBlock);
                Capture(window, "vi-block-graphite.png");
                Key(editor, System.Windows.Input.Key.Escape);
                practice.SelectLessonForTest(ViLessons.All.Count - 1); await Settle(window);
                Type(editor, ":set nu"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, "jVj"); await Settle(window);
                Capture(window, "vi-visual-graphite.png");
                ThemeService.Apply("nord"); await Settle(window); Capture(window, "vi-visual-nord.png"); ThemeService.Apply("graphite");
                Key(editor, System.Windows.Input.Key.Escape); Type(editor, ":set nonu"); Key(editor, System.Windows.Input.Key.Enter);

                // Terminal track
                Find<Button>(practice, "PracticeShellTrack").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Settle(window);
                Check("[터미널 명령어] switches the practice area to a terminal", practice.ShellModeForTest && Descendants(practice).OfType<ShellView>().Any(v => v.IsVisible));
                var terminal = Find<ShellView>(practice, "PracticeTerminal");
                Check("The terminal turns off Korean IME for commands", !InputMethod.GetIsInputMethodEnabled(terminal));
                Type(terminal, "pwd"); Key(terminal, System.Windows.Input.Key.Enter); await Settle(window);
                Check("Typing pwd and Enter runs it through the real key handlers", practice.ShellForTest.Screen.Contains("/home/user") && practice.ShellForTest.Ran("pwd"));
                Type(terminal, "ls -l"); Key(terminal, System.Windows.Input.Key.Enter); Type(terminal, "ls -a"); Key(terminal, System.Windows.Input.Key.Enter); Type(terminal, "ls"); Key(terminal, System.Windows.Input.Key.Enter); await Settle(window);
                Check("Terminal lesson 1 completes from real key events", practice.LessonCompleteForTest);
                Type(terminal, "cat no"); Key(terminal, System.Windows.Input.Key.Tab); await Settle(window);
                Check("Tab completes file names in the terminal", practice.ShellForTest.Input == "cat notes.txt ");
                Key(terminal, System.Windows.Input.Key.Enter); await Settle(window);
                Capture(window, "shell-graphite.png");
                int viLesson = ShellLessons.All.ToList().FindIndex(l => l.Title == "찾아서 vi 로 고치기");
                practice.SelectLessonForTest(viLesson); await Settle(window);
                Type(terminal, "vi recipes/ETCH_A01.rcp"); Key(terminal, System.Windows.Input.Key.Enter); await Settle(window);
                var shellEditor = Find<ViEditorView>(practice, "ShellEditor");
                Check("vi from the terminal opens the embedded editor", practice.ShellForTest.Editing && shellEditor.IsVisible && practice.ShellEditorForTest.Text.Contains("temp = 65"));
                Capture(window, "shell-vi-graphite.png");
                Type(shellEditor, "/65"); Key(shellEditor, System.Windows.Input.Key.Enter); Type(shellEditor, "cw70"); Key(shellEditor, System.Windows.Input.Key.Escape); Type(shellEditor, ":wq"); Key(shellEditor, System.Windows.Input.Key.Enter); await Settle(window);
                Check(":wq in the embedded vi saves and returns to the terminal", !practice.ShellForTest.Editing && terminal.IsVisible && practice.ShellForTest.ReadFile("~/recipes/ETCH_A01.rcp")!.Contains("temp = 70"));
                int ftpLesson = ShellLessons.All.ToList().FindIndex(l => l.Title == "ftp -i 로 파일 받기");
                practice.SelectLessonForTest(ftpLesson); await Settle(window);
                foreach (var line in new[] { "ftp -i ftp-server", "guest", "guest", "cd log/2026-11", "ls" }) { Type(terminal, line); Key(terminal, System.Windows.Input.Key.Enter); }
                await Settle(window);
                Check("ftp logs in from the keyboard and hides the password", practice.ShellForTest.RemoteLoggedIn && !practice.ShellForTest.Screen.Contains("Password: guest"));
                Capture(window, "shell-ftp-graphite.png");
                ThemeService.Apply("nord"); await Settle(window); Capture(window, "shell-ftp-nord.png"); ThemeService.Apply("graphite");
                Find<Button>(practice, "PracticeViTrack").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Settle(window);
                Check("Switching back keeps the vi lesson that was open", !practice.ShellModeForTest && practice.LessonIndexForTest == ViLessons.All.Count - 1 && practice.EngineForTest.ShowNumbers == false && practice.EngineForTest.RecentKeys.Count > 0);
                Find<Button>(practice, "PracticeShellTrack").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Settle(window);
                Check("Switching to the terminal again keeps its session", practice.ShellForTest.RemoteLoggedIn && practice.LessonIndexForTest == ftpLesson);
                Find<Button>(practice, "PracticeViTrack").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Settle(window);
                window.ShowFtpForTest(false); await Settle(window);
                Check("Leaving practice returns to the terminal workspace", window.TerminalModeForTest && !practice.IsVisible);
            }
            finally { window.Close(); }
            if (failures.Count > 0) throw new InvalidOperationException("FAIL: " + string.Join(" | ", failures));
        }
        finally { File.WriteAllText(Path.Combine(AppPaths.Root, "vi-test-report.txt"), report.ToString()); }
    }

    internal static void Type(UIElement target, string text)
    {
        foreach (char c in text)
        {
            var composition = new TextComposition(InputManager.Current, target, c.ToString());
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = UIElement.PreviewTextInputEvent });
        }
    }
    internal static void Key(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        // Keyboard.Modifiers reflects the physical keyboard, so Ctrl chords are sent through the engine directly.
        if (modifiers == ModifierKeys.Control)
        {
            string token = "<C-" + char.ToLowerInvariant(key.ToString()[0]) + ">";
            if (target is ViEditorView view) view.Engine.Feed(token); else if (target is ShellView shell) shell.Engine.Key(token);
            return;
        }
        var source = PresentationSource.FromVisual((Visual)target);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    }
    internal static async Task Settle(Window window) { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    internal static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().First(x => x.Name == name);
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(AppPaths.Root, name)); png.Save(file);
    }
}
