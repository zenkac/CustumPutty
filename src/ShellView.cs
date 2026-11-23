using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EquipLink;

/// <summary>Terminal-style view of a <see cref="ShellEngine"/>. Colors follow the PuTTY palette of the current theme.</summary>
public sealed class ShellView : FrameworkElement
{
    private const double Size = 14, Pad = 10;
    public ShellEngine Engine { get; }
    private readonly Typeface typeface = new(ViEditorView.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface bold = new(ViEditorView.Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private Brush[] palette = [];
    private string paletteTheme = "";
    private int scroll;

    public ShellView(ShellEngine engine)
    {
        Engine = engine; Name = "PracticeTerminal"; Focusable = true; FocusVisualStyle = null; ClipToBounds = true; Cursor = Cursors.IBeam;
        InputMethod.SetIsInputMethodEnabled(this, false);
        engine.Changed += InvalidateVisual;
        void Repaint() { paletteTheme = ""; InvalidateVisual(); }
        ThemeService.Changed += Repaint;
        Unloaded += (_, _) => ThemeService.Changed -= Repaint;
        Loaded += (_, _) => { ThemeService.Changed -= Repaint; ThemeService.Changed += Repaint; InvalidateVisual(); };
        SizeChanged += (_, _) => { Engine.Columns = Math.Max(20, (int)((ActualWidth - Pad * 2) / CharWidth)); InvalidateVisual(); };
        PreviewMouseDown += (_, e) => { Focus(); if (e.ChangedButton == MouseButton.Right) { Paste(); e.Handled = true; } };
        MouseWheel += (_, e) => { scroll = Math.Max(0, scroll + (e.Delta > 0 ? 3 : -3)); InvalidateVisual(); e.Handled = true; };
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
    }

    private double LineHeight => Math.Ceiling(Size * 1.42);
    private FormattedText Format(string text, Brush brush, bool strong = false) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, strong ? bold : typeface, Size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private double CharWidth => Format("0", Brushes.Black).WidthIncludingTrailingWhitespace;

    private Brush[] Palette()
    {
        if (paletteTheme == ThemeService.Current.Id && palette.Length == 22) return palette;
        palette = ThemeService.PuttyColors.Select(rgb =>
        {
            var parts = rgb.Split(',').Select(byte.Parse).ToArray();
            var brush = new SolidColorBrush(Color.FromRgb(parts[0], parts[1], parts[2])); brush.Freeze(); return (Brush)brush;
        }).ToArray();
        paletteTheme = ThemeService.Current.Id; return palette;
    }

    private void Paste()
    {
        string text; try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) { Engine.Type(lines[i]); if (i < lines.Length - 1) Engine.Key("<CR>"); }
        scroll = 0;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) || (modifiers & ModifierKeys.Alt) != 0) return;
        if (key == Key.PageUp || key == Key.PageDown) { int page = Math.Max(1, (int)(ActualHeight / LineHeight) - 2); scroll = Math.Max(0, scroll + (key == Key.PageUp ? page : -page)); InvalidateVisual(); e.Handled = true; return; }
        if (key == Key.Insert && (modifiers & ModifierKeys.Shift) != 0) { Paste(); e.Handled = true; return; }
        string? token = null;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (key == Key.V) { Paste(); e.Handled = true; return; }
            if (key >= Key.A && key <= Key.Z) token = "<C-" + (char)('a' + (key - Key.A)) + ">";
        }
        else token = key switch
        {
            Key.Enter => "<CR>", Key.Back => "<BS>", Key.Delete => "<Del>", Key.Tab => "<Tab>", Key.Escape => "<Esc>",
            Key.Left => "<Left>", Key.Right => "<Right>", Key.Up => "<Up>", Key.Down => "<Down>", Key.Home => "<Home>", Key.End => "<End>", _ => null
        };
        if (token == null) return;
        e.Handled = true; scroll = 0; Engine.Key(token);
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true; scroll = 0;
        var sb = new StringBuilder(); foreach (char c in e.Text) if (!char.IsControl(c)) sb.Append(c);
        if (sb.Length > 0) Engine.Type(sb.ToString());
    }

    private readonly record struct Piece(string Text, int Color, bool Bold);

    /// <summary>Splits ANSI SGR colored text into pieces. Color is a PuTTY palette index.</summary>
    private static List<Piece> Pieces(string text, int baseColor)
    {
        var pieces = new List<Piece>(); int color = baseColor; bool strong = false; var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int end = text.IndexOf('m', i + 2); if (end < 0) break;
                if (sb.Length > 0) { pieces.Add(new(sb.ToString(), color, strong)); sb.Clear(); }
                foreach (var code in text[(i + 2)..end].Split(';'))
                {
                    if (!int.TryParse(code.Length == 0 ? "0" : code, out int n)) continue;
                    if (n == 0) { color = baseColor; strong = false; }
                    else if (n == 1) strong = true;
                    else if (n is >= 30 and <= 37) color = 6 + (n - 30) * 2;
                    else if (n == 39) color = baseColor;
                }
                i = end; continue;
            }
            sb.Append(text[i]);
        }
        if (sb.Length > 0) pieces.Add(new(sb.ToString(), color, strong));
        return pieces;
    }

    private static int Cells(char c) => ShellEngine.DisplayWidth(c.ToString());

    /// <summary>Wraps pieces to the terminal width.</summary>
    private static List<List<Piece>> Wrap(List<Piece> pieces, int columns)
    {
        var rows = new List<List<Piece>> { new() }; int used = 0;
        foreach (var piece in pieces)
        {
            var sb = new StringBuilder();
            foreach (char c in piece.Text)
            {
                int width = Cells(c);
                if (used + width > columns) { if (sb.Length > 0) rows[^1].Add(piece with { Text = sb.ToString() }); sb.Clear(); rows.Add([]); used = 0; }
                sb.Append(c); used += width;
            }
            if (sb.Length > 0) rows[^1].Add(piece with { Text = sb.ToString() });
        }
        return rows;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var colors = Palette();
        dc.DrawRectangle(colors[2], null, new Rect(RenderSize));
        int columns = Math.Max(20, Engine.Columns), visible = Math.Max(1, (int)((ActualHeight - Pad * 2) / LineHeight));
        double lh = LineHeight, cw = CharWidth;
        var noteColor = -1;
        // The prompt line is the last logical line; the cursor sits after the typed text.
        string prompt = Engine.Prompt, typed = Engine.Secret ? "" : Engine.Input;
        int caretCells = ShellEngine.DisplayWidth(ShellEngine.StripAnsi(prompt)) + ShellEngine.DisplayWidth(Engine.Secret ? "" : Engine.Input[..Math.Min(Engine.Caret, Engine.Input.Length)]);
        var rows = new List<List<Piece>>(); int cursorRow = -1, cursorCol = 0;
        var promptRows = Engine.Editing ? [] : Wrap(Pieces(prompt + typed, 0), columns);
        if (!Engine.Editing)
        {
            cursorRow = caretCells / columns; cursorCol = caretCells % columns;
            while (promptRows.Count <= cursorRow) promptRows.Add([]);
        }
        int needed = visible + scroll;
        var output = Engine.Output;
        for (int i = output.Count - 1; i >= 0 && rows.Count + promptRows.Count < needed; i--)
        {
            var line = output[i];
            var wrapped = Wrap(Pieces(line.Text, line.Kind == ShellLineKind.Note ? noteColor : 0), columns);
            rows.InsertRange(0, wrapped);
        }
        int promptStart = rows.Count; rows.AddRange(promptRows);
        int maxScroll = Math.Max(0, rows.Count - visible); if (scroll > maxScroll) scroll = maxScroll;
        int first = Math.Max(0, rows.Count - visible - scroll);
        var note = Ui.Brush("#E9C786");
        for (int r = first; r < Math.Min(rows.Count, first + visible); r++)
        {
            double y = Pad + (r - first) * lh, x = Pad;
            foreach (var piece in rows[r])
            {
                var brush = piece.Color < 0 ? note : colors[Math.Clamp(piece.Color + (piece.Bold && piece.Color is >= 6 and < 20 && piece.Color % 2 == 0 ? 1 : 0), 0, 21)];
                var text = Format(piece.Text, brush, piece.Bold);
                dc.DrawText(text, new Point(x, y + (lh - Size * 1.2) / 2));
                x += ShellEngine.DisplayWidth(piece.Text) * cw;
            }
            if (cursorRow >= 0 && r == promptStart + cursorRow && IsKeyboardFocused)
            {
                double cx = Pad + cursorCol * cw;
                dc.DrawRectangle(colors[5], null, new Rect(cx, y + 2, cw, lh - 4));
                string under = Engine.Secret || Engine.Caret >= Engine.Input.Length ? "" : Engine.Input.Substring(Engine.Caret, 1);
                if (under.Length > 0) dc.DrawText(Format(under, colors[4]), new Point(cx, y + (lh - Size * 1.2) / 2));
            }
            else if (cursorRow >= 0 && r == promptStart + cursorRow)
            {
                double cx = Pad + cursorCol * cw;
                dc.DrawRectangle(null, new Pen(colors[5], 1), new Rect(cx + .5, y + 2.5, cw - 1, lh - 5));
            }
        }
        if (scroll > 0)
        {
            var badge = Format($"↑ {scroll}줄 위 · 아무 키나 누르면 맨 아래로", colors[7]);
            dc.DrawRectangle(colors[3], null, new Rect(ActualWidth - badge.Width - 22, 6, badge.Width + 14, lh));
            dc.DrawText(badge, new Point(ActualWidth - badge.Width - 15, 6 + (lh - Size * 1.2) / 2));
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
