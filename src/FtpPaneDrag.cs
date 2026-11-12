using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EquipLink;

public sealed partial class FtpWorkspace
{
    private const string InternalDragFormat="EquipLink.FtpPaneSelection";
    private sealed record PaneDrag(bool Remote,int Generation,FtpEntry[] Entries,string[] Paths,string Token);
    private PaneDrag? paneDrag;
    private DropTargetAdorner? dropAdorner;
    private DateTime lastDragScroll;
    internal Action<DependencyObject,IDataObject>? DragLoopForTest;
    internal string DropHintForTest {get;private set;}="";
    internal void StartPaneDragForTest(bool remote){if(!App.IsTesting)throw new InvalidOperationException();StartPaneDrag(remote?remoteFiles:localFiles);}
    private void StartPaneDrag(ListBox list)
    {
        if(disposed||deleting||!list.IsEnabled)return;
        bool remote=list==remoteFiles;
        var entries=remote?list.SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).OfType<FtpEntry>().ToArray():[];
        var paths=remote?[]:list.SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).OfType<LocalEntry>().Select(x=>x.FullPath).ToArray();
        if(remote&&(!session.IsConnected||session.IsBusy||entries.Length==0)||!remote&&paths.Length==0)return;
        if(entries.Any(x=>x.IsLink)){Ui.Error(new ArgumentException("심볼릭 링크는 직접 전송하지 않습니다. 실제 대상 파일 또는 폴더를 선택해 주세요."));return;}
        dragging=true;paneDrag=new(remote,session.ConnectionGeneration,entries,paths,Guid.NewGuid().ToString("N"));
        try
        {
            if(remote)DragRemoteFiles(entries);
            else RunDragLoop(localFiles,new DataObject(DataFormats.FileDrop,paths));
        }
        catch(Exception error){Ui.Error(error);}
        finally
        {
            ClearDropFeedback();dragItem=null;deferredSelection=false;dragging=false;paneDrag=null;activeDrag?.Dispose();activeDrag=null;
        }
    }
    private void RunDragLoop(ListBox source,IDataObject data)
    {
        if(App.IsTesting&&DragLoopForTest!=null)DragLoopForTest(source,data);else DragDrop.DoDragDrop(source,data,DragDropEffects.Copy);
    }
    private void HandlePaneDrag(ListBox target,DragEventArgs e,bool drop)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;
        try
        {
            bool remote=target==remoteFiles;
            if(disposed||deleting||!session.IsConnected||session.IsBusy||!target.IsEnabled||!remote&&(terminalMode||localLoading||localPath.Length==0))return;
            if((e.AllowedEffects&DragDropEffects.Copy)==0)return;
            bool ownRemote=paneDrag is {Remote:true}&&((activeDrag!=null&&e.Data.GetDataPresent("FileGroupDescriptorW"))||e.Data.GetDataPresent(InternalDragFormat)&&Equals(e.Data.GetData(InternalDragFormat),paneDrag.Token));
            bool fileDrop=e.Data.GetDataPresent(DataFormats.FileDrop);
            if(paneDrag!=null&&(paneDrag.Generation!=session.ConnectionGeneration||paneDrag.Remote==remote))return;
            if(remote?!fileDrop||ownRemote:!ownRemote)return;
            var item=ItemsControl.ContainerFromElement(target,e.OriginalSource as DependencyObject) as ListBoxItem;
            if(item?.Tag is FtpEntry{IsLink:true})return;
            string destination=remote?(item?.Tag is FtpEntry{IsDirectory:true} r?r.FullPath:session.CurrentPath):(item?.Tag is LocalEntry{IsDirectory:true} l?l.FullPath:localPath);
            if(!remote&&(!Directory.Exists(destination)||(File.GetAttributes(destination)&FileAttributes.ReparsePoint)!=0))return;
            string[] paths=[];
            if(remote)
            {
                paths=paneDrag is {Remote:false} local?local.Paths:e.Data.GetData(DataFormats.FileDrop) as string[]??[];
                if(paths.Length==0)return;
            }
            int count=remote?paths.Length:paneDrag!.Entries.Length;e.Effects=DragDropEffects.Copy;
            if(drop)
            {
                if(remote)EnqueueUploads(paths,destination);else DownloadEntries(paneDrag!.Entries,destination);
                return;
            }
            bool folder=item?.Tag is FtpEntry{IsDirectory:true} or LocalEntry{IsDirectory:true};
            ShowDropFeedback(target,folder?item:null,$"{count:N0}개 {(remote?"업로드":"다운로드")} · {destination} · 원본 유지");
            // Scroll a long directory while holding a drag near the list edge.
            if((DateTime.UtcNow-lastDragScroll).TotalMilliseconds>120)
            {
                var point=e.GetPosition(target);var scroll=VisualChildren(target).OfType<ScrollViewer>().FirstOrDefault();
                if(point.Y<26)scroll?.LineUp();else if(point.Y>target.ActualHeight-26)scroll?.LineDown();lastDragScroll=DateTime.UtcNow;
            }
        }
        catch(Exception error){e.Effects=DragDropEffects.None;if(drop)Ui.Error(error);}
        finally{if(drop||e.Effects==DragDropEffects.None)ClearDropFeedback();}
    }
    private void ShowDropFeedback(ListBox list,ListBoxItem? folder,string message)
    {
        if(dropAdorner?.AdornedElement!=list){ClearDropFeedback();var layer=AdornerLayer.GetAdornerLayer(list);if(layer!=null){dropAdorner=new DropTargetAdorner(list);layer.Add(dropAdorner);}}
        dropAdorner?.SetFolder(folder);DropHintForTest=message;
        var count=list==remoteFiles?remoteCount:localCount;count.Text=message;count.ToolTip=message;
    }
    private void ClearDropFeedback()
    {
        if(dropAdorner!=null){AdornerLayer.GetAdornerLayer(dropAdorner.AdornedElement)?.Remove(dropAdorner);dropAdorner=null;}
        if(DropHintForTest.Length>0){DropHintForTest="";UpdateFileCounts();}
    }
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject value){yield return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(value);i++)foreach(var child in VisualChildren(VisualTreeHelper.GetChild(value,i)))yield return child;}
}

internal sealed class DropTargetAdorner:Adorner
{
    private ListBoxItem? folder;
    internal DropTargetAdorner(UIElement owner):base(owner){IsHitTestVisible=false;}
    internal void SetFolder(ListBoxItem? value){folder=value;InvalidateVisual();}
    protected override void OnRender(DrawingContext context)
    {
        var bounds=new Rect(1,1,Math.Max(0,AdornedElement.RenderSize.Width-2),Math.Max(0,AdornedElement.RenderSize.Height-2));
        var brush=Ui.Brush("#5DD6BE");context.DrawRoundedRectangle(null,new Pen(brush,2),bounds,5,5);
        if(folder!=null&&folder.IsVisible)
        {
            try{var rect=folder.TransformToAncestor(AdornedElement).TransformBounds(new Rect(folder.RenderSize));rect.Intersect(bounds);if(!rect.IsEmpty){var fill=brush.Clone();fill.Opacity=.16;context.DrawRoundedRectangle(fill,new Pen(brush,2),rect,4,4);}}catch(InvalidOperationException){}
        }
    }
}
