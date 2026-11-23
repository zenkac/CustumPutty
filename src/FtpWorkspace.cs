using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace EquipLink;

/// <summary>Compact two-pane FTP workspace; terminal sessions remain owned by MainWindow.</summary>
public sealed partial class FtpWorkspace : Grid,IDisposable
{
    private readonly Window owner;private readonly AppStore store;private readonly FtpSession session;private readonly FtpTransferQueue queue;
    private readonly ListBox sites=new(),localFiles=FilesList(),remoteFiles=FilesList(),transfers=new(){SelectionMode=SelectionMode.Single};
    private readonly TextBox siteSearch=Ui.Input(),localAddress=Ui.Input(),remoteAddress=Ui.Input();
    private readonly TextBlock remoteCaption=Ui.Text("서버 · 연결 대기",12,"#DCE5F0",true),connectionState=Ui.Text("연결 대기",11,"#8797AA"),localCount=Ui.Text("",10,"#8797AA"),remoteCount=Ui.Text("",10,"#8797AA"),transferState=Ui.Text("전송 내역이 없습니다",11,"#8797AA");
    private readonly FtpFolderTree folderTree;private readonly Border transferDrawer;private readonly Grid transferContent=new();
    private readonly Button connectButton,disconnectButton,refreshButton,uploadButton,downloadButton,folderButton,transferButton,cancelButton,queueTab,historyTab;
    private bool disposed,historyView,localLoading,transferRefreshQueued;private string localPath="",remoteStamp="",historyStamp="";private int localGeneration;
    private readonly HashSet<string> observedFinished=[];
    private sealed record LocalEntry(string Name,string FullPath,bool IsDirectory,long? Size,DateTimeOffset Modified);

