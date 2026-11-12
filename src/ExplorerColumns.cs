using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EquipLink;

internal enum FileSort { Name,Size,Type,Modified }

// The same column definitions drive the header and virtualized ListBox rows.
// Resizing and horizontal scrolling therefore preserve exact column alignment.
internal sealed class ExplorerColumns
{
    internal readonly Grid Header=new();
    internal readonly ScrollViewer HeaderScroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
    private readonly TextBlock[] labels=new TextBlock[4];
    private readonly double[] widths=[150,76,90,126];
    private bool fixedName;private double viewport=450;
    private ScrollViewer? attachedScroll;
    internal FileSort Sort=FileSort.Name;internal bool Descending;
    internal FrameworkElement Build(string prefix,Action<FileSort> sort)
    {
        HeaderScroll.Content=Header;
        for(int i=0;i<4;i++)
        {
            int column=i;Header.ColumnDefinitions.Add(new(){Width=new GridLength(widths[i])});
            labels[i]=Ui.Text("",11,"#AAB8C8",true);labels[i].TextWrapping=TextWrapping.NoWrap;
            var button=new Button{Content=labels[i],Name=prefix+(FileSort)i+"Column",Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(8,5,8,5),Margin=new Thickness(0),MinHeight=28,HorizontalContentAlignment=HorizontalAlignment.Left,ToolTip="클릭하여 정렬 · 오른쪽 경계선을 끌어 너비 조절"};
            button.Click+=(_,_)=>sort((FileSort)column);Header.Children.Add(button);Grid.SetColumn(button,i);
            var grip=new Thumb{Width=7,HorizontalAlignment=HorizontalAlignment.Right,Cursor=Cursors.SizeWE,Background=Brushes.Transparent};
            var factory=new FrameworkElementFactory(typeof(Border));factory.SetValue(Border.BackgroundProperty,Brushes.Transparent);factory.SetValue(Border.BorderBrushProperty,Ui.Brush("#2A394A"));factory.SetValue(Border.BorderThicknessProperty,new Thickness(0,0,1,0));grip.Template=new ControlTemplate(typeof(Thumb)){VisualTree=factory};
            grip.DragDelta+=(_,e)=>Resize(column,e.HorizontalChange);Header.Children.Add(grip);Grid.SetColumn(grip,i);
        }
        Reflow(viewport);UpdateLabels();return HeaderScroll;
    }
    internal void UpdateLabels()
    {
        string[] names=["이름","크기","유형","수정한 날짜"];
        for(int i=0;i<labels.Length;i++)if(labels[i]!=null)labels[i].Text=names[i]+(Sort==(FileSort)i?(Descending?"  ↓":"  ↑"):"");
    }
    internal void Reflow(double available)
    {
        viewport=Math.Max(0,available);if(!fixedName)widths[0]=Math.Max(130,viewport-widths.Skip(1).Sum());
        if(Header.ColumnDefinitions.Count==0)return;
        for(int i=0;i<4;i++)Header.ColumnDefinitions[i].Width=new GridLength(widths[i]);Header.Width=widths.Sum();
    }
    internal void Resize(int column,double delta)
    {
        if(column==0)fixedName=true;widths[column]=Math.Clamp(widths[column]+delta,column==0?80:54,column==0?1200:600);Reflow(viewport);
    }
    internal Grid Row()
    {
        var row=new Grid();row.SetBinding(FrameworkElement.WidthProperty,new Binding(nameof(FrameworkElement.Width)){Source=Header});
        foreach(var source in Header.ColumnDefinitions)
        {
            var column=new ColumnDefinition();BindingOperations.SetBinding(column,ColumnDefinition.WidthProperty,new Binding(nameof(ColumnDefinition.Width)){Source=source});row.ColumnDefinitions.Add(column);
        }
        return row;
    }
    internal void Attach(ListBox list)
    {
        void Size()=>Reflow(list.ActualWidth-22);
        list.SizeChanged+=(_,_)=>Size();
        list.Loaded+=(_,_)=>
        {
            Size();var scroll=Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
            if(scroll!=null&&scroll!=attachedScroll){attachedScroll=scroll;scroll.ScrollChanged+=(_,e)=>{if(e.HorizontalChange!=0)HeaderScroll.ScrollToHorizontalOffset(scroll.HorizontalOffset);};}
        };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value){yield return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(value);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(value,i)))yield return child;}
}
