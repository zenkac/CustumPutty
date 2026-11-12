using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

public sealed partial class FtpWorkspace
{
    private readonly Grid filePaneHost=new();
    private readonly ExplorerColumns localColumns=new(),remoteColumns=new();
    private FrameworkElement sourcePane=null!,serverPane=null!;
    private GridSplitter paneSplitter=null!;
    private LocalFolderTree localFolderTree=null!;
    private Button swapPanesButton=null!,singlePaneButton=null!,localTreeButton=null!,remoteTreeButton=null!;
    private bool activeRemote=true;
    private string appliedLayout="";

    private void AddExplorerActions(Panel toolbar)
    {
        swapPanesButton=Ui.ToolButton("⇄ 좌우 바꾸기",()=>{store.Settings.FtpSwapPanes=!store.Settings.FtpSwapPanes;ApplyExplorerLayout();SaveStore();},"내 PC와 FTP 서버의 위치 바꾸기 · 연결과 경로는 유지됩니다");
        swapPanesButton.Name="FtpSwapPanes";
        singlePaneButton=Ui.ToolButton("한 창 보기",()=>{store.Settings.FtpSinglePane=!store.Settings.FtpSinglePane;ApplyExplorerLayout();SaveStore();},"선택한 위치를 넓게 보기 · 폴더 영역에서 내 PC / 서버 선택");
        singlePaneButton.Name="FtpSinglePane";toolbar.Children.Add(swapPanesButton);toolbar.Children.Add(singlePaneButton);
    }
    private FrameworkElement BuildTreeChoice()
    {
        var row=new Grid{Margin=new Thickness(0,11,0,5)};
        row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        localTreeButton=Ui.ToolButton("내 PC",()=>ActivatePane(false),"내 PC 폴더 탐색");localTreeButton.Name="FtpLocalTreeChoice";
        remoteTreeButton=Ui.ToolButton("FTP 서버",()=>ActivatePane(true),"연결된 서버 폴더 탐색");remoteTreeButton.Name="FtpRemoteTreeChoice";
        row.Children.Add(localTreeButton);row.Children.Add(remoteTreeButton);SetColumn(remoteTreeButton,1);return row;
    }
    private void ActivatePane(bool remote)
    {
        activeRemote=remote;ApplyExplorerLayout();UpdateButtons();
    }
    private void ApplyExplorerLayout()
    {
        if(sourcePane==null||serverPane==null)return;
        bool swapped=store.Settings.FtpSwapPanes,single=store.Settings.FtpSinglePane;
        SetColumn(sourcePane,swapped?2:0);SetColumn(serverPane,swapped?0:2);
        sourcePane.Visibility=single&&activeRemote?Visibility.Collapsed:Visibility.Visible;
        serverPane.Visibility=single&&!activeRemote?Visibility.Collapsed:Visibility.Visible;
        int visibleColumn=activeRemote?(swapped?0:2):(swapped?2:0);
        string layout=$"{swapped}|{single}|{single&&activeRemote}";
        if(appliedLayout!=layout)for(int i=0;i<3;i++)
        {
            var column=filePaneHost.ColumnDefinitions[i];column.MinWidth=single||i==1?0:150;
            column.Width=i==1?new GridLength(single?0:5):single&&i!=visibleColumn?new GridLength(0):new GridLength(1,GridUnitType.Star);
        }
        appliedLayout=layout;
        paneSplitter.Visibility=single?Visibility.Collapsed:Visibility.Visible;
        folderTree.Visibility=activeRemote?Visibility.Visible:Visibility.Collapsed;
        localFolderTree.Visibility=activeRemote?Visibility.Collapsed:Visibility.Visible;
        localTreeButton.Background=Ui.Brush(activeRemote?"#17212D":"#203A3C");remoteTreeButton.Background=Ui.Brush(activeRemote?"#203A3C":"#17212D");
        singlePaneButton.Content=Ui.Text(single?"두 창 보기":"한 창 보기",12,"#DCE5F0",true);
        singlePaneButton.Background=Ui.Brush(single?"#203A3C":"#17212D");
        UpdateTransferLabels();
    }
    private void UpdateTransferLabels()
    {
        bool swapped=store.Settings.FtpSwapPanes;
        uploadButton.Content=Ui.Text(terminalMode?"설비 → 서버 명령":swapped?"← 업로드":"업로드 →",12,"#DCE5F0",true);
        downloadButton.Content=Ui.Text(terminalMode?"서버 → 설비 준비":swapped?"다운로드 →":"← 다운로드",12,"#DCE5F0",true);
        folderButton.ToolTip=activeRemote?"현재 서버 경로에 폴더 만들기":"현재 내 PC 경로에 폴더 만들기";
    }
    private FrameworkElement BuildExplorerHeader(bool remote)
    {
        var columns=remote?remoteColumns:localColumns;
        var header=columns.Build(remote?"FtpRemote":"FtpLocal",sort=>
        {
            if(columns.Sort==sort)columns.Descending=!columns.Descending;else{columns.Sort=sort;columns.Descending=false;}
            columns.UpdateLabels();RenderFiles(remote);
        });
        columns.Attach(remote?remoteFiles:localFiles);return header;
    }
    private IEnumerable<T> SortEntries<T>(IEnumerable<T> entries,bool remote,Func<T,bool> folder,Func<T,string> name,Func<T,long?> size,Func<T,DateTimeOffset?> modified)
    {
        var columns=remote?remoteColumns:localColumns;var result=entries.OrderByDescending(folder);
        result=columns.Sort switch
        {
            FileSort.Size=>columns.Descending?result.ThenByDescending(size):result.ThenBy(size),
            FileSort.Modified=>columns.Descending?result.ThenByDescending(modified):result.ThenBy(modified),
            FileSort.Type=>columns.Descending?result.ThenByDescending(x=>ShellFileIcons.TypeName(name(x),folder(x)),StringComparer.OrdinalIgnoreCase):result.ThenBy(x=>ShellFileIcons.TypeName(name(x),folder(x)),StringComparer.OrdinalIgnoreCase),
            _=>columns.Descending?result.ThenByDescending(name,StringComparer.OrdinalIgnoreCase):result.ThenBy(name,StringComparer.OrdinalIgnoreCase)
        };
        return result.ThenBy(name,StringComparer.OrdinalIgnoreCase);
    }
}
