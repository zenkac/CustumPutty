using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EquipLink;

public sealed class SftpBrowserPanel : Grid,IDisposable
{
    readonly Window owner;readonly AppStore store;readonly Func<SessionPane?> selected;
    readonly TextBlock title=Ui.Text("SSH Browser",12,"#DCE5F0",true),status=Ui.Text("",10,"#8797AA"),notice=Ui.Text("",11,"#8797AA");
    readonly TextBlock instruction=Ui.Text("",12,"#8797AA");
    readonly TextBox address=Ui.Input();
    readonly ListBox entries=new(){Name="SftpEntries",Background=Ui.Brush("#17212D"),BorderThickness=new Thickness(0)};
    readonly StackPanel setup;readonly DockPanel pathRow;
    readonly Button connect,disconnect,up,reload,upload,download,mkdir,history;
    SftpSession? bound;IReadOnlyList<SftpEntry>? displayed;
    bool disposed;
    public SftpBrowserPanel(Window owner,AppStore store,Func<SessionPane?> selected)
    {
        this.owner=owner;this.store=store;this.selected=selected;Margin=new Thickness(10,12,10,8);
        foreach(var h in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})RowDefinitions.Add(new(){Height=h});
        title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;status.Margin=new Thickness(0,4,0,10);Children.Add(Ui.Stack(title,status));
        address.Name="SftpAddress";address.FontSize=11;address.ToolTip="SFTP 경로 · 입력 후 Enter";
        address.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;var session=bound;string path=address.Text;await Run(()=>session!.NavigateAsync(path));}};
        up=Tool("↑",()=>bound!.NavigateAsync(SftpSession.Parent(bound.CurrentPath)),"상위 폴더","SftpUp");
        reload=Tool("↻",()=>bound!.RefreshAsync(),"목록 새로고침","SftpRefresh");up.Margin=reload.Margin=new Thickness(0);
        pathRow=new DockPanel{Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(reload,Dock.Right);pathRow.Children.Add(reload);DockPanel.SetDock(up,Dock.Right);pathRow.Children.Add(up);pathRow.Children.Add(address);Children.Add(pathRow);SetRow(pathRow,1);
        var body=new Grid();body.Children.Add(entries);
        connect=Ui.Button("SFTP 연결",async()=>await Run(ConnectAsync),true);connect.Name="SftpConnect";connect.Margin=new Thickness(0,12,0,0);
        setup=Ui.Stack(Ui.Text("SSH 파일 탐색",14,"#DCE5F0",true),instruction,connect);setup.Margin=new Thickness(4,16,4,0);setup.VerticalAlignment=VerticalAlignment.Top;body.Children.Add(setup);Children.Add(body);SetRow(body,2);
        upload=Tool("↑ 업로드",UploadAsync,"내 PC 파일 업로드","SftpUpload");download=Tool("↓ 다운로드",DownloadAsync,"선택 파일 저장","SftpDownload");
        mkdir=Tool("새 폴더",NewFolderAsync,"현재 SFTP 경로에 폴더 생성","SftpNewFolder");
        disconnect=Ui.ToolButton("연결 끊기",()=>bound?.Disconnect(),"현재 SFTP 작업을 중단하고 연결을 닫습니다.");disconnect.Name="SftpDisconnect";
        history=Ui.ToolButton("전송 기록",ShowHistory);history.Name="SftpHistory";
        var actions=new WrapPanel();foreach(var button in new[]{upload,download,mkdir,history,disconnect}){button.FontSize=11;button.Margin=new Thickness(0,3,2,0);actions.Children.Add(button);}
        notice.Margin=new Thickness(0,8,0,4);var footer=Ui.Stack(notice,actions);Children.Add(footer);SetRow(footer,3);
        entries.SelectionChanged+=(_,_)=>UpdateActions();
        entries.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&entries.SelectedItem is ListBoxItem{Tag:SftpEntry item}&&(item.IsDirectory||item.IsLink)){e.Handled=true;var session=bound;await Run(()=>session!.NavigateAsync(item.FullPath));}};
        entries.AllowDrop=true;entries.DragOver+=(_,e)=>{e.Effects=bound?.IsConnected==true&&!bound.IsBusy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        entries.Drop+=async(_,e)=>{e.Handled=true;if(e.Data.GetData(DataFormats.FileDrop) is not string[] files)return;var session=bound;await Run(async()=>{if(files.Any(Directory.Exists))throw new ArgumentException("파일만 업로드할 수 있습니다. 폴더 전체 전송은 FTP 화면을 이용하세요.");foreach(string file in files)await session!.UploadAsync(file);});};
        IsVisibleChanged+=(_,_)=>Refresh();Refresh();
    }
    Button Tool(string text,Func<Task> action,string tip,string name){var button=Ui.ToolButton(text,async()=>await Run(action),tip);button.Name=name;return button;}
    async Task Run(Func<Task> action){try{await action();}catch(OperationCanceledException){}catch(Exception error){notice.Text=error.Message;notice.Foreground=Ui.Brush("#E9C786");}}
    async Task ConnectAsync()
    {
        var pane=selected();if(pane?.Equipment.IsSsh!=true)return;var session=pane.Sftp;
        var saved=store.Equipment.FirstOrDefault(x=>x.Id==pane.Equipment.Id&&x.Host==pane.Equipment.Host&&x.Port==pane.Equipment.Port&&x.IsSsh)??pane.Equipment;
        var request=SftpDialogs.Login(owner,saved);if(request==null)return;
        try
        {
            await session.ConnectAsync(request,
                key=>Dispatcher.Invoke(()=>!disposed&&owner.IsVisible&&SftpDialogs.ConfirmHost(owner,key)),
                (prompt,echo)=>Dispatcher.Invoke(()=>!disposed&&owner.IsVisible?SftpDialogs.Authentication(owner,prompt,echo):null));
            if(!session.IsConnected)return;
            saved.Username=request.Target.Username;saved.SftpInitialPath=request.InitialPath;
            saved.EncryptedPassword=request.Remember?CredentialProtector.Protect(saved,request.Password):null;
            if(store.Equipment.Contains(saved))store.Save();
        }
        finally{request.Clear();Refresh();}
    }
    async Task UploadAsync()
    {
        var session=bound;if(session==null)return;var picker=new OpenFileDialog{Title="SFTP 업로드 · 파일 선택",Multiselect=true};
        if(picker.ShowDialog(owner)!=true)return;foreach(string path in picker.FileNames)await session.UploadAsync(path);
    }
    async Task DownloadAsync()
    {
        var session=bound;if(session==null||entries.SelectedItem is not ListBoxItem{Tag:SftpEntry entry}||entry.IsDirectory||entry.IsLink)return;
        string name=FtpPaths.SafeLocalName(entry.Name);var picker=new SaveFileDialog{Title="SFTP 다운로드",FileName=name,OverwritePrompt=false};
        if(picker.ShowDialog(owner)==true)await session.DownloadAsync(entry,picker.FileName);
    }
    async Task NewFolderAsync(){var session=bound;if(session==null)return;string? name=FtpDialogs.NewFolder(owner,session.CurrentPath);if(name!=null)await session.CreateDirectoryAsync(name);}
    void ShowHistory()
    {
        if(bound==null)return;var dialog=new FormDialog(owner,"SFTP 전송 기록",620,550);
        dialog.Body.Children.Add(Ui.Text("현재 탭의 최근 파일 전송 50건",12,"#8797AA"));
        foreach(var item in bound.History)dialog.Body.Children.Add(Ui.Text($"{item.At:HH:mm:ss} · {item.Direction} · {item.Status}\n{item.Name}  ({item.Bytes:N0} B)",12,"#DCE5F0"));
        dialog.Actions.Children.Add(Ui.Button("닫기",()=>dialog.Close()));dialog.ShowDialog();
    }
    public void Refresh()
    {
        if(disposed)return;var pane=selected();var state=pane?.Sftp;
        if(!ReferenceEquals(bound,state)){if(bound!=null)bound.Changed-=Refresh;bound=state;if(bound!=null)bound.Changed+=Refresh;displayed=null;address.Text=bound?.CurrentPath??"";}
        bool ssh=pane?.Equipment.IsSsh==true,connected=ssh&&bound?.IsConnected==true,busy=bound?.IsBusy==true;
        title.Text=pane?.Equipment.Name??"SSH Browser";title.ToolTip=pane==null?null:pane.Equipment.Host+":"+pane.Equipment.Port;
        status.Text=pane==null?"SSH 세션을 선택하세요":ssh?"SFTP · "+(connected?"연결됨":busy?"연결 중":"연결 대기"):"Telnet · 파일 탐색 미지원";
        status.Foreground=Ui.Brush(connected?"#5DD6BE":"#8797AA");
        instruction.Text=pane==null?"SSH 설비 탭을 연 뒤 SFTP에 연결하세요.":ssh?"설비의 ID·비밀번호 또는 개인 키로 SFTP에 연결합니다.":"Telnet에는 파일 탐색 채널이 없습니다. SSH 연결과 서버의 SFTP 지원이 필요합니다. 파일 전송은 상단 FTP 화면에서도 이용할 수 있습니다.";
        setup.Visibility=connected?Visibility.Collapsed:Visibility.Visible;connect.Visibility=ssh?Visibility.Visible:Visibility.Collapsed;connect.IsEnabled=ssh&&!busy;
        pathRow.Visibility=ssh?Visibility.Visible:Visibility.Collapsed;address.IsEnabled=up.IsEnabled=reload.IsEnabled=connected&&!busy;
        if(!address.IsKeyboardFocusWithin)address.Text=bound?.CurrentPath??"";
        entries.Visibility=connected?Visibility.Visible:Visibility.Collapsed;entries.IsEnabled=connected&&!busy;
        notice.Text=!ssh?"":bound!.Error.Length>0?bound.Error:bound.Notice;
        notice.Foreground=Ui.Brush(bound?.Error.Length>0?"#E9C786":"#8797AA");
        UpdateActions();
        if(ReferenceEquals(displayed,bound?.Entries))return;displayed=bound?.Entries;entries.Items.Clear();if(!connected)return;
        foreach(var entry in bound!.Entries)
        {
            var glyph=Ui.Glyph(entry.IsDirectory||entry.IsLink?"\uE8B7":"\uE8A5",12);glyph.Foreground=Ui.Brush(entry.IsDirectory||entry.IsLink?"#5DD6BE":"#8797AA");glyph.Margin=new Thickness(0,0,7,0);
            var label=Ui.Text(entry.Name,11,"#DCE5F0");label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;
            var row=new DockPanel{Margin=new Thickness(2,3,2,3)};DockPanel.SetDock(glyph,Dock.Left);row.Children.Add(glyph);row.Children.Add(label);
            var item=new ListBoxItem{Tag=entry,Content=row,ToolTip=entry.FullPath+(entry.IsLink?"\n심볼릭 링크: 폴더 링크는 탐색 가능":""),Cursor=entry.IsDirectory||entry.IsLink?Cursors.Hand:Cursors.Arrow};
            var target=bound;item.PreviewMouseLeftButtonUp+=async(_,e)=>{if(!entry.IsDirectory&&!entry.IsLink||!ReferenceEquals(target,bound))return;e.Handled=true;await Run(()=>target.NavigateAsync(entry.FullPath));};entries.Items.Add(item);
        }
        if(entries.Items.Count==0)entries.Items.Add(new ListBoxItem{Content=Ui.Text("빈 폴더",11,"#8797AA"),IsEnabled=false});
    }
    void UpdateActions()
    {
        bool ssh=selected()?.Equipment.IsSsh==true,ready=ssh&&bound?.IsConnected==true&&!bound.IsBusy;
        foreach(var button in new[]{upload,download,mkdir,history,disconnect})button.Visibility=ssh?Visibility.Visible:Visibility.Collapsed;
        upload.IsEnabled=mkdir.IsEnabled=ready;download.IsEnabled=ready&&entries.SelectedItem is ListBoxItem{Tag:SftpEntry{IsDirectory:false,IsLink:false}};
        history.IsEnabled=bound?.History.Count>0;disconnect.IsEnabled=bound?.IsConnected==true||bound?.IsBusy==true;
    }
    public void Dispose(){disposed=true;if(bound!=null)bound.Changed-=Refresh;bound=null;entries.Items.Clear();}
}