    public FtpWorkspace(Window owner,AppStore store,FtpSession session,FtpTransferQueue queue,Func<IReadOnlyList<TerminalFtpTarget>>? targets=null)
    {
        this.owner=owner;this.store=store;this.session=session;this.queue=queue;terminalTargets=targets??(()=>[]);iconView=store.Settings.FtpIconView;Background=Ui.Brush("#111923");
        sites.Name="FtpSites";localFiles.Name="FtpLocalFiles";remoteFiles.Name="FtpRemoteFiles";localAddress.Name="FtpLocalAddress";remoteAddress.Name="FtpRemoteAddress";
        RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});RowDefinitions.Add(new(){Height=GridLength.Auto});
        connectButton=Ui.AsyncToolButton("접속하기",QuickConnectAsync,"IP · ID · 비밀번호로 FTP 연결",true);
        disconnectButton=Ui.ToolButton("접속 해제",session.Disconnect,"파일 탐색 연결 해제 · 전송 대기열은 유지됩니다");
        refreshButton=Ui.AsyncToolButton("새로고침",RefreshBothAsync,"내 PC와 서버 목록 새로고침 · F5");
        uploadButton=Ui.AsyncToolButton("↑ 업로드",UploadSelectedAsync,"선택한 내 PC 파일 / 폴더를 서버로 전송");
        downloadButton=Ui.ToolButton("↓ 다운로드",DownloadSelected,"선택한 서버 파일 / 폴더를 현재 내 PC 폴더로 전송");
        folderButton=Ui.AsyncToolButton("새 폴더",()=>activeRemote?CreateRemoteFolderAsync():CreateLocalFolderAsync(),"현재 위치에 폴더 만들기");
        transferButton=Ui.ToolButton("전송창",()=>SetTransfersVisible(transferContent.Visibility!=Visibility.Visible),"진행 상황과 전송 히스토리 보기");
        uploadButton.Name="FtpUpload";downloadButton.Name="FtpDownload";folderButton.Name="FtpNewFolder";transferButton.Name="FtpTransfers";connectButton.Name="FtpConnect";
        var toolbar=new WrapPanel{Margin=new Thickness(10,7,8,7),VerticalAlignment=VerticalAlignment.Center};
        foreach(var button in new[]{connectButton,disconnectButton,refreshButton})toolbar.Children.Add(button);toolbar.Children.Add(Ui.Divider());
        foreach(var button in new[]{uploadButton,downloadButton,folderButton})toolbar.Children.Add(button);toolbar.Children.Add(Ui.Divider());
        iconViewButton=Ui.ToolButton("▦ 아이콘",()=>SetFileView(true),"파일을 큰 아이콘으로 보기");iconViewButton.Name="FtpIconView";
        listViewButton=Ui.ToolButton("☷ 목록",()=>SetFileView(false),"이름·크기·유형·날짜 목록으로 보기");listViewButton.Name="FtpListView";
        toolbar.Children.Add(iconViewButton);toolbar.Children.Add(listViewButton);toolbar.Children.Add(Ui.Divider());AddExplorerActions(toolbar);toolbar.Children.Add(Ui.Divider());toolbar.Children.Add(transferButton);Children.Add(new Border{Background=Ui.Brush("#17212D"),BorderThickness=new Thickness(0,0,0,1),BorderBrush=Ui.Brush("#2A394A"),Child=toolbar});

        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(224),MinWidth=170,MaxWidth=340});body.ColumnDefinitions.Add(new(){Width=new GridLength(4)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        // The site list takes only the height it needs (up to ~40% of the panel)
        // so the folder tree directly follows it instead of floating mid-panel.
        var navigation=new Grid{Margin=new Thickness(12,12,10,8)};foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})navigation.RowDefinitions.Add(new(){Height=height});
        navigation.RowDefinitions[4].MinHeight=60;sites.MinHeight=40;navigation.SizeChanged+=(_,_)=>sites.MaxHeight=Math.Max(40,Math.Min(navigation.ActualHeight*.4,navigation.ActualHeight-230));
        var addSite=Ui.ToolButton("",AddSite,"FTP 사이트 추가");addSite.Content=Ui.Glyph("\uE710",12);addSite.Width=28;addSite.Margin=new Thickness(0);System.Windows.Automation.AutomationProperties.SetName(addSite,"FTP 사이트 추가");
        var siteMenu=Ui.ToolButton("",ShowSiteMenu,"선택한 FTP 사이트 편집 / 삭제");siteMenu.Content=Ui.Glyph("\uE712",14);siteMenu.Width=28;siteMenu.Margin=new Thickness(0);System.Windows.Automation.AutomationProperties.SetName(siteMenu,"사이트 메뉴");
        var siteHeading=new DockPanel();var siteActions=Ui.Row(addSite,siteMenu);DockPanel.SetDock(siteActions,Dock.Right);siteHeading.Children.Add(siteActions);var siteTitle=Ui.Text("사이트맵",13,"#DCE5F0",true);siteTitle.VerticalAlignment=VerticalAlignment.Center;siteHeading.Children.Add(siteTitle);navigation.Children.Add(siteHeading);
        siteSearch.MinHeight=29;siteSearch.FontSize=11;siteSearch.ToolTip="사이트 이름 · IP 검색";siteSearch.TextChanged+=(_,_)=>RefreshSites();var siteSearchField=Ui.WithPlaceholder(siteSearch,"사이트 · IP 검색");siteSearchField.Margin=new Thickness(0,8,0,8);navigation.Children.Add(siteSearchField);SetRow(siteSearchField,1);
        navigation.Children.Add(sites);SetRow(sites,2);sites.MouseDoubleClick+=async (_,_)=>{if(sites.SelectedItem is ListBoxItem item&&item.IsMouseOver&&item.Tag is FtpSite site)await ConnectSavedSafeAsync(site);};sites.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter&&SelectedSite!=null){e.Handled=true;await ConnectSavedSafeAsync(SelectedSite);}};
        var treeHeading=BuildTreeChoice();navigation.Children.Add(treeHeading);SetRow(treeHeading,3);
        folderTree=new FtpFolderTree(session,NavigateRemoteSafeAsync){Name="FtpDirectoryTree"};navigation.Children.Add(folderTree);SetRow(folderTree,4);
        localFolderTree=new LocalFolderTree(folderTree.ItemContainerStyle,NavigateLocalSafeAsync){Name="FtpLocalDirectoryTree",Visibility=Visibility.Collapsed};navigation.Children.Add(localFolderTree);SetRow(localFolderTree,4);
        body.Children.Add(new Border{Background=Ui.Brush("#17212D"),Child=navigation});var navSplitter=Splitter();body.Children.Add(navSplitter);SetColumn(navSplitter,1);
        filePaneHost.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=150});filePaneHost.ColumnDefinitions.Add(new(){Width=new GridLength(5)});filePaneHost.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=150});
        sourcePane=BuildSourcePane();serverPane=BuildFilePane(true);paneSplitter=Splitter();filePaneHost.Children.Add(sourcePane);filePaneHost.Children.Add(paneSplitter);SetColumn(paneSplitter,1);filePaneHost.Children.Add(serverPane);body.Children.Add(filePaneHost);SetColumn(filePaneHost,2);Children.Add(body);SetRow(body,1);
        foreach(var pane in new[]{sourcePane,serverPane}){pane.PreviewMouseDown+=(_,_)=>ActivatePane(pane==serverPane);pane.GotKeyboardFocus+=(_,_)=>{if(activeRemote!=(pane==serverPane))ActivatePane(pane==serverPane);};}ApplyExplorerLayout();

        transferDrawer=new Border{Background=Ui.Brush("#17212D"),BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(0,1,0,0)};
        var drawer=new Grid();drawer.RowDefinitions.Add(new(){Height=GridLength.Auto});drawer.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var drawerHead=new DockPanel{Margin=new Thickness(10,3,8,3)};var right=Ui.Row();DockPanel.SetDock(right,Dock.Right);
        cancelButton=Ui.ToolButton("선택 취소",()=>{if(transfers.SelectedItem is FtpTransferItem item)queue.Cancel(item);},"선택한 대기 / 진행 중 전송 취소");right.Children.Add(cancelButton);right.Children.Add(Ui.ToolButton("전체 취소",queue.CancelAll,"대기 및 진행 중인 전송 취소"));right.Children.Add(Ui.ToolButton("⌃",()=>SetTransfersVisible(transferContent.Visibility!=Visibility.Visible),"전송창 펼치기 / 접기"));drawerHead.Children.Add(right);
        queueTab=Ui.ToolButton("전송 대기열",()=>SwitchTransferView(false));historyTab=Ui.ToolButton("히스토리",()=>SwitchTransferView(true));var left=Ui.Row(queueTab,historyTab,transferState);transferState.VerticalAlignment=VerticalAlignment.Center;transferState.Margin=new Thickness(12,0,0,0);transferState.TextWrapping=TextWrapping.NoWrap;drawerHead.Children.Add(left);drawer.Children.Add(drawerHead);
        transferContent.Height=180;transferContent.Visibility=Visibility.Collapsed;transferContent.Margin=new Thickness(8,0,8,7);transferContent.RowDefinitions.Add(new(){Height=GridLength.Auto});transferContent.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var transferHeader=TransferHeader();transferContent.Children.Add(transferHeader);transfers.ItemTemplate=TransferTemplate();transfers.SelectionChanged+=(_,_)=>UpdateTransferControls();transferContent.Children.Add(transfers);SetRow(transfers,1);drawer.Children.Add(transferContent);SetRow(transferContent,1);transferDrawer.Child=drawer;Children.Add(transferDrawer);SetRow(transferDrawer,2);

        localAddress.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter){e.Handled=true;await NavigateLocalSafeAsync(localAddress.Text);}};
        remoteAddress.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter){e.Handled=true;await NavigateRemoteSafeAsync(remoteAddress.Text);}};
        localFiles.MouseDoubleClick+=async (_,_)=>{if(localFiles.SelectedItem is ListBoxItem item&&item.IsMouseOver&&item.Tag is LocalEntry{IsDirectory:true} entry)await NavigateLocalSafeAsync(entry.FullPath);};
        remoteFiles.MouseDoubleClick+=async (_,e)=>
        {
            if(e.ChangedButton!=MouseButton.Left||disposed||dragging||!remoteFiles.IsEnabled)return;
            var item=ItemsControl.ContainerFromElement(remoteFiles,e.OriginalSource as DependencyObject) as ListBoxItem;
            if(item?.Tag is not FtpEntry entry)return;
            e.Handled=true;
            if(entry.IsDirectory)await NavigateRemoteSafeAsync(entry.FullPath);
            else DownloadEntries([entry]);
        };
        localFiles.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter&&localFiles.SelectedItem is ListBoxItem{Tag:LocalEntry{IsDirectory:true} entry}){e.Handled=true;await NavigateLocalSafeAsync(entry.FullPath);}else if(e.Key==Key.Back){e.Handled=true;await NavigateLocalSafeAsync(Directory.GetParent(localPath)?.FullName??localPath);}};
        remoteFiles.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter&&remoteFiles.SelectedItem is ListBoxItem{Tag:FtpEntry{IsDirectory:true} entry}){e.Handled=true;await NavigateRemoteSafeAsync(entry.FullPath);}else if(e.Key==Key.Back){e.Handled=true;await NavigateRemoteSafeAsync(FtpPaths.Parent(session.CurrentPath));}};
        PreviewKeyDown+=async (_,e)=>{if(e.Key==Key.F5){e.Handled=true;await RunSafeAsync(RefreshBothAsync);}};
        localFiles.SelectionChanged+=(_,_)=>UpdateButtons();remoteFiles.SelectionChanged+=(_,_)=>UpdateButtons();
        InstallFileTools();AddFileMenus();session.Changed+=OnSessionChanged;queue.Changed+=OnQueueChanged;
        queue.ConfirmOverwrite=ConfirmConflictAsync;ConfigureFileView();InstallDragDrop();
        foreach(var item in queue.Items.Where(x=>!x.IsPending))observedFinished.Add(item.Id);
        RefreshSites();OnSessionChanged();SwitchTransferView(false);UpdateTransferControls();
        Loaded+=async (_,_)=>{if(localPath==""&&!localLoading)await NavigateLocalSafeAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));};
    }

    private FtpSite? SelectedSite=>(sites.SelectedItem as ListBoxItem)?.Tag as FtpSite;
    private static GridSplitter Splitter()=>new(){Background=Ui.Brush("#2A394A"),HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,ShowsPreview=true};
    private static ListBox FilesList()
    {
        var list=new ListBox{Background=Ui.Brush("#111923"),Foreground=Ui.Brush("#DCE5F0"),SelectionMode=SelectionMode.Extended,Margin=new Thickness(4,0,4,0),Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        // Resource dictionaries freeze their brushes, so refresh this one on theme changes.
        void Canvas(){var brush=new SolidColorBrush(ThemeService.Brush("#111923").Color);brush.Freeze();list.Resources[SystemColors.ControlBrushKey]=brush;}
        Canvas();ThemeService.Changed+=Canvas;list.Unloaded+=(_,_)=>ThemeService.Changed-=Canvas;list.Loaded+=(_,_)=>{ThemeService.Changed-=Canvas;ThemeService.Changed+=Canvas;Canvas();};return list;
    }
    private FrameworkElement BuildFilePane(bool remote)
    {
        var pane=new Grid{Background=Ui.Brush("#111923")};foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})pane.RowDefinitions.Add(new(){Height=height});
        if(remote)
        {
            var head=new DockPanel{Margin=new Thickness(12,11,10,8),MinHeight=29};remoteCaption.TextWrapping=TextWrapping.NoWrap;remoteCaption.TextTrimming=TextTrimming.CharacterEllipsis;remoteCaption.VerticalAlignment=VerticalAlignment.Center;
            DockPanel.SetDock(connectionState,Dock.Right);connectionState.VerticalAlignment=VerticalAlignment.Center;connectionState.Margin=new Thickness(8,0,0,0);head.Children.Add(connectionState);head.Children.Add(remoteCaption);pane.Children.Add(head);
        }
        var path=new DockPanel{Margin=new Thickness(8,0,8,8)};var up=Ui.AsyncToolButton("↑",remote?()=>NavigateRemoteSafeAsync(FtpPaths.Parent(session.CurrentPath)):()=>NavigateLocalSafeAsync(Directory.GetParent(localPath)?.FullName??localPath),"상위 폴더");up.Margin=new Thickness(0,0,4,0);DockPanel.SetDock(up,Dock.Left);path.Children.Add(up);var input=remote?remoteAddress:localAddress;input.FontSize=11;input.MinHeight=30;input.ToolTip="경로를 직접 입력하고 Enter";path.Children.Add(input);pane.Children.Add(path);SetRow(path,1);
        var filter=BuildFileSearch(remote);pane.Children.Add(filter);SetRow(filter,2);
        var headerBorder=new Border{Child=BuildExplorerHeader(remote),Padding=new Thickness(9,0,13,0),Background=Ui.Brush("#17212D"),BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(0,1,0,1)};fileHeaders.Add(headerBorder);pane.Children.Add(headerBorder);SetRow(headerBorder,3);
        var list=remote?remoteFiles:localFiles;var selectionLayer=new System.Windows.Documents.AdornerDecorator{Child=list};pane.Children.Add(selectionLayer);SetRow(selectionLayer,4);var count=remote?remoteCount:localCount;count.Margin=new Thickness(12,6,12,6);count.TextWrapping=TextWrapping.NoWrap;count.TextTrimming=TextTrimming.CharacterEllipsis;pane.Children.Add(count);SetRow(count,5);return pane;
    }
    private Grid FileRow(string name,string size,string modified,bool folder,bool remote)
    {
        var row=(remote?remoteColumns:localColumns).Row();row.MinHeight=23;
        var label=Ui.Text(name,11,"#DCE5F0");label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.VerticalAlignment=VerticalAlignment.Center;
        var namePanel=new DockPanel{Margin=new Thickness(7,0,8,0)};var glyph=ShellFileIcons.Create(name,folder,16);glyph.Margin=new Thickness(0,0,8,0);glyph.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(glyph,Dock.Left);namePanel.Children.Add(glyph);namePanel.Children.Add(label);row.Children.Add(namePanel);
        string[] values=[size,ShellFileIcons.TypeName(name,folder),modified];
        for(int i=0;i<values.Length;i++){var text=Ui.Text(values[i],10,"#8797AA");text.TextWrapping=TextWrapping.NoWrap;text.TextTrimming=TextTrimming.CharacterEllipsis;text.Margin=new Thickness(8,0,8,0);text.VerticalAlignment=VerticalAlignment.Center;if(i==0)text.TextAlignment=TextAlignment.Right;row.Children.Add(text);SetColumn(text,i+1);}return row;
    }
    public void RefreshSites()
    {
        var selected=SelectedSite?.Id;sites.Items.Clear();string query=siteSearch.Text.Trim();
        foreach(var site in store.FtpSites.Where(x=>(x.Name+" "+x.Host).Contains(query,StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x.Name))
        {
            var name=Ui.Text(site.Name,11,"#DCE5F0",true);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;
            var detail=Ui.Text(site.Host+":"+site.Port,10,"#8797AA");detail.TextWrapping=TextWrapping.NoWrap;detail.TextTrimming=TextTrimming.CharacterEllipsis;detail.Margin=new Thickness(0,3,0,0);
            var item=new ListBoxItem{Content=Ui.Stack(name,detail),Tag=site,Padding=new Thickness(7,6,7,6),ToolTip=site.Username+" @ "+site.Host+"\n"+site.InitialPath};sites.Items.Add(item);if(site.Id==selected)item.IsSelected=true;
        }
        if(sites.Items.Count==0)sites.Items.Add(new ListBoxItem{Content=Ui.Text(query.Length>0?"검색 결과가 없습니다":"+ 버튼으로 자주 쓰는 서버를 저장하세요.",11,"#8797AA"),IsEnabled=false});
    }
    private void AddSite(){var request=FtpDialogs.EditSite(owner,new FtpSite());if(request==null)return;store.FtpSites.Add(request.Site);SaveStore();RefreshSites();}
    private void EditSite(){var original=SelectedSite;if(original==null)return;var request=FtpDialogs.EditSite(owner,original);if(request==null)return;int index=store.FtpSites.IndexOf(original);if(index>=0)store.FtpSites[index]=request.Site;SaveStore();RefreshSites();}
    private void RemoveSite(){var site=SelectedSite;if(site==null)return;if(!Dialogs.Confirm(owner,"사이트 삭제",$"‘{site.Name}’ 사이트를 사이트맵에서 삭제할까요? 서버의 파일은 그대로 남습니다.","삭제",true))return;store.FtpSites.Remove(site);SaveStore();RefreshSites();}
    private void ShowSiteMenu(){var menu=new ContextMenu();Menu(menu,"사이트 추가",AddSite);Menu(menu,"선택한 사이트 편집",EditSite,SelectedSite!=null);Menu(menu,"선택한 사이트 삭제",RemoveSite,SelectedSite!=null);sites.ContextMenu=menu;menu.PlacementTarget=sites;menu.IsOpen=true;}
    private async Task QuickConnectAsync(){var request=FtpDialogs.EditSite(owner,store.LastFtpConnection?.Copy()??new FtpSite(),true);if(request!=null)await ConnectAsync(request.Site,request.Password);}
    private async Task ConnectSavedSafeAsync(FtpSite site)
    {
        await RunSafeAsync(async()=>
        {
            if(!site.HasSavedPassword){var request=FtpDialogs.EditSite(owner,site,true);if(request!=null){int index=store.FtpSites.IndexOf(site);if(index>=0)store.FtpSites[index]=request.Site;RefreshSites();await ConnectAsync(request.Site,request.Password);}return;}
            string password=CredentialProtector.Unprotect(site.CredentialTarget(),site.EncryptedPassword!);try{await ConnectAsync(site,password);}finally{password="";}
        });
    }
    private Task ConnectAsync(FtpSite site,string password)=>session.ConnectAsync(site,password);
    private void SaveStore(){try{store.Save();}catch(Exception error){Ui.Error(error);}}
    private async Task BrowseLocalAsync(){var picker=new OpenFolderDialog{Title="내 PC 폴더 선택",InitialDirectory=Directory.Exists(localPath)?localPath:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};if(picker.ShowDialog(owner)==true)await NavigateLocalSafeAsync(picker.FolderName);}
    private async Task NavigateLocalSafeAsync(string path)=>await RunSafeAsync(()=>NavigateLocalAsync(path));
    private async Task NavigateLocalAsync(string path)
    {
        string resolved=Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));int generation=++localGeneration;localLoading=true;UpdateButtons();
        try
        {
            var entries=await Task.Run(()=>
            {
                if(!Directory.Exists(resolved))throw new DirectoryNotFoundException("내 PC 폴더를 찾을 수 없습니다: "+resolved);
                var list=new List<LocalEntry>();foreach(string file in Directory.EnumerateFileSystemEntries(resolved).Take(20000))
                {
                    try{var attr=File.GetAttributes(file);bool dir=(attr&FileAttributes.Directory)!=0;var info=new FileInfo(file);list.Add(new(Path.GetFileName(file),file,dir,dir?null:info.Length,new DateTimeOffset(info.LastWriteTime)));}catch(UnauthorizedAccessException){}catch(IOException){}
                }
                return list.OrderByDescending(x=>x.IsDirectory).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if(disposed||generation!=localGeneration)return;bool changed=!string.Equals(localPath,resolved,StringComparison.OrdinalIgnoreCase);localPath=resolved;localAddress.Text=resolved;localEntries=entries;
            if(changed)localFilter.Clear();RenderFiles(false);localFolderTree.Sync(localPath,entries.Where(x=>x.IsDirectory).Select(x=>x.FullPath));
        }
        finally{if(generation==localGeneration){localLoading=false;UpdateButtons();}}
    }
    private async Task NavigateRemoteSafeAsync(string path)=>await RunSafeAsync(async()=>{if(session.IsConnected)await session.NavigateAsync(path);});
    private async Task RefreshBothAsync(){if(localPath!="")await NavigateLocalAsync(localPath);if(session.IsConnected&&!session.IsBusy)await session.RefreshAsync();}
    private void OnSessionChanged()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(OnSessionChanged);return;}if(disposed)return;
        remoteCaption.Text=session.IsConnected?session.Site!.Name+" · "+session.Site.Host:session.Error.Length>0?"서버 · 접속 확인 필요":"서버 · 연결 대기";remoteCaption.ToolTip=session.Site?.Host;
        connectionState.Text=session.IsBusy?"연결 / 읽는 중…":session.IsConnected?"● 연결됨":"○ 연결 안 됨";connectionState.Foreground=Ui.Brush(session.IsConnected?"#5DD6BE":"#8797AA");
        if(!remoteAddress.IsKeyboardFocusWithin)remoteAddress.Text=session.IsConnected?session.CurrentPath:"/";
        string stamp=session.IsConnected?session.Site!.Id+"|"+session.CurrentPath+"|"+session.Error+"|"+session.ListingNotice+"|"+string.Join(';',session.Entries.Select(x=>$"{x.Name}|{x.Size}|{x.Modified}")):"disconnected|"+session.Error;
        if(stamp!=remoteStamp)
        {
            remoteStamp=stamp;string folder=(session.Site?.Id??"")+"|"+session.CurrentPath;
            if(folder!=filterFolder){filterFolder=folder;remoteFilter.Clear();remoteMessage="";}RenderFiles(true);
        }
        remoteCount.Text=session.Error.Length>0?session.Error:session.IsConnected?$"{session.Entries.Count:N0}개 항목 · {session.EncodingDisplay}"+(session.ListingNotice.Length>0?" · "+session.ListingNotice:""):"일반 FTP · 사이트맵에 IP · ID · 비밀번호를 저장할 수 있습니다.";
        remoteCount.ToolTip=remoteCount.Text;terminalHelper?.SetServer(session.Site,session.CurrentPath);UpdateFileCounts();folderTree.Refresh();UpdateButtons();
    }
    private void UpdateButtons()
    {
        if(disposed)return;bool ready=session.IsConnected&&!session.IsBusy&&!deleting;
        connectButton.IsEnabled=!session.IsBusy;disconnectButton.IsEnabled=session.IsConnected||session.IsBusy;refreshButton.IsEnabled=!session.IsBusy&&!localLoading;uploadButton.IsEnabled=ready&&!localLoading;downloadButton.IsEnabled=ready&&remoteFiles.SelectedItems.Cast<object>().Any(x=>x is ListBoxItem{Tag:FtpEntry});folderButton.IsEnabled=ready;remoteAddress.IsEnabled=ready;remoteFiles.IsEnabled=ready;
        localFiles.IsEnabled=!localLoading&&!deleting;localAddress.IsEnabled=!deleting;folderButton.IsEnabled=activeRemote?ready:!localLoading&&!deleting&&localPath.Length>0&&!terminalMode;
        if(deleting){connectButton.IsEnabled=false;disconnectButton.IsEnabled=false;refreshButton.IsEnabled=false;}
        UpdateFileCounts();UpdateDeleteButtons();
    }
    private async Task UploadSelectedAsync()
    {
        if(terminalMode){terminalHelper?.PrepareTransfer(true);return;}
        if(!session.IsConnected)return;var selected=localFiles.SelectedItems.Cast<object>().OfType<ListBoxItem>().Select(x=>x.Tag).OfType<LocalEntry>().Select(x=>x.FullPath).ToArray();
        if(selected.Length==0){await PickUploadFilesAsync();return;}EnqueueUploads(selected);await Task.CompletedTask;
    }
    private Task PickUploadFilesAsync()
    {
        var picker=new OpenFileDialog{Title="업로드할 파일 선택",Multiselect=true,CheckFileExists=true,InitialDirectory=localPath};if(picker.ShowDialog(owner)==true)EnqueueUploads(picker.FileNames);return Task.CompletedTask;
    }
    private Task PickUploadFolderAsync(){var picker=new OpenFolderDialog{Title="업로드할 폴더 선택",InitialDirectory=localPath};if(picker.ShowDialog(owner)==true)EnqueueUploads([picker.FolderName]);return Task.CompletedTask;}
    private void EnqueueUploads(IEnumerable<string> paths,string? destination=null)
    {
        if(!session.IsConnected||deleting)return;var selected=paths.ToArray();if(selected.Length==0)return;string password=session.PasswordForTransfer();
        try
        {
            foreach(string path in selected)
            {
                string leaf=Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));string remote=FtpPaths.Join(destination??session.CurrentPath,leaf);
                if(Directory.Exists(path))queue.EnqueueUploadDirectory(session.Site!,password,path,remote);else queue.EnqueueUpload(session.Site!,password,path,remote);
            }
            SwitchTransferView(false);SetTransfersVisible(true);
        }
        catch(Exception error){Ui.Error(error);}finally{password="";}
    }
    private void DownloadSelected()
    {
        DownloadEntries(remoteFiles.SelectedItems.OfType<ListBoxItem>().Select(x=>x.Tag).OfType<FtpEntry>().ToArray());
    }
    private void DownloadEntries(FtpEntry[] selected,string? destination=null)
    {
        if(terminalMode&&destination==null){terminalHelper?.ChooseServerFiles(selected,session.CurrentPath);return;}
        // Transfers use their own connections and absolute paths, so a folder
        // read on the browsing connection does not need to block a download.
        if(selected.Length==0)return;
        string? blocked=!session.IsConnected?"서버 연결이 끊겨 다운로드할 수 없습니다. 다시 연결해 주세요.":deleting?"삭제가 끝난 뒤 다시 시도해 주세요.":localPath.Length==0?"내 PC 폴더를 먼저 선택해 주세요.":null;
        if(blocked!=null){remoteMessage=blocked;UpdateFileCounts();return;}
        string password=session.PasswordForTransfer();
        try
        {
            foreach(var entry in selected)
            {
                if(entry.IsLink)throw new ArgumentException("심볼릭 링크는 직접 다운로드하지 않습니다. 실제 대상 파일 또는 폴더를 선택해 주세요.");
                string target=Path.Combine(destination??localPath,FtpPaths.SafeLocalName(entry.Name));
                if(entry.IsDirectory)queue.EnqueueDownloadDirectory(session.Site!,password,entry.FullPath,target);else queue.EnqueueDownload(session.Site!,password,entry.FullPath,target,entry.Size);
            }
            SwitchTransferView(false);SetTransfersVisible(true);
            remoteMessage=selected.Length==1?$"다운로드 대기열에 추가: {selected[0].Name}":$"다운로드 대기열에 추가: {selected.Length:N0}개";UpdateFileCounts();
        }
        catch(Exception error){Ui.Error(error);}finally{password="";}
    }
    private async Task CreateRemoteFolderAsync(){if(!session.IsConnected)return;var name=FtpDialogs.NewFolder(owner,session.CurrentPath);if(name!=null)await session.MakeDirectoryAsync(name);}
    private async Task CreateLocalFolderAsync(){if(localPath.Length==0)return;var name=FtpDialogs.NewFolder(owner,localPath);if(name==null)return;var path=Path.Combine(localPath,FtpPaths.SafeLocalName(name));if(Directory.Exists(path)||File.Exists(path))throw new IOException("같은 이름의 항목이 이미 있습니다.");Directory.CreateDirectory(path);await NavigateLocalAsync(localPath);}
    private void AddFileMenus()
    {
        var local=new ContextMenu();Menu(local,"선택 항목 업로드",async()=>await RunSafeAsync(UploadSelectedAsync));Menu(local,"파일을 골라 업로드…",async()=>await RunSafeAsync(PickUploadFilesAsync));Menu(local,"폴더를 골라 업로드…",async()=>await RunSafeAsync(PickUploadFolderAsync));local.Items.Add(new Separator());Menu(local,"새 폴더",async()=>await RunSafeAsync(CreateLocalFolderAsync));Menu(local,"Windows 탐색기에서 열기",()=>{if(localPath!="")Ui.OpenFolder(localPath);});Menu(local,"현재 경로 복사",()=>Ui.Copy(localPath));localFiles.ContextMenu=local;
        var remote=new ContextMenu();Menu(remote,"선택 항목 다운로드",DownloadSelected);Menu(remote,"새 폴더",async()=>await RunSafeAsync(CreateRemoteFolderAsync));Menu(remote,"새로고침",async()=>await RunSafeAsync(async()=>{if(session.IsConnected)await session.RefreshAsync();}));Menu(remote,"현재 경로 복사",()=>Ui.Copy(session.CurrentPath));remoteFiles.ContextMenu=remote;
        local.Items.Add(new Separator());Menu(local,"선택 항목 영구 삭제 · Delete",async()=>await DeleteSelectionAsync(false));remote.Items.Add(new Separator());Menu(remote,"선택 항목 영구 삭제 · Delete",async()=>await DeleteSelectionAsync(true));
    }
    private static void Menu(ContextMenu menu,string label,Action action,bool enabled=true){var item=new MenuItem{Header=label,IsEnabled=enabled};item.Click+=(_,_)=>action();menu.Items.Add(item);}
    private static string FormatSize(long? size){if(!size.HasValue)return "—";double value=size.Value;string[] units=["B","KB","MB","GB","TB"];int unit=0;while(value>=1024&&unit<units.Length-1){value/=1024;unit++;}return value.ToString(unit==0?"N0":"0.#")+" "+units[unit];}
    private void SetTransfersVisible(bool visible){transferContent.Visibility=visible?Visibility.Visible:Visibility.Collapsed;transferButton.Background=Ui.Brush(visible?"#203A3C":"#17212D");}
    private void SwitchTransferView(bool history)
    {
        historyView=history;if(history){historyStamp="";RefreshHistory();}else transfers.ItemsSource=queue.Items;
        queueTab.Background=Ui.Brush(history?"#17212D":"#203A3C");historyTab.Background=Ui.Brush(history?"#203A3C":"#17212D");UpdateTransferControls();
    }
    private void RefreshHistory()
    {
        var stamp=string.Join('|',store.TransferHistory.Select(x=>x.Id+":"+x.Status));if(stamp==historyStamp)return;historyStamp=stamp;transfers.ItemsSource=store.TransferHistory.Select(FtpTransferItem.FromRecord).ToArray();
    }
    private void OnQueueChanged()
    {
        if(disposed||transferRefreshQueued)return;transferRefreshQueued=true;Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>
        {
            transferRefreshQueued=false;if(disposed)return;if(historyView)RefreshHistory();UpdateTransferControls();UpdateDeleteButtons();
            var finished=queue.Items.Where(x=>!x.IsPending&&observedFinished.Add(x.Id)).ToArray();
            if(finished.Any(x=>x.Status=="완료"))
            {
                try{if(localPath!=""&&!localLoading)await NavigateLocalAsync(localPath);if(session.IsConnected&&!session.IsBusy&&finished.Any(x=>x.Direction=="업로드"&&string.Equals(x.Host,session.Site!.Host,StringComparison.OrdinalIgnoreCase)))await session.RefreshAsync();}catch{ /* Explicit refresh remains available after a disconnected transfer. */ }
            }
        }));
    }
    private void UpdateTransferControls()
    {
        int pending=queue.Items.Count(x=>x.IsPending),complete=queue.Items.Count(x=>x.Status is "완료" or "전달 완료"),failed=queue.Items.Count(x=>x.Status=="실패");transferState.Text=$"대기 / 진행 {pending}  ·  완료 {complete}"+(failed>0?$"  ·  실패 {failed}":"");cancelButton.IsEnabled=!historyView&&transfers.SelectedItem is FtpTransferItem{IsPending:true};
    }
    private static FrameworkElement TransferHeader()
    {
        var grid=TransferColumns();string[] labels=["파일 / 폴더 · 서버","방향","진행","상태","전송 시각"];for(int i=0;i<labels.Length;i++){var text=Ui.Text(labels[i],10,"#8797AA",true);text.Margin=new Thickness(8,4,4,6);grid.Children.Add(text);SetColumn(text,i);}return grid;
    }
    private static Grid TransferColumns()
    {
        var grid=new Grid();foreach(var width in new[]{new GridLength(1,GridUnitType.Star),new GridLength(96),new GridLength(120),new GridLength(70),new GridLength(115)})grid.ColumnDefinitions.Add(new(){Width=width});return grid;
    }
    private static DataTemplate TransferTemplate()
    {
        return (DataTemplate)XamlReader.Parse("""
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid><Grid.ToolTip><StackPanel MaxWidth="440"><TextBlock Text="{Binding SiteName}" FontWeight="SemiBold"/><TextBlock Text="{Binding LocalPath,StringFormat='PC: {0}'}" TextWrapping="Wrap"/><TextBlock Text="{Binding RemotePath,StringFormat='FTP: {0}'}" TextWrapping="Wrap"/><TextBlock Text="{Binding Error}" TextWrapping="Wrap"/></StackPanel></Grid.ToolTip><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="96"/><ColumnDefinition Width="120"/><ColumnDefinition Width="70"/><ColumnDefinition Width="115"/></Grid.ColumnDefinitions>
            <StackPanel><TextBlock Text="{Binding Name}" FontSize="11" Foreground="{DynamicResource Ink}" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Host}" FontSize="10" Foreground="{DynamicResource Muted}" TextTrimming="CharacterEllipsis"/></StackPanel>
            <TextBlock Grid.Column="1" Text="{Binding Direction}" FontSize="11" Foreground="{DynamicResource Muted}" VerticalAlignment="Center"/>
            <StackPanel Grid.Column="2" Margin="3,2,14,0"><TextBlock Text="{Binding Percent,StringFormat={}{0:0}%}" FontSize="10" Foreground="{DynamicResource Muted}"/><ProgressBar Value="{Binding Percent,Mode=OneWay}" Minimum="0" Maximum="100" Height="3" Foreground="{DynamicResource Accent}" Background="{DynamicResource Selected}" BorderThickness="0"/></StackPanel>
            <TextBlock Grid.Column="3" Text="{Binding Status}" FontSize="11" Foreground="{DynamicResource Muted}" VerticalAlignment="Center"/>
            <TextBlock Grid.Column="4" Text="{Binding At,StringFormat={}{0:MM-dd HH:mm:ss}}" FontSize="10" Foreground="{DynamicResource Muted}" VerticalAlignment="Center"/>
          </Grid></DataTemplate>
          """);
    }
    private static async Task RunSafeAsync(Func<Task> action){try{await action();}catch(OperationCanceledException){}catch(Exception error){Ui.Error(error);}}
    internal Task NavigateLocalForTestAsync(string path)=>NavigateLocalAsync(path);
    public void Dispose(){disposed=true;fileLifetime.Cancel();foreach(var selector in rectangleSelectors)selector.Dispose();ClearDropFeedback();activeDrag?.Dispose();queue.ConfirmOverwrite=null;session.Changed-=OnSessionChanged;queue.Changed-=OnQueueChanged;folderTree.Dispose();localFolderTree.Dispose();}
}
