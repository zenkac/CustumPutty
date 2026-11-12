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
        listViewButton.ToolTip=(!iconView?"선택됨 · ":"")+"이름 · 크기 · 유형 · 수정한 날짜 · 제목을 클릭해 정렬, 경계선을 끌어 너비 조절";
        foreach(var header in fileHeaders)header.Visibility=iconView?Visibility.Collapsed:Visibility.Visible;
        foreach(var list in new[]{localFiles,remoteFiles})
        {
            var factory=new FrameworkElementFactory(iconView?typeof(WrapPanel):typeof(VirtualizingStackPanel));
            if(iconView){factory.SetValue(WrapPanel.ItemWidthProperty,112d);factory.SetValue(WrapPanel.ItemHeightProperty,106d);}
            list.ItemsPanel=new ItemsPanelTemplate(factory);ScrollViewer.SetHorizontalScrollBarVisibility(list,iconView?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto);
            foreach(var item in list.Items.OfType<ListBoxItem>())
            {
                if(item.Tag!=null){item.Padding=iconView?new Thickness(5):new Thickness(5,2,5,2);item.Margin=new Thickness(0,0,0,iconView?2:1);}
                if(item.Tag is LocalEntry l)item.Content=FileContent(l.Name,l.IsDirectory,l.Size,l.Modified.ToString("yy-MM-dd HH:mm"),false);
                else if(item.Tag is FtpEntry r)item.Content=FileContent(r.Name+(r.IsLink?" ↗":""),r.IsDirectory,r.Size,r.Modified?.ToLocalTime().ToString("yy-MM-dd HH:mm")??"—",true);
            }
        }
    }
    ListBoxItem FileItem(object tag,string name,bool directory,long? size,string modified,string tooltip)=>new(){Tag=tag,Content=FileContent(name,directory,size,modified,tag is FtpEntry),Padding=iconView?new Thickness(5):new Thickness(5,2,5,2),Margin=new Thickness(0,0,0,iconView?2:1),ToolTip=tooltip};
    FrameworkElement FileContent(string name,bool directory,long? size,string modified,bool remote)
    {
        if(!iconView)return FileRow(name,directory?"—":FormatSize(size),modified,directory,remote);
        string ext=Path.GetExtension(name).TrimStart('.').ToUpperInvariant();if(ext.Length>6)ext="FILE";
        string color=directory?"#E9C786":ext is "CSV" or "XLS" or "XLSX"?"#5DD6BE":ext is "ZIP" or "GZ" or "TAR"?"#C1A4F4":"#88B8F2";
        var glyph=ShellFileIcons.Create(name,directory,32);glyph.HorizontalAlignment=HorizontalAlignment.Center;glyph.Margin=new Thickness(0,3,0,1);
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
        foreach(var list in new[]{localFiles,remoteFiles})
        {
            list.AllowDrop=true;
            list.DragOver+=(_,e)=>HandlePaneDrag(list,e,false);
            list.Drop+=(_,e)=>HandlePaneDrag(list,e,true);
            list.DragLeave+=(_,_)=>ClearDropFeedback();
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
                e.Handled=true;StartPaneDrag(list);
            };
        }
        remoteFiles.ToolTip="파일 더블클릭: 현재 내 PC 폴더로 다운로드 · 폴더 더블클릭: 열기\n내 PC 창이나 Windows 탐색기로 끌어 다운로드 · 폴더 위에 놓으면 해당 폴더로 복사";
        localFiles.ToolTip="파일·폴더를 서버 창으로 끌어 업로드 · 서버 파일·폴더를 여기로 끌어 다운로드\nCtrl/Shift + 클릭으로 여러 개 선택 · 전송해도 원본은 유지됩니다.";
    }
    void DragRemoteFiles(FtpEntry[] selected)
    {
        if(selected.Any(x=>x.IsDirectory)||selected.Length>1000)
        {
            var data=new DataObject(InternalDragFormat,paneDrag!.Token);RunDragLoop(remoteFiles,data);return;
        }
        var origin=session.Site!.Copy();string secret=session.PasswordForTransfer();
        try{activeDrag=new FtpVirtualFiles(origin,secret,selected);}finally{secret="";}
        uint effect;
        if(App.IsTesting&&DragLoopForTest!=null){DragLoopForTest(remoteFiles,new DataObject(activeDrag));effect=0;}else effect=activeDrag.DoDragDrop();
        if(effect==1&&!disposed)
        {
            foreach(var file in activeDrag.Delivered)queue.RecordExternalDownload(origin,file.Entry,"Windows 탐색기",file.Bytes);
            SwitchTransferView(false);SetTransfersVisible(true);
        }
    }
    internal void DropLocalFilesForTest(string[] paths,string? directory=null)=>EnqueueUploads(paths,directory);
}
