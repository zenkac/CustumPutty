using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EquipLink;

/// <summary>Terminal-styled view of a <see cref="ViEngine"/>, including split windows and the command line.</summary>
public sealed class ViEditorView : Grid
{
    internal static readonly FontFamily Mono = new("Consolas, D2Coding, Malgun Gothic");
    public ViEngine Engine { get; }
    private readonly Grid host = new();
    private readonly TextBlock bottomText = Ui.Text("", 13), showCmd = Ui.Text("", 12, "#8797AA");
    private readonly List<ViWindowView> views = [];
    private int renderedLayout = -1;

    public ViEditorView(ViEngine engine)
    {
        Engine = engine; Name = "ViEditor"; Focusable = true; FocusVisualStyle = null; ClipToBounds = true; Background = Ui.Brush("#111923");
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(host);
        foreach (var text in new[] { bottomText, showCmd }) { text.FontFamily = Mono; text.TextWrapping = TextWrapping.NoWrap; text.VerticalAlignment = VerticalAlignment.Center; }
        bottomText.TextTrimming = TextTrimming.CharacterEllipsis; bottomText.Name = "ViCommandLine";
        var line = new DockPanel { Margin = new Thickness(10, 4, 10, 5), MinHeight = 20 }; DockPanel.SetDock(showCmd, Dock.Right); line.Children.Add(showCmd); line.Children.Add(bottomText);
        Children.Add(line); SetRow(line, 1);
        InputMethod.SetIsInputMethodEnabled(this, false);
        Engine.Changed += Refresh;
        ThemeService.Changed += Repaint;
        Unloaded += (_, _) => ThemeService.Changed -= Repaint;
        Loaded += (_, _) => { ThemeService.Changed -= Repaint; ThemeService.Changed += Repaint; Refresh(); };
        PreviewMouseDown += (_, _) => Focus();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
        Refresh();
    }

    private void Repaint() { foreach (var view in views) view.Refresh(); }

    private void Refresh()
    {
        if (Engine.LayoutVersion != renderedLayout) { renderedLayout = Engine.LayoutVersion; host.Children.Clear(); views.Clear(); host.Children.Add(Build(Engine.Root)); }
        foreach (var view in views) view.Refresh();
        if (Engine.Mode == ViMode.Command) { bottomText.Text = Engine.CommandText + "▌"; bottomText.Foreground = Ui.Brush("#DCE5F0"); bottomText.FontWeight = FontWeights.Normal; }
        else if (Engine.Message.Length > 0) { bottomText.Text = Engine.Message; bottomText.Foreground = Ui.Brush(Engine.MessageIsError ? "#E78494" : "#DCE5F0"); bottomText.FontWeight = FontWeights.Normal; }
        else
        {
            bottomText.Text = Engine.Mode switch { ViMode.Insert => "-- INSERT --", ViMode.Visual => "-- VISUAL --", ViMode.VisualLine => "-- VISUAL LINE --", _ => "" };
            bottomText.Foreground = Ui.Brush("#5DD6BE"); bottomText.FontWeight = FontWeights.SemiBold;
        }
        showCmd.Text = Engine.PendingKeys;
        // Korean input only while typing text; commands always arrive as Latin keys.
        InputMethod.SetIsInputMethodEnabled(this, Engine.Mode == ViMode.Insert || Engine.CommandIsSearch);
    }

