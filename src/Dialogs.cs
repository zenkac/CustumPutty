using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

public sealed class FormDialog : Window
{
    internal static Action<FormDialog>? TestLoaded { get; set; }
    public StackPanel Body { get; } = new();
    public StackPanel Actions { get; } = new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
    public FormDialog(Window owner,string title,int width=740,int height=780,bool compact=false)
    {
        Ui.ApplyWindowTheme(this);
        Owner=owner;Title=title+" · EquipLink";Width=width;Height=height;MinWidth=540;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        if(compact){MinWidth=360;MinHeight=0;Height=double.NaN;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;}
        Closed+=(_,_)=>{if(owner is MainWindow workspace)workspace.RestoreTerminalFocus();};
        if(App.IsTesting&&TestLoaded!=null){WindowStartupLocation=WindowStartupLocation.Manual;Left=-30000;Top=-30000;ShowInTaskbar=false;ShowActivated=false;Loaded+=(_,_)=>TestLoaded?.Invoke(this);}
        var grid=new Grid{Margin=new Thickness(compact?20:26)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=compact?GridLength.Auto:new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=Ui.Text(title,compact?18:23,"#ECF3FC",true);heading.Margin=new Thickness(0,0,0,compact?12:22);grid.Children.Add(heading);
        var scroll=new ScrollViewer{Content=Body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(0,0,10,0)};grid.Children.Add(scroll);Grid.SetRow(scroll,1);
        if(compact){scroll.Padding=new Thickness(0);scroll.MaxHeight=240;}
        Actions.Margin=new Thickness(0,compact?16:20,0,0);grid.Children.Add(Actions);Grid.SetRow(Actions,2);Content=grid;
    }
}

public static class Dialogs
{
    public static Equipment? EquipmentEditor(Window owner,Equipment original)
    {
        var item=original.Copy();var dialog=new FormDialog(owner,"설비 설정");
        var name=Ui.Input(item.Name);var group=Ui.Input(item.Group);var host=Ui.Input(item.Host);var port=Ui.Input(item.Port.ToString());
        var protocol=Ui.Select(["Telnet","SSH"],item.Protocol);var username=Ui.Input(item.Username);username.Name="EquipmentUsername";
        var password=new PasswordBox{Name="EquipmentPassword",MaxLength=1024,MinHeight=32};
        var remember=new CheckBox{Name="RememberPassword",Content="이 Windows 계정에 비밀번호 저장",IsChecked=item.HasSavedPassword};
        var automatic=new CheckBox{Name="AutomaticLogin",Content="더블클릭 접속 시 저장한 ID · 비밀번호로 자동 로그인",IsChecked=item.AutoLogin};
        bool readable=item.HasSavedPassword;
        if(readable)try{_ = CredentialProtector.Unprotect(item,item.EncryptedPassword!);}catch(System.Security.Cryptography.CryptographicException){readable=false;}
        var passwordHint=Ui.Text("",11,"#8797AA");passwordHint.Name="EquipmentPasswordStatus";
        var passwordGrid=new Grid();passwordGrid.Children.Add(password);
        var savedPlaceholder=Ui.Text("●●●●●●●●  저장됨 · 변경할 때만 입력",12,"#8797AA");savedPlaceholder.Name="PasswordSavedPlaceholder";savedPlaceholder.Margin=new Thickness(12,0,0,0);savedPlaceholder.VerticalAlignment=VerticalAlignment.Center;savedPlaceholder.IsHitTestVisible=false;passwordGrid.Children.Add(savedPlaceholder);
        void UpdatePasswordState()
        {
            bool saved=remember.IsChecked==true&&item.HasSavedPassword&&readable&&password.Password.Length==0;
            savedPlaceholder.Visibility=saved?Visibility.Visible:Visibility.Collapsed;
            automatic.IsEnabled=remember.IsChecked==true;
            passwordHint.Text=password.Password.Length>0?"설비 저장을 누르면 비밀번호를 암호화하여 저장합니다.":saved?"저장됨 · 새 비밀번호를 입력하지 않으면 기존 값을 유지합니다.":remember.IsChecked!=true?"비밀번호를 입력하면 저장 옵션이 자동으로 켜집니다.":!readable&&item.HasSavedPassword?"저장된 비밀번호를 읽을 수 없습니다. 비밀번호를 다시 입력해 주세요.":"저장할 비밀번호를 입력해 주세요.";
            passwordHint.Foreground=Ui.Brush(saved?"#69DDC0":"#8797AA");
        }
        password.PasswordChanged+=(_,_)=>{if(password.Password.Length>0)remember.IsChecked=true;UpdatePasswordState();};
        remember.Checked+=(_,_)=>UpdatePasswordState();remember.Unchecked+=(_,_)=>{password.Clear();UpdatePasswordState();};UpdatePasswordState();
        var encoding=Ui.Select(["UTF-8","CP949","ISO-8859-1"],item.Encoding);
        var mode=Ui.Select(["자동 · Telnet 직접 / SSH 전달","직접 · PC IP로 연결"],item.X11Mode=="Direct"?"직접 · PC IP로 연결":"자동 · Telnet 직접 / SSH 전달");
        var address=Ui.Input(item.DisplayAddress);var shell=Ui.Select(["sh / bash","csh / tcsh"],item.Shell=="csh"?"csh / tcsh":"sh / bash");
        var command=Ui.Input(item.GuiCommand,true);var notes=Ui.Input(item.Notes,true);var key=Ui.Input(item.PrivateKeyPath);
        var favorite=new CheckBox{Content="즐겨찾기",IsChecked=item.Favorite};var log=new CheckBox{Content="터미널 출력 로그 저장",IsChecked=item.LogSession};var x11=new CheckBox{Content="X11 / GUI 사용",IsChecked=item.UseX11};
        protocol.SelectionChanged+=(_,_)=>{if(port.Text is "22" or "23")port.Text=protocol.SelectedIndex==1?"22":"23";};
        dialog.Body.Children.Add(Ui.Section("연결",null,true));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("설비 이름",name),Ui.Field("그룹",group)));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("IP 주소 / 호스트",host),Ui.Columns(Ui.Field("접속 방식",protocol),Ui.Field("포트",port)),1.25,1));
        dialog.Body.Children.Add(Ui.Field("문자 인코딩",encoding));
        dialog.Body.Children.Add(Ui.Section("로그인","ID와 비밀번호는 이 Windows 계정으로 암호화해 저장합니다."));
        dialog.Body.Children.Add(Ui.Field("로그인 ID · 선택",username));
        dialog.Body.Children.Add(Ui.Field("비밀번호",passwordGrid));passwordHint.Margin=new Thickness(0,-8,0,10);dialog.Body.Children.Add(passwordHint);dialog.Body.Children.Add(remember);dialog.Body.Children.Add(automatic);
        var loginHint=Ui.Text("저장 후 설비를 더블클릭하면 자동으로 로그인합니다. Telnet은 login / Password 안내에 한 번만 입력합니다. SSH의 최초 서버 키는 직접 확인하며, OTP 등 추가 인증이 있으면 자동 로그인을 끄고 접속하세요. 다른 PC로 내보내는 백업에는 비밀번호가 포함되지 않습니다.",11,"#8797AA");loginHint.Margin=new Thickness(0,4,0,14);dialog.Body.Children.Add(loginHint);
        dialog.Body.Children.Add(Ui.Section("X11 / GUI","로그인 후 xterm 또는 지정한 GUI 명령을 PC의 X 서버(Xming)로 띄웁니다."));
        x11.Margin=new Thickness(0,0,0,12);dialog.Body.Children.Add(x11);
        var modeHint=Ui.Text("",11,"#8797AA");modeHint.Margin=new Thickness(0,-8,0,14);
        var x11Fields=Ui.Stack(Ui.Columns(Ui.Field("GUI 연결 방식",mode),Ui.Field("설비의 셸",shell)),modeHint,
            Ui.Field("이 설비에서 사용할 PC IP · 선택",address,"비워 두면 공통 설정 또는 설비와 연결된 네트워크의 PC IP를 사용합니다."),
            Ui.Field("테스트 GUI 실행 명령 · 선택",command,"비워 두면 새 xterm 창을 엽니다. 로그인 후 GUI 버튼으로 실행합니다."));
        dialog.Body.Children.Add(x11Fields);
        void UpdateX11State()
        {
            bool enabled=x11.IsChecked==true,ssh=protocol.SelectedIndex==1,direct=mode.SelectedIndex==1;
            x11Fields.IsEnabled=enabled;x11Fields.Opacity=enabled?1:.55;
            modeHint.Text=!enabled?"X11 / GUI 사용을 켜면 아래 항목을 설정할 수 있습니다."
                :ssh&&!direct?"SSH X11 전달 · PC IP 없이 SSH 터널로 GUI를 띄웁니다. DISPLAY는 SSH가 자동 지정합니다."
                :"직접 연결 · 설비가 PC IP의 TCP 6000+디스플레이 번호로 접속합니다. 방화벽과 X 서버 접근 허용이 필요합니다.";
        }
        x11.Checked+=(_,_)=>UpdateX11State();x11.Unchecked+=(_,_)=>UpdateX11State();mode.SelectionChanged+=(_,_)=>UpdateX11State();protocol.SelectionChanged+=(_,_)=>UpdateX11State();UpdateX11State();
        dialog.Body.Children.Add(Ui.Section("기타"));
        var flags=Ui.Row(favorite,log);favorite.Margin=new Thickness(0,0,24,12);log.Margin=new Thickness(0,0,0,12);dialog.Body.Children.Add(flags);
        dialog.Body.Children.Add(Ui.Field("SSH 개인 키 파일 (.ppk) · 선택",PathInput(key,"PuTTY 개인 키|*.ppk|모든 파일|*.*",dialog)));
        dialog.Body.Children.Add(Ui.Field("설비 메모",notes));
        if(item.BasePuttySession!="")dialog.Body.Children.Add(Ui.Text("고급 연결 설정 기준: PuTTY / "+item.BasePuttySession,11,"#98ACC4"));
        dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        var saveButton=Ui.Button("설비 저장",()=>
        {
            try
            {
                if(!int.TryParse(port.Text,out var portNumber))throw new ArgumentException("포트에 숫자를 입력해 주세요.");
                item.Name=name.Text;item.Group=group.Text;item.Host=host.Text;item.Port=portNumber;item.Protocol=(string)protocol.SelectedItem;item.Username=username.Text.Trim();item.Encoding=(string)encoding.SelectedItem;
                item.Favorite=favorite.IsChecked==true;item.LogSession=log.IsChecked==true;item.UseX11=x11.IsChecked==true;item.X11Mode=mode.SelectedIndex==1?"Direct":"Auto";item.Shell=shell.SelectedIndex==1?"csh":"sh";
                item.DisplayAddress=address.Text.Trim();item.GuiCommand=command.Text.Trim();item.Notes=notes.Text;item.PrivateKeyPath=key.Text.Trim();item.Validate();
                if(remember.IsChecked==true)
                {
                    if(string.IsNullOrEmpty(item.Username))throw new ArgumentException("비밀번호를 저장하려면 로그인 ID를 함께 입력해 주세요.");
                    if(password.Password.Length>0)item.EncryptedPassword=CredentialProtector.Protect(item,password.Password);
                    else if(!original.HasSavedPassword||!readable)throw new ArgumentException("저장할 비밀번호를 다시 입력해 주세요.");
                    else if(!CredentialProtector.MatchesTarget(original,item))throw new ArgumentException("IP, 포트, 접속 방식 또는 ID를 변경했습니다. 비밀번호를 다시 입력하거나 비밀번호 저장을 해제해 주세요.");
                }
                else item.EncryptedPassword=null;
                item.AutoLogin=automatic.IsChecked==true;
                password.Clear();dialog.DialogResult=true;
            }
            catch(Exception exception){Ui.Error(exception);}
        },true);saveButton.Name="SaveEquipment";dialog.Actions.Children.Add(saveButton);
        dialog.Closed+=(_,_)=>password.Clear();
        return dialog.ShowDialog()==true?item:null;
    }
    private static FrameworkElement PathInput(TextBox field,string filter,Window owner,Action<string>? selected=null)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.Children.Add(field);
        var button=Ui.Button("찾기",()=>{var picker=new OpenFileDialog{Filter=filter,CheckFileExists=true};if(picker.ShowDialog(owner)==true){field.Text=picker.FileName;selected?.Invoke(picker.FileName);}});button.Margin=new Thickness(8,0,0,0);grid.Children.Add(button);Grid.SetColumn(button,1);return grid;
    }
    public static Preferences? Settings(Window owner,Preferences original)
    {
        var item=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(original))!;XServerService.Discover(item);
        var dialog=new FormDialog(owner,"Xming 및 앱 설정",800,820);
        var server=Ui.Input(item.XServerPath);var launcher=Ui.Input(item.XLaunchPath);var config=Ui.Input(item.XLaunchConfig);var auth=Ui.Input(item.XAuthorityFile);
        var pc=Ui.Input(item.PcAddress);var display=Ui.Input(item.DisplayNumber.ToString());var font=Ui.Input(item.FontSize.ToString());var scrollback=Ui.Input(item.Scrollback.ToString());
        var auto=new CheckBox{Content="설비에 연결할 때 Xming을 자동으로 실행",IsChecked=item.AutoStartX};
        var external=new CheckBox{Content="PuTTY를 탭 대신 별도 창에서 사용",IsChecked=item.ExternalTerminal};
        var theme=new ComboBox{Name="ThemeSelector",ItemsSource=ThemeService.All,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=item.ThemeId};
        dialog.Body.Children.Add(Ui.Section("X 서버 (Xming)","한 번 설정하면 다음 접속부터 같은 X 서버 설정을 자동으로 사용합니다.",true));
        var xStatus=Ui.Text("○  X 서버 확인 중…",12,"#8797AA",true);xStatus.VerticalAlignment=VerticalAlignment.Center;xStatus.Name="XServerStatus";
        async Task CheckX()
        {
            if(!int.TryParse(display.Text,out int number)||number is <0 or >99){xStatus.Text="○  디스플레이 번호를 확인하세요 (0~99)";xStatus.Foreground=Ui.Brush("#E9C786");return;}
            xStatus.Text="○  확인 중…";xStatus.Foreground=Ui.Brush("#8797AA");
            bool open=await NetworkTools.PortOpenAsync("127.0.0.1",6000+number,600);
            xStatus.Text=open?$"●  X 서버 실행 중 · 127.0.0.1:{6000+number} 응답":$"○  X 서버 꺼짐 · 127.0.0.1:{6000+number} 응답 없음";xStatus.Foreground=Ui.Brush(open?"#69DDC0":"#E9C786");
        }
        var statusRow=new DockPanel{Margin=new Thickness(0,0,0,16)};var check=Ui.AsyncButton("상태 확인",CheckX);check.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(check,Dock.Right);statusRow.Children.Add(check);statusRow.Children.Add(xStatus);
        statusRow.Margin=new Thickness(0);var statusCard=Ui.Card(statusRow,"#17212D",12);statusCard.Margin=new Thickness(0,0,0,16);dialog.Body.Children.Add(statusCard);
        if(!App.IsTesting)dialog.Loaded+=async (_,_)=>{try{await CheckX();}catch{}};
        dialog.Body.Children.Add(Ui.Field("Xming.exe / VcXsrv.exe",PathInput(server,"X 서버|Xming.exe;vcxsrv.exe|실행 파일|*.exe",dialog,path=>
        {var adjacent=Path.Combine(Path.GetDirectoryName(path)!,"XLaunch.exe");if(File.Exists(adjacent))launcher.Text=adjacent;})));
        dialog.Body.Children.Add(Ui.Field("XLaunch.exe",PathInput(launcher,"XLaunch|XLaunch.exe|실행 파일|*.exe",dialog)));
        dialog.Body.Children.Add(Ui.Field("기존 XLaunch 설정 파일 (.xlaunch) · 선택",PathInput(config,"XLaunch 설정|*.xlaunch|모든 파일|*.*",dialog,path=>
        {try{var number=XServerService.ConfigDisplay(path);if(number.HasValue)display.Text=number.Value.ToString();}catch(Exception exception){Ui.Error(exception);}}),"파일을 지정하면 기존 옵션으로 실행합니다. 비워 두면 Multiple windows + Clipboard로 실행합니다."));
        var configActions=Ui.Row(Ui.Button("설정 파일 사용 해제",()=>config.Text=""),Ui.Button("XLaunch 설정 마법사",()=>
        {try{if(!File.Exists(launcher.Text))throw new FileNotFoundException("먼저 XLaunch.exe를 선택해 주세요.");Process.Start(new ProcessStartInfo(launcher.Text){UseShellExecute=true});}catch(Exception exception){Ui.Error(exception);}}));configActions.Margin=new Thickness(0,0,0,16);dialog.Body.Children.Add(configActions);
        dialog.Body.Children.Add(auto);
        dialog.Body.Children.Add(Ui.Section("디스플레이 · 네트워크","Telnet이나 '직접' 방식의 설비가 접속할 PC 주소와 디스플레이 번호입니다."));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("Windows PC IP · 비우면 자동",pc),Ui.Field("디스플레이 번호",display),3,1));
        var addresses=new WrapPanel{Margin=new Thickness(0,-6,0,16)};var addressButtons=new List<(Button Button,string Value)>();
        foreach(var address in NetworkTools.LocalAddresses()){var b=Ui.Button(address,()=>pc.Text=address);b.Margin=new Thickness(0,0,6,6);addresses.Children.Add(b);addressButtons.Add((b,address));}
        var autoAddress=Ui.Button("자동 선택",()=>pc.Text="");autoAddress.Margin=new Thickness(0,0,6,6);addresses.Children.Add(autoAddress);addressButtons.Add((autoAddress,""));dialog.Body.Children.Add(addresses);
        void MarkAddress(){foreach(var (b,value) in addressButtons){bool on=pc.Text.Trim()==value;b.Background=Ui.Brush(on?"#25413F":"#1D2A38");b.BorderBrush=Ui.Brush(on?"#4A8D81":"#2A394A");}}
        pc.TextChanged+=(_,_)=>MarkAddress();MarkAddress();display.TextChanged+=(_,_)=>{xStatus.Text="○  디스플레이 번호 변경됨 · 상태 확인을 눌러 다시 확인하세요";xStatus.Foreground=Ui.Brush("#8797AA");};
        var networkHint=Ui.Text("Telnet GUI는 설비 → PC의 TCP 6000 + 디스플레이 번호로 연결됩니다. 기존 방화벽과 X 서버 접근 설정을 사용합니다.",11,"#98ACC4");networkHint.Margin=new Thickness(0,0,0,12);dialog.Body.Children.Add(networkHint);
        dialog.Body.Children.Add(Ui.Field("Xauthority 파일 · SSH 전달에 필요한 경우",PathInput(auth,"모든 파일|*.*",dialog)));
        dialog.Body.Children.Add(Ui.Section("화면 · 터미널"));
        dialog.Body.Children.Add(Ui.Field("화면 테마",theme,"설정 저장 후 앱을 다시 열면 메뉴와 터미널에 함께 적용됩니다."));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("터미널 글자 크기",font),Ui.Field("스크롤 보관 줄 수",scrollback)));
        dialog.Body.Children.Add(external);
        dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        var saveSettings=Ui.Button("설정 저장",()=>
        {
            try
            {
                if(!int.TryParse(display.Text,out var number)||!int.TryParse(font.Text,out var size)||!int.TryParse(scrollback.Text,out var lines))throw new ArgumentException("디스플레이 번호, 글자 크기, 보관 줄 수에 숫자를 입력해 주세요.");
                item.ThemeId=theme.SelectedValue as string??"midnight";
                item.XServerPath=server.Text.Trim();item.XLaunchPath=launcher.Text.Trim();item.XLaunchConfig=config.Text.Trim();item.XAuthorityFile=auth.Text.Trim();item.PcAddress=pc.Text.Trim();item.DisplayNumber=number;item.FontSize=size;item.Scrollback=lines;item.AutoStartX=auto.IsChecked==true;item.ExternalTerminal=external.IsChecked==true;item.Validate();dialog.DialogResult=true;
            }
            catch(Exception exception){Ui.Error(exception);}
        },true);saveSettings.Name="SaveSettings";dialog.Actions.Children.Add(saveSettings);
        return dialog.ShowDialog()==true?item:null;
    }
    public static void Snippets(Window owner,AppStore store,Action<string>? choose=null)
    {
        var dialog=new FormDialog(owner,"명령 모음",720,680);var list=new ListBox{Height=220};var name=Ui.Input();var command=Ui.Input("",true);command.Height=120;
        void Refresh(){list.Items.Clear();foreach(var item in store.Snippets)list.Items.Add(new ListBoxItem{Content=Ui.Text(item.Name),Tag=item});}
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ListBoxItem{Tag:Snippet item}){name.Text=item.Name;command.Text=item.Command;}};
        Refresh();dialog.Body.Children.Add(list);dialog.Body.Children.Add(Ui.Field("명령 이름",name));dialog.Body.Children.Add(Ui.Field("명령 내용",command,"명령을 선택하면 입력란에 준비됩니다. 실행은 직접 누를 때만 이뤄집니다."));
        dialog.Body.Children.Add(Ui.Row(Ui.Button("새 명령",()=>{list.SelectedItem=null;name.Text="";command.Text="";}),Ui.Button("저장",()=>
        {
            if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(command.Text)){Ui.Error(new ArgumentException("이름과 명령을 입력해 주세요."));return;}
            if(command.Text.Length>16000){Ui.Error(new ArgumentException("명령은 16,000자 이내로 입력해 주세요."));return;}
            if(list.SelectedItem is ListBoxItem{Tag:Snippet item}){item.Name=name.Text.Trim();item.Command=command.Text;}else store.Snippets.Add(new(){Name=name.Text.Trim(),Command=command.Text});store.Save();Refresh();
        }),Ui.Button("선택 삭제",()=>{if(list.SelectedItem is ListBoxItem{Tag:Snippet item}){store.Snippets.Remove(item);store.Save();Refresh();}})));
        dialog.Actions.Children.Add(Ui.Button("닫기",()=>dialog.Close()));
        if(choose!=null)dialog.Actions.Children.Add(Ui.Button("입력란에 가져오기",()=>{if(!string.IsNullOrWhiteSpace(command.Text)){choose(command.Text);dialog.Close();}},true));
        dialog.ShowDialog();
    }
    public static void Help(Window owner)
    {
        var dialog=new FormDialog(owner,"처음 사용하기",780,750);
        var sections=new[]{
            ("01  Xming 설정은 한 번만","설정에서 기존 XLaunch 파일(.xlaunch)을 선택하고 '연결할 때 자동 실행'을 켜세요. 이미 X 서버가 실행 중이면 다시 실행하지 않습니다. 설정 파일이 없으면 Xming.exe 경로만 지정할 수도 있습니다."),
            ("02  설비 저장 또는 빠른 접속","왼쪽 설비 라이브러리의 +에서 이름·IP를 저장하고 더블클릭하세요. 처음 접속하는 IP는 상단 '빠른 접속' 창에 입력합니다. Telnet은 23, SSH는 22가 기본입니다. 설비 목록 우클릭으로 편집과 포트 확인을 할 수 있습니다."),
            ("03  로그인 후 새 xterm","터미널에서 로그인한 뒤 '새 xterm 열기'를 누르면 xterm -display PC_IP:0.0 & 명령이 실행됩니다. SSH의 자동 모드에서는 X11 전달을 사용하므로 DISPLAY를 덮어쓰지 않고 xterm & 를 실행합니다. SSH에서도 기존 직접 연결을 쓰려면 설비 설정에서 '직접'을 선택하세요."),
            ("04  PC IP가 여러 개일 때","설정의 PC IP에서 설비와 통신하는 유선망/VPN 주소를 선택하세요. 설비별로 다른 PC IP를 지정할 수도 있습니다. 기본 자동 선택은 설비로 가는 네트워크 경로를 따릅니다."),
            ("05  GUI 창이 열리지 않을 때","터미널 접속과 X11 연결은 별도입니다. Telnet에서는 설비가 PC의 TCP 6000(디스플레이 0)에 접근해야 합니다. Xming이 실행 중인지, PC IP가 맞는지 확인하세요. '설비 X11 허용'은 xhost가 설치되어 있을 때 해당 설비 IP만 허용합니다. 프로그램은 Windows 방화벽 규칙을 변경하지 않습니다."),
            ("06  터미널 분할과 집중","상단에서 단일·좌우·상하·4분할을 선택하세요. 분할 경계를 드래그하면 크기를 바꿀 수 있습니다. 제목을 더블클릭하거나 Ctrl+Shift+Enter로 선택한 패널을 확대·복원합니다. F11 집중 모드는 메뉴와 사이드바를 숨깁니다. 다른 탭을 선택해도 기존 연결은 유지됩니다."),
            ("07  단축키와 명령","Ctrl+Shift+K 빠른 접속 / Ctrl+Shift+B 설비 목록 / Ctrl+Shift+J 명령 입력. 명령 입력에서 Ctrl+Enter는 현재 터미널에만 실행합니다. 분할 화면에서 숨겨진 탭을 선택하면 선택된 패널만 바뀝니다. 드래그 복사 / Shift+Insert 붙여넣기 / Shift+PageUp·PageDown 스크롤을 지원합니다."),
            ("08  ID와 비밀번호 저장","설비 편집에서 로그인 ID와 비밀번호를 저장할 수 있습니다. 비밀번호는 Windows 계정으로 암호화합니다. 로그인 프롬프트에서 상단 로그인 → ID 입력 / 비밀번호 입력을 선택하고 Enter를 누르세요. 비밀번호는 백업 내보내기에 포함하지 않으므로 다른 PC에서는 다시 입력하세요."),
            ("09  화면 테마","설정의 화면 테마에서 Midnight · 민트 / Graphite · 앰버 / Nord · 블루를 선택할 수 있습니다. 저장 후 앱을 다시 열면 메뉴와 터미널 색상이 함께 바뀝니다. 기존 연결을 자동으로 끊지 않습니다."),
            ("데이터 및 구성","설정과 로그: "+AppPaths.Root+"\n터미널 설정 변경은 새 접속부터, 테마 변경은 앱을 다시 열 때 적용됩니다. 앱 종료 시 열려 있는 접속을 확인하며, 실행 중인 X 서버는 계속 둡니다. PuTTY 0.85(MIT)를 포함하고 Xming/VcXsrv는 기존 설치를 사용합니다.")
        };
        foreach(var (title,text) in sections){var heading=Ui.Text(title,15,"#69DDC0",true);heading.Margin=new Thickness(0,14,0,7);dialog.Body.Children.Add(heading);dialog.Body.Children.Add(Ui.Text(text,13,"#BDCCDD"));}
        dialog.Actions.Children.Add(Ui.Button("데이터 폴더",()=>Ui.OpenFolder(AppPaths.Root)));
        dialog.Actions.Children.Add(Ui.Button("닫기",()=>dialog.Close(),true));dialog.ShowDialog();
    }
}
