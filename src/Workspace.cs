using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace EquipLink;

public sealed partial class MainWindow : Window
{
    private readonly AppStore store;
    private readonly bool preview;
    private readonly XServerService xServer=new();
    private readonly Grid root=new(),workspace=new();
    private readonly ListBox equipmentList=new();
    private readonly TextBox search=Ui.Input();
    private readonly ComboBox groupFilter=Ui.Select(["전체 설비","즐겨찾기"],"전체 설비");
    private readonly StackPanel tabBar=new(){Orientation=Orientation.Horizontal};
    private readonly ScrollViewer home=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock footer=Ui.Text("준비됨 · 설비를 더블클릭하거나 빠른 접속을 사용하세요",11,"#8797AA");
    private readonly TextBlock xState=Ui.Text("○  X 서버 확인 중",11,"#8797AA");
    private bool? xServerResponding;
    private TextBlock? homeXState;
    internal static string AppVersion=>typeof(App).Assembly.GetName().Version?.ToString(3)??"";
    private readonly TextBlock sessionCount=Ui.Text("0 SESSIONS",10,"#8797AA");
    private readonly List<SessionPane> sessions=[],slots=[];
    private readonly List<UIElement> layoutChrome=[],terminalOnlyChrome=[];
    private readonly Dictionary<string,Button> layoutButtons=[];
    private readonly DispatcherTimer statusTimer=new(){Interval=TimeSpan.FromSeconds(12)};
    private FrameworkElement sidebar=null!,toolbar=null!,statusBar=null!;
    private Button focusExit=null!,guiButton=null!,commandButton=null!,loginButton=null!;
    private SessionPane? active,zoomed;
    private string layout="single",renderedLayout="";
    private readonly Dictionary<string,(double Column,double Row)> splitRatios=[];
    private bool showingHome=true,refreshing,checkingX,focusMode,sidebarVisible=true;
    private AppShortcuts? shortcuts;
    private Equipment? Selected=>(equipmentList.SelectedItem as ListBoxItem)?.Tag as Equipment;
    private int Capacity=>layout=="grid"?4:layout=="single"?1:2;

