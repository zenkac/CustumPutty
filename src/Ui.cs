using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EquipLink;

public static class Ui
{
    /// <summary>Apply the common theme explicitly: WPF's implicit Window style
    /// does not automatically match derived window classes such as FormDialog.</summary>
    public static void ApplyWindowTheme(Window window)
    {
        if(Application.Current?.TryFindResource(typeof(Window)) is Style style)window.Style=style;
        // Resource references (not copied values) keep windows in step with live theme changes.
        if(Application.Current?.TryFindResource("Bg") is Brush){window.SetResourceReference(Control.BackgroundProperty,"Bg");window.SetResourceReference(Control.ForegroundProperty,"Ink");}
        else{window.Background=Brush("#111923");window.Foreground=Brush("#DCE5F0");}
        window.FontFamily=new FontFamily("Segoe UI, Malgun Gothic");window.FontSize=12;
        window.UseLayoutRounding=true;TextOptions.SetTextFormattingMode(window,TextFormattingMode.Display);
        window.SourceInitialized-=ApplyWindowChrome;window.SourceInitialized+=ApplyWindowChrome;
        if(new WindowInteropHelper(window).Handle!=IntPtr.Zero)ApplyWindowChrome(window,EventArgs.Empty);
    }
    /// <summary>Repaint the native caption after a live theme change.</summary>
    internal static void RefreshWindowChrome(Window window)=>ApplyWindowChrome(window,EventArgs.Empty);
    private static void ApplyWindowChrome(object? sender,EventArgs args)
    {
        if(sender is not Window window)return;
        var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
        try
        {
            int dark=1,background=ThemeService.ColorRef("#111923"),foreground=ThemeService.ColorRef("#DCE5F0"),border=ThemeService.ColorRef("#2A394A");
            if(DwmSetWindowAttribute(handle,20,ref dark,4)!=0)DwmSetWindowAttribute(handle,19,ref dark,4);
            // Windows 11 adds caption, text and border colors. Older Windows
            // versions retain dark mode and safely ignore these attributes.
            DwmSetWindowAttribute(handle,35,ref background,4);
            DwmSetWindowAttribute(handle,36,ref foreground,4);
            DwmSetWindowAttribute(handle,34,ref border,4);
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,uint attribute,ref int value,uint size);
    public static Brush Brush(string hex) => ThemeService.Brush(hex);
    public static TextBlock Text(string text,double size=13,string color="#DCE5F0",bool bold=false) => new(){Text=text,FontSize=size,Foreground=Brush(color),FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
    public static TextBlock Glyph(string glyph,double size=16) => new(){Text=glyph,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=size,Foreground=Brush("#DCE5F0"),TextWrapping=TextWrapping.NoWrap,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
    public static StackPanel Stack(params UIElement[] children) { var panel=new StackPanel(); foreach(var child in children) panel.Children.Add(child); return panel; }
    public static StackPanel Row(params UIElement[] children) { var panel=Stack(children); panel.Orientation=Orientation.Horizontal; return panel; }
    public static Border Card(UIElement child,string background="#1D2A38",double padding=16) => new(){Child=child,Background=Brush(background),Padding=new Thickness(padding),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),BorderBrush=Brush("#2A394A")};
    private static Button CreateButton(string text,bool primary,bool compact)
    {
        var label=Text(text,12,primary?"#102D29":"#DCE5F0",true);label.TextWrapping=TextWrapping.NoWrap;
        var button=new Button{Content=label,Margin=new Thickness(0,0,compact?4:8,0)};
        if(primary)button.Style=(Style)Application.Current.FindResource("PrimaryButton");
        else if(compact)button.Style=(Style)Application.Current.FindResource("ToolButton");
        if(compact){button.Height=30;button.MinHeight=28;button.Padding=new Thickness(8,4,8,4);}
        return button;
    }
    public static Button Button(string text,Action action,bool primary=false)
    {
        var button=CreateButton(text,primary,false);
        button.Click+=(_,_)=>action(); return button;
    }
    public static Button AsyncButton(string text,Func<Task> action,bool primary=false)
    {
        var button=CreateButton(text,primary,false);
        button.Click+=async (_,_)=> { button.IsEnabled=false; try{await action();}catch(Exception exception){Error(exception);}finally{button.IsEnabled=true;} }; return button;
    }
    public static Button ToolButton(string text,Action action,string? tip=null,bool primary=false)
    {
        var button=CreateButton(text,primary,true);button.ToolTip=tip;button.Click+=(_,_)=>action();return button;
    }
    public static Button AsyncToolButton(string text,Func<Task> action,string? tip=null,bool primary=false)
    {
        var button=CreateButton(text,primary,true);button.ToolTip=tip;
        button.Click+=async (_,_)=>{button.IsEnabled=false;try{await action();}catch(Exception exception){Error(exception);}finally{button.IsEnabled=true;}};return button;
    }
    public static TextBox Input(string text="",bool multiline=false) => new(){Text=text,AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden,VerticalContentAlignment=multiline?VerticalAlignment.Top:VerticalAlignment.Center,MinHeight=multiline?78:32};
    public static ComboBox Select(IEnumerable<string> values,string selected)
    { var combo=new ComboBox(); foreach(var value in values)combo.Items.Add(value); combo.SelectedItem=selected; if(combo.SelectedIndex<0)combo.SelectedIndex=0; return combo; }
    public static FrameworkElement Field(string label,FrameworkElement input,string? hint=null)
    {
        var title=Text(label,12,"#AAB8C8",true); title.Margin=new Thickness(0,0,0,6);
        var panel=Stack(title,input); panel.Margin=new Thickness(0,0,0,14);
        if(hint!=null){var help=Text(hint,11,"#8797AA");help.Margin=new Thickness(0,5,0,0);panel.Children.Add(help);} return panel;
    }
    /// <summary>A two- or three-way switch whose buttons share one rounded frame.</summary>
    public static Border Segmented(bool stretch,params Button[] buttons)
    {
        Panel host;
        if(stretch){var grid=new Grid();foreach(var button in buttons){grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Grid.SetColumn(button,grid.ColumnDefinitions.Count-1);grid.Children.Add(button);}host=grid;}
        else{var row=Row();foreach(var button in buttons)row.Children.Add(button);host=row;}
        foreach(var button in buttons){button.Margin=new Thickness(0);button.Height=26;button.MinHeight=24;button.Padding=new Thickness(12,0,12,0);button.BorderThickness=new Thickness(0);}
        return new Border{Child=host,BorderBrush=Brush("#2A394A"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(2),VerticalAlignment=VerticalAlignment.Center};
    }
    public static void SetSegment(Button button,bool selected)
    {
        button.Background=selected?Brush("#25413F"):System.Windows.Media.Brushes.Transparent;
        if(button.Content is TextBlock label)label.Foreground=Brush(selected?"#DCE5F0":"#8797AA");
        System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?"선택됨":"");
    }
    public static Border Divider(double height=20)=>new(){Width=1,Height=height,Background=Brush("#2A394A"),Margin=new Thickness(6,0,8,0),VerticalAlignment=VerticalAlignment.Center};
    /// <summary>Overlay a hint that disappears while the box has text.</summary>
    public static Grid WithPlaceholder(TextBox box,string hint)
    {
        var grid=new Grid();grid.Children.Add(box);var watermark=Text(hint,box.FontSize>0?box.FontSize:11,"#687B91");watermark.TextWrapping=TextWrapping.NoWrap;watermark.Margin=new Thickness(10,0,0,0);watermark.VerticalAlignment=VerticalAlignment.Center;watermark.IsHitTestVisible=false;grid.Children.Add(watermark);
        void Update()=>watermark.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;box.TextChanged+=(_,_)=>Update();Update();return grid;
    }
    /// <summary>Form section heading with a thin divider, used to group long dialogs.</summary>
    public static FrameworkElement Section(string title,string? description=null,bool first=false)
    {
        var heading=Text(title,13,"#5DD6BE",true);
        var panel=Stack(heading);panel.Margin=new Thickness(0,0,0,12);
        if(description!=null){var help=Text(description,11,"#8797AA");help.Margin=new Thickness(0,4,0,0);panel.Children.Add(help);}
        return new Border{Child=panel,BorderBrush=Brush("#2A394A"),BorderThickness=new Thickness(0,first?0:1,0,0),Padding=new Thickness(0,first?0:14,0,0),Margin=new Thickness(0,first?0:6,0,0)};
    }
    public static Grid Columns(UIElement left,UIElement right,double leftWeight=1,double rightWeight=1)
    {
        var grid=new Grid(); grid.ColumnDefinitions.Add(new(){Width=new GridLength(leftWeight,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(16)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(rightWeight,GridUnitType.Star)});
        grid.Children.Add(left);grid.Children.Add(right);Grid.SetColumn(right,2);return grid;
    }
    public static void Error(Exception exception)
    {
        if(App.IsTesting)throw exception;
        var owner=Application.Current?.Windows.OfType<Window>().FirstOrDefault(w=>w.IsActive&&w.IsVisible)??Application.Current?.MainWindow;
        if(owner==null||!owner.IsVisible){MessageBox.Show(exception.Message,"EquipLink",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        Dialogs.Notice(owner,"확인이 필요합니다",exception.Message);
    }
    public static void Copy(string text) { try{Clipboard.SetText(text);}catch(Exception exception){Error(exception);} }
    public static void OpenFolder(string folder) { System.IO.Directory.CreateDirectory(folder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder){UseShellExecute=true}); }
}
