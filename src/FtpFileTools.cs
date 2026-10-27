using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EquipLink;

public sealed partial class FtpWorkspace
{
    private readonly TextBox localFilter=Ui.Input(),remoteFilter=Ui.Input();
    private readonly List<MarqueeSelection> rectangleSelectors=[];
    private readonly CancellationTokenSource fileLifetime=new();
    private LocalEntry[] localEntries=[];
    private Button localDelete=null!,remoteDelete=null!;
    private string filterFolder="",localMessage="",remoteMessage="";
    private bool renderingFiles,deleting;
    internal bool IsDeletingForTest=>deleting;
    private FrameworkElement BuildFileSearch(bool remote)
    {
        var row=new DockPanel{Margin=new Thickness(8,0,8,7)};
        var search=remote?remoteFilter:localFilter;search.Name=remote?"FtpRemoteSearch":"FtpLocalSearch";
        search.FontSize=11;search.MinHeight=28;search.ToolTip="현재 폴더의 파일명 검색 · 하위 폴더는 검색하지 않습니다 · Esc로 지우기";
        var label=Ui.Text("검색",11,"#8797AA");label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(2,0,7,0);DockPanel.SetDock(label,Dock.Left);row.Children.Add(label);
        var delete=Ui.AsyncToolButton("삭제",()=>DeleteSelectionAsync(remote),"선택 항목 영구 삭제 · Delete");delete.Name=remote?"FtpRemoteDelete":"FtpLocalDelete";delete.Margin=new Thickness(4,0,0,0);DockPanel.SetDock(delete,Dock.Right);row.Children.Add(delete);
        if(remote)remoteDelete=delete;else localDelete=delete;
        var clear=Ui.ToolButton("×",()=>search.Clear(),"검색 지우기");clear.Margin=new Thickness(4,0,0,0);clear.Width=25;DockPanel.SetDock(clear,Dock.Right);row.Children.Add(clear);row.Children.Add(search);
        search.TextChanged+=(_,_)=>RenderFiles(remote);
        search.KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;search.Clear();}};
        return row;
    }
    private void InstallFileTools()
    {
        localCount.Name="FtpLocalCount";remoteCount.Name="FtpRemoteCount";
        foreach(var list in new[]{localFiles,remoteFiles})
        {
            rectangleSelectors.Add(new MarqueeSelection(list));
            list.KeyDown+=async (_,e)=>
            {
                if(e.Key==Key.Delete&&Keyboard.Modifiers==ModifierKeys.None){e.Handled=true;await DeleteSelectionAsync(list==remoteFiles);}
                else if(e.Key==Key.A&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;foreach(var item in list.Items.OfType<ListBoxItem>().Where(x=>x.Tag!=null))item.IsSelected=true;}
            };
        }
    }
    private static string? EntryPath(object? tag)=>tag switch{LocalEntry e=>e.FullPath,FtpEntry e=>e.FullPath,_=>null};
    private void RenderFiles(bool remote)
    {
        if(disposed||renderingFiles)return;renderingFiles=true;
        try
        {
            var list=remote?remoteFiles:localFiles;string query=(remote?remoteFilter:localFilter).Text.Trim();
            var selected=list.SelectedItems.OfType<ListBoxItem>().Select(x=>EntryPath(x.Tag)).Where(x=>x!=null).ToHashSet(StringComparer.Ordinal);
            list.Items.Clear();
            if(remote&&session.IsConnected)
                foreach(var e in session.Entries.Where(x=>x.Name.Contains(query,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.IsDirectory).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
                    list.Items.Add(FileItem(e,e.Name+(e.IsLink?" ↗":""),e.IsDirectory,e.Size,e.Modified?.ToLocalTime().ToString("yy-MM-dd HH:mm")??"—",e.FullPath+(e.IsLink?"\n심볼릭 링크":"")));
            else if(!remote)
                foreach(var e in localEntries.Where(x=>x.Name.Contains(query,StringComparison.OrdinalIgnoreCase)))list.Items.Add(FileItem(e,e.Name,e.IsDirectory,e.Size,e.Modified.ToString("yy-MM-dd HH:mm"),e.FullPath));
            foreach(var item in list.Items.OfType<ListBoxItem>())if(selected.Contains(EntryPath(item.Tag)))item.IsSelected=true;
            if(list.Items.Count==0)
            {
                string message=remote&&!session.IsConnected?"접속하기 또는 사이트를 더블클릭해 연결하세요.":query.Length>0?"현재 폴더에 검색 결과가 없습니다.":"이 폴더는 비어 있습니다.";
                list.Items.Add(new ListBoxItem{IsEnabled=false,Content=Ui.Text(message,12,"#8797AA"),Padding=new Thickness(14,24,14,12)});
            }
        }
        finally{renderingFiles=false;UpdateFileCounts();UpdateDeleteButtons();}
    }
    private void UpdateFileCounts()
    {
        if(renderingFiles)return;
        string Count(ListBox list,int total)=>$"{list.Items.OfType<ListBoxItem>().Count(x=>x.Tag!=null):N0} / {total:N0}개 · 선택 {list.SelectedItems.OfType<ListBoxItem>().Count(x=>x.Tag!=null):N0}개";
        localCount.Text=(localMessage.Length>0?localMessage+" · ":"")+Count(localFiles,localEntries.Length)+(localEntries.Length>=20000?" · 최대 20,000개 표시":"");
        remoteCount.Text=session.Error.Length>0?session.Error:!session.IsConnected?"일반 FTP · 서버에 연결해 주세요.":(remoteMessage.Length>0?remoteMessage+" · ":"")+Count(remoteFiles,session.Entries.Count)+" · "+session.EncodingDisplay+(session.ListingNotice.Length>0?" · "+session.ListingNotice:"");
        foreach(var count in new[]{localCount,remoteCount})count.ToolTip=count.Text+"\nCtrl/Shift + 클릭 · 빈 공간에서 드래그해 영역 선택 · Delete로 삭제";
    }
    private void UpdateDeleteButtons()
    {
        if(localDelete==null||remoteDelete==null)return;
        bool available=!deleting&&!queue.HasPending;
        localDelete.IsEnabled=available&&!localLoading&&localFiles.SelectedItems.OfType<ListBoxItem>().Any(x=>x.Tag is LocalEntry);
        remoteDelete.IsEnabled=available&&session.IsConnected&&!session.IsBusy&&remoteFiles.SelectedItems.OfType<ListBoxItem>().Any(x=>x.Tag is FtpEntry);
    }
    private async Task DeleteSelectionAsync(bool remote)
    {
        if(disposed||deleting||queue.HasPending||remote&&(!session.IsConnected||session.IsBusy)||!remote&&localLoading)return;
        var entries=(remote?remoteFiles:localFiles).SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).Where(x=>x is FtpEntry or LocalEntry).ToArray();
        if(entries.Length==0)return;
        string folder=remote?session.CurrentPath:localPath,location=remote?$"서버 · {session.Site!.Host}:{session.Site.Port}":"내 PC";
        string[] names=entries.Select(x=>x is FtpEntry r?r.Name:((LocalEntry)x).Name).ToArray();
        var dialog=new FormDialog(owner,"영구 삭제 확인",480,compact:true);
        dialog.Body.Children.Add(Ui.Text($"선택한 {names.Length:N0}개 항목을 영구 삭제할까요?",14,"#E9C786",true));
        var warning=Ui.Text("휴지통으로 이동하지 않으며 복구할 수 없습니다. 폴더를 선택하면 내부 파일과 하위 폴더도 삭제됩니다.",12,"#DCE5F0");warning.Name="FtpDeleteWarning";warning.Margin=new Thickness(0,8,0,12);dialog.Body.Children.Add(warning);
        var details=Ui.Stack(Ui.Text(location,12,"#DCE5F0",true),Ui.Text(folder,11,"#8797AA"),Ui.Text(string.Join("\n",names.Take(6))+(names.Length>6?$"\n외 {names.Length-6}개":""),12,"#DCE5F0"));dialog.Body.Children.Add(Ui.Card(details,"#17212D",12));
        var cancel=Ui.Button("취소",()=>dialog.DialogResult=false);cancel.Name="FtpDeleteCancel";cancel.IsCancel=true;cancel.IsDefault=true;
        var confirm=Ui.Button("영구 삭제",()=>dialog.DialogResult=true);confirm.Name="FtpDeleteConfirm";
        dialog.Actions.Children.Add(cancel);dialog.Actions.Children.Add(confirm);dialog.Loaded+=(_,_)=>cancel.Focus();
        if(dialog.ShowDialog()!=true||disposed||queue.HasPending)return;
        deleting=true;UpdateButtons();int completed=0;
        try
        {
            if(remote)await session.DeleteEntriesAsync(entries.Cast<FtpEntry>().ToArray(),folder,()=>completed++);
            else await Task.Run(()=>FtpFileDeletion.DeleteLocal(folder,entries.Cast<LocalEntry>().Select(x=>x.FullPath).ToArray(),()=>completed++,fileLifetime.Token));
            if(remote)remoteMessage=$"삭제 완료 {completed:N0}개";else localMessage=$"삭제 완료 {completed:N0}개";
        }
        catch(Exception error)
        {
            string message=$"삭제를 완료하지 못했습니다. 이미 삭제된 항목 {completed:N0}개는 복구할 수 없습니다.\n"+error.Message;
            if(remote)remoteMessage=$"삭제 중단 · 삭제됨 {completed:N0}개";else localMessage=$"삭제 중단 · 삭제됨 {completed:N0}개";
            if(!disposed)Ui.Error(new IOException(message));
        }
        finally
        {
            deleting=false;
            if(!disposed){try{if(remote&&session.IsConnected)await session.RefreshAsync();else if(!remote)await NavigateLocalAsync(folder);}catch{}UpdateButtons();}
        }
    }
    internal Task DeleteForTestAsync(bool remote)=>DeleteSelectionAsync(remote);
    internal void SelectRectangleForTest(bool remote,Point from,Point to,ModifierKeys modifiers)=>rectangleSelectors[remote?1:0].SelectForTest(from,to,modifiers);
}
