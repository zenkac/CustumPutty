using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

public sealed record TerminalFtpTarget(string Id,string Label,Func<bool> IsAvailable,Action<string> PrepareCommand);

public sealed partial class FtpWorkspace
{
    private readonly Func<IReadOnlyList<TerminalFtpTarget>> terminalTargets;
    private readonly ComboBox sourceChoice=Ui.Select(["내 PC"],"내 PC");
    private FrameworkElement pcFilePane=null!,helperView=null!;
    private TerminalFtpHelper? terminalHelper;
    private Button sourceBrowse=null!;
    private bool refreshingTargets,terminalMode;
    private FrameworkElement BuildSourcePane()
    {
        var panel=new Grid();panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var row=new DockPanel{Margin=new Thickness(12,11,10,8),MinHeight=29};
        sourceBrowse=Ui.AsyncToolButton("폴더 선택",BrowseLocalAsync,"내 PC 폴더 열기");sourceBrowse.Margin=new Thickness(5,0,0,0);sourceBrowse.Height=25;sourceBrowse.MinHeight=24;DockPanel.SetDock(sourceBrowse,Dock.Right);row.Children.Add(sourceBrowse);
        sourceChoice.Name="FtpSourceChoice";sourceChoice.MinHeight=29;sourceChoice.FontSize=11;sourceChoice.ToolTip="내 PC 또는 열린 터미널의 FTP 명령 도우미 · 경유 설비는 해당 탭의 현재 접속 위치에서 작업";row.Children.Add(sourceChoice);panel.Children.Add(row);
        pcFilePane=BuildFilePane(false);panel.Children.Add(pcFilePane);SetRow(pcFilePane,1);
        terminalHelper=new TerminalFtpHelper(owner);helperView=new ScrollViewer{Content=terminalHelper,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};panel.Children.Add(helperView);SetRow(helperView,1);
        RefreshTerminalTargets();sourceChoice.DropDownOpened+=(_,_)=>RefreshTerminalTargets();
        sourceChoice.SelectionChanged+=(_,_)=>{if(!refreshingTargets)ApplySourceChoice();};
        return panel;
    }
    internal void RefreshTerminalTargets()
    {
        string? selected=(sourceChoice.SelectedItem as ComboBoxItem)?.Tag is TerminalFtpTarget previous?previous.Id:null;
        refreshingTargets=true;
        try
        {
            sourceChoice.Items.Clear();sourceChoice.Items.Add(new ComboBoxItem{Content="내 PC",Tag=null});
            foreach(var target in terminalTargets())sourceChoice.Items.Add(new ComboBoxItem{Content="터미널 FTP · "+target.Label,Tag=target,ToolTip=target.Label});
            sourceChoice.SelectedItem=sourceChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>x.Tag is TerminalFtpTarget target&&target.Id==selected)??sourceChoice.Items[0];
        }
        finally{refreshingTargets=false;}
        if(helperView!=null)ApplySourceChoice();
    }
    private void ApplySourceChoice()
    {
        var target=(sourceChoice.SelectedItem as ComboBoxItem)?.Tag as TerminalFtpTarget;terminalMode=target!=null;
        pcFilePane.Visibility=terminalMode?Visibility.Collapsed:Visibility.Visible;helperView.Visibility=terminalMode?Visibility.Visible:Visibility.Collapsed;
        sourceBrowse.Visibility=terminalMode?Visibility.Collapsed:Visibility.Visible;
        terminalHelper!.SetTarget(target);terminalHelper.SetServer(session.Site,session.CurrentPath);
        uploadButton.Content=Ui.Text(terminalMode?"설비 → 서버 명령":"↑ 업로드",12,"#DCE5F0",true);
        downloadButton.Content=Ui.Text(terminalMode?"서버 → 설비 준비":"↓ 다운로드",12,"#DCE5F0",true);
        uploadButton.ToolTip=terminalMode?"설비의 파일명 / 패턴으로 put 또는 mput 명령 준비":"선택한 내 PC 파일 / 폴더를 서버로 전송";
        downloadButton.ToolTip=terminalMode?"선택한 서버 파일로 get / mget 명령 준비 · 터미널에서 실행":"선택한 서버 파일 / 폴더를 현재 내 PC 폴더로 전송";
        remoteFiles.ToolTip=(terminalMode?"파일 더블클릭: 설비로 받을 파일명을 명령 도우미에 준비":"파일 더블클릭: 현재 내 PC 폴더로 다운로드")+" · 폴더 더블클릭: 열기\nWindows 탐색기와의 드래그는 항상 내 PC ↔ 서버 전송입니다.";
        if(remoteFiles.ContextMenu?.Items[0] is MenuItem download)download.Header=terminalMode?"선택 파일 get / mget 준비":"선택 항목 다운로드";
    }
}

