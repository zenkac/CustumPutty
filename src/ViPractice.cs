using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EquipLink;

/// <summary>Terminal-styled view of a <see cref="ViEngine"/>, including split windows, color schemes and the command line.</summary>
public sealed class ViEditorView : Grid
{
    internal static readonly FontFamily Mono = new("Consolas, D2Coding, Malgun Gothic");
    public ViEngine Engine { get; }
    private readonly Grid host = new();
    private readonly TextBlock bottomText = Ui.Text("", 13), showCmd = Ui.Text("", 12, "#8797AA");
    private readonly List<ViWindowView> views = [];
    private readonly List<Border> dividers = [];
    private int renderedLayout = -1;

    public ViEditorView(ViEngine engine, string name = "ViEditor")
    {
        Engine = engine; Name = name; Focusable = true; FocusVisualStyle = null; ClipToBounds = true;
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(host);
        foreach (var text in new[] { bottomText, showCmd }) { text.FontFamily = Mono; text.TextWrapping = TextWrapping.NoWrap; text.VerticalAlignment = VerticalAlignment.Center; }
        bottomText.TextTrimming = TextTrimming.CharacterEllipsis; bottomText.Name = name == "ViEditor" ? "ViCommandLine" : name + "CommandLine";
        var line = new DockPanel { Margin = new Thickness(10, 4, 10, 5), MinHeight = 20 }; DockPanel.SetDock(showCmd, Dock.Right); line.Children.Add(showCmd); line.Children.Add(bottomText);
        Children.Add(line); SetRow(line, 1);
        InputMethod.SetIsInputMethodEnabled(this, false);
        Engine.Changed += Refresh;
        ThemeService.Changed += Repaint;
        Unloaded += (_, _) => ThemeService.Changed -= Repaint;
        Loaded += (_, _) => { ThemeService.Changed -= Repaint; ThemeService.Changed += Repaint; Refresh(); };
        PreviewMouseDown += (_, e) =>
        {
            Focus();
            if (e.ChangedButton != MouseButton.Right) return;
            e.Handled = true;
            try { if (Clipboard.GetText() is { Length: > 0 } text) Engine.Paste(text); } catch { }
        };
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
        Refresh();
    }

    private void Repaint() => Refresh();

