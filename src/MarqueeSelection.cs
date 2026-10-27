using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EquipLink;

// Empty-space dragging selects files. Item dragging remains available to the
// existing transfer handler; Ctrl/Shift clicks use WPF's Extended selection.
internal sealed class MarqueeSelection:IDisposable
{
    private readonly ListBox list;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(40)};
    private readonly Dictionary<ListBoxItem,Rect> positions=[];
    private HashSet<ListBoxItem> baseline=[];
    private Point start,last;
    private ModifierKeys modifiers;
    private bool active,moved,testing;
    private ScrollViewer? scroll;
    private Outline? outline;private AdornerLayer? layer;
    public MarqueeSelection(ListBox list)
    {
        this.list=list;VirtualizingPanel.SetScrollUnit(list,ScrollUnit.Pixel);
        list.PreviewMouseLeftButtonDown+=Down;list.PreviewMouseMove+=Move;list.PreviewMouseLeftButtonUp+=Up;list.LostMouseCapture+=Lost;list.PreviewKeyDown+=Key;
        list.Unloaded+=Unloaded;timer.Tick+=Tick;
    }
    private void Down(object sender,MouseButtonEventArgs e)
    {
        if(!list.IsEnabled||e.ClickCount!=1||e.OriginalSource is not DependencyObject source)return;
        if(ItemsControl.ContainerFromElement(list,source) is ListBoxItem{Tag:not null}||Ancestor<ScrollBar>(source)!=null)return;
        Begin(e.GetPosition(list),Keyboard.Modifiers);list.Focus();list.CaptureMouse();e.Handled=true;
    }
    private void Begin(Point point,ModifierKeys keys)
    {
        Finish(false);scroll=Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
        baseline=list.SelectedItems.OfType<ListBoxItem>().ToHashSet();positions.Clear();modifiers=keys;last=point;start=new(point.X,point.Y+(scroll?.VerticalOffset??0));active=true;moved=false;
        if((keys&(ModifierKeys.Control|ModifierKeys.Shift))==0)list.UnselectAll();
        layer=AdornerLayer.GetAdornerLayer(list);if(layer!=null){outline=new(list){IsHitTestVisible=false};layer.Add(outline);}if(!testing)timer.Start();
    }
    private void Move(object sender,MouseEventArgs e){if(!active)return;last=e.GetPosition(list);Apply();e.Handled=true;}
    private void Tick(object? sender,EventArgs e)
    {
        if(!active)return;
        if(!testing&&Mouse.LeftButton!=MouseButtonState.Pressed){Finish(false);return;}
        if(scroll!=null){if(last.Y<24)scroll.ScrollToVerticalOffset(scroll.VerticalOffset-18);else if(last.Y>list.ActualHeight-24)scroll.ScrollToVerticalOffset(scroll.VerticalOffset+18);}
        Apply();
    }
    private void Apply()
    {
        double offset=scroll?.VerticalOffset??0;var end=new Point(last.X,last.Y+offset);
        if(!moved&&Math.Abs(end.X-start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(end.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        moved=true;var rectangle=new Rect(start,end);
        foreach(var item in list.Items.OfType<ListBoxItem>().Where(x=>x.Tag!=null))
        {
            if(item.IsVisible&&item.ActualHeight>0)
            {
                try{Rect bounds=item.TransformToAncestor(list).TransformBounds(new Rect(item.RenderSize));bounds.Offset(0,offset);positions[item]=bounds;}catch(InvalidOperationException){}
            }
            bool hit=positions.TryGetValue(item,out var bounds2)&&rectangle.IntersectsWith(bounds2);
            bool selected=(modifiers&ModifierKeys.Control)!=0?baseline.Contains(item)^hit:(modifiers&ModifierKeys.Shift)!=0?baseline.Contains(item)||hit:hit;
            if(item.IsSelected!=selected)item.IsSelected=selected;
        }
        if(outline!=null){outline.Rectangle=new Rect(new Point(start.X,start.Y-offset),last);outline.InvalidateVisual();}
    }
    private void Up(object sender,MouseButtonEventArgs e){if(active){last=e.GetPosition(list);Apply();Finish(false);e.Handled=true;}}
    private void Lost(object sender,MouseEventArgs e){if(active)Finish(false);}
    private void Key(object sender,KeyEventArgs e){if(active&&e.Key==System.Windows.Input.Key.Escape){Finish(true);e.Handled=true;}}
    private void Unloaded(object sender,RoutedEventArgs e)=>Finish(false);
    private void Finish(bool restore)
    {
        if(restore)foreach(var item in list.Items.OfType<ListBoxItem>())item.IsSelected=baseline.Contains(item);
        active=false;timer.Stop();if(outline!=null)layer?.Remove(outline);outline=null;layer=null;
        if(list.IsMouseCaptured)list.ReleaseMouseCapture();positions.Clear();
    }
    internal void SelectForTest(Point from,Point to,ModifierKeys keys){testing=true;try{Begin(from,keys);last=to;Apply();Finish(false);}finally{testing=false;}}
    public void Dispose(){Finish(false);list.PreviewMouseLeftButtonDown-=Down;list.PreviewMouseMove-=Move;list.PreviewMouseLeftButtonUp-=Up;list.LostMouseCapture-=Lost;list.PreviewKeyDown-=Key;list.Unloaded-=Unloaded;timer.Tick-=Tick;}
    private static T? Ancestor<T>(DependencyObject? value)where T:DependencyObject{while(value!=null){if(value is T match)return match;value=value is Visual||value is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(value):LogicalTreeHelper.GetParent(value);}return null;}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private sealed class Outline(UIElement element):Adorner(element)
    {
        public Rect Rectangle;
        protected override void OnRender(DrawingContext dc){IsHitTestVisible=false;dc.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));var fill=Ui.Brush("#5DD6BE").Clone();fill.Opacity=.16;dc.DrawRectangle(fill,new Pen(Ui.Brush("#5DD6BE"),1),Rectangle);dc.Pop();}
    }
}