    private UIElement Build(ViLayout node)
    {
        if (node is ViLeaf leaf) { var view = new ViWindowView(Engine, leaf.Window); views.Add(view); return view; }
        var split = (ViSplit)node; var grid = new Grid();
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (i > 0)
            {
                var divider = new Border { Background = Ui.Brush("#2A394A") };
                if (split.Vertical) { grid.ColumnDefinitions.Add(new() { Width = new GridLength(1) }); SetColumn(divider, grid.ColumnDefinitions.Count - 1); }
                else { grid.RowDefinitions.Add(new() { Height = new GridLength(1) }); SetRow(divider, grid.RowDefinitions.Count - 1); }
                grid.Children.Add(divider);
            }
            var child = Build(split.Children[i]);
            if (split.Vertical) { grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); SetColumn(child, grid.ColumnDefinitions.Count - 1); }
            else { grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); SetRow(child, grid.RowDefinitions.Count - 1); }
            grid.Children.Add(child);
        }
        return grid;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // Ctrl+Shift shortcuts belong to the workspace; Alt combinations to Windows.
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) || (modifiers & ModifierKeys.Alt) != 0) return;
        string? token = null;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (key >= Key.A && key <= Key.Z) token = "<C-" + (char)('a' + (key - Key.A)) + ">";
            else if (key == Key.OemOpenBrackets) token = "<Esc>";
        }
        else token = key switch
        {
            Key.Escape => "<Esc>", Key.Enter => "<CR>", Key.Back => "<BS>", Key.Delete => "<Del>", Key.Tab => "<Tab>",
            Key.Left => "<Left>", Key.Right => "<Right>", Key.Up => "<Up>", Key.Down => "<Down>", Key.Home => "<Home>", Key.End => "<End>",
            Key.PageDown => "<C-d>", Key.PageUp => "<C-u>", _ => null
        };
        if (token == null) return;
        e.Handled = true; Engine.Feed(token);
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        var text = e.Text;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsControl(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { Engine.Feed(text.Substring(i, 2)); i++; }
            else Engine.Feed(text[i].ToString());
        }
    }
}

/// <summary>One vi window: text surface plus its status line.</summary>
internal sealed class ViWindowView : Grid
{
    private readonly ViEngine engine; private readonly ViWindow window;
    private readonly ViSurface surface; private readonly Border status = new() { Padding = new Thickness(10, 2, 10, 2) };
    private readonly TextBlock name = Ui.Text("", 12), position = Ui.Text("", 12);
    public ViWindowView(ViEngine engine, ViWindow window)
    {
        this.engine = engine; this.window = window;
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        surface = new ViSurface(engine, window); Children.Add(surface);
        foreach (var text in new[] { name, position }) { text.FontFamily = ViEditorView.Mono; text.TextWrapping = TextWrapping.NoWrap; }
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new DockPanel(); DockPanel.SetDock(position, Dock.Right); row.Children.Add(position); row.Children.Add(name); status.Child = row;
        Children.Add(status); SetRow(status, 1);
    }
    public void Refresh()
    {
        bool active = window == engine.Current;
        status.Background = Ui.Brush(active ? "#25413F" : "#1B2936");
        name.Text = engine.FileName + (engine.Modified ? " [+]" : ""); name.Foreground = position.Foreground = Ui.Brush(active ? "#DCE5F0" : "#8797AA");
        int count = engine.Lines.Count;
        string where = count <= window.Height ? "All" : window.Top == 0 ? "Top" : window.Top + window.Height >= count ? "Bot" : $"{window.Top * 100 / Math.Max(1, count - window.Height)}%";
        position.Text = $"{window.Row + 1},{window.Col + 1}    {where}";
        surface.InvalidateVisual();
    }
}

