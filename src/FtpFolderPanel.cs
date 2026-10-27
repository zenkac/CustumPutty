using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace EquipLink;

internal sealed class FtpFolderTree : TreeView,IDisposable
{
    private readonly FtpSession session;private readonly Func<string,Task> navigate;private bool updating;private string stamp="",connection="";
    private sealed class FolderNode(string path,bool loaded=false){public string Path=path;public bool Loaded=loaded;public bool Loading;}
    public FtpFolderTree(FtpSession session,Func<string,Task> navigate)
    {
        this.session=session;this.navigate=navigate;Background=Ui.Brush("#17212D");Foreground=Ui.Brush("#DCE5F0");BorderThickness=new Thickness(0);Padding=new Thickness(0,4,0,4);
        ItemContainerStyle=(Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TreeViewItem">
              <Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TreeViewItem"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="17"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                <ToggleButton x:Name="expander" Width="17" Height="28" Focusable="False" IsChecked="{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent}}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><Path x:Name="arrow" Data="M 0,0 L 4,4 L 0,8" Stroke="{DynamicResource Muted}" StrokeThickness="1.2" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin=".5,.5"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="arrow" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton>
                <Border x:Name="header" Grid.Column="1" Background="Transparent" CornerRadius="4" Padding="5,6" Margin="0,1"><ContentPresenter ContentSource="Header" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/></Border><ItemsPresenter x:Name="children" Grid.Row="1" Grid.Column="1"/>
              </Grid><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="False"><Setter TargetName="children" Property="Visibility" Value="Collapsed"/></Trigger><Trigger Property="HasItems" Value="False"><Setter TargetName="expander" Property="Visibility" Value="Hidden"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="header" Property="Background" Value="{DynamicResource ListSelected}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="header" Property="BorderBrush" Value="{DynamicResource Accent}"/><Setter TargetName="header" Property="BorderThickness" Value="1"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        SelectedItemChanged+=async (_,_)=>{if(!updating&&SelectedItem is TreeViewItem{Tag:FolderNode node}&&session.IsConnected&&node.Path!=session.CurrentPath)await navigate(node.Path);};
    }
    public void Refresh()
    {
        if(session.IsBusy)return;
        var next=session.IsConnected?$"{session.Site!.Id}|{session.CurrentPath}|{string.Join('|',session.Entries.Where(x=>x.IsDirectory).Select(x=>x.Name))}":"disconnected";
        if(next==stamp)return;stamp=next;updating=true;var ancestors=new List<TreeViewItem>();
        try
        {
            if(!session.IsConnected){Items.Clear();connection="";return;}
            var key=$"{session.Site!.Id}|{session.Site.Host}|{session.Site.Port}";if(key!=connection){connection=key;Items.Clear();}
            string path="/";if(Items.Count==0)Items.Add(Node("/",path,false));TreeViewItem current=(TreeViewItem)Items[0];
            foreach(string part in session.CurrentPath.Split('/',StringSplitOptions.RemoveEmptyEntries))
            {
                ancestors.Add(current);path=FtpPaths.Join(path,part);var child=current.Items.OfType<TreeViewItem>().FirstOrDefault(x=>x.Tag is FolderNode data&&data.Path==path);
                if(child==null){foreach(var placeholder in current.Items.OfType<TreeViewItem>().Where(x=>x.Tag is not FolderNode).ToArray())current.Items.Remove(placeholder);child=Node(part,path,false);current.Items.Add(child);}current.IsExpanded=true;current=child;
            }
            Populate(current,session.Entries.Where(x=>x.IsDirectory&&!x.IsLink));
            ((FolderNode)current.Tag).Loaded=true;current.IsSelected=true;current.IsExpanded=true;
        }
        finally{updating=false;}
        _=LoadAncestorsAsync(ancestors);
    }
    private async Task LoadAncestorsAsync(IEnumerable<TreeViewItem> nodes){foreach(var node in nodes)if(node.Tag is FolderNode{Loaded:false,Loading:false})await LoadChildrenAsync(node);}
    private void Populate(TreeViewItem node,IEnumerable<FtpEntry> entries)
    {
        var existing=node.Items.OfType<TreeViewItem>().Where(x=>x.Tag is FolderNode).ToDictionary(x=>((FolderNode)x.Tag).Path);node.Items.Clear();
        foreach(var entry in entries.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))node.Items.Add(existing.TryGetValue(entry.FullPath,out var child)?child:Node(entry.Name,entry.FullPath,false));
    }
    private async Task LoadChildrenAsync(TreeViewItem node)
    {
        if(node.Tag is not FolderNode data||data.Loaded||data.Loading||!session.IsConnected)return;
        data.Loading=true;var key=connection;
        try
        {
            var folders=await session.ListFoldersAsync(data.Path);if(connection!=key||!session.IsConnected)return;
            bool previous=updating;updating=true;try{Populate(node,folders);data.Loaded=true;}finally{updating=previous;}
        }
        catch(OperationCanceledException){}
        catch(Exception error){node.ToolTip=data.Path+"\n"+error.Message;}
        finally{data.Loading=false;}
    }
    private TreeViewItem Node(string name,string path,bool loaded)
    {
        var text=Ui.Text(name,11,"#AAB8C8");text.TextWrapping=TextWrapping.NoWrap;text.TextTrimming=TextTrimming.CharacterEllipsis;
        var glyph=Ui.Glyph("\uE8B7",12);glyph.Foreground=Ui.Brush("#5DD6BE");glyph.Margin=new Thickness(0,0,6,0);
        var header=new DockPanel();DockPanel.SetDock(glyph,Dock.Left);header.Children.Add(glyph);header.Children.Add(text);
        // ItemContainerStyle on TreeView only styles its immediate children.
        // Apply it to every explicit node so nested selections and expanders
        // retain the same palette instead of using the native white template.
        var node=new TreeViewItem{Header=header,Tag=new FolderNode(path,loaded),ToolTip=path,Style=ItemContainerStyle,ItemContainerStyle=ItemContainerStyle};
        if(!loaded)node.Items.Add(new TreeViewItem{Header="…",IsEnabled=false});
        node.Expanded+=async (_,e)=>
        {
            if(e.OriginalSource!=node||updating)return;await LoadChildrenAsync(node);
        };
        return node;
    }
    public void Dispose(){Items.Clear();}
}
