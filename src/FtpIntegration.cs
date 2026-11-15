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
    private Button ftpModeButton=null!,terminalModeButton=null!;
    private void UpdateModeSwitch()
    {
        foreach(var (button,selected) in new[]{(terminalModeButton,!ftpMode),(ftpModeButton,ftpMode)})
        {
            button.Background=selected?Ui.Brush("#25413F"):Brushes.Transparent;
            if(button.Content is TextBlock label){label.Foreground=Ui.Brush(selected?"#DCE5F0":"#8797AA");}
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?"선택됨":"");
        }
    }
    private bool ftpMode;

    private FrameworkElement WithFolderSidebar(FrameworkElement library)
    {
        var panel=new Grid();panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        sftpBrowserPanel=new SftpBrowserPanel(this,store,()=>showingHome?null:active){Visibility=Visibility.Collapsed};
        panel.Children.Add(library);Grid.SetRow(library,1);panel.Children.Add(sftpBrowserPanel);Grid.SetRow(sftpBrowserPanel,1);
        Button devices=null!,folders=null!;
        void Select(bool folder)
        {
            library.Visibility=folder?Visibility.Collapsed:Visibility.Visible;sftpBrowserPanel.Visibility=folder?Visibility.Visible:Visibility.Collapsed;
            devices.Background=Ui.Brush(folder?"#17212D":"#203A3C");folders.Background=Ui.Brush(folder?"#203A3C":"#17212D");
            devices.BorderBrush=Ui.Brush(folder?"#2A394A":"#4A8D81");folders.BorderBrush=Ui.Brush(folder?"#4A8D81":"#2A394A");
            if(folder)sftpBrowserPanel.Refresh();
        }
        devices=Ui.ToolButton("설비",()=>Select(false),"저장한 설비 목록");folders=Ui.ToolButton("SSH Browser",()=>Select(true),"SSH 연결의 SFTP 파일 탐색기");
        var tabs=Ui.Columns(devices,folders);tabs.Margin=new Thickness(10,10,6,0);panel.Children.Add(tabs);Select(false);return panel;
    }
    private async Task ConnectFtpSafeAsync(FtpSite site,string password)
    {
        try{await ftpSession.ConnectAsync(site,password);SetStatus(ftpSession.IsConnected?"FTP 연결됨 · "+site.Host+" · "+ftpSession.CurrentPath:"FTP 연결 취소됨");}
        catch(OperationCanceledException){SetStatus("FTP 연결 취소됨");}
        catch(Exception error){SetStatus("FTP 접속 실패 · "+error.Message);Ui.Error(error);}
    }
    private void ShowFtp(bool show)
    {
        if(show&&ftpWorkspace==null)
        {
            ftpWorkspace=new FtpWorkspace(this,store,ftpSession,ftpQueue,()=>sessions.Where(p=>p.Host.HasProcess).Select(p=>new TerminalFtpTarget(
                p.Equipment.Id+":"+p.Host.ProcessId,p.Equipment.Name+" · "+p.Equipment.Host+" ("+p.Equipment.Protocol+")",
                ()=>p.Host.HasProcess,command=>{if(!sessions.Contains(p)||!p.Host.HasProcess)throw new InvalidOperationException("선택한 터미널이 닫혔습니다.");ShowFtp(false);ShowSession(p);Dispatcher.BeginInvoke(()=>p.SetComposer(command),System.Windows.Threading.DispatcherPriority.ContextIdle);})).ToArray());
            root.Children.Add(ftpWorkspace);Grid.SetRow(ftpWorkspace,1);Grid.SetColumnSpan(ftpWorkspace,2);
        }
        ftpMode=show;
        if(show)focusMode=false;
        terminalRegion.Visibility=show?Visibility.Collapsed:Visibility.Visible;
        if(ftpWorkspace!=null)ftpWorkspace.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        if(show)ftpWorkspace?.RefreshTerminalTargets();
        UpdateModeSwitch();
        ApplyChrome();RebuildTabs();
        if(!show)Dispatcher.BeginInvoke(()=>{foreach(var pane in VisiblePanesForTest)pane.Host.RefreshViewport();RestoreTerminalFocus();});
    }
    internal void ShowFtpForTest(bool show)=>ShowFtp(show);
    internal FtpSession FtpSessionForTest=>ftpSession;
    internal FtpTransferQueue FtpQueueForTest=>ftpQueue;
    internal FtpWorkspace? FtpWorkspaceForTest=>ftpWorkspace;
}
