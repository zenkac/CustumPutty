using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EquipLink;

public sealed partial class MainWindow
{
    private readonly FtpSession ftpSession;
    private readonly FtpTransferQueue ftpQueue;
    private FtpWorkspace? ftpWorkspace;
    private SftpBrowserPanel? sftpBrowserPanel;
    private Grid terminalRegion=null!;
    private Button ftpModeButton=null!,terminalModeButton=null!,viModeButton=null!;
    private PracticeWorkspace? viWorkspace;
    private void UpdateModeSwitch()
    {
        Ui.SetSegment(terminalModeButton,TerminalMode);Ui.SetSegment(ftpModeButton,ftpMode);Ui.SetSegment(viModeButton,viMode);
    }
    private bool ftpMode,viMode;
    /// <summary>True while terminals are on screen (neither FTP nor VI practice is shown).</summary>
    private bool TerminalMode=>!ftpMode&&!viMode;

    private FrameworkElement WithFolderSidebar(FrameworkElement library)
    {
        var panel=new Grid();panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        sftpBrowserPanel=new SftpBrowserPanel(this,store,()=>showingHome?null:active){Visibility=Visibility.Collapsed};
        panel.Children.Add(library);Grid.SetRow(library,1);panel.Children.Add(sftpBrowserPanel);Grid.SetRow(sftpBrowserPanel,1);
        Button devices=null!,folders=null!;
        void Select(bool folder)
        {
            library.Visibility=folder?Visibility.Collapsed:Visibility.Visible;sftpBrowserPanel.Visibility=folder?Visibility.Visible:Visibility.Collapsed;
            Ui.SetSegment(devices,!folder);Ui.SetSegment(folders,folder);
            if(folder)sftpBrowserPanel.Refresh();
        }
        devices=Ui.ToolButton("설비",()=>Select(false),"저장한 설비 목록");folders=Ui.ToolButton("SSH Browser",()=>Select(true),"SSH 연결의 SFTP 파일 탐색기");
        var tabs=Ui.Segmented(true,devices,folders);tabs.Margin=new Thickness(12,12,12,0);panel.Children.Add(tabs);Select(false);return panel;
    }
    private async Task ConnectFtpSafeAsync(FtpSite site,string password)
    {
        try{await ftpSession.ConnectAsync(site,password);SetStatus(ftpSession.IsConnected?"FTP 연결됨 · "+site.Host+" · "+ftpSession.CurrentPath:"FTP 연결 취소됨");}
        catch(OperationCanceledException){SetStatus("FTP 연결 취소됨");}
        catch(Exception error){SetStatus("FTP 접속 실패 · "+error.Message);Ui.Error(error);}
    }
    private void ShowFtp(bool show)=>SetWorkMode(show?"ftp":"terminal");
    private void ShowVi(bool show)=>SetWorkMode(show?"vi":"terminal");
    private void SetWorkMode(string mode)
    {
        bool show=mode=="ftp",vi=mode=="vi";
        if(vi&&viWorkspace==null){viWorkspace=new PracticeWorkspace();root.Children.Add(viWorkspace);Grid.SetRow(viWorkspace,1);Grid.SetColumnSpan(viWorkspace,2);}
        if(show&&ftpWorkspace==null)
        {
            ftpWorkspace=new FtpWorkspace(this,store,ftpSession,ftpQueue,()=>sessions.Where(p=>p.Host.HasProcess).Select(p=>new TerminalFtpTarget(
                p.Equipment.Id+":"+p.Host.ProcessId,p.Equipment.Name+" · "+p.Equipment.Host+" ("+p.Equipment.Protocol+")",
                ()=>p.Host.HasProcess,command=>{if(!sessions.Contains(p)||!p.Host.HasProcess)throw new InvalidOperationException("선택한 터미널이 닫혔습니다.");ShowFtp(false);ShowSession(p);Dispatcher.BeginInvoke(()=>p.SetComposer(command),System.Windows.Threading.DispatcherPriority.ContextIdle);})).ToArray());
            root.Children.Add(ftpWorkspace);Grid.SetRow(ftpWorkspace,1);Grid.SetColumnSpan(ftpWorkspace,2);
        }
        ftpMode=show;viMode=vi;
        if(show||vi)focusMode=false;
        terminalRegion.Visibility=TerminalMode?Visibility.Visible:Visibility.Collapsed;
        if(ftpWorkspace!=null)ftpWorkspace.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        if(viWorkspace!=null)viWorkspace.Visibility=vi?Visibility.Visible:Visibility.Collapsed;
        if(show)ftpWorkspace?.RefreshTerminalTargets();
        UpdateModeSwitch();
        ApplyChrome();RebuildTabs();
        if(vi){viWorkspace!.FocusEditor();SetStatus("연습 · vi 편집기와 터미널 명령어를 기초부터 연습합니다. 설비·파일·네트워크에는 연결하지 않습니다.");}
        if(TerminalMode)Dispatcher.BeginInvoke(()=>{foreach(var pane in VisiblePanesForTest)pane.Host.RefreshViewport();RestoreTerminalFocus();});
    }
    internal void ShowFtpForTest(bool show)=>ShowFtp(show);
    internal void ShowViForTest(bool show)=>ShowVi(show);
    internal PracticeWorkspace? ViWorkspaceForTest=>viWorkspace;
    internal bool TerminalModeForTest=>TerminalMode;
    internal FtpSession FtpSessionForTest=>ftpSession;
    internal FtpTransferQueue FtpQueueForTest=>ftpQueue;
    internal FtpWorkspace? FtpWorkspaceForTest=>ftpWorkspace;
}