// Preparing a command never writes to the terminal or network. The existing
// composer shows it next to the real terminal; the user verifies the shell/ftp
// prompt and executes. Nested Telnet sessions are never guessed or inspected.
internal sealed class TerminalFtpHelper:StackPanel
{
    private readonly Window owner;
    private TerminalFtpTarget? target;
    private readonly TextBlock context=Ui.Text("",12,"#5DD6BE",true),notice=Ui.Text("",11,"#E9C786");
    private readonly TextBox serverHost=Ui.Input(),serverPort=Ui.Input("21"),equipmentPath=Ui.Input(),serverPath=Ui.Input("/"),files=Ui.Input("",true);
    private readonly TextBox preview=Ui.Input("",true);
    private string serverIdentity="";
    public TerminalFtpHelper(Window owner)
    {
        this.owner=owner;Margin=new Thickness(16,14,16,14);Name="TerminalFtpHelper";
        Children.Add(Ui.Text("경유 설비 · 터미널 FTP",17,"#DCE5F0",true));context.Margin=new Thickness(0,7,0,8);Children.Add(context);
        Children.Add(Ui.Text("선택한 탭의 현재 설비에서 실행합니다. 경유 접속 위치와 설비 파일 목록은 터미널에서 확인하세요.",12,"#8797AA"));
        var flow=Ui.Text("① 셸에서 FTP 접속·로그인 → ② ftp>에서 경로 설정 → ③ 전송",12,"#DCE5F0");flow.Margin=new Thickness(0,8,0,12);Children.Add(flow);
        serverHost.Name="TerminalFtpHost";serverPort.Name="TerminalFtpPort";equipmentPath.Name="TerminalFtpEquipmentPath";serverPath.Name="TerminalFtpServerPath";files.Name="TerminalFtpFiles";preview.Name="TerminalFtpPreview";
        Children.Add(Ui.Columns(Ui.Field("서버 IP / 호스트",serverHost),Ui.Field("포트",serverPort),3,1));
        Children.Add(Button("① FTP 접속 명령 준비",()=>TerminalFtpCommands.Open(serverHost.Text,serverPort.Text),"TerminalFtpOpen"));
        Children.Add(PathField("설비 쪽 폴더 · lcd",equipmentPath,"lcd", "TerminalFtpLcd"));
        Children.Add(PathField("서버 쪽 폴더 · cd",serverPath,"cd", "TerminalFtpCd"));
        var utilities=new WrapPanel();foreach(var button in new[]{Button("binary 준비",()=>"binary","TerminalFtpBinary"),Button("목록 ls",()=>"ls","TerminalFtpList"),Button("위치 pwd",()=>"pwd","TerminalFtpPwd")})utilities.Children.Add(button);Children.Add(utilities);
        files.MinHeight=62;files.MaxHeight=100;Children.Add(Ui.Field("파일명 / 패턴 · 한 줄에 하나",files,"예: result.csv 또는 *.log · 폴더 전체 전송은 지원하지 않습니다."));
        var transfer=new WrapPanel();transfer.Children.Add(Ui.Button("설비 → 서버 put/mput",()=>PrepareTransfer(true),true));transfer.Children.Add(Ui.Button("서버 → 설비 get/mget",()=>PrepareTransfer(false)));transfer.Margin=new Thickness(0,0,0,8);Children.Add(transfer);
        preview.IsReadOnly=true;preview.MinHeight=40;preview.MaxHeight=90;Children.Add(Ui.Field("준비한 명령",preview));notice.Margin=new Thickness(0,4,0,0);Children.Add(notice);
        Children.Add(Ui.Text("명령은 선택한 터미널의 명령 입력창에 준비됩니다. 프롬프트를 확인한 뒤 실행하세요. 이 방식의 진행률·덮어쓰기 여부는 FTP 전송창에서 확인할 수 없습니다.",11,"#8797AA"));
    }
    private Button Button(string label,Func<string> command,string name)
    {
        var button=Ui.Button(label,()=>Prepare(command));button.Name=name;button.Margin=new Thickness(0,0,5,12);return button;
    }
    private FrameworkElement PathField(string label,TextBox input,string verb,string name)
    {
        var row=new DockPanel();var ready=Button("준비",()=>TerminalFtpCommands.Directory(verb,input.Text),name);ready.Margin=new Thickness(6,0,0,0);DockPanel.SetDock(ready,Dock.Right);row.Children.Add(ready);row.Children.Add(input);
        input.ToolTip=verb=="cd"?"FTP 서버의 폴더 · 오른쪽에서 고른 파일을 받기 전에 이동하세요.":"현재 터미널에 접속한 설비의 폴더";return Ui.Field(label,row);
    }
    public void SetTarget(TerminalFtpTarget? value){target=value;context.Text=value?.Label??"열린 터미널을 선택하세요.";}
    public void SetServer(FtpSite? site,string path)
    {
        if(site==null)return;string identity=site.Host+":"+site.Port;
        if(serverIdentity!=identity){serverIdentity=identity;serverHost.Text=site.Host;serverPort.Text=site.Port.ToString();serverPath.Text=path;}
    }
    private void Prepare(Func<string> create,bool transfer=false)
    {
        try
        {
            if(target==null||!target.IsAvailable())throw new InvalidOperationException("열린 터미널을 다시 선택해 주세요.");
            string command=create();preview.Text=command;
            if(transfer)
            {
                var dialog=new FormDialog(owner,"터미널 FTP 전송 준비",480,compact:true);
                dialog.Body.Children.Add(Ui.Text("같은 이름의 대상 파일이 있으면 덮어쓸 수 있습니다. 현재 설비와 ftp>의 cd / lcd 경로를 확인한 뒤 실행하세요.",13,"#E9C786"));
                dialog.Body.Children.Add(Ui.Card(Ui.Text(command,12,"#DCE5F0"),"#17212D",12));
                var cancel=Ui.Button("취소",()=>dialog.DialogResult=false);cancel.Name="TerminalFtpCancel";cancel.IsCancel=true;cancel.IsDefault=true;
                var ready=Ui.Button("명령 입력창에 준비",()=>dialog.DialogResult=true,true);ready.Name="TerminalFtpConfirm";dialog.Actions.Children.Add(cancel);dialog.Actions.Children.Add(ready);
                if(dialog.ShowDialog()!=true)return;
            }
            target.PrepareCommand(command);notice.Text="터미널의 명령 입력창에 준비했습니다. 현재 프롬프트를 확인하고 실행하세요.";
        }
        catch(Exception error){Ui.Error(error);}
    }
    public void PrepareTransfer(bool upload)=>Prepare(()=>TerminalFtpCommands.Transfer(upload,files.Text),true);
    public void ChooseServerFiles(FtpEntry[] selected,string directory)
    {
        try
        {
            if(selected.Length==0)return;if(selected.Any(x=>x.IsDirectory||x.IsLink))throw new ArgumentException("일반 파일을 선택하세요. 경유 설비의 폴더 전송은 터미널에서 직접 진행해 주세요.");
            foreach(var entry in selected)TerminalFtpCommands.LiteralName(entry.Name);
            serverPath.Text=directory;files.Text=string.Join("\n",selected.Select(x=>x.Name));preview.Text=TerminalFtpCommands.Transfer(false,files.Text);
            notice.Text=$"{selected.Length}개 파일 준비됨 · 먼저 서버 폴더 설정(cd)을 실행한 뒤 get/mget 명령을 준비하세요.";
        }
        catch(Exception error){Ui.Error(error);}
    }
}

