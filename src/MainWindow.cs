using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
namespace EquipLink;
public sealed partial class MainWindow
{
    private async Task ProbeAsync(Equipment item)
    {
        SetStatus(item.Host+":"+item.Port+" 응답 확인 중…");var ok=await NetworkTools.PortOpenAsync(item.Host,item.Port,3000);SetStatus(ok?"포트 응답 있음 · 로그인 성공 여부는 접속 후 확인하세요":"포트 응답 없음 · IP, 포트, 설비 상태와 네트워크 경로를 확인하세요");
    }
    private void OpenSettings(){var preferences=Dialogs.Settings(this,store.Settings);if(preferences==null)return;store.Settings=preferences;store.Save();RefreshHome();_ = UpdateXStatusAsync();SetStatus(preferences.ThemeId!=ThemeService.Current.Id?"설정 저장됨 · 선택한 테마는 앱을 다시 열면 적용됩니다":"설정 저장됨 · 변경한 터미널 설정은 새 접속부터 적용됩니다");}
    private async Task StartXAsync(){SetStatus(await xServer.EnsureAsync(store.Settings));await UpdateXStatusAsync();}
    private async Task UpdateXStatusAsync()
    {
        if(checkingX)return;checkingX=true;
        try{int port=6000+store.Settings.DisplayNumber;bool open=await NetworkTools.PortOpenAsync("127.0.0.1",port,600);xState.Text=open?"●  X 서버 포트 "+port+" 응답":"○  X 서버 포트 "+port+" 대기";xState.Foreground=Ui.Brush(open?"#69DDC0":"#8EA6C1");}finally{checkingX=false;}
    }
    private void ImportPutty()
    {
        try
        {
            var imported=PuttyService.ImportSessions();int count=0;
            foreach(var item in imported)if(!store.Equipment.Any(p=>p.Host==item.Host&&p.Port==item.Port&&p.Protocol==item.Protocol&&p.Name==item.Name)){store.Equipment.Add(item);count++;}
            store.Save();RefreshGroups();RefreshList();SetStatus(count+"개 PuTTY 세션을 가져왔습니다.");
        }
        catch(Exception exception){Ui.Error(exception);}
    }
    private void ExportSettings()
    {
        var dialog=new SaveFileDialog{FileName="EquipLink-backup-"+DateTime.Now.ToString("yyyyMMdd")+".json",Filter="EquipLink 백업|*.json"};
        if(dialog.ShowDialog(this)!=true)return;try{File.WriteAllText(dialog.FileName,store.ExportJson());SetStatus("설비·FTP 사이트·설정·명령 모음을 백업했습니다. 저장된 비밀번호는 제외됩니다.");}catch(Exception exception){Ui.Error(exception);}
    }
    private void ImportSettings()
    {
        var picker=new OpenFileDialog{Filter="EquipLink 백업|*.json"};if(picker.ShowDialog(this)!=true)return;
        try
        {
            if(new FileInfo(picker.FileName).Length>10_000_000)throw new InvalidDataException("백업 파일이 너무 큽니다.");
            var imported=AppStore.ReadImport(File.ReadAllText(picker.FileName));
            if(MessageBox.Show(this,$"설비 {imported.Equipment.Count}개, FTP 사이트 {imported.FtpSites.Count}개와 명령 {imported.Snippets.Count}개를 현재 목록에 추가하고, 공통 설정을 가져올까요?","백업 가져오기",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            foreach(var item in imported.Equipment)if(!store.Equipment.Any(p=>p.Host==item.Host&&p.Port==item.Port&&p.Name==item.Name&&p.Protocol==item.Protocol)){item.Id=Guid.NewGuid().ToString("N");store.Equipment.Add(item);}
            foreach(var site in imported.FtpSites)if(!store.FtpSites.Any(p=>p.Host==site.Host&&p.Port==site.Port&&p.Username==site.Username&&p.Name==site.Name)){site.Id=Guid.NewGuid().ToString("N");store.FtpSites.Add(site);}
            foreach(var item in imported.Snippets)if(!store.Snippets.Any(p=>p.Name==item.Name&&p.Command==item.Command))store.Snippets.Add(item);
            ftpWorkspace?.RefreshSites();
            store.Settings=imported.Settings;store.Save();RefreshGroups();RefreshList();RefreshHome();SetStatus("백업을 가져왔습니다. 현재 열린 세션은 유지됩니다.");
        }
        catch(Exception exception){Ui.Error(exception);}
    }
    private void ShowHistory()
    {
        var dialog=new FormDialog(this,"최근 접속 기록",720,620);dialog.Body.Children.Add(Ui.Text("세션을 연 기록입니다. 로그인 성공 여부를 의미하지 않습니다.",12,"#98ACC4"));
        if(store.Recent.Count==0)dialog.Body.Children.Add(Ui.Text("아직 접속 기록이 없습니다.",16));
        foreach(var item in store.Recent.Take(30))
        {
            var row=new DockPanel{Margin=new Thickness(0,12,0,0)};var connect=Ui.AsyncButton("연결",async()=>{dialog.Close();var saved=store.Equipment.FirstOrDefault(p=>p.Host==item.Host&&p.Port==item.Port&&p.Protocol==item.Protocol);await ConnectSafeAsync(saved??new Equipment{Name=item.Name,Host=item.Host,Port=item.Port,Protocol=item.Protocol});});DockPanel.SetDock(connect,Dock.Right);row.Children.Add(connect);row.Children.Add(Ui.Stack(Ui.Text(item.Name+"  ·  "+item.Host+":"+item.Port,13,"#E6EFFB",true),Ui.Text(item.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+"   "+item.Protocol,11,"#8EA6C1")));dialog.Body.Children.Add(row);
        }
        dialog.Actions.Children.Add(Ui.Button("닫기",()=>dialog.Close()));dialog.ShowDialog();
    }
}
public sealed class SessionPane : Grid
{
    public Equipment Equipment { get; }
    public TerminalHost Host { get; }=new();
    public SftpSession Sftp { get; }
    private readonly Window owner;
    private readonly AppStore store;
    private readonly Preferences preferences;
    private readonly Action<string> status;
    private readonly XServerService xServer;
    private readonly TextBlock sessionState=Ui.Text("터미널 시작 중…",11,"#8DA6C2");
    private readonly TextBox composer=Ui.Input();
    private readonly TextBlock overlay=Ui.Text("터미널을 준비하고 있습니다…",15,"#8DA6C2");
    private readonly TextBlock guiNotice=Ui.Text("",11,"#E9C786");
    private readonly Border paneHead=new();
    private readonly Grid area=new();
    private readonly StackPanel commandDock=new();
    private Button zoomButton=null!;
    public event Action? Activated;
    public event Action? ZoomRequested;
    public event Action? CloseRequested;
    public SessionPane(Window owner,Equipment equipment,AppStore store,Action<string> status,Func<Task> duplicate,XServerService xServer)
    {
        this.owner=owner;Equipment=equipment;this.store=store;this.status=status;this.xServer=xServer;
        Sftp=new SftpSession(Dispatcher);
        preferences=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(store.Settings))!;
        Background=Ui.Brush("#111923");ClipToBounds=true;
        RowDefinitions.Add(new(){Height=new GridLength(31)});RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=GridLength.Auto});
        var head=new DockPanel{Margin=new Thickness(8,0,2,0)};
        var controls=Ui.Row();DockPanel.SetDock(controls,Dock.Right);zoomButton=Ui.ToolButton("",()=>ZoomRequested?.Invoke(),"확대 / 복원 · 제목 더블클릭 · Ctrl+Shift+Enter");zoomButton.Content=Ui.Glyph("\uE740",13);zoomButton.Width=28;zoomButton.Margin=new Thickness(0);controls.Children.Add(zoomButton);
        var more=Ui.ToolButton("",()=>{},"터미널 메뉴");more.Content=Ui.Glyph("\uE712",15);more.Width=28;more.Margin=new Thickness(0);controls.Children.Add(more);
        more.Click+=(_,_)=>
        {
            Activated?.Invoke();var menu=new ContextMenu();
            MainWindow.AddMenu(menu,"새 xterm 열기",async()=>await RunSafe(()=>ExecuteGuiAsync(false)));
            MainWindow.AddMenu(menu,"테스트 GUI 실행",async()=>await RunSafe(()=>ExecuteGuiAsync(true)));
            MainWindow.AddMenu(menu,"DISPLAY 명령 준비",async()=>await RunSafe(async()=>{var settings=GuiPreferences();SetComposer(Commands.SetDisplay(Equipment,settings,await AddressAsync(settings)));}));
            MainWindow.AddMenu(menu,"명령 입력창",ToggleComposer);MainWindow.AddMenu(menu,"명령 모음",()=>Dialogs.Snippets(owner,store,SetComposer));menu.Items.Add(new Separator());
            AddCredentialItems(menu);menu.Items.Add(new Separator());
            MainWindow.AddMenu(menu,"같은 설비에 새 접속",async()=>await RunSafe(duplicate));MainWindow.AddMenu(menu,"재연결",async()=>await RunSafe(ReconnectAsync));MainWindow.AddMenu(menu,"별도 창으로 분리",()=>Host.Detach());
            if(equipment.UseX11&&!equipment.ForwardX11)MainWindow.AddMenu(menu,"설비 X11 접근 허용",async()=>await RunSafe(AllowHostAsync));
            MainWindow.AddMenu(menu,"세션 종료",()=>CloseRequested?.Invoke());MainWindow.OpenMenu(more,menu);
        };
        head.Children.Add(controls);var label=Ui.Text("",11,"#8797AA");label.Inlines.Add(new Run(equipment.Name){FontWeight=FontWeights.SemiBold,Foreground=Ui.Brush("#DCE5F0")});label.Inlines.Add(new Run("  ·  "+equipment.Host+":"+equipment.Port));label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.VerticalAlignment=VerticalAlignment.Center;label.ToolTip=equipment.Protocol+" · "+equipment.Encoding;head.Children.Add(label);
        paneHead.Background=Ui.Brush("#17212D");paneHead.Child=head;paneHead.BorderThickness=new Thickness(0,0,0,1);paneHead.BorderBrush=Ui.Brush("#2A394A");paneHead.MouseLeftButtonDown+=(_,e)=>{Activated?.Invoke();if(e.ClickCount==2)ZoomRequested?.Invoke();else Host.FocusTerminal();};Children.Add(paneHead);
        area.Background=Ui.Brush("#111923");overlay.HorizontalAlignment=HorizontalAlignment.Center;overlay.VerticalAlignment=VerticalAlignment.Center;overlay.TextWrapping=TextWrapping.Wrap;overlay.Margin=new Thickness(24);area.Children.Add(overlay);area.Children.Add(Host);Children.Add(area);SetRow(area,1);
        Host.Activated+=()=>Activated?.Invoke();Host.StateChanged+=message=>{sessionState.Text=message;paneHead.ToolTip=message;status(message);overlay.Text=message;if(Host.External||!Host.HasProcess)Host.Visibility=Visibility.Collapsed;if(!Host.HasProcess)Sftp.Disconnect();};
        guiNotice.Visibility=Visibility.Collapsed;guiNotice.Margin=new Thickness(10,5,10,5);Children.Add(guiNotice);SetRow(guiNotice,2);
        commandDock.Margin=new Thickness(10,8,10,8);commandDock.Visibility=Visibility.Collapsed;
        var commandTop=new DockPanel();var close=Ui.ToolButton("×",ToggleComposer,"명령 입력 접기");DockPanel.SetDock(close,Dock.Right);commandTop.Children.Add(close);var title=Ui.Text("명령 입력  ·  이 터미널에만 전송",11,"#8797AA");title.VerticalAlignment=VerticalAlignment.Center;commandTop.Children.Add(title);commandDock.Children.Add(commandTop);
        composer.ToolTip="로그인 후 실행 · Ctrl+Enter로 실행 · Enter는 줄바꿈";composer.AcceptsReturn=true;composer.TextWrapping=TextWrapping.Wrap;composer.MinHeight=34;composer.MaxHeight=78;composer.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;composer.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;SendComposer(true);}};commandDock.Children.Add(composer);
        var send=new WrapPanel();foreach(var button in new[]{Ui.ToolButton("명령 모음",()=>Dialogs.Snippets(owner,store,SetComposer)),Ui.ToolButton("복사",()=>Ui.Copy(composer.Text)),Ui.ToolButton("입력만",()=>SendComposer(false)),Ui.ToolButton("실행 ↵",()=>SendComposer(true),"현재 터미널에 즉시 실행",true)})send.Children.Add(button);send.Margin=new Thickness(0,6,0,0);commandDock.Children.Add(send);
        var commandScroll=new ScrollViewer{Content=commandDock,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=145};Children.Add(commandScroll);SetRow(commandScroll,3);SizeChanged+=(_,_)=>commandScroll.MaxHeight=Math.Max(54,Math.Min(145,ActualHeight*.42));
    }
    public async Task ConnectAsync()
    {
        try{var target=Equipment.Copy();var saved=SavedCredentialSource();target.EncryptedPassword=saved.EncryptedPassword;target.AutoLogin=saved.AutoLogin;await Host.StartAsync(target,preferences);overlay.Text=Host.External?"별도 PuTTY 창에서 작업 중입니다.":"";if(Host.External)Host.Visibility=Visibility.Collapsed;}
        catch(Exception exception){overlay.Text=exception.Message;Host.Visibility=Visibility.Collapsed;sessionState.Text="시작 실패 · 재연결을 눌러 다시 시도하세요";throw;}
    }
    private async Task RunSafe(Func<Task> action){try{await action();}catch(Exception e){Ui.Error(e);}}
    public Task OpenXtermAsync()=>ExecuteGuiAsync(false);
    public void SetActive(bool value){paneHead.Background=Ui.Brush(value?"#203039":"#17212D");paneHead.BorderBrush=Ui.Brush(value?"#5DD6BE":"#2A394A");}
    public void SetZoomed(bool value){zoomButton.Content=Ui.Glyph(value?"\uE73F":"\uE740",13);zoomButton.ToolTip=value?"분할 화면으로 복원":"한 화면으로 확대";}
    public void ToggleComposer(){commandDock.Visibility=commandDock.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;if(commandDock.Visibility==Visibility.Visible)composer.Focus();else Host.FocusTerminal();}
    public void SetComposer(string value){composer.Text=value;commandDock.Visibility=Visibility.Visible;composer.Focus();}
    private Equipment SavedCredentialSource()
    {
        var saved=store.Equipment.FirstOrDefault(p=>p.Id==Equipment.Id);
        if(saved!=null && CredentialProtector.MatchesTarget(saved,Equipment))return saved;
        var unavailable=Equipment.Copy();unavailable.EncryptedPassword=null;unavailable.Username="";return unavailable;
    }
    public void ShowCredentialMenu(FrameworkElement source)
    {
        Activated?.Invoke();var menu=new ContextMenu();AddCredentialItems(menu);MainWindow.OpenMenu(source,menu);
    }
    private void AddCredentialItems(ContextMenu menu)
    {
        var saved=SavedCredentialSource();
        var id=new MenuItem{Header="저장한 ID 입력",IsEnabled=!string.IsNullOrEmpty(saved.Username),ToolTip="login 프롬프트에서 사용하세요. 입력 후 Enter를 누르세요."};id.Click+=(_,_)=>FillCredential(false);menu.Items.Add(id);
        var password=new MenuItem{Header="저장한 비밀번호 입력",IsEnabled=saved.HasSavedPassword,ToolTip="Password 프롬프트에서 사용하세요. 입력 후 Enter를 누르세요."};password.Click+=(_,_)=>FillCredential(true);menu.Items.Add(password);
    }
    public void FillCredential(bool password)
    {
        try
        {
            var saved=SavedCredentialSource();
            string value=password?CredentialProtector.Unprotect(saved,saved.EncryptedPassword??""):saved.Username;
            try{Host.SendCredential(value);}finally{value=string.Empty;}
            status(password?"저장한 비밀번호를 입력했습니다. 터미널에서 Enter를 누르세요.":"저장한 ID를 입력했습니다. 터미널에서 Enter를 누르세요.");
        }
        catch(Exception exception){Ui.Error(exception);}
    }
    private async Task ReconnectAsync()
    {
        if(Host.HasProcess&&MessageBox.Show(owner,"현재 세션을 종료하고 다시 접속할까요?","재연결",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        Sftp.Disconnect();Host.CloseSession();Host.Visibility=Visibility.Visible;overlay.Text="터미널 재시작 중…";await ConnectAsync();
    }
    private Preferences GuiPreferences()
    {
        var current=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(store.Settings))!;
        if(Equipment.ForwardX11)current.DisplayNumber=preferences.DisplayNumber;
        return current;
    }
    private async Task<string> AddressAsync(Preferences settings)=>Equipment.ForwardX11?"127.0.0.1":await NetworkTools.LocalForAsync(Equipment,settings);
    private async Task ExecuteGuiAsync(bool custom)
    {
        if(!Host.HasProcess)throw new InvalidOperationException("먼저 터미널에 연결하고 로그인해 주세요.");
        var settings=GuiPreferences();
        if(settings.AutoStartX)status(await xServer.EnsureAsync(settings));
        string address=await AddressAsync(settings);composer.Text=custom?Commands.Gui(Equipment,settings,address):Commands.Xterm(Equipment,settings,address);
        Host.Send(composer.Text,true);guiNotice.Visibility=Visibility.Collapsed;status("GUI 명령 전송됨: "+composer.Text);
    }
    private async Task AllowHostAsync()
    {
        var settings=GuiPreferences();status(await xServer.EnsureAsync(settings));
        // This explicit user action grants only the selected equipment's address.
        status(await XServerService.AllowHostAsync(Equipment,settings));
    }
    private void SendComposer(bool execute)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(composer.Text))return;
            if(!execute && (composer.Text.Contains('\n')||composer.Text.Contains('\r')))throw new InvalidOperationException("'입력만'은 한 줄 명령에 사용할 수 있습니다. 여러 줄을 실행하려면 내용을 확인한 뒤 '실행'을 누르세요.");
            Host.Send(composer.Text,execute);status(execute?"명령을 현재 터미널로 전송했습니다":"터미널에 명령을 입력했습니다. 실행하려면 Enter를 누르세요.");
        }
        catch(Exception exception){Ui.Error(exception);}
    }
    internal void ShowGuiNotice(string text){guiNotice.Text=text;guiNotice.Visibility=Visibility.Visible;}
    internal Task OpenXtermForTestAsync()=>ExecuteGuiAsync(false);
}