    private void Refresh()
    {
        if (Engine.LayoutVersion != renderedLayout) { renderedLayout = Engine.LayoutVersion; host.Children.Clear(); views.Clear(); dividers.Clear(); host.Children.Add(Build(Engine.Root)); }
        var scheme = ViColorSchemes.Get(Engine.ColorScheme);
        Background = scheme.Background;
        foreach (var divider in dividers) divider.Background = scheme.StatusInactive;
        foreach (var view in views) view.Refresh(scheme);
        string recording = Engine.Recording is char reg ? $"recording @{reg}" : "";
        if (Engine.Mode == ViMode.Command) { bottomText.Text = Engine.CommandText + "▌"; bottomText.Foreground = scheme.Foreground; bottomText.FontWeight = FontWeights.Normal; }
        else if (Engine.Message.Length > 0) { bottomText.Text = Engine.Message; bottomText.Foreground = Engine.MessageIsError ? scheme.ErrorText : scheme.Foreground; bottomText.FontWeight = FontWeights.Normal; }
        else
        {
            string mode = Engine.Mode switch { ViMode.Insert => "-- INSERT --", ViMode.Visual => "-- VISUAL --", ViMode.VisualLine => "-- VISUAL LINE --", ViMode.VisualBlock => "-- VISUAL BLOCK --", _ => "" };
            bottomText.Text = mode + recording; bottomText.Foreground = scheme.ModeText; bottomText.FontWeight = FontWeights.SemiBold;
        }
        showCmd.Text = Engine.PendingKeys; showCmd.Foreground = scheme.NonText;
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
                var divider = new Border(); dividers.Add(divider);
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
    public void Refresh(ViScheme scheme)
    {
        bool active = window == engine.Current;
        status.Background = active ? scheme.Status : scheme.StatusInactive;
        name.Text = engine.FileName + (engine.Modified ? " [+]" : ""); name.Foreground = position.Foreground = active ? scheme.StatusText : scheme.StatusInactiveText;
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
    private double Gutter() => engine.ShowNumbers || engine.RelativeNumbers ? (Math.Max(3, engine.Lines.Count.ToString().Length) + 1) * CharWidth : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var scheme = ViColorSchemes.Get(engine.ColorScheme);
        dc.DrawRectangle(scheme.Background, null, new Rect(RenderSize));
        var lines = engine.Lines; double lh = LineHeight, gutter = Gutter(), left = gutter + Pad, cw = CharWidth;
        int rows = Math.Max(1, (int)Math.Ceiling(ActualHeight / lh));
        bool current = window == engine.Current, visual = current && engine.IsVisual;
        var (ar, ac) = engine.VisualAnchor; var (sr, sc, er, ec) = (ar, ac, window.Row, window.Col);
        if (sr > er || sr == er && sc > ec) (sr, sc, er, ec) = (er, ec, sr, sc);
        var block = current ? engine.BlockColumns : null;
        string type = engine.FileType; bool syntax = engine.SyntaxOn && type != "text";
        bool comment = false;
        if (syntax) for (int r = 0; r < Math.Min(window.Top, lines.Count); r++) ViSyntax.Spans(lines[r], type, ref comment);
        for (int i = 0; i < rows; i++)
        {
            int r = window.Top + i; double y = i * lh, baseline = y + (lh - Size * 1.18) / 2;
            if (r >= lines.Count) { dc.DrawText(Format("~", scheme.NonText), new Point(Pad, baseline)); continue; }
            string line = lines[r];
            if (current && engine.CursorLine && r == window.Row && !visual) dc.DrawRectangle(scheme.CursorLine, null, new Rect(0, y, ActualWidth, lh));
            if (engine.ShowNumbers || engine.RelativeNumbers)
            {
                int value = engine.RelativeNumbers ? (r == window.Row ? (engine.ShowNumbers ? r + 1 : 0) : Math.Abs(r - window.Row)) : r + 1;
                var number = Format(value.ToString(), r == window.Row && current ? scheme.CursorLineNr : scheme.LineNr);
                dc.DrawText(number, new Point(gutter - cw - number.Width + Pad / 2, baseline));
            }
            var highlights = engine.HighlightRanges(r).ToList();
            foreach (var (start, length) in highlights)
                dc.DrawRectangle(scheme.Search, null, new Rect(left + TextWidth(line, start), y + 1, Math.Max(cw, TextWidth(line, start + length) - TextWidth(line, start)), lh - 2));
            if (visual && r >= sr && r <= er)
            {
                double x1, x2;
                if (block is { } b)
                {
                    x1 = left + b.Left * cw; x2 = b.ToEnd ? left + Math.Max(TextWidth(line, line.Length), b.Left * cw) + cw : left + (b.Right + 1) * cw;
                    if (line.Length > b.Left) { x1 = left + TextWidth(line, b.Left); if (!b.ToEnd) x2 = left + (b.Right < line.Length ? TextWidth(line, b.Right + 1) : TextWidth(line, line.Length) + (b.Right + 1 - line.Length) * cw); }
                }
                else if (engine.Mode == ViMode.VisualLine) { x1 = left; x2 = left + Math.Max(cw, TextWidth(line, line.Length)); }
                else
                {
                    x1 = left + (r == sr ? TextWidth(line, sc) : 0);
                    x2 = left + (r == er ? TextWidth(line, Math.Min(line.Length, ec + 1)) : TextWidth(line, line.Length) + cw);
                }
                if (x2 <= x1) x2 = x1 + cw;
                dc.DrawRectangle(scheme.Visual, null, new Rect(x1, y, x2 - x1, lh));
            }
            string shown = line; int trailing = 0;
            if (engine.ListChars) { trailing = line.Length - line.TrimEnd(' ').Length; if (trailing > 0) shown = line[..^trailing] + new string('·', trailing); }
            if (shown.Length > 0)
            {
                var text = Format(shown, scheme.Foreground);
                if (syntax) foreach (var (start, length, group) in ViSyntax.Spans(line, type, ref comment))
                    if (start >= 0 && length > 0 && start + length <= shown.Length) text.SetForegroundBrush(scheme.Group(group), start, length);
                if (!scheme.FollowsTheme) foreach (var (start, length) in highlights) if (start + length <= shown.Length) text.SetForegroundBrush(scheme.SearchText, start, length);
                if (trailing > 0) text.SetForegroundBrush(scheme.NonText, shown.Length - trailing, trailing);
                dc.DrawText(text, new Point(left, baseline));
            }
            else if (syntax) ViSyntax.Spans(line, type, ref comment);
            if (engine.ListChars) dc.DrawText(Format("$", scheme.NonText), new Point(left + TextWidth(line, line.Length), baseline));
            if (current && r == window.Row)
            {
                int col = Math.Min(window.Col, line.Length); double x = left + TextWidth(line, col);
                string under = col < line.Length ? line.Substring(col, char.IsHighSurrogate(line[col]) && col + 1 < line.Length ? 2 : 1) : " ";
                double w = Math.Max(cw * .6, Format(under, Brushes.Black).WidthIncludingTrailingWhitespace);
                if (engine.Mode == ViMode.Insert) dc.DrawRectangle(scheme.Cursor, null, new Rect(x, y + 2, 2, lh - 4));
                else if (engine.Mode == ViMode.Command) dc.DrawRectangle(null, new Pen(scheme.Cursor, 1), new Rect(x + .5, y + 1.5, w - 1, lh - 3));
                else
                {
                    dc.DrawRectangle(scheme.Cursor, null, new Rect(x, y + 1, w, lh - 2));
                    if (under != " ") dc.DrawText(Format(under, scheme.CursorText), new Point(x, baseline));
                }
            }
        }
    }
}

/// <summary>
/// [연습] workspace: vi editor and terminal command lessons in three levels, a practice
/// editor or terminal, and a key reference. Nothing here connects anywhere.
/// </summary>
public sealed class PracticeWorkspace : Grid
{
    private readonly ViEngine engine = new();
    private readonly ShellEngine shell = new();
    private readonly ViEngine shellEditor = new() { Embedded = true };
    private readonly ViEditorView editor, shellEditorView;
    private readonly ShellView terminal;
    private readonly Grid stage = new();
    private readonly ListBox lessons = new() { Name = "PracticeLessons" };
    private readonly Button viTrack, shellTrack, next, solution;
    private readonly TextBlock title = Ui.Text("", 20, "#DCE5F0", true), keys = Ui.Text("", 12, "#5DD6BE", true), explain = Ui.Text("", 13, "#A8B7C9"),
        state = Ui.Text("", 12, "#8797AA", true), history = Ui.Text("", 12, "#8797AA"), hint = Ui.Text("", 12, "#E9C786"), level = Ui.Text("", 11, "#102D29", true), sheetTitle = Ui.Text("", 15, "#DCE5F0", true), sheetHint = Ui.Text("", 11, "#8797AA");
    private readonly Border levelChip, stageFrame;
    private readonly StackPanel steps = new(), sheet = new() { Margin = new Thickness(16, 14, 14, 16) };
    private readonly HashSet<string> completed = [];
    private readonly List<int?> rows = [];
    private readonly int[] lastLesson = [0, 0], loadedLesson = [-1, -1];
    private bool shellMode, rendering;
    private int lessonIndex = -1, stepIndex;

