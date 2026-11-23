using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

/// <summary>Checks the vi practice editor and lessons. No files, processes or network are used.</summary>
public static class ViTests
{
    public static async Task RunAsync()
    {
        if (!App.IsTesting) throw new InvalidOperationException("VI tests run only in an explicit test run.");
        Directory.CreateDirectory(AppPaths.Root);
        var report = new StringBuilder();
        void Check(string label, bool ok) { if (!ok) throw new InvalidOperationException("FAIL: " + label); report.AppendLine("PASS: " + label); }
        ViEngine Run(string text, string keys, int row = 0, int col = 0) { var e = new ViEngine(); e.Load(text, row, col); e.FeedKeys(keys); return e; }
        try
        {
            // Modes and insertion
            var e = Run("abc", "ix<Esc>");
            Check("i inserts before the cursor and Esc returns to normal mode", e.Text == "xabc" && e.Mode == ViMode.Normal && e.Current.Col == 0);
            Check("a, A, I and o/O insert at the expected places", Run("abc", "aX<Esc>").Text == "aXbc" && Run("abc", "AX<Esc>").Text == "abcX" && Run("  abc", "$IX<Esc>").Text == "  Xabc" && Run("  a\nb", "ox<Esc>").Text == "  a\n  x\nb" && Run("a", "Ox<Esc>").Text == "x\na");
            Check("Enter and Backspace split and join lines in insert mode", Run("abcd", "lli<CR><Esc>").Text == "ab\ncd" && Run("ab\ncd", "ji<BS><Esc>").Text == "abcd");
            Check("Typed Korean text is inserted as characters", Run("", "i한글 입력<Esc>").Text == "한글 입력");

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
            Check(">> and << indent by four spaces", Run("x", ">>").Text == "    x" && Run("      x", "<<").Text == "  x");

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

            // Ex commands
            e = Run("a\nb\nc", ":3<CR>"); Check(":3 goes to line 3", e.Current.Row == 2);
            e = Run("a", ":set nu<CR>"); Check(":set nu shows line numbers", e.ShowNumbers); e.FeedKeys(":set nonu<CR>"); Check(":set nonu hides them", !e.ShowNumbers);
            e = Run("a", "x:w<CR>"); Check(":w saves in memory only", !e.Modified && e.SaveCount == 1 && e.Message.Contains("연습용"));
            e = Run("a", "x:q<CR>"); Check(":q refuses to quit with unsaved changes", e.QuitBlockedCount == 1 && e.MessageIsError && e.Message.StartsWith("E37"));
            e.FeedKeys(":q!<CR>"); Check(":q! discards changes back to the last save", e.Text == "a" && e.DiscardCount == 1);
            e = Run("a", "x:w<CR>ix<Esc>:e!<CR>"); Check(":e! returns to the last saved text", e.Text == "");
            e = Run("a", ":bogus<CR>"); Check("Unknown commands report E492", e.MessageIsError && e.Message.StartsWith("E492"));
            e = Run("a", ":wq<CR>"); Check(":wq saves and reports a practice quit", e.SaveCount == 1 && !e.MessageIsError);

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
            var random = new Random(4321); string[] pool = ["h", "j", "k", "l", "w", "b", "e", "x", "d", "y", "p", "P", "u", "<C-r>", ".", "i", "a", "o", "O", "<Esc>", "v", "V", ">", "<", "c", "r", "J", "~", "G", "g", "$", "0", "/", ":", "<CR>", "<BS>", "n", "N", "*", "%", "f", "t", "3", "2", "<C-w>", "s", "q", "z", "가"];
            e = new ViEngine(); e.Load("first line\n  second (line)\nthird\n\nfifth word word", 0, 0);
            for (int i = 0; i < 20000; i++)
            {
                e.Feed(pool[random.Next(pool.Length)]);
                if (e.Lines.Count == 0 || e.Windows.Count is < 1 or > 6 || e.Windows.Any(w => w.Row < 0 || w.Row >= e.Lines.Count || w.Col < 0 || w.Col > e.Lines[w.Row].Length))
                    throw new InvalidOperationException("FAIL: random input produced an invalid cursor or buffer at step " + i);
            }
            Check("20,000 random keys keep cursors and windows valid", true);

            // Every lesson can be completed with its example keys.
            for (int i = 0; i < ViLessons.All.Count; i++)
            {
                var lesson = ViLessons.All[i]; if (lesson.Steps.Length == 0) continue;
                var engine = new ViEngine(); engine.Load(lesson.Text, lesson.Row, lesson.Col); int step = 0;
                Check($"Lesson {i + 1} starts incomplete", !lesson.Steps[0].Done(engine));
                foreach (var key in ViEngine.Tokens(lesson.Solution)) { engine.Feed(key); while (step < lesson.Steps.Length && lesson.Steps[step].Done(engine)) step++; }
                Check($"Lesson {i + 1} '{lesson.Title}' is completed by its example keys", step == lesson.Steps.Length);
            }

            // Real workspace: mode switch, keyboard routing and lesson progress.
            ThemeService.Apply("graphite");
            var window = new MainWindow(AppStore.Demo(), true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.ShowViForTest(true); await Settle(window);
                var practice = window.ViWorkspaceForTest!;
                Check("[VI 연습] opens its own workspace and hides terminals", practice.IsVisible && !window.TerminalModeForTest);
                var editor = Find<ViEditorView>(practice, "ViEditor");
                Check("Normal mode turns off Korean IME so commands are Latin keys", !InputMethod.GetIsInputMethodEnabled(editor));
                Type(editor, "i"); await Settle(window);
                Check("Typed i enters insert mode through the real keyboard handlers", practice.EngineForTest.Mode == ViMode.Insert && InputMethod.GetIsInputMethodEnabled(editor));
                Type(editor, "hello vi"); Key(editor, System.Windows.Input.Key.Escape); await Settle(window);
                Check("Lesson 1 completes from real key events", practice.LessonCompleteForTest && Find<TextBlock>(practice, "ViLessonState").Text.Contains("완료"));
                Capture(window, "vi-lesson-complete.png");
                practice.SelectLessonForTest(10); await Settle(window);
                Type(editor, ":vsp"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, ":sp"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, "/temp"); Key(editor, System.Windows.Input.Key.Enter); await Settle(window);
                Check("Split windows render as separate panes", Descendants(editor).OfType<ViWindowView>().Count() == 3);
                Capture(window, "vi-split-graphite.png");
                Key(editor, System.Windows.Input.Key.W, ModifierKeys.Control); Type(editor, "w"); await Settle(window);
                Check("Ctrl+W W from the keyboard switches windows", practice.EngineForTest.WindowSwitches >= 1);
                practice.SelectLessonForTest(ViLessons.All.Count - 1); await Settle(window);
                Type(editor, ":set nu"); Key(editor, System.Windows.Input.Key.Enter); Type(editor, "jVj"); await Settle(window);
                Capture(window, "vi-visual-graphite.png");
                ThemeService.Apply("nord"); await Settle(window); Capture(window, "vi-visual-nord.png"); ThemeService.Apply("graphite");
                window.ShowFtpForTest(false); await Settle(window);
                Check("Leaving VI practice returns to the terminal workspace", window.TerminalModeForTest && !practice.IsVisible);
            }
            finally { window.Close(); }
        }
        finally { File.WriteAllText(Path.Combine(AppPaths.Root, "vi-test-report.txt"), report.ToString()); }
    }

    private static void Type(UIElement target, string text)
    {
        foreach (char c in text)
        {
            var composition = new TextComposition(InputManager.Current, target, c.ToString());
            target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = UIElement.PreviewTextInputEvent });
        }
    }
    private static void Key(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        // Keyboard.Modifiers reflects the physical keyboard, so Ctrl chords are sent through the engine directly.
        if (modifiers == ModifierKeys.Control) { ((ViEditorView)target).Engine.Feed("<C-" + char.ToLowerInvariant(key.ToString()[0]) + ">"); return; }
        var source = PresentationSource.FromVisual((Visual)target);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    }
    private static async Task Settle(Window window) { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().First(x => x.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(AppPaths.Root, name)); png.Save(file);
    }
}