internal static class TerminalFtpCommands
{
    public static string Open(string host,string port)
    {
        host=host.Trim();if(!Rules.ValidHost(host)||host.StartsWith('-'))throw new ArgumentException("서버 IP를 확인해 주세요.");
        if(!int.TryParse(port,out int number)||number is <1 or >65535)throw new ArgumentException("FTP 포트를 확인해 주세요.");
        return "ftp -i "+Rules.QuoteShell(host)+(number==21?"":" "+number);
    }
    public static string Directory(string verb,string path)
    {
        if(verb is not ("cd" or "lcd"))throw new ArgumentException("지원하지 않는 경로 명령입니다.");return verb+" "+Token(path,false);
    }
    public static void LiteralName(string name)
    {
        if(name.IndexOfAny(['*','?','[',']','{','}','/'])>=0)throw new ArgumentException("패턴 문자가 포함된 파일명은 터미널에서 직접 지정해 주세요.");_ = Token(name,false);
    }
    public static string Transfer(bool upload,string input)
    {
        string[] names=input.Replace("\r\n","\n").Split('\n',StringSplitOptions.RemoveEmptyEntries);
        if(names.Length==0||names.Length>100)throw new ArgumentException("파일명 또는 패턴을 한 줄에 하나씩, 100개 이내로 입력해 주세요.");
        string[] tokens=names.Select(x=>Token(x,true)).ToArray();bool multiple=names.Length>1||names[0].IndexOfAny(['*','?','['])>=0;
        string command=(multiple?"m":"")+(upload?"put":"get")+" "+string.Join(" ",tokens);
        if(command.Length>4000)throw new ArgumentException("전송 명령이 너무 깁니다. 파일을 나누어 선택해 주세요.");return command;
    }
    private static string Token(string value,bool file)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>1024||value.Any(char.IsControl)||value.IndexOfAny(['"','\'','\\','|',';','&','`','$','<','>','(',')','{','}'])>=0||value.StartsWith('-')||value.StartsWith('!'))throw new ArgumentException("이름·경로의 줄바꿈이나 특수 문자는 명령 도우미에서 사용할 수 없습니다. 터미널에서 직접 입력해 주세요.");
        if(file&&(value.Contains('/')||value.StartsWith('~')))throw new ArgumentException("파일명 또는 패턴만 입력하고 폴더는 cd / lcd로 먼저 설정하세요.");
        return "\""+value+"\"";
    }
}