internal sealed class ViSurface : FrameworkElement
{
    private const double Size = 15, Pad = 8;
    private readonly ViEngine engine; private readonly ViWindow window;
    private readonly Typeface typeface = new(ViEditorView.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double LineHeight => Math.Ceiling(Size * 1.45);

    public ViSurface(ViEngine engine, ViWindow window)
    {
        this.engine = engine; this.window = window; ClipToBounds = true; Cursor = Cursors.IBeam;
        SizeChanged += (_, _) => { engine.SetHeight(window, Math.Max(1, (int)(ActualHeight / LineHeight))); InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(this); int row = Math.Min(engine.Lines.Count - 1, window.Top + (int)(point.Y / LineHeight));
            string line = engine.Lines[row]; double x = point.X - Gutter() - Pad; int col = 0;
            while (col < line.Length && TextWidth(line, col + 1) < x) col++;
            engine.Click(window, row, col);
        };
    }

    private FormattedText Format(string text, Brush brush) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private double TextWidth(string line, int col) => col <= 0 ? 0 : Format(line[..Math.Min(col, line.Length)], Brushes.Black).WidthIncludingTrailingWhitespace;
    private double CharWidth => Format("0", Brushes.Black).WidthIncludingTrailingWhitespace;
    private double Gutter() => engine.ShowNumbers ? (Math.Max(3, engine.Lines.Count.ToString().Length) + 1) * CharWidth : 0;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Ui.Brush("#111923"), null, new Rect(RenderSize));
        var lines = engine.Lines; double lh = LineHeight, gutter = Gutter(), left = gutter + Pad, cw = CharWidth;
        int rows = Math.Max(1, (int)Math.Ceiling(ActualHeight / lh));
        bool current = window == engine.Current, visual = current && engine.Mode is ViMode.Visual or ViMode.VisualLine;
        var (ar, ac) = engine.VisualAnchor; var (sr, sc, er, ec) = (ar, ac, window.Row, window.Col);
        if (sr > er || sr == er && sc > ec) (sr, sc, er, ec) = (er, ec, sr, sc);
        string? highlight = engine.Highlight;
        for (int i = 0; i < rows; i++)
        {
            int r = window.Top + i; double y = i * lh, baseline = y + (lh - Size * 1.18) / 2;
            if (r >= lines.Count) { dc.DrawText(Format("~", Ui.Brush("#687B91")), new Point(Pad, baseline)); continue; }
            string line = lines[r];
            if (engine.ShowNumbers)
            {
                var number = Format((r + 1).ToString(), Ui.Brush(r == window.Row && current ? "#E9C786" : "#687B91"));
                dc.DrawText(number, new Point(gutter - cw - number.Width + Pad / 2, baseline));
            }
            if (highlight != null)
                for (int at = line.IndexOf(highlight, StringComparison.Ordinal); at >= 0; at = line.IndexOf(highlight, at + Math.Max(1, highlight.Length), StringComparison.Ordinal))
                    dc.DrawRectangle(Ui.Brush("#4A3D2A"), null, new Rect(left + TextWidth(line, at), y + 1, Math.Max(cw, TextWidth(line, at + highlight.Length) - TextWidth(line, at)), lh - 2));
            if (visual && r >= sr && r <= er)
            {
                double x1, x2;
                if (engine.Mode == ViMode.VisualLine) { x1 = left; x2 = left + Math.Max(cw, TextWidth(line, line.Length)); }
                else
                {
                    x1 = left + (r == sr ? TextWidth(line, sc) : 0);
                    x2 = left + (r == er ? TextWidth(line, Math.Min(line.Length, ec + 1)) : TextWidth(line, line.Length) + cw);
                    if (x2 <= x1) x2 = x1 + cw;
                }
                dc.DrawRectangle(Ui.Brush("#35685F"), null, new Rect(x1, y, x2 - x1, lh));
            }
            if (line.Length > 0) dc.DrawText(Format(line, Ui.Brush("#DCE5F0")), new Point(left, baseline));
            if (current && r == window.Row)
            {
                int col = Math.Min(window.Col, line.Length); double x = left + TextWidth(line, col);
                string under = col < line.Length ? line.Substring(col, char.IsHighSurrogate(line[col]) && col + 1 < line.Length ? 2 : 1) : " ";
                double w = Math.Max(cw * .6, Format(under, Brushes.Black).WidthIncludingTrailingWhitespace);
                if (engine.Mode == ViMode.Insert) dc.DrawRectangle(Ui.Brush("#5DD6BE"), null, new Rect(x, y + 2, 2, lh - 4));
                else if (engine.Mode == ViMode.Command) dc.DrawRectangle(null, new Pen(Ui.Brush("#5DD6BE"), 1), new Rect(x + .5, y + 1.5, w - 1, lh - 3));
                else
                {
                    dc.DrawRectangle(Ui.Brush("#5DD6BE"), null, new Rect(x, y + 1, w, lh - 2));
                    if (under != " ") dc.DrawText(Format(under, Ui.Brush("#102D29")), new Point(x, baseline));
                }
            }
        }
    }
}