    public PracticeWorkspace()
    {
        Name = "PracticeWorkspace"; Background = Ui.Brush("#111923");
        ColumnDefinitions.Add(new() { Width = new GridLength(262) }); ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 }); ColumnDefinitions.Add(new() { Width = new GridLength(330) });

        // Track switch and lessons
        var side = new Grid { Margin = new Thickness(12, 14, 10, 10) };
        for (int i = 0; i < 3; i++) side.RowDefinitions.Add(new() { Height = GridLength.Auto });
        side.RowDefinitions.Insert(2, new() { Height = new GridLength(1, GridUnitType.Star) });
        var intro = Ui.Stack(Ui.Text("연습", 15, "#DCE5F0", true), Ui.Text("어디에도 연결하지 않는 연습 공간입니다. 기초부터 순서대로 따라 해 보세요.", 11, "#8797AA"));
        ((FrameworkElement)intro.Children[1]).Margin = new Thickness(0, 4, 0, 10); side.Children.Add(intro);
        viTrack = Ui.ToolButton("vi 편집기", () => SelectTrack(false), "vi 키 연습 · 기초 / 중급 / 고급"); viTrack.Name = "PracticeViTrack";
        shellTrack = Ui.ToolButton("터미널 명령어", () => SelectTrack(true), "리눅스 명령 · find · grep · ftp 연습"); shellTrack.Name = "PracticeShellTrack";
        var tracks = Ui.Segmented(true, viTrack, shellTrack); tracks.Margin = new Thickness(0, 0, 0, 10);
        side.Children.Add(tracks); SetRow(tracks, 1);
        VirtualizingPanel.SetIsVirtualizing(lessons, false);
        lessons.SelectionChanged += (_, _) =>
        {
            if (rendering || lessons.SelectedIndex < 0 || lessons.SelectedIndex >= rows.Count) return;
            if (rows[lessons.SelectedIndex] is int index && index != lessonIndex) LoadLesson(index);
        };
        side.Children.Add(lessons); SetRow(lessons, 2);
        var note = Ui.Text("연습용 vi · 셸은 실제 파일·장비·네트워크를 쓰지 않습니다. 실제 vim · bash 와 일부 동작이 다를 수 있습니다.", 10, "#687B91"); note.Margin = new Thickness(2, 10, 0, 0);
        side.Children.Add(note); SetRow(note, 3);
        var sidePanel = new Border { Background = Ui.Brush("#17212D"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        Children.Add(sidePanel);

        // Lesson card, practice area and key history
        var center = new Grid { Margin = new Thickness(18, 14, 18, 12) };
        center.RowDefinitions.Add(new() { Height = GridLength.Auto }); center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 }); center.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var restart = Ui.Button("처음부터", () => LoadLesson(lessonIndex)); restart.Name = "PracticeRestart";
        solution = Ui.Button("예시 입력 보기", ShowSolution); solution.Name = "PracticeSolution";
        next = Ui.Button("다음 과제 →", () => { if (lessonIndex + 1 < Count) SelectLesson(lessonIndex + 1); }); next.Name = "PracticeNext";
        var actions = Ui.Row(restart, solution, next); actions.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(actions, Dock.Right);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; keys.FontFamily = ViEditorView.Mono; keys.Margin = new Thickness(0, 4, 0, 0); keys.TextWrapping = TextWrapping.NoWrap; keys.TextTrimming = TextTrimming.CharacterEllipsis;
        levelChip = new Border { Child = level, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new DockPanel(); DockPanel.SetDock(levelChip, Dock.Left); titleRow.Children.Add(levelChip); titleRow.Children.Add(title);
        var heading = new DockPanel(); heading.Children.Add(actions); heading.Children.Add(Ui.Stack(titleRow, keys));
        explain.Margin = new Thickness(0, 10, 0, 10); steps.Margin = new Thickness(0, 0, 0, 6); state.Name = "PracticeState"; hint.FontFamily = ViEditorView.Mono; hint.Visibility = Visibility.Collapsed; hint.Margin = new Thickness(0, 6, 0, 0);
        var card = Ui.Card(Ui.Stack(heading, explain, steps, state, hint), "#17212D", 18); card.Margin = new Thickness(0, 0, 0, 12);
        center.Children.Add(card);
        editor = new ViEditorView(engine);
        shellEditorView = new ViEditorView(shellEditor, "ShellEditor");
        terminal = new ShellView(shell);
        stageFrame = new Border { Child = stage, BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(1), ClipToBounds = true, Background = Ui.Brush("#111923") };
        foreach (UIElement view in new UIElement[] { editor, shellEditorView, terminal })
        {
            view.GotKeyboardFocus += (_, _) => stageFrame.BorderBrush = Ui.Brush("#5DD6BE");
            view.LostKeyboardFocus += (_, _) => stageFrame.BorderBrush = Ui.Brush("#2A394A");
        }
        center.Children.Add(stageFrame); SetRow(stageFrame, 1);
        history.FontFamily = ViEditorView.Mono; history.TextWrapping = TextWrapping.NoWrap; history.TextTrimming = TextTrimming.CharacterEllipsis; history.Margin = new Thickness(2, 8, 0, 0); history.Name = "PracticeKeyHistory";
        center.Children.Add(history); SetRow(history, 2);
        Children.Add(center); SetColumn(center, 1);

        // Key reference
        var sheetPanel = new Border { Background = Ui.Brush("#17212D"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = sheet, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Children.Add(sheetPanel); SetColumn(sheetPanel, 2);

        engine.Changed += OnProgress;
        shell.Changed += OnProgress;
        shellEditor.Changed += OnProgress;
        shell.EditRequested += OpenShellEditor;
        shellEditor.SaveHandler = shell.SaveFromEditor;
        shellEditor.ExitHandler = CloseShellEditor;
        SelectTrack(false);
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusEditor(); };
    }

    internal ViEngine EngineForTest => engine;
    internal ShellEngine ShellForTest => shell;
    internal ViEngine ShellEditorForTest => shellEditor;
    internal bool ShellModeForTest => shellMode;
    internal int LessonIndexForTest => lessonIndex;
    internal int StepIndexForTest => stepIndex;
    internal bool LessonCompleteForTest => completed.Contains(Key(lessonIndex));
    internal void SelectLessonForTest(int index) => SelectLesson(index);
    internal void SelectTrackForTest(bool terminalCommands) => SelectTrack(terminalCommands);

    private int Count => shellMode ? ShellLessons.All.Count : ViLessons.All.Count;
    private string Key(int index) => (shellMode ? "sh:" : "vi:") + index;
    private PracticeLevel LevelOf(int index) => shellMode ? ShellLessons.All[index].Level : ViLessons.All[index].Level;

    public void FocusEditor() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsVisible) return;
        UIElement target = !shellMode ? editor : shell.Editing ? shellEditorView : terminal;
        target.Focus(); Keyboard.Focus(target);
    }, System.Windows.Threading.DispatcherPriority.Input);

    private void SelectTrack(bool terminalCommands)
    {
        if (lessonIndex >= 0) lastLesson[shellMode ? 1 : 0] = lessonIndex;
        shellMode = terminalCommands; lessonIndex = -1;
        Ui.SetSegment(viTrack, !shellMode); Ui.SetSegment(shellTrack, shellMode);
        BuildSheet();
        // Coming back to a track keeps the lesson and whatever was typed there.
        int index = lastLesson[shellMode ? 1 : 0];
        if (loadedLesson[shellMode ? 1 : 0] == index) { LoadLesson(index, keep: true); RenderLessonList(); }
        else { RenderLessonList(); SelectLesson(index); }
    }

    private void SelectLesson(int index)
    {
        int row = rows.IndexOf(index);
        if (row >= 0 && lessons.SelectedIndex != row) lessons.SelectedIndex = row;
        if (lessonIndex != index) LoadLesson(index);
        if (row >= 0) lessons.ScrollIntoView(lessons.Items[row]);
    }

    private void BuildSheet()
    {
        sheet.Children.Clear();
        sheetTitle.Text = shellMode ? "명령어 모음" : "단축키 모음";
        sheetHint.Text = shellMode ? "프롬프트($)에서 입력하고 Enter 를 누릅니다. 명령 --help 또는 man 명령 으로 설명을 볼 수 있습니다." : "명령 모드(Esc)에서 누릅니다. 입력 모드에서는 글자로 입력됩니다.";
        sheetHint.Margin = new Thickness(0, 4, 0, 4);
        sheet.Children.Add(sheetTitle); sheet.Children.Add(sheetHint);
        foreach (var (section, items) in shellMode ? ShellLessons.CheatSheet : ViLessons.CheatSheet)
        {
            var header = Ui.Text(section, 12, "#5DD6BE", true); header.Margin = new Thickness(0, 14, 0, 6); sheet.Children.Add(header);
            foreach (var (key, text) in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(shellMode ? 150 : 136) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var label = Ui.Text(key, 12, "#E9C786", true); label.FontFamily = ViEditorView.Mono; label.TextWrapping = TextWrapping.Wrap;
                var chip = new Border { Child = label, Background = Ui.Brush("#1B2936"), BorderBrush = Ui.Brush("#2A394A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var description = Ui.Text(text, 12, "#A8B7C9"); description.Margin = new Thickness(8, 1, 0, 0); SetColumn(description, 1);
                row.Children.Add(chip); row.Children.Add(description); sheet.Children.Add(row);
            }
        }
    }

    private static string LevelColor(PracticeLevel level) => level switch { PracticeLevel.Basic => "#69DDC0", PracticeLevel.Intermediate => "#E9C786", _ => "#E78494" };

    private void RenderLessonList()
    {
        rendering = true;
        try
        {
            lessons.Items.Clear(); rows.Clear(); PracticeLevel? group = null;
            for (int i = 0; i < Count; i++)
            {
                var lessonLevel = LevelOf(i);
                if (lessonLevel != group)
                {
                    group = lessonLevel; int total = Enumerable.Range(0, Count).Count(k => LevelOf(k) == lessonLevel), done = Enumerable.Range(0, Count).Count(k => LevelOf(k) == lessonLevel && completed.Contains(Key(k)));
                    var label = Ui.Text(PracticeLevels.Name(lessonLevel), 11, LevelColor(lessonLevel), true);
                    var count = Ui.Text($"{done} / {total}", 10, "#687B91"); DockPanel.SetDock(count, Dock.Right); count.VerticalAlignment = VerticalAlignment.Center;
                    var header = new DockPanel { Margin = new Thickness(0, rows.Count == 0 ? 0 : 8, 0, 0) }; header.Children.Add(count); header.Children.Add(label);
                    lessons.Items.Add(new ListBoxItem { Content = header, IsEnabled = false, Focusable = false, Padding = new Thickness(8, 4, 8, 2) }); rows.Add(null);
                }
                string name = shellMode ? ShellLessons.All[i].Title : ViLessons.All[i].Title, detail = shellMode ? ShellLessons.All[i].Keys : ViLessons.All[i].Keys;
                bool done2 = completed.Contains(Key(i));
                var number = Ui.Text(done2 ? "✓" : (i + 1).ToString("00"), 11, done2 ? "#69DDC0" : "#687B91", true); number.Width = 24; number.FontFamily = ViEditorView.Mono;
                var nameText = Ui.Text(name, 12, "#DCE5F0", true); nameText.TextWrapping = TextWrapping.NoWrap; nameText.TextTrimming = TextTrimming.CharacterEllipsis;
                var detailText = Ui.Text(detail, 10, "#8797AA"); detailText.FontFamily = ViEditorView.Mono; detailText.TextWrapping = TextWrapping.NoWrap; detailText.TextTrimming = TextTrimming.CharacterEllipsis; detailText.Margin = new Thickness(0, 2, 0, 0);
                var row = new DockPanel(); DockPanel.SetDock(number, Dock.Left); row.Children.Add(number); row.Children.Add(Ui.Stack(nameText, detailText));
                lessons.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(8, 6, 8, 6) }); rows.Add(i);
            }
            int selected = rows.IndexOf(lessonIndex); if (selected >= 0) lessons.SelectedIndex = selected;
        }
        finally { rendering = false; }
    }

    private void LoadLesson(int index, bool keep = false)
    {
        if (index < 0 || index >= Count) return;
        lessonIndex = index; stepIndex = 0; hint.Visibility = Visibility.Collapsed; loadedLesson[shellMode ? 1 : 0] = index;
        var lessonLevel = LevelOf(index);
        level.Text = PracticeLevels.Name(lessonLevel); levelChip.Background = Ui.Brush(LevelColor(lessonLevel));
        if (shellMode)
        {
            var lesson = ShellLessons.All[index];
            title.Text = $"{index + 1:00}  {lesson.Title}"; keys.Text = lesson.Keys; explain.Text = lesson.Explain;
            solution.Visibility = lesson.Solution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!keep) { if (shell.Editing) CloseShellEditor(); shell.Reset(lesson.Start); }
            ShowStage(shell.Editing ? shellEditorView : terminal);
        }
        else
        {
            var lesson = ViLessons.All[index];
            title.Text = $"{index + 1:00}  {lesson.Title}"; keys.Text = lesson.Keys; explain.Text = lesson.Explain;
            solution.Visibility = lesson.Solution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!keep) { engine.FileName = lesson.File.Length > 0 ? lesson.File : $"lesson{index + 1:00}.txt"; engine.Load(lesson.Text, lesson.Row, lesson.Col); }
            ShowStage(editor);
        }
        OnProgress(); FocusEditor();
    }

    private void ShowStage(UIElement view)
    {
        if (stage.Children.Count == 1 && stage.Children[0] == view) return;
        stage.Children.Clear(); stage.Children.Add(view);
    }

    private void OpenShellEditor(string path, string content)
    {
        string text = content.EndsWith('\n') ? content[..^1] : content;
        shellEditor.FileName = ShellFs.BaseName(path);
        int start = shell.EditStartLine == int.MaxValue ? int.MaxValue : Math.Max(0, shell.EditStartLine - 1);
        shellEditor.Load(text, start == int.MaxValue ? text.Count(c => c == '\n') : start, 0);
        ShowStage(shellEditorView); FocusEditor(); OnProgress();
    }

    private void CloseShellEditor()
    {
        shell.EditorClosed();
        ShowStage(terminal); FocusEditor(); OnProgress();
    }

    private void ShowSolution()
    {
        string keysText = shellMode ? ShellLessons.All[lessonIndex].Solution : ViLessons.All[lessonIndex].Solution;
        if (shellMode)
        {
            // Terminal input reads like typed lines: "pwd ⏎  ls -l ⏎  cat no[Tab] ⏎".
            var sb = new System.Text.StringBuilder();
            foreach (var token in ViEngine.Tokens(keysText))
                sb.Append(token switch { "<CR>" => " ⏎   ", _ when token.Length > 1 && token.StartsWith('<') => "[" + ViEngine.Display(token) + "]", _ => token });
            hint.Text = "예시 입력:  " + sb.ToString().TrimEnd();
        }
        else hint.Text = "예시 입력:  " + string.Join(" ", ViEngine.Tokens(keysText).Select(ViEngine.Display));
        hint.Visibility = Visibility.Visible; FocusEditor();
    }

    private void OnProgress()
    {
        if (lessonIndex < 0) return;
        string[] stepTexts; int total; bool free;
        if (shellMode)
        {
            var lesson = ShellLessons.All[lessonIndex];
            while (stepIndex < lesson.Steps.Length && lesson.Steps[stepIndex].Done(shell)) stepIndex++;
            stepTexts = lesson.Steps.Select(s => s.Text).ToArray(); total = lesson.Steps.Length; free = total == 0;
        }
        else
        {
            var lesson = ViLessons.All[lessonIndex];
            while (stepIndex < lesson.Steps.Length && lesson.Steps[stepIndex].Done(engine)) stepIndex++;
            stepTexts = lesson.Steps.Select(s => s.Text).ToArray(); total = lesson.Steps.Length; free = total == 0;
        }
        bool done = !free && stepIndex >= total;
        if (done && completed.Add(Key(lessonIndex))) RenderLessonList();
        steps.Children.Clear();
        for (int i = 0; i < stepTexts.Length; i++)
        {
            bool finished = i < stepIndex, now = i == stepIndex;
            var mark = Ui.Text(finished ? "✓" : now ? "▶" : "○", 12, finished ? "#69DDC0" : now ? "#5DD6BE" : "#687B91", true); mark.Width = 20;
            var text = Ui.Text(stepTexts[i], 13, finished ? "#8797AA" : now ? "#DCE5F0" : "#A8B7C9", now);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; DockPanel.SetDock(mark, Dock.Left); row.Children.Add(mark); row.Children.Add(text); steps.Children.Add(row);
        }
        bool insert = !shellMode && engine.Mode == ViMode.Insert;
        state.Text = free ? (shellMode ? "자유 연습 · 마음껏 입력해 보세요. [처음부터]를 누르면 연습 서버가 처음 상태로 돌아갑니다." : "자유 연습 · 마음껏 입력해 보세요. 막히면 Esc 를 누르고 [처음부터]를 누르세요.")
            : done ? (lessonIndex + 1 < Count ? "✓ 과제 완료!  [다음 과제 →]로 넘어가세요." : "✓ 모든 과제를 마쳤습니다! 자유 연습에서 복습해 보세요.")
            : $"진행 {stepIndex} / {total}" + (insert ? " · 입력 모드입니다. 명령을 쓰려면 Esc" : shellMode && shell.Editing ? " · vi 편집 중입니다. :wq 로 저장하고 터미널로 돌아갑니다" : "");
        state.Foreground = Ui.Brush(done ? "#69DDC0" : "#8797AA");
        next.Style = (Style)Application.Current.FindResource(done ? "PrimaryButton" : typeof(Button));
        if (next.Content is TextBlock label) label.Foreground = Ui.Brush(done ? "#102D29" : "#DCE5F0");
        next.IsEnabled = lessonIndex + 1 < Count;
        if (!shellMode || shell.Editing)
        {
            var keysUsed = shellMode ? shellEditor.RecentKeys : engine.RecentKeys;
            history.Text = keysUsed.Count == 0 ? "입력한 키가 여기에 표시됩니다" : "입력한 키   " + string.Join(" ", keysUsed.TakeLast(32));
        }
        else history.Text = "Tab 자동 완성 · ↑↓ 이전 명령 · Ctrl+R 기록 검색 · Ctrl+C 취소 · Ctrl+L 화면 지우기 · 오른쪽 클릭 붙여넣기 · PgUp 위로";
    }
}
