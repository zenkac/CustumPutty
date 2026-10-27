using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EquipLink;

public sealed partial class FtpWorkspace
{
    readonly List<Border> fileHeaders=[];
    Button iconViewButton=null!,listViewButton=null!;bool iconView=true,dragging;
    Point dragStart;ListBoxItem? dragItem;bool deferredSelection;FtpVirtualFiles? activeDrag;
    void SetFileView(bool icons)
    {
        if(iconView==icons)return;
        iconView=icons;ConfigureFileView();store.Settings.FtpIconView=icons;SaveStore();
    }
    void ConfigureFileView()
    {
        iconViewButton.Content=Ui.Text("▦ 아이콘",12,iconView?"#5DD6BE":"#8797AA",true);
        listViewButton.Content=Ui.Text("☷ 목록",12,iconView?"#8797AA":"#5DD6BE",true);
        iconViewButton.Background=Ui.Brush(iconView?"#203A3C":"#17212D");
        listViewButton.Background=Ui.Brush(iconView?"#17212D":"#203A3C");
        iconViewButton.ToolTip=(iconView?"선택됨 · ":"")+"파일 형식과 폴더를 큰 아이콘으로 보기";
        listViewButton.ToolTip=(!iconView?"선택됨 · ":"")+"이름 · 크기 · 수정한 날짜를 목록으로 보기";
        foreach(var header in fileHeaders)header.Visibility=iconView?Visibility.Collapsed:Visibility.Visible;
        foreach(var list in new[]{localFiles,remoteFiles})
        {
            var factory=new FrameworkElementFactory(iconView?typeof(WrapPanel):typeof(VirtualizingStackPanel));
            if(iconView){factory.SetValue(WrapPanel.ItemWidthProperty,112d);factory.SetValue(WrapPanel.ItemHeightProperty,106d);}
            list.ItemsPanel=new ItemsPanelTemplate(factory);ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
            foreach(var item in list.Items.OfType<ListBoxItem>())
            {
                if(item.Tag is LocalEntry l)item.Content=FileContent(l.Name,l.IsDirectory,l.Size,l.Modified.ToString("yy-MM-dd HH:mm"));
                else if(item.Tag is FtpEntry r)item.Content=FileContent(r.Name+(r.IsLink?" ↗":""),r.IsDirectory,r.Size,r.Modified?.ToLocalTime().ToString("yy-MM-dd HH:mm")??"—");
            }
        }
    }
    ListBoxItem FileItem(object tag,string name,bool directory,long? size,string modified,string tooltip)=>new(){Tag=tag,Content=FileContent(name,directory,size,modified),Padding=new Thickness(5),ToolTip=tooltip};
    FrameworkElement FileContent(string name,bool directory,long? size,string modified)
    {
        if(!iconView)return FileRow(name,directory?"폴더":FormatSize(size),modified,directory);
        string ext=Path.GetExtension(name).TrimStart('.').ToUpperInvariant();if(ext.Length>6)ext="FILE";
        string color=directory?"#E9C786":ext is "CSV" or "XLS" or "XLSX"?"#5DD6BE":ext is "ZIP" or "GZ" or "TAR"?"#C1A4F4":"#88B8F2";
        var glyph=Ui.Glyph(directory?"\uE8B7":"\uE8A5",29);glyph.Foreground=Ui.Brush(color);glyph.HorizontalAlignment=HorizontalAlignment.Center;glyph.Margin=new Thickness(0,5,0,1);
        var type=Ui.Text(directory?"폴더":ext.Length>0?ext:"FILE",9,color,true);type.HorizontalAlignment=HorizontalAlignment.Center;
        var label=Ui.Text(name,11,"#DCE5F0");label.TextAlignment=TextAlignment.Center;label.TextWrapping=TextWrapping.Wrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.Height=33;label.Margin=new Thickness(0,4,0,0);
        return new StackPanel{Width=94,Children={glyph,type,label}};
    }
    async Task<FtpOverwriteDecision> ConfirmConflictAsync(FtpConflict conflict,CancellationToken ct)
    {
        return await Dispatcher.InvokeAsync(()=>
        {
            if(disposed||!owner.IsVisible||!owner.IsEnabled||ct.IsCancellationRequested)return FtpOverwriteDecision.Cancel;
            var dialog=new FormDialog(owner,"파일 덮어쓰기",width:480,compact:true);
            var question=Ui.Text((conflict.Direction=="업로드"?"서버":"내 PC")+"에 같은 이름이 있습니다. 덮어쓸까요?",13,"#DCE5F0");question.Margin=new Thickness(0,0,0,12);dialog.Body.Children.Add(question);
            var filename=Ui.Text(Path.GetFileName(conflict.LocalPath),14,"#E9C786",true);filename.TextWrapping=TextWrapping.NoWrap;filename.TextTrimming=TextTrimming.CharacterEllipsis;filename.ToolTip=filename.Text;filename.Margin=new Thickness(0,0,0,6);
            var details=Ui.Stack(filename);
            foreach(string path in new[]{"내 PC: "+conflict.LocalPath,"서버: "+conflict.RemotePath})
            {var line=Ui.Text(path,11,"#8797AA");line.TextWrapping=TextWrapping.NoWrap;line.TextTrimming=TextTrimming.CharacterEllipsis;line.ToolTip=path;details.Children.Add(line);}
            dialog.Body.Children.Add(Ui.Card(details,"#17212D",12));
            FtpOverwriteDecision choice=FtpOverwriteDecision.Cancel;
            foreach(var item in new[]{("취소",FtpOverwriteDecision.Cancel,"FtpConflictCancel"),("건너뛰기",FtpOverwriteDecision.Skip,"FtpConflictSkip"),("덮어쓰기",FtpOverwriteDecision.Replace,"FtpConflictReplace")})
            {var button=Ui.Button(item.Item1,()=>{choice=item.Item2;dialog.DialogResult=true;},item.Item2==FtpOverwriteDecision.Replace);button.Name=item.Item3;button.IsCancel=item.Item2==FtpOverwriteDecision.Cancel;dialog.Actions.Children.Add(button);}
            using var registration=ct.Register(()=>Dispatcher.BeginInvoke(()=>{if(dialog.IsVisible)dialog.DialogResult=false;}));dialog.ShowDialog();return choice;
        });
    }
    void InstallDragDrop()
    {
        remoteFiles.AllowDrop=true;
        remoteFiles.DragOver+=(_,e)=>{e.Handled=true;e.Effects=activeDrag==null&&session.IsConnected&&!session.IsBusy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;};
        remoteFiles.Drop+=(_,e)=>
        {
            e.Handled=true;if(activeDrag!=null||!session.IsConnected||session.IsBusy||e.Data.GetData(DataFormats.FileDrop) is not string[] paths)return;
            var item=ItemsControl.ContainerFromElement(remoteFiles,e.OriginalSource as DependencyObject) as ListBoxItem;
            string destination=item?.Tag is FtpEntry{IsDirectory:true,IsLink:false} directory?directory.FullPath:session.CurrentPath;
            EnqueueUploads(paths,destination);
        };
        foreach(var list in new[]{localFiles,remoteFiles})
        {
            list.PreviewMouseLeftButtonDown+=(_,e)=>
            {
                dragStart=e.GetPosition(list);dragItem=ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) as ListBoxItem;
                deferredSelection=list.SelectedItems.Count>1&&dragItem?.IsSelected==true&&Keyboard.Modifiers==ModifierKeys.None&&e.ClickCount==1;
                // Preserve an existing multi-selection until a click finishes or a drag begins.
                if(deferredSelection){list.Focus();e.Handled=true;}
            };
            list.PreviewMouseLeftButtonUp+=(_,e)=>{if(deferredSelection&&dragItem!=null&&!dragging){list.SelectedItem=dragItem;e.Handled=true;}deferredSelection=false;dragItem=null;};
            list.PreviewMouseMove+=(_,e)=>
            {
                if(dragging||dragItem?.IsSelected!=true||e.LeftButton!=MouseButtonState.Pressed)return;
                var point=e.GetPosition(list);if(Math.Abs(point.X-dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
                e.Handled=true;dragging=true;
                try
                {
                    if(list==localFiles)
                    {
                        string[] paths=localFiles.SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).OfType<LocalEntry>().Select(x=>x.FullPath).ToArray();
                        if(paths.Length>0)DragDrop.DoDragDrop(localFiles,new DataObject(DataFormats.FileDrop,paths),DragDropEffects.Copy);
                    }
                    else DragRemoteFiles();
                }
                catch(Exception error){Ui.Error(error);}
                finally{dragItem=null;deferredSelection=false;dragging=false;activeDrag?.Dispose();activeDrag=null;}
            };
        }
        remoteFiles.ToolTip="파일 더블클릭: 현재 내 PC 폴더로 다운로드 · 폴더 더블클릭: 열기\nWindows 탐색기의 파일·폴더를 여기로 끌어 업로드 · 서버 파일을 탐색기 폴더로 끌어 다운로드";
    }
    void DragRemoteFiles()
    {
        if(!session.IsConnected||session.IsBusy)return;
        var selected=remoteFiles.SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).OfType<FtpEntry>().ToArray();if(selected.Length==0)return;
        if(selected.Any(x=>x.IsDirectory||x.IsLink))throw new ArgumentException("Windows 탐색기로 끌어내기는 일반 파일을 지원합니다. 폴더 전체는 다운로드 버튼을 이용하세요.");
        var origin=session.Site!.Copy();string secret=session.PasswordForTransfer();
        try{activeDrag=new FtpVirtualFiles(origin,secret,selected);}finally{secret="";}
        uint effect=activeDrag.DoDragDrop();
        if(effect==1&&!disposed)
        {
            foreach(var file in activeDrag.Delivered)queue.RecordExternalDownload(origin,file.Entry,"Windows 탐색기",file.Bytes);
            SwitchTransferView(false);SetTransfersVisible(true);
        }
    }
    internal void DropLocalFilesForTest(string[] paths,string? directory=null)=>EnqueueUploads(paths,directory);
}