/// <summary>[VI 연습] workspace: lessons, a practice editor and a key reference. Nothing here connects anywhere.</summary>
public sealed class ViPracticeWorkspace : Grid
{
    private readonly ViEngine engine = new();
    private readonly ViEditorView editor;
    private readonly ListBox lessons = new() { Name = "ViLessons" };
    private readonly TextBlock title = Ui.Text("", 20, "#DCE5F0", true), keys = Ui.Text("", 12, "#5DD6BE", true), explain = Ui.Text("", 13, "#A8B7C9"),
        state = Ui.Text("", 12, "#8797AA", true), history = Ui.Text("", 12, "#8797AA"), hint = Ui.Text("", 12, "#E9C786");
    private readonly StackPanel steps = new();
    private readonly Button next, solution;
    private readonly Border editorFrame;
    private readonly HashSet<int> completed = [];
    private int lessonIndex = -1, stepIndex;

    public ViPracticeWorkspace()
    {
        Name = "ViPractice"; Background = Ui.Brush("#111923");
        ColumnDefinitions.Add(new() { Width = new GridLength(250) }); ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 }); ColumnDefinitions.Add(new() { Width = new GridLength(330) });

        // Lessons
        var side = new Grid { Margin = new Thickness(12, 14, 10, 10) };
        side.RowDefinitions.Add(new() { Height = GridLength.Auto }); side.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var intro = Ui.Stack(Ui.Text("VI 연습", 15, "#DCE5F0", true), Ui.Text("어디에도 연결하지 않는 연습용 vi 편집기입니다. 과제를 순서대로 따라 해 보세요.", 11, "#8797AA"));
        ((FrameworkElement)intro.Children[1]).Margin = new Thickness(0, 4, 0, 12); side.Children.Add(intro);
        lessons.SelectionChanged += (_, _) => { if (lessons.SelectedIndex >= 0 && lessons.SelectedIndex != lessonIndex) LoadLesson(lessons.SelectedIndex); };
        side.Children.Add(lessons); SetRow(lessons, 1);
        var note = Ui.Text("연습창의 :w 는 실제 파일을 만들지 않습니다. 실제 vim과 일부 동작(정규식 등)이 다를 수 있습니다.", 10, "#687B91"); note.Margin = new Thickness(2, 10, 0, 0);
        side.Children.Add(note); SetRow(note, 2);
        var sidePanel = new Border { Background = Ui.Brush("#17212D"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        Children.Add(sidePanel);

        // Lesson card, editor and key history
        var center = new Grid { Margin = new Thickness(18, 14, 18, 12) };
        center.RowDefinitions.Add(new() { Height = GridLength.Auto }); center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 }); center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var restart = Ui.Button("처음부터", () => LoadLesson(lessonIndex)); restart.Name = "ViRestart";
        solution = Ui.Button("예시 입력 보기", ShowSolution); solution.Name = "ViSolution";
        next = Ui.Button("다음 과제 →", () => { if (lessonIndex + 1 < ViLessons.All.Count) lessons.SelectedIndex = lessonIndex + 1; }); next.Name = "ViNext";
        var actions = Ui.Row(restart, solution, next); actions.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(actions, Dock.Right);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; keys.FontFamily = ViEditorView.Mono; keys.Margin = new Thickness(0, 4, 0, 0);
        var heading = new DockPanel(); heading.Children.Add(actions); heading.Children.Add(Ui.Stack(title, keys));
        explain.Margin = new Thickness(0, 10, 0, 10); steps.Margin = new Thickness(0, 0, 0, 6); state.Name = "ViLessonState"; hint.FontFamily = ViEditorView.Mono; hint.Visibility = Visibility.Collapsed; hint.Margin = new Thickness(0, 6, 0, 0);
        var card = Ui.Card(Ui.Stack(heading, explain, steps, state, hint), "#17212D", 18); card.Margin = new Thickness(0, 0, 0, 12);
        center.Children.Add(card);
        editor = new ViEditorView(engine);
        editorFrame = new Border { Child = editor, BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(1), ClipToBounds = true, Background = Ui.Brush("#111923") };
        editor.GotKeyboardFocus += (_, _) => editorFrame.BorderBrush = Ui.Brush("#5DD6BE");
        editor.LostKeyboardFocus += (_, _) => editorFrame.BorderBrush = Ui.Brush("#2A394A");
        center.Children.Add(editorFrame); SetRow(editorFrame, 1);
        history.FontFamily = ViEditorView.Mono; history.TextWrapping = TextWrapping.NoWrap; history.TextTrimming = TextTrimming.CharacterEllipsis; history.Margin = new Thickness(2, 8, 0, 0); history.Name = "ViKeyHistory";
        center.Children.Add(history); SetRow(history, 2);
        Children.Add(center); SetColumn(center, 1);

        // Key reference
        var sheet = new StackPanel { Margin = new Thickness(16, 14, 14, 16) };
        sheet.Children.Add(Ui.Text("단축키 모음", 15, "#DCE5F0", true));
        var sheetHint = Ui.Text("명령 모드(Esc)에서 누릅니다. 입력 모드에서는 글자로 입력됩니다.", 11, "#8797AA"); sheetHint.Margin = new Thickness(0, 4, 0, 4); sheet.Children.Add(sheetHint);
        foreach (var (section, items) in ViLessons.CheatSheet)
        {
            var header = Ui.Text(section, 12, "#5DD6BE", true); header.Margin = new Thickness(0, 14, 0, 6); sheet.Children.Add(header);
            foreach (var (key, text) in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(136) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var label = Ui.Text(key, 12, "#E9C786", true); label.FontFamily = ViEditorView.Mono; label.TextWrapping = TextWrapping.NoWrap;
                var chip = new Border { Child = label, Background = Ui.Brush("#1B2936"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var description = Ui.Text(text, 12, "#A8B7C9"); description.Margin = new Thickness(8, 1, 0, 0); SetColumn(description, 1);
                row.Children.Add(chip); row.Children.Add(description); sheet.Children.Add(row);
            }
        }
        var sheetPanel = new Border { Background = Ui.Brush("#17212D"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = sheet, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Children.Add(sheetPanel); SetColumn(sheetPanel, 2);

        engine.Changed += OnEngineChanged;
        RenderLessonList();
        lessons.SelectedIndex = 0;
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusEditor(); };
    }

    internal ViEngine EngineForTest => engine;
    internal int LessonIndexForTest => lessonIndex;
    internal int StepIndexForTest => stepIndex;
    internal bool LessonCompleteForTest => completed.Contains(lessonIndex);
    internal void SelectLessonForTest(int index) => lessons.SelectedIndex = index;

    public void FocusEditor() => Dispatcher.BeginInvoke(() => { if (IsVisible) { editor.Focus(); Keyboard.Focus(editor); } }, System.Windows.Threading.DispatcherPriority.Input);

    private void RenderLessonList()
    {
        int selected = lessons.SelectedIndex; lessons.Items.Clear();
        for (int i = 0; i < ViLessons.All.Count; i++)
        {
            var lesson = ViLessons.All[i]; bool done = completed.Contains(i);
            var number = Ui.Text(done ? "✓" : (i + 1).ToString("00"), 11, done ? "#69DDC0" : "#687B91", true); number.Width = 24; number.FontFamily = ViEditorView.Mono;
            var name = Ui.Text(lesson.Title, 12, "#DCE5F0", true); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var detail = Ui.Text(lesson.Keys, 10, "#8797AA"); detail.FontFamily = ViEditorView.Mono; detail.TextWrapping = TextWrapping.NoWrap; detail.Margin = new Thickness(0, 2, 0, 0);
            var row = new DockPanel(); DockPanel.SetDock(number, Dock.Left); row.Children.Add(number); row.Children.Add(Ui.Stack(name, detail));
            lessons.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(8, 6, 8, 6) });
        }
        if (selected >= 0) lessons.SelectedIndex = selected;
    }

    private void LoadLesson(int index)
    {
        if (index < 0 || index >= ViLessons.All.Count) return;
        lessonIndex = index; stepIndex = 0; var lesson = ViLessons.All[index];
        title.Text = $"{index + 1:00}  {lesson.Title}"; keys.Text = lesson.Keys; explain.Text = lesson.Explain;
        hint.Visibility = Visibility.Collapsed; solution.Visibility = lesson.Solution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        engine.FileName = index == ViLessons.All.Count - 1 ? "recipe.conf" : $"lesson{index + 1:00}.txt";
        engine.Load(lesson.Text, lesson.Row, lesson.Col);
        FocusEditor();
    }

    private void ShowSolution()
    {
        var lesson = ViLessons.All[lessonIndex];
        hint.Text = "예시 입력:  " + string.Join(" ", ViEngine.Tokens(lesson.Solution).Select(ViEngine.Display));
        hint.Visibility = Visibility.Visible; FocusEditor();
    }

    private void OnEngineChanged()
    {
        if (lessonIndex < 0) return;
        var lesson = ViLessons.All[lessonIndex];
        while (stepIndex < lesson.Steps.Length && lesson.Steps[stepIndex].Done(engine)) stepIndex++;
        bool done = lesson.Steps.Length > 0 && stepIndex >= lesson.Steps.Length;
        if (done && completed.Add(lessonIndex)) RenderLessonList();
        steps.Children.Clear();
        for (int i = 0; i < lesson.Steps.Length; i++)
        {
            bool finished = i < stepIndex, now = i == stepIndex;
            var mark = Ui.Text(finished ? "✓" : now ? "▶" : "○", 12, finished ? "#69DDC0" : now ? "#5DD6BE" : "#687B91", true); mark.Width = 20;
            var text = Ui.Text(lesson.Steps[i].Text, 13, finished ? "#8797AA" : now ? "#DCE5F0" : "#A8B7C9", now);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark); row.Children.Add(text); steps.Children.Add(row);
        }
        state.Text = lesson.Steps.Length == 0 ? "자유 연습 · 마음껏 입력해 보세요. 막히면 Esc 를 누르고 [처음부터]를 누르세요."
            : done ? (lessonIndex + 1 < ViLessons.All.Count ? "✓ 과제 완료!  [다음 과제 →]로 넘어가세요." : "✓ 모든 과제를 마쳤습니다! 자유 연습에서 복습해 보세요.")
            : $"진행 {stepIndex} / {lesson.Steps.Length}" + (engine.Mode == ViMode.Insert ? " · 입력 모드입니다. 명령을 쓰려면 Esc" : "");
        state.Foreground = Ui.Brush(done ? "#69DDC0" : "#8797AA");
        next.Style = (Style)Application.Current.FindResource(done ? "PrimaryButton" : typeof(Button));
        if (next.Content is TextBlock label) label.Foreground = Ui.Brush(done ? "#102D29" : "#DCE5F0");
        next.IsEnabled = lessonIndex + 1 < ViLessons.All.Count;
        history.Text = engine.RecentKeys.Count == 0 ? "입력한 키가 여기에 표시됩니다" : "입력한 키   " + string.Join(" ", engine.RecentKeys.TakeLast(32));
    }
}