    public MainWindow(AppStore data,bool preview=false)
    {
        Ui.ApplyWindowTheme(this);
        store=data;ftpSession=new(store);ftpQueue=new(store);this.preview=preview;Title="EquipLink "+AppVersion+" · 설비 워크스페이스";Width=1440;Height=940;MinWidth=900;MinHeight=560;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        root.Background=Ui.Brush("#111923");root.ColumnDefinitions.Add(new(){Width=new GridLength(224)});root.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        toolbar=BuildToolbar();root.Children.Add(toolbar);Grid.SetColumnSpan(toolbar,2);sidebar=BuildSidebar();root.Children.Add(sidebar);Grid.SetRow(sidebar,1);
        var main=terminalRegion=new Grid();main.RowDefinitions.Add(new(){Height=GridLength.Auto});main.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var tabs=new DockPanel{Background=Ui.Brush("#141E29"),LastChildFill=true};focusExit=Ui.ToolButton("집중 해제",ToggleFocus,"집중 모드 해제 · F11");focusExit.Visibility=Visibility.Collapsed;DockPanel.SetDock(focusExit,Dock.Right);tabs.Children.Add(focusExit);
        tabs.Children.Add(new ScrollViewer{Content=tabBar,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(6,4,6,0)});main.Children.Add(tabs);
        workspace.Background=Ui.Brush("#111923");workspace.Children.Add(home);main.Children.Add(workspace);Grid.SetRow(workspace,1);root.Children.Add(main);Grid.SetColumn(main,1);Grid.SetRow(main,1);
        var bottom=new DockPanel{Margin=new Thickness(12,2,6,2)};xState.TextWrapping=TextWrapping.NoWrap;var xButton=Ui.ToolButton("",()=>{},"X 서버(Xming) 상태 · 클릭하여 시작 / 확인 / 설정");xButton.Content=xState;xButton.Height=24;xButton.MinHeight=22;xButton.Padding=new Thickness(8,0,8,0);xButton.Margin=new Thickness(8,0,0,0);xButton.Click+=(_,_)=>OpenMenu(xButton,BuildXMenu());DockPanel.SetDock(xButton,Dock.Right);bottom.Children.Add(xButton);footer.TextWrapping=TextWrapping.NoWrap;footer.TextTrimming=TextTrimming.CharacterEllipsis;bottom.Children.Add(footer);
        statusBar=new Border{BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(0,1,0,0),Child=bottom};root.Children.Add(statusBar);Grid.SetRow(statusBar,2);Grid.SetColumnSpan(statusBar,2);Content=root;
        search.TextChanged+=(_,_)=>RefreshList();groupFilter.SelectionChanged+=(_,_)=>{if(!refreshing)RefreshList();};equipmentList.SelectionChanged+=(_,_)=>{if(!refreshing&&showingHome){RefreshHome();sftpBrowserPanel?.Refresh();}};
        equipmentList.MouseDoubleClick+=async (_,_)=>{if(Selected!=null&&equipmentList.SelectedItem is ListBoxItem item&&item.IsMouseOver)await ConnectSafeAsync(Selected);};
        equipmentList.KeyDown+=async (_,e)=>{if(e.Key==Key.Enter&&Selected!=null){e.Handled=true;await ConnectSafeAsync(Selected);}};
        Closing+=OnClosing;Closed+=(_,_)=>shortcuts?.Dispose();RefreshGroups();RefreshList();RebuildTabs();RefreshHome();if(store.Equipment.Count>0)equipmentList.SelectedIndex=0;
        if(!preview){Loaded+=async (_,_)=>{if(!App.IsTesting){shortcuts=new AppShortcuts(this,HandleShortcut);shortcuts.TerminalInput+=handle=>sessions.FirstOrDefault(p=>p.Host.TerminalHandle==handle)?.Host.CancelAutoLogin();}if(store.Notice!="")SetStatus(store.Notice);await UpdateXStatusAsync();};statusTimer.Tick+=async (_,_)=>await UpdateXStatusAsync();statusTimer.Start();}
    }
    private FrameworkElement BuildToolbar()
    {
        var bar=new DockPanel{Margin=new Thickness(10,6,8,6),LastChildFill=true};var mark=Ui.Text(">_",16,"#5DD6BE",true);mark.HorizontalAlignment=HorizontalAlignment.Center;mark.VerticalAlignment=VerticalAlignment.Center;var brand=Ui.Row(new Border{Width=29,Height=27,CornerRadius=new CornerRadius(6),Background=Ui.Brush("#203039"),Child=mark},Ui.Text("EquipLink",16,"#DCE5F0",true));brand.Width=144;brand.VerticalAlignment=VerticalAlignment.Center;((FrameworkElement)brand.Children[1]).Margin=new Thickness(9,0,0,0);((FrameworkElement)brand.Children[1]).VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(brand,Dock.Left);bar.Children.Add(brand);
        var tools=Ui.Row();DockPanel.SetDock(tools,Dock.Right);loginButton=Ui.ToolButton("로그인",()=>active?.ShowCredentialMenu(loginButton),"저장한 ID / 비밀번호 입력 · 로그인 프롬프트에서 사용");tools.Children.Add(loginButton);guiButton=Ui.AsyncToolButton("xterm",()=>active?.OpenXtermAsync()??Task.CompletedTask,"로그인 후 선택한 터미널에서 새 xterm 실행");commandButton=Ui.ToolButton("명령",()=>active?.ToggleComposer(),"선택한 터미널의 명령 입력창 · Ctrl+Shift+J");tools.Children.Add(guiButton);tools.Children.Add(commandButton);tools.Children.Add(Ui.ToolButton("집중",ToggleFocus,"메뉴와 사이드바 숨기기 · F11"));var more=Ui.ToolButton("•••",()=>{},"작업공간 메뉴");more.Click+=(_,_)=>OpenMenu(more,BuildAppMenu());tools.Children.Add(more);bar.Children.Add(tools);
        var sidebarButton=Ui.ToolButton("",ToggleSidebar,"설비 목록 접기/펼치기 · Ctrl+Shift+B");sidebarButton.Content=Ui.Glyph("\uE700",14);sidebarButton.Width=30;
        terminalModeButton=Ui.ToolButton("터미널",()=>{if(ftpMode)ShowFtp(false);},"Telnet / SSH 터미널 작업공간");ftpModeButton=Ui.ToolButton("FTP",()=>{if(!ftpMode)ShowFtp(true);},"FTP 파일 전송 작업공간");
        var modeSwitch=Ui.Segmented(false,terminalModeButton,ftpModeButton);modeSwitch.Margin=new Thickness(0,0,8,0);UpdateModeSwitch();
        var left=Ui.Row(modeSwitch,sidebarButton,Ui.ToolButton("＋ 빠른 접속",QuickConnectDialog,"새 IP로 접속 · Ctrl+Shift+K",true));left.Children.Add(new Border{Width=1,Height=20,Background=Ui.Brush("#2A394A"),Margin=new Thickness(8,0,10,0)});
        foreach(var (mode,tip) in new[]{("single","단일 터미널"),("columns","좌우 2분할"),("rows","상하 2분할"),("grid","4분할")}){var button=Ui.ToolButton("",()=>SetLayout(mode),tip);button.Content=LayoutGlyph(mode);button.Width=34;System.Windows.Automation.AutomationProperties.SetName(button,tip);layoutButtons[mode]=button;left.Children.Add(button);}bar.Children.Add(left);
        // Layout, login, xterm, command and focus act on terminals only; FTP has its own toolbar.
        terminalOnlyChrome.AddRange(left.Children.Cast<UIElement>().Skip(1));terminalOnlyChrome.AddRange(tools.Children.Cast<UIElement>().Take(tools.Children.Count-1));
        return new Border{Background=Ui.Brush("#17212D"),BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(0,0,0,1),Child=bar};
    }
    // Small vector icons stay sharp at any display scale without depending on
    // uncommon Unicode glyphs or adding bitmap assets to the portable package.
    private static FrameworkElement LayoutGlyph(string mode)
    {
        var icon=new Canvas{Width=16,Height=14,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,IsHitTestVisible=false};
        icon.Children.Add(new Rectangle{Width=15,Height=13,RadiusX=1.5,RadiusY=1.5,Stroke=Ui.Brush("#DCE5F0"),StrokeThickness=1});
        if(mode is "columns" or "grid")icon.Children.Add(new Line{X1=7.5,Y1=0,X2=7.5,Y2=13,Stroke=Ui.Brush("#DCE5F0"),StrokeThickness=1});
        if(mode is "rows" or "grid")icon.Children.Add(new Line{X1=0,Y1=6.5,X2=15,Y2=6.5,Stroke=Ui.Brush("#DCE5F0"),StrokeThickness=1});
        return icon;
    }
    private FrameworkElement BuildSidebar()
    {
        var side=new Grid{Margin=new Thickness(12,16,12,10)};foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})side.RowDefinitions.Add(new(){Height=height});
        var title=new DockPanel();var add=Ui.ToolButton("",AddEquipment,"설비 등록");add.Content=Ui.Glyph("\uE710",12);add.Width=28;add.Margin=new Thickness(0);System.Windows.Automation.AutomationProperties.SetName(add,"설비 등록");DockPanel.SetDock(add,Dock.Right);title.Children.Add(add);var libraryTitle=Ui.Text("설비 라이브러리",13,"#DCE5F0",true);libraryTitle.VerticalAlignment=VerticalAlignment.Center;title.Children.Add(libraryTitle);side.Children.Add(title);
        search.ToolTip="이름 · IP · 그룹 검색";var searchField=new Grid{Margin=new Thickness(0,4,0,7)};searchField.Children.Add(search);var watermark=Ui.Text("이름 · IP 검색",11,"#687B91");watermark.Margin=new Thickness(10,0,0,0);watermark.VerticalAlignment=VerticalAlignment.Center;watermark.IsHitTestVisible=false;searchField.Children.Add(watermark);search.TextChanged+=(_,_)=>watermark.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;var filter=Ui.Stack(searchField,groupFilter);filter.Margin=new Thickness(0,6,0,14);side.Children.Add(filter);Grid.SetRow(filter,1);side.Children.Add(equipmentList);Grid.SetRow(equipmentList,2);
        var settingsButton=Ui.ToolButton("",OpenSettings,"X 서버(Xming)·테마·터미널 설정");var settingsIcon=Ui.Glyph("\uE713",13);settingsIcon.Margin=new Thickness(0,0,8,0);var settingsLabel=Ui.Text("설정 · Xming",12,"#DCE5F0",true);settingsLabel.TextWrapping=TextWrapping.NoWrap;settingsLabel.VerticalAlignment=VerticalAlignment.Center;settingsButton.Content=Ui.Row(settingsIcon,settingsLabel);settingsButton.HorizontalContentAlignment=HorizontalAlignment.Left;settingsButton.Margin=new Thickness(-6,0,0,0);
        var bottom=Ui.Stack(sessionCount,settingsButton);bottom.Margin=new Thickness(2,12,0,0);sessionCount.Margin=new Thickness(0,0,0,8);side.Children.Add(bottom);Grid.SetRow(bottom,3);
        return new Border{Background=Ui.Brush("#17212D"),BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(0,0,1,0),Child=WithFolderSidebar(side)};
    }
    private ContextMenu BuildAppMenu()
    {
        var menu=new ContextMenu();AddMenu(menu,"새 설비 등록",AddEquipment);AddMenu(menu,"PuTTY 세션 가져오기",ImportPutty);menu.Items.Add(new Separator());AddMenu(menu,"명령 모음",()=>Dialogs.Snippets(this,store,command=>active?.SetComposer(command)));AddMenu(menu,"최근 접속",ShowHistory);AddMenu(menu,"Xming 시작",async()=>{try{await StartXAsync();}catch(Exception e){Ui.Error(e);}});AddMenu(menu,"설정",OpenSettings);menu.Items.Add(new Separator());AddMenu(menu,"설정 백업",ExportSettings);AddMenu(menu,"백업 가져오기",ImportSettings);AddMenu(menu,"로그 폴더",()=>Ui.OpenFolder(AppPaths.Logs));AddMenu(menu,"분할 비율 초기화",()=>ApplyLayout(true));AddMenu(menu,"도움말",()=>Dialogs.Help(this));return menu;
    }
    private ContextMenu BuildXMenu()
    {
        var menu=new ContextMenu();
        AddMenu(menu,xServerResponding==true?"X 서버 실행 중 · 다시 확인":"Xming 시작",async()=>{try{if(xServerResponding==true)await UpdateXStatusAsync();else await StartXAsync();}catch(Exception e){Ui.Error(e);}});
        AddMenu(menu,"X 서버 설정",OpenSettings);AddMenu(menu,"GUI가 열리지 않을 때",()=>Dialogs.Help(this));return menu;
    }
    internal static void AddMenu(ContextMenu menu,string label,Action action){var item=new MenuItem{Header=label};item.Click+=(_,_)=>action();menu.Items.Add(item);}
    internal static void OpenMenu(FrameworkElement source,ContextMenu menu){source.ContextMenu=menu;menu.PlacementTarget=source;menu.IsOpen=true;}
    private void RefreshGroups(){refreshing=true;var old=groupFilter.SelectedItem as string;groupFilter.Items.Clear();groupFilter.Items.Add("전체 설비");groupFilter.Items.Add("즐겨찾기");foreach(var group in store.Equipment.Select(p=>p.Group).Distinct().Order())groupFilter.Items.Add(group);groupFilter.SelectedItem=old;if(groupFilter.SelectedIndex<0)groupFilter.SelectedIndex=0;refreshing=false;}
    private void RefreshList(string? selectedId=null)
    {
        selectedId??=Selected?.Id;refreshing=true;equipmentList.Items.Clear();string query=search.Text.Trim(),filter=groupFilter.SelectedItem as string??"전체 설비";
        var items=store.Equipment.Where(p=>query==""||(p.Name+" "+p.Host+" "+p.Group).Contains(query,StringComparison.OrdinalIgnoreCase)).Where(p=>filter=="전체 설비"||(filter=="즐겨찾기"?p.Favorite:p.Group==filter)).OrderByDescending(p=>p.Favorite).ThenBy(p=>p.Group).ThenBy(p=>p.Name);
        foreach(var e in items)
        {
            var heading=new DockPanel();
            var protocol=new Border{Child=Ui.Text(e.IsSsh?"SSH":"TEL",9,e.IsSsh?"#B8A6EE":"#5DD6BE",true),Background=Ui.Brush("#1B2936"),CornerRadius=new CornerRadius(3),Padding=new Thickness(4,0,4,0),Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            DockPanel.SetDock(protocol,Dock.Right);heading.Children.Add(protocol);
            var name=Ui.Text((e.Favorite?"★ ":"")+e.Name,12,"#DCE5F0",true);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;heading.Children.Add(name);
            var ip=Ui.Text(e.Host,11,"#8797AA");ip.Margin=new Thickness(0,4,0,0);ip.TextWrapping=TextWrapping.NoWrap;ip.TextTrimming=TextTrimming.CharacterEllipsis;
            var row=new ListBoxItem{Content=Ui.Stack(heading,ip),Tag=e,ToolTip=$"{e.Name} · {e.Host}:{e.Port}\n{e.Group} · 더블클릭 접속"};var menu=new ContextMenu();AddMenu(menu,"연결",async()=>await ConnectSafeAsync(e));AddMenu(menu,"설비 편집",()=>EditEquipment(e));AddMenu(menu,e.Favorite?"즐겨찾기 해제":"즐겨찾기 추가",()=>{e.Favorite=!e.Favorite;store.Save();RefreshList(e.Id);});AddMenu(menu,"포트 확인",async()=>await ProbeAsync(e));menu.Items.Add(new Separator());AddMenu(menu,"목록에서 삭제",()=>DeleteEquipment(e));row.ContextMenu=menu;equipmentList.Items.Add(row);if(e.Id==selectedId)equipmentList.SelectedItem=row;
        }
        if(equipmentList.Items.Count==0){var hint=Ui.Text(store.Equipment.Count==0?"등록된 설비가 없습니다.\n위의 + 또는 빠른 접속으로 추가하세요.":"조건에 맞는 설비가 없습니다.",11,"#8797AA");equipmentList.Items.Add(new ListBoxItem{Content=hint,IsEnabled=false,Focusable=false});}
        refreshing=false;if(showingHome)RefreshHome();
    }
    private void RefreshHome()
    {
        var body=new StackPanel{Margin=new Thickness(42,40,42,30),MaxWidth=1000,HorizontalAlignment=HorizontalAlignment.Stretch};body.Children.Add(Ui.Text("작업을 시작하세요",30,"#DCE5F0",true));var sub=Ui.Text("설비를 선택하고 연결하세요. 여러 터미널을 같은 작업공간에 배치할 수 있습니다.",13,"#8797AA");sub.Margin=new Thickness(0,10,0,24);body.Children.Add(sub);body.Children.Add(Ui.Row(Ui.Button("＋ 빠른 접속",QuickConnectDialog,true),Ui.Button("설비 등록",AddEquipment),Ui.Button("최근 접속",ShowHistory),Ui.Button("FTP 열기",()=>ShowFtp(true))));var section=Ui.Text("선택한 설비",11,"#8797AA",true);section.Margin=new Thickness(0,34,0,12);body.Children.Add(section);
        if(Selected is Equipment item)
        {
            var tag=Ui.Text(item.Protocol.ToUpperInvariant()+"  /  "+item.Group,10,"#5DD6BE",true);var name=Ui.Text(item.Name,23,"#DCE5F0",true);name.Margin=new Thickness(0,12,0,6);var details=Ui.Stack(tag,name,Ui.Text(item.Host+":"+item.Port+"   ·   "+item.Encoding,13,"#8797AA"));
            var chips=new WrapPanel{Margin=new Thickness(0,12,0,0)};
            chips.Children.Add(Chip(!item.UseX11?"X11 사용 안 함":item.ForwardX11?"X11 · SSH 전달":"X11 · 직접 (PC IP)",item.UseX11?"#69DDC0":"#8797AA",!item.UseX11?"이 설비는 GUI 실행을 사용하지 않습니다.":item.ForwardX11?"SSH X11 전달을 사용합니다. DISPLAY는 SSH가 자동으로 지정합니다.":"설비가 PC의 TCP "+(6000+store.Settings.DisplayNumber)+"로 직접 연결합니다. PC 방화벽과 X 서버 접근 허용이 필요합니다."));
            chips.Children.Add(Chip(item.CanAutoLogin?"자동 로그인":item.HasSavedPassword?"비밀번호 저장됨":string.IsNullOrEmpty(item.Username)?"로그인 정보 없음":"ID 저장됨",item.CanAutoLogin?"#69DDC0":"#8797AA",item.CanAutoLogin?"더블클릭 접속 시 저장한 ID와 비밀번호로 로그인합니다.":"설비 편집에서 ID와 비밀번호를 저장할 수 있습니다."));
            if(item.LogSession)chips.Children.Add(Chip("출력 로그 저장","#8797AA","터미널 출력을 로그 폴더에 저장합니다."));
            if(item.Favorite)chips.Children.Add(Chip("★ 즐겨찾기","#E9C786",null));
            details.Children.Add(chips);
            if(item.Notes!=""){var note=Ui.Text(item.Notes,12,"#A8B7C9");note.Margin=new Thickness(0,12,0,0);details.Children.Add(note);}var buttons=Ui.Row(Ui.AsyncButton("연결  →",()=>ConnectSafeAsync(item),true),Ui.Button("설비 편집",()=>EditEquipment(item)),Ui.AsyncButton("포트 확인",()=>ProbeAsync(item)));buttons.Margin=new Thickness(0,20,0,0);details.Children.Add(buttons);body.Children.Add(Ui.Card(details,"#17212D",24));
        }
        else body.Children.Add(Ui.Card(Ui.Stack(Ui.Text("설비 목록을 만들어 보세요",18,"#DCE5F0",true),Ui.Text("이름과 IP를 저장하면 다음부터 더블클릭으로 연결됩니다.",13,"#8797AA")),"#17212D",24));
        var layoutGuide=Ui.Row();foreach(var (mode,label) in new[]{("columns","좌우 분할"),("rows","상하 분할"),("grid","4분할")}){var icon=LayoutGlyph(mode);icon.Margin=new Thickness(0,0,6,0);var option=Ui.Row(icon,Ui.Text(label,12,"#AAB8C8"));option.Margin=new Thickness(0,0,18,0);layoutGuide.Children.Add(option);}
        var guide=Ui.Stack(Ui.Text("작업공간 사용법",13,"#DCE5F0",true),layoutGuide,Ui.Text("상단 버튼으로 분할을 고르고, 경계를 드래그해 크기를 조정하세요. 터미널 제목을 더블클릭하면 한 화면으로 확대됩니다.",12,"#8797AA"));foreach(FrameworkElement child in guide.Children)child.Margin=new Thickness(0,0,0,10);
        var keys=new Grid{Margin=new Thickness(0,2,0,0)};keys.ColumnDefinitions.Add(new(){Width=GridLength.Auto});keys.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        foreach(var (key,action) in new[]{("Ctrl+Shift+K","빠른 접속"),("Ctrl+Shift+J","명령 입력창"),("Ctrl+Shift+Enter","선택한 터미널 확대 / 복원"),("Ctrl+Shift+B","설비 목록 접기"),("F11","집중 모드")}){keys.RowDefinitions.Add(new(){Height=GridLength.Auto});var k=Ui.Text(key,11,"#DCE5F0",true);k.Margin=new Thickness(0,3,16,3);k.TextWrapping=TextWrapping.NoWrap;var a=Ui.Text(action,11,"#8797AA");a.Margin=new Thickness(0,3,0,3);Grid.SetRow(k,keys.RowDefinitions.Count-1);Grid.SetRow(a,keys.RowDefinitions.Count-1);Grid.SetColumn(a,1);keys.Children.Add(k);keys.Children.Add(a);}
        guide.Children.Add(keys);
        homeXState=Ui.Text("",13,"#8797AA",true);homeXState.Margin=new Thickness(0,0,0,12);UpdateHomeXState();
        var settings=store.Settings;var facts=new Grid();facts.ColumnDefinitions.Add(new(){Width=GridLength.Auto});facts.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        foreach(var (label,value) in new[]{("X 서버",settings.XServerPath==""?"자동 탐색 · 설정에서 지정 가능":System.IO.Path.GetFileName(settings.XServerPath)+(settings.XLaunchConfig!=""?" · "+System.IO.Path.GetFileName(settings.XLaunchConfig):"")),("DISPLAY",":"+settings.DisplayNumber+"  (TCP "+(6000+settings.DisplayNumber)+")"),("PC IP",settings.PcAddress==""?"자동 · 설비로 가는 경로 기준":settings.PcAddress),("자동 실행",settings.AutoStartX?"접속할 때 Xming 자동 실행":"꺼짐 · 직접 시작")})
        {facts.RowDefinitions.Add(new(){Height=GridLength.Auto});var l=Ui.Text(label,11,"#8797AA");l.Margin=new Thickness(0,3,16,3);l.TextWrapping=TextWrapping.NoWrap;var v=Ui.Text(value,11,"#DCE5F0");v.Margin=new Thickness(0,3,0,3);v.TextWrapping=TextWrapping.NoWrap;v.TextTrimming=TextTrimming.CharacterEllipsis;v.ToolTip=value;Grid.SetRow(l,facts.RowDefinitions.Count-1);Grid.SetRow(v,facts.RowDefinitions.Count-1);Grid.SetColumn(v,1);facts.Children.Add(l);facts.Children.Add(v);}
        var xActions=Ui.Row(Ui.AsyncButton("Xming 시작",StartXAsync,xServerResponding!=true),Ui.Button("X 서버 설정",OpenSettings));xActions.Margin=new Thickness(0,14,0,0);
        var xHint=Ui.Text("로그인 후 상단 xterm 버튼 또는 터미널 메뉴(⋯)에서 GUI를 실행합니다.",11,"#8797AA");xHint.Margin=new Thickness(0,12,0,0);
        var xTitle=Ui.Text("X11 / GUI 준비 상태",13,"#DCE5F0",true);xTitle.Margin=new Thickness(0,0,0,10);
        var x=Ui.Stack(xTitle,homeXState,facts,xActions,xHint);
        var cards=Ui.Columns(Ui.Card(guide,"#17212D",20),Ui.Card(x,"#17212D",20),1,1);cards.Margin=new Thickness(0,18,0,0);body.Children.Add(cards);if(store.Notice!=""){var note=Ui.Text(store.Notice,11,"#D2B479");note.Margin=new Thickness(0,18,0,0);body.Children.Add(note);}home.Content=body;
    }
    private static Border Chip(string text,string color,string? tip)
    {
        var label=Ui.Text(text,11,color,true);label.TextWrapping=TextWrapping.NoWrap;
        return new Border{Child=label,Background=Ui.Brush("#1B2936"),BorderBrush=Ui.Brush("#2A394A"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(8,3,8,3),Margin=new Thickness(0,0,6,6),ToolTip=tip};
    }
    private void RebuildTabs()
    {
        tabBar.Children.Clear();var start=Ui.ToolButton("",ShowHome,"시작 화면");start.Content=Ui.Glyph("\uE80F",14);start.Width=36;start.Margin=new Thickness(0,0,5,0);tabBar.Children.Add(start);
        foreach(var session in sessions)
        {
            bool selected=!showingHome&&active==session,visible=!showingHome&&slots.Contains(session);
            var name=Ui.ToolButton("",()=>ShowSession(session),session.Equipment.Protocol+" · "+session.Equipment.Host+":"+session.Equipment.Port);name.MaxWidth=175;name.MinWidth=90;name.Margin=new Thickness(0);name.Background=Brushes.Transparent;name.BorderThickness=new Thickness(0);name.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            var caption=new Grid();caption.ColumnDefinitions.Add(new(){Width=GridLength.Auto});caption.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var indicator=new Ellipse{Width=5,Height=5,Fill=Ui.Brush(selected?"#5DD6BE":visible?"#8797AA":"#687B91"),Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};caption.Children.Add(indicator);
            var label=Ui.Text(session.Equipment.Name,12,selected?"#DCE5F0":"#A8B7C9",selected);label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;caption.Children.Add(label);Grid.SetColumn(label,1);name.Content=caption;
            var close=Ui.ToolButton("×",()=>CloseTab(session),"세션 종료");close.Margin=new Thickness(0);close.Width=24;close.Background=Brushes.Transparent;close.BorderThickness=new Thickness(0);
            var border=new Border{Child=Ui.Row(name,close),Background=Ui.Brush(selected?"#22353E":visible?"#1B2936":"#17212D"),BorderBrush=Ui.Brush(selected?"#5DD6BE":"#2A394A"),BorderThickness=new Thickness(0,0,0,selected?2:1),CornerRadius=new CornerRadius(5,5,0,0),Margin=new Thickness(0,0,4,0)};
            var menu=new ContextMenu();AddMenu(menu,"이 터미널 선택",()=>ShowSession(session));AddMenu(menu,"한 화면으로 확대 / 복원",()=>ToggleZoom(session));AddMenu(menu,"같은 설비에 새 접속",async()=>await ConnectSafeAsync(session.Equipment));AddMenu(menu,"세션 종료",()=>CloseTab(session));border.ContextMenu=menu;tabBar.Children.Add(border);
        }
        var plus=Ui.ToolButton("",QuickConnectDialog,"새 접속 · Ctrl+Shift+K");plus.Content=Ui.Glyph("\uE710",12);plus.Width=32;System.Windows.Automation.AutomationProperties.SetName(plus,"새 접속");tabBar.Children.Add(plus);sessionCount.Text=$"{sessions.Count:00} SESSIONS  /  {store.Equipment.Count:00} DEVICES";foreach(var pair in layoutButtons){pair.Value.Background=Ui.Brush(pair.Key==layout?"#25413F":"#17212D");pair.Value.BorderBrush=Ui.Brush(pair.Key==layout?"#4A8D81":"#2A394A");}sftpBrowserPanel?.Refresh();loginButton.IsEnabled=!ftpMode&&!showingHome&&active!=null;guiButton.IsEnabled=!ftpMode&&!showingHome&&active?.Equipment.UseX11==true;commandButton.IsEnabled=!ftpMode&&!showingHome&&active!=null;
    }
    private void ShowHome(){showingHome=true;zoomed=null;foreach(var p in sessions)p.Visibility=Visibility.Collapsed;foreach(var item in layoutChrome)item.Visibility=Visibility.Collapsed;home.Visibility=Visibility.Visible;Grid.SetRow(home,0);Grid.SetColumn(home,0);Grid.SetRowSpan(home,Math.Max(1,workspace.RowDefinitions.Count));Grid.SetColumnSpan(home,Math.Max(1,workspace.ColumnDefinitions.Count));RefreshHome();RebuildTabs();}
    private void ShowSession(SessionPane pane){if(ftpMode)ShowFtp(false);showingHome=false;if(!slots.Contains(pane)){int index=active!=null?slots.IndexOf(active):-1;if(slots.Count<Capacity)slots.Add(pane);else slots[Math.Max(0,index)]=pane;}active=pane;if(zoomed!=null)zoomed=pane;ApplyLayout();FocusActive();}
    private void ActivatePane(SessionPane pane){if(showingHome||!slots.Contains(pane)||active==pane)return;active=pane;foreach(var item in sessions)item.SetActive(item==active);RebuildTabs();}
    private void SetLayout(string mode){if(ftpMode)ShowFtp(false);if(mode is not ("single" or "columns" or "rows" or "grid"))throw new ArgumentException("Unknown layout");layout=mode;zoomed=null;var ordered=new List<SessionPane>();if(active!=null)ordered.Add(active);ordered.AddRange(slots.Where(s=>!ordered.Contains(s)));ordered.AddRange(sessions.Where(s=>!ordered.Contains(s)));slots.Clear();slots.AddRange(ordered.Take(Capacity));if(slots.Count>0){showingHome=false;active=slots[0];}ApplyLayout(true);FocusActive();}
    private void ApplyLayout(bool reset=false)
    {
        if(showingHome){ShowHome();return;}home.Visibility=Visibility.Collapsed;string mode=zoomed!=null?"single":layout;
        if(renderedLayout!=mode && renderedLayout!="")
        {
            double column=workspace.ColumnDefinitions.Count==3?workspace.ColumnDefinitions[0].ActualWidth/Math.Max(1,workspace.ColumnDefinitions[0].ActualWidth+workspace.ColumnDefinitions[2].ActualWidth):.5;
            double row=workspace.RowDefinitions.Count==3?workspace.RowDefinitions[0].ActualHeight/Math.Max(1,workspace.RowDefinitions[0].ActualHeight+workspace.RowDefinitions[2].ActualHeight):.5;
            splitRatios[renderedLayout]=(Math.Clamp(column,.1,.9),Math.Clamp(row,.1,.9));
        }
        if(reset)splitRatios.Remove(mode);
        if(renderedLayout!=mode||reset){renderedLayout=mode;foreach(var e in layoutChrome)workspace.Children.Remove(e);layoutChrome.Clear();workspace.RowDefinitions.Clear();workspace.ColumnDefinitions.Clear();bool cols=mode is "columns" or "grid",rows=mode is "rows" or "grid";workspace.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=200});if(cols){workspace.ColumnDefinitions.Add(new(){Width=new GridLength(5)});workspace.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=200});}workspace.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=120});if(rows){workspace.RowDefinitions.Add(new(){Height=new GridLength(5)});workspace.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=120});}
            if(cols){var s=new GridSplitter{Width=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Background=Ui.Brush("#273747"),ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,ShowsPreview=false};Grid.SetColumn(s,1);Grid.SetRowSpan(s,rows?3:1);workspace.Children.Add(s);layoutChrome.Add(s);}if(rows){var s=new GridSplitter{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Background=Ui.Brush("#273747"),ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,ShowsPreview=false};Grid.SetRow(s,1);Grid.SetColumnSpan(s,cols?3:1);workspace.Children.Add(s);layoutChrome.Add(s);}
            int count=mode=="grid"?4:mode=="single"?1:2;for(int i=0;i<count;i++){var body=Ui.Stack(Ui.Text($"{i+1:00}  /  빈 작업 공간",14,"#8797AA",true),Ui.Text("탭 또는 설비를 선택해 연결하세요",11,"#687B91"),Ui.Button("＋ 빠른 접속",QuickConnectDialog));foreach(FrameworkElement child in body.Children)child.Margin=new Thickness(0,0,0,12);body.HorizontalAlignment=HorizontalAlignment.Center;body.VerticalAlignment=VerticalAlignment.Center;var empty=new Border{Child=body,Background=Ui.Brush("#111923"),Margin=new Thickness(1)};Position(empty,i,mode);Panel.SetZIndex(empty,-1);workspace.Children.Add(empty);layoutChrome.Add(empty);}}
        if(splitRatios.TryGetValue(mode,out var ratio))
        {
            if(workspace.ColumnDefinitions.Count==3){workspace.ColumnDefinitions[0].Width=new GridLength(ratio.Column,GridUnitType.Star);workspace.ColumnDefinitions[2].Width=new GridLength(1-ratio.Column,GridUnitType.Star);}
            if(workspace.RowDefinitions.Count==3){workspace.RowDefinitions[0].Height=new GridLength(ratio.Row,GridUnitType.Star);workspace.RowDefinitions[2].Height=new GridLength(1-ratio.Row,GridUnitType.Star);}
            splitRatios.Remove(mode);
        }
        foreach(var e in layoutChrome)e.Visibility=Visibility.Visible;foreach(var pane in sessions){int index=slots.IndexOf(pane);bool visible=zoomed!=null?pane==zoomed:index>=0&&index<Capacity;pane.Visibility=visible?Visibility.Visible:Visibility.Collapsed;if(visible){Position(pane,zoomed!=null?0:index,mode);pane.SetActive(pane==active);pane.SetZoomed(zoomed==pane);}}RebuildTabs();Dispatcher.BeginInvoke(()=>{foreach(var pane in VisiblePanesForTest)pane.Host.RefreshViewport();},DispatcherPriority.Loaded);
    }
    private static void Position(UIElement e,int index,string mode){Grid.SetRowSpan(e,1);Grid.SetColumnSpan(e,1);Grid.SetRow(e,mode=="rows"?index*2:mode=="grid"?index/2*2:0);Grid.SetColumn(e,mode=="columns"?index*2:mode=="grid"?index%2*2:0);}
    private void FocusActive()=>Dispatcher.BeginInvoke(()=>{if(!ftpMode)active?.Host.FocusTerminal();},DispatcherPriority.Input);
    internal void RestoreTerminalFocus()
    {
        // ShowDialog re-enables its owner as it unwinds. Wait until that has
        // finished; never take focus from another app or an open child dialog.
        Dispatcher.BeginInvoke(()=>
        {
            if(ftpMode||!IsVisible||!IsEnabled||!IsActive||showingHome||active?.IsVisible!=true)return;
            if(OwnedWindows.Cast<Window>().Any(window=>window.IsVisible))return;
            if(!active.Host.External)active.Host.FocusTerminal();
        },DispatcherPriority.ContextIdle);
    }
    private void ToggleZoom(SessionPane p){if(!slots.Contains(p))ShowSession(p);active=p;zoomed=zoomed==p?null:p;showingHome=false;ApplyLayout();FocusActive();}
    private void ToggleSidebar(){sidebarVisible=!sidebarVisible;ApplyChrome();}
    private void ToggleFocus(){if(ftpMode)ShowFtp(false);focusMode=!focusMode;ApplyChrome();FocusActive();}
    private void ApplyChrome(){foreach(var item in terminalOnlyChrome)item.Visibility=ftpMode?Visibility.Collapsed:Visibility.Visible;sidebar.Visibility=sidebarVisible&&!focusMode&&!ftpMode?Visibility.Visible:Visibility.Collapsed;root.ColumnDefinitions[0].Width=new GridLength(sidebarVisible&&!focusMode&&!ftpMode?224:0);toolbar.Visibility=focusMode?Visibility.Collapsed:Visibility.Visible;statusBar.Visibility=focusMode?Visibility.Collapsed:Visibility.Visible;focusExit.Visibility=focusMode?Visibility.Visible:Visibility.Collapsed;}
    private void HandleShortcut(string cmd){switch(cmd){case "focus":ToggleFocus();break;case "sidebar":ToggleSidebar();break;case "quick":QuickConnectDialog();break;case "composer":if(!ftpMode)active?.ToggleComposer();break;case "zoom":if(!ftpMode&&active!=null)ToggleZoom(active);break;}}
    private void QuickConnectDialog()
    {
        if(ftpMode)ShowFtp(false);
        var dialog=new FormDialog(this,"빠른 접속",620,480);var address=Ui.Input();address.Name="QuickConnectAddress";var protocol=Ui.Select(["Telnet","SSH"],"Telnet");var port=Ui.Input("23");var save=new CheckBox{Content="이 설비를 목록에 저장"};var name=Ui.Input();protocol.SelectionChanged+=(_,_)=>{if(port.Text is "22" or "23")port.Text=protocol.SelectedIndex==1?"22":"23";};dialog.Body.Children.Add(Ui.Field("IP 주소 / 호스트",address));dialog.Body.Children.Add(Ui.Columns(Ui.Field("접속 방식",protocol),Ui.Field("포트",port)));dialog.Body.Children.Add(save);dialog.Body.Children.Add(Ui.Field("설비 이름 · 비우면 IP 사용",name));Equipment? chosen=null;
        void Accept(){try{if(!int.TryParse(port.Text,out int n))throw new ArgumentException("포트 번호를 확인하세요.");chosen=new(){Host=address.Text,Name=string.IsNullOrWhiteSpace(name.Text)?address.Text:name.Text,Protocol=(string)protocol.SelectedItem,Port=n};chosen.Validate();dialog.DialogResult=true;}catch(Exception e){Ui.Error(e);}}
        var cancel=Ui.Button("취소",()=>dialog.DialogResult=false);cancel.Name="CancelQuickConnect";dialog.Actions.Children.Add(cancel);dialog.Actions.Children.Add(Ui.Button("연결",Accept,true));address.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Accept();}};dialog.Loaded+=(_,_)=>address.Focus();bool? accepted=dialog.ShowDialog();RestoreTerminalFocus();if(accepted!=true||chosen==null)return;if(save.IsChecked==true){store.Equipment.Add(chosen);store.Save();RefreshGroups();RefreshList(chosen.Id);}_=ConnectSafeAsync(chosen);
    }
    private void AddEquipment(){var item=Dialogs.EquipmentEditor(this,new Equipment());if(item==null)return;store.Equipment.Add(item);store.Save();RefreshGroups();RefreshList(item.Id);}
    private void EditEquipment(Equipment e){var item=Dialogs.EquipmentEditor(this,e);if(item==null)return;store.Equipment[store.Equipment.FindIndex(p=>p.Id==e.Id)]=item;store.Save();RefreshGroups();RefreshList(item.Id);}
    private void DeleteEquipment(Equipment e){if(!Dialogs.Confirm(this,"설비 삭제","저장된 설비 '"+e.Name+"'를 목록에서 삭제할까요? 저장한 로그인 정보도 함께 지워집니다.","삭제",true))return;store.Equipment.Remove(e);store.Save();RefreshGroups();RefreshList();}
    private async Task ConnectSafeAsync(Equipment e)
    {
        if(preview)return;try{e=e.Copy();e.Validate();SetStatus(e.Name+" 접속 준비 중…");string warning="";if(e.UseX11&&store.Settings.AutoStartX){try{SetStatus(await xServer.EnsureAsync(store.Settings));}catch(Exception ex){warning="Xming 설정 확인: "+ex.Message;SetStatus(warning);}}var pane=CreatePane(e);sessions.Add(pane);workspace.Children.Add(pane);ShowSession(pane);await pane.ConnectAsync();store.Record(e);RefreshList(e.Id);if(warning!=""){pane.ShowGuiNotice(warning);SetStatus(warning);}}catch(Exception ex){SetStatus("접속 준비 실패: "+ex.Message);Ui.Error(ex);}
    }
    private SessionPane CreatePane(Equipment e){var p=new SessionPane(this,e,store,SetStatus,()=>ConnectSafeAsync(e),xServer);p.Activated+=()=>ActivatePane(p);p.ZoomRequested+=()=>ToggleZoom(p);p.CloseRequested+=()=>CloseTab(p);return p;}
    private void CloseTab(SessionPane p,bool confirm=true){if(confirm&&p.Host.HasProcess&&!Dialogs.Confirm(this,"세션 닫기","'"+p.Equipment.Name+"' 세션을 종료할까요? 실행 중인 작업에 영향을 줄 수 있습니다.","세션 종료",true))return;int index=slots.IndexOf(p);p.Sftp.Dispose();p.Host.CloseSession();workspace.Children.Remove(p);p.Host.Dispose();sessions.Remove(p);slots.Remove(p);var replacement=sessions.FirstOrDefault(s=>!slots.Contains(s));if(index>=0&&replacement!=null)slots.Insert(Math.Min(index,slots.Count),replacement);if(zoomed==p)zoomed=null;if(active==p)active=slots.FirstOrDefault()??sessions.FirstOrDefault();if(sessions.Count==0)ShowHome();else if(showingHome)RebuildTabs();else ApplyLayout();}
    private bool closingTransfers,closeConfirmed;
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closingTransfers){e.Cancel=true;return;}
        int count=sessions.Count(s=>s.Host.HasProcess);
        if(!closeConfirmed&&(count>0||ftpQueue.HasPending)&&!Dialogs.Confirm(this,"EquipLink 종료",$"열려 있는 터미널 {count}개"+(ftpQueue.HasPending?"와 진행 중인 FTP 전송을 종료하고 EquipLink를 닫을까요?":"를 종료하고 EquipLink를 닫을까요?"),"종료",true)){e.Cancel=true;return;}
        if(ftpQueue.HasPending)
        {
            e.Cancel=true;closingTransfers=true;IsEnabled=false;SetStatus("FTP 전송을 취소하고 임시 파일을 정리하는 중…");ftpSession.Disconnect();ftpQueue.CancelAll();
            try{await ftpQueue.WaitForIdleAsync();}
            finally{closingTransfers=false;closeConfirmed=true;IsEnabled=true;_=Dispatcher.BeginInvoke(Close);}
            return;
        }
        statusTimer.Stop();ftpWorkspace?.Dispose();sftpBrowserPanel?.Dispose();ftpQueue.Dispose();ftpSession.Dispose();foreach(var p in sessions){p.Sftp.Dispose();p.Host.CloseSession();}
    }
    private void SetStatus(string text){footer.Text=text;footer.ToolTip=text;}
    internal async Task<SessionPane> ConnectForTestAsync(Equipment e){await ConnectSafeAsync(e);return sessions[^1];}
    internal void QuickConnectForTest()=>QuickConnectDialog();
    internal void ShowHomeForTest()=>ShowHome();internal void ShowSessionForTest(SessionPane p)=>ShowSession(p);internal void SetLayoutForTest(string mode)=>SetLayout(mode);
    internal IReadOnlyList<SessionPane> VisiblePanesForTest=>showingHome?[]:zoomed!=null?[zoomed]:slots.ToArray();internal SessionPane? ActiveForTest=>active;internal bool FocusModeForTest=>focusMode;internal double SidebarWidthForTest=>root.ColumnDefinitions[0].Width.Value;
    internal void ToggleSidebarForTest()=>ToggleSidebar();internal void ToggleFocusForTest()=>ToggleFocus();internal void ToggleZoomForTest(SessionPane p)=>ToggleZoom(p);internal void CloseForTest(SessionPane p)=>CloseTab(p,false);
}








