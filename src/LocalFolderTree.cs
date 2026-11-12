using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

internal sealed class LocalFolderTree:TreeView,IDisposable
{
    private sealed class NodeData(string path){internal string Path=path;internal bool Loaded,Loading;}
    private readonly Func<string,Task> navigate;private readonly CancellationTokenSource lifetime=new();
    private bool updating,disposed;private string current="";
    internal LocalFolderTree(Style style,Func<string,Task> navigate)
    {
        this.navigate=navigate;ItemContainerStyle=style;Background=Ui.Brush("#17212D");Foreground=Ui.Brush("#DCE5F0");BorderThickness=new Thickness(0);Padding=new Thickness(0,4,0,4);
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var folder in new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.UserProfile})
        {
            string path=Environment.GetFolderPath(folder);if(path.Length>0&&roots.Add(path))Items.Add(Node(folder==Environment.SpecialFolder.DesktopDirectory?"바탕 화면":folder==Environment.SpecialFolder.MyDocuments?"문서":"사용자 폴더",path));
        }
        foreach(string drive in Directory.GetLogicalDrives())if(roots.Add(drive))Items.Add(Node(drive,drive));
        SelectedItemChanged+=async (_,_)=>{if(!updating&&!disposed&&SelectedItem is TreeViewItem{Tag:NodeData data}&&!Same(data.Path,current))await navigate(data.Path);};
    }
    internal void Sync(string path,IEnumerable<string> children)
    {
        if(disposed||path.Length==0)return;current=Path.GetFullPath(path);updating=true;
        try
        {
            var node=Items.OfType<TreeViewItem>().Where(x=>x.Tag is NodeData data&&Inside(current,data.Path)).OrderByDescending(x=>((NodeData)x.Tag).Path.Length).FirstOrDefault();
            if(node==null){string root=Path.GetPathRoot(current)!;node=Node(root,root);Items.Add(node);}
            string position=((NodeData)node.Tag).Path;
            string relative=Path.GetRelativePath(position,current);
            foreach(string part in relative=="."?[]:relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
            {
                position=Path.Combine(position,part);RemovePlaceholders(node);
                var next=node.Items.OfType<TreeViewItem>().FirstOrDefault(x=>x.Tag is NodeData data&&Same(data.Path,position));
                if(next==null){next=Node(part,position);node.Items.Add(next);}node.IsExpanded=true;node=next;
            }
            Populate(node,children);((NodeData)node.Tag).Loaded=true;node.IsExpanded=true;node.IsSelected=true;
        }
        finally{updating=false;}
    }
    private static bool Same(string first,string second)=>string.Equals(Path.TrimEndingDirectorySeparator(first),Path.TrimEndingDirectorySeparator(second),StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path,string root)=>Same(path,root)||path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private TreeViewItem Node(string name,string path)
    {
        var header=new DockPanel();var icon=ShellFileIcons.Create(name,true,16);icon.Margin=new Thickness(0,0,6,0);DockPanel.SetDock(icon,Dock.Left);header.Children.Add(icon);
        var text=Ui.Text(name,11,"#AAB8C8");text.TextWrapping=TextWrapping.NoWrap;text.TextTrimming=TextTrimming.CharacterEllipsis;header.Children.Add(text);
        var node=new TreeViewItem{Header=header,Tag=new NodeData(path),ToolTip=path,Style=ItemContainerStyle,ItemContainerStyle=ItemContainerStyle};node.Items.Add(new TreeViewItem{Header="…",IsEnabled=false});
        node.Expanded+=async (_,e)=>{if(e.OriginalSource==node&&!updating)await LoadAsync(node);};return node;
    }
    private static void RemovePlaceholders(TreeViewItem node){foreach(var child in node.Items.OfType<TreeViewItem>().Where(x=>x.Tag is not NodeData).ToArray())node.Items.Remove(child);}
    private void Populate(TreeViewItem node,IEnumerable<string> paths)
    {
        var existing=node.Items.OfType<TreeViewItem>().Where(x=>x.Tag is NodeData).ToDictionary(x=>((NodeData)x.Tag).Path,StringComparer.OrdinalIgnoreCase);node.Items.Clear();
        foreach(string path in paths.OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))node.Items.Add(existing.TryGetValue(path,out var child)?child:Node(Path.GetFileName(path),path));
    }
    private async Task LoadAsync(TreeViewItem node)
    {
        if(disposed||node.Tag is not NodeData data||data.Loaded||data.Loading)return;data.Loading=true;
        try
        {
            var paths=await Task.Run(()=>Directory.EnumerateDirectories(data.Path).Take(2000).ToArray(),lifetime.Token);if(disposed)return;
            bool previous=updating;updating=true;try{Populate(node,paths);data.Loaded=true;}finally{updating=previous;}
        }
        catch(OperationCanceledException){}
        catch(Exception error){node.ToolTip=data.Path+"\n"+error.Message;}
        finally{data.Loading=false;}
    }
    public void Dispose(){disposed=true;lifetime.Cancel();Items.Clear();}
}
