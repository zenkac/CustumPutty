using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EquipLink;

internal static class FtpFileToolsTests
{
    internal static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException("Owned fixtures only.");
        var report=new StringBuilder();
        void Check(string label,bool ok){report.AppendLine((ok?"PASS: ":"FAIL: ")+label);File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-tools-test-report.txt"),report.ToString());if(!ok)throw new InvalidOperationException(label);}
        void Reject(string label,Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(label,rejected);}
        await using var server=new FtpTestServer{RequirePassword=true};
        foreach(string name in new[]{"alpha.log","BETA.LOG","보고서.csv","keep.txt","folder/hidden.log"})server.SetFile("/workspace/"+name,[1,2,3]);
        string local=Path.Combine(AppPaths.Root,"local");Directory.CreateDirectory(local);
        foreach(string name in new[]{"alpha.log","BETA.LOG","보고서.csv","keep.txt"})File.WriteAllText(Path.Combine(local,name),"owned fixture");
        Directory.CreateDirectory(Path.Combine(local,"folder"));File.WriteAllText(Path.Combine(local,"folder","hidden.log"),"nested fixture");
        var site=server.Site("/workspace");var store=new AppStore{Settings=new(){AutoStartX=false},FtpSites=[site]};
        using var session=new FtpSession(store);using var queue=new FtpTransferQueue(store);
        var prepared=new List<string>();bool targetOpen=true;
        var terminal=new TerminalFtpTarget("fixture-terminal","설비1 · 경유 설비2 작업",()=>targetOpen,prepared.Add);
        var owner=new Window{Title="EquipLink FTP tools · local fixture",Width=1320,Height=850,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false,Background=Ui.Brush("#111923")};
        using var workspace=new FtpWorkspace(owner,store,session,queue,()=>targetOpen?[terminal]:[]);owner.Content=workspace;
        try
        {
            await workspace.NavigateLocalForTestAsync(local);owner.Show();await session.ConnectAsync(site,server.Password);await Settle(owner);
            var remote=Find<ListBox>(workspace,"FtpRemoteFiles");var pc=Find<ListBox>(workspace,"FtpLocalFiles");
            var search=Find<TextBox>(workspace,"FtpRemoteSearch");var pcSearch=Find<TextBox>(workspace,"FtpLocalSearch");
            remote.SelectedItem=Items(remote).Single(x=>Name(x)=="keep.txt");search.Text=".LOG";pcSearch.Text=".log";await Settle(owner);
            Check("Current-directory search is case insensitive and never includes nested files",Items(remote).Select(Name).Order().SequenceEqual(new[]{"BETA.LOG","alpha.log"}.Order())&&Items(pc).Count()==2);
            Check("Filtering removes hidden items from selection and delete is disabled",remote.SelectedItems.Count==0&&!Find<Button>(workspace,"FtpRemoteDelete").IsEnabled);
            remote.SelectAll();Check("Filtered multi-select contains only matching visible files",remote.SelectedItems.Count==2&&Find<TextBlock>(workspace,"FtpRemoteCount").Text.Contains("선택 2개"));
            queue.ConfirmOverwrite=(_,_)=>Task.FromResult(FtpOverwriteDecision.Skip);
            Find<Button>(workspace,"FtpDownload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await queue.WaitForIdleAsync();
            Check("A filtered transfer queues only the two visible selected files",queue.Items.Count==2&&queue.Items.All(x=>x.RemotePath.EndsWith(".log",StringComparison.OrdinalIgnoreCase)));
            search.Text="보고";pcSearch.Text="보고";Check("Korean filename search works on both panes",Items(remote).Single().Tag is FtpEntry{Name:"보고서.csv"}&&Items(pc).Count()==1);
            search.Text="not present";Check("Empty results use an inert placeholder",Items(remote).Count()==0&&remote.Items.Count==1&&((ListBoxItem)remote.Items[0]).Tag==null);
            search.Text=".log";await session.NavigateAsync("/workspace/folder");await Settle(owner);
            Check("Navigating to another server directory clears its search",search.Text==""&&Items(remote).Single().Tag is FtpEntry{Name:"hidden.log"});
            await session.NavigateAsync("/workspace");pcSearch.Clear();await Settle(owner);

            foreach(bool icons in new[]{true,false})
            {
                Click(workspace,icons?"FtpIconView":"FtpListView");await Settle(owner);string view=icons?"Icon":"List";
                Check(view+" keeps native Ctrl/Shift extended selection",remote.SelectionMode==SelectionMode.Extended&&pc.SelectionMode==SelectionMode.Extended);
                workspace.SelectRectangleForTest(true,new Point(1,1),new Point(remote.ActualWidth-20,remote.ActualHeight-20),ModifierKeys.None);
                Check(view+" drag rectangle selects several server items",remote.SelectedItems.Count==5);
                workspace.SelectRectangleForTest(false,new Point(1,1),new Point(pc.ActualWidth-20,pc.ActualHeight-20),ModifierKeys.None);
                Check(view+" drag rectangle selects several PC items",pc.SelectedItems.Count==5);
                var chosen=Items(remote).First(x=>Name(x)=="alpha.log");Rect bounds=chosen.TransformToAncestor(remote).TransformBounds(new Rect(chosen.RenderSize));
                var from=new Point(bounds.Left+3,bounds.Top+3);var to=new Point(bounds.Right-3,bounds.Bottom-3);
                workspace.SelectRectangleForTest(true,from,to,ModifierKeys.Control);
                Check(view+" Ctrl drag toggles hit items while retaining the rest",!chosen.IsSelected&&remote.SelectedItems.Count==4);
                workspace.SelectRectangleForTest(true,from,to,ModifierKeys.Shift);
                Check(view+" Shift drag adds to the previous selection",chosen.IsSelected&&remote.SelectedItems.Count==5);
                workspace.SelectRectangleForTest(true,from,to,ModifierKeys.None);
                Check(view+" an ordinary drag replaces selection",chosen.IsSelected&&remote.SelectedItems.Count==1);
                Capture(owner,"ftp-search-"+(icons?"icons":"list")+".png");
            }

            search.Text=".log";remote.SelectAll();int beforeDelete=server.CommandCount("DELE");
            await Dialog(()=>workspace.DeleteForTestAsync(true),dialog=>
            {
                Check("Delete popup explicitly warns permanent removal and defaults to Cancel",Find<TextBlock>(dialog,"FtpDeleteWarning").Text.Contains("복구할 수 없습니다")&&Find<Button>(dialog,"FtpDeleteCancel").IsDefault&&!Find<Button>(dialog,"FtpDeleteConfirm").IsDefault);
                Check("Delete popup is compact and dark",dialog.ActualWidth<=500&&dialog.ActualHeight<480&&dialog.Background.ToString()==Ui.Brush("#111923").ToString());
                Capture(dialog,"ftp-delete.png");Click(dialog,"FtpDeleteCancel");
            });
            Check("Cancel sends no delete command and preserves every file",server.CommandCount("DELE")==beforeDelete&&server.FileExists("/workspace/alpha.log")&&server.FileExists("/workspace/BETA.LOG"));
            await Dialog(()=>workspace.DeleteForTestAsync(true),d=>Click(d,"FtpDeleteConfirm"));await Settle(owner);
            Check("Confirmed filtered deletion removes only selected files",!server.FileExists("/workspace/alpha.log")&&!server.FileExists("/workspace/BETA.LOG")&&server.FileExists("/workspace/keep.txt")&&server.FileExists("/workspace/folder/hidden.log"));
            Check("The current filtered listing refreshes after deletion",Items(remote).Count()==0&&!workspace.IsDeletingForTest);
            search.Clear();remote.SelectedItem=Items(remote).Single(x=>Name(x)=="folder");
            await Dialog(()=>workspace.DeleteForTestAsync(true),d=>Click(d,"FtpDeleteConfirm"));
            Check("Confirmed remote folder deletion removes children and then the empty directory",!server.DirectoryExists("/workspace/folder")&&server.CommandCount("RMD")>0&&server.FileExists("/workspace/keep.txt"));
            pcSearch.Text=".log";pc.SelectAll();
            await Dialog(()=>workspace.DeleteForTestAsync(false),d=>Click(d,"FtpDeleteCancel"));Check("Cancelling PC deletion keeps local files",File.Exists(Path.Combine(local,"alpha.log")));
            await Dialog(()=>workspace.DeleteForTestAsync(false),d=>Click(d,"FtpDeleteConfirm"));
            Check("Confirmed PC deletion honors the filtered selection",!File.Exists(Path.Combine(local,"alpha.log"))&&!File.Exists(Path.Combine(local,"BETA.LOG"))&&File.Exists(Path.Combine(local,"keep.txt")));
            pcSearch.Clear();pc.SelectedItem=Items(pc).Single(x=>x.ToolTip?.ToString()==Path.Combine(local,"folder"));
            await Dialog(()=>workspace.DeleteForTestAsync(false),d=>Click(d,"FtpDeleteConfirm"));Check("PC directory deletion removes only the approved subtree",!Directory.Exists(Path.Combine(local,"folder"))&&File.Exists(Path.Combine(local,"keep.txt")));

            // Destructive-operation boundaries exercised against owned fixture data only.
            await using(var client=await FtpClient.ConnectAsync(site,server.Password))
            {
                var keep=(await client.ListAsync("/workspace")).Single(x=>x.Name=="keep.txt");int deleted=0;
                bool rejected=false;try{await FtpFileDeletion.DeleteRemoteAsync(client,"/",[keep],()=>deleted++,default);}catch(IOException){rejected=true;}
                Check("Remote deletion rejects an entry outside the displayed folder",rejected&&deleted==0&&server.FileExists(keep.FullPath));
                server.InjectedListing="type=file;size=3; keep.txt\r\ntype=file;size=3; ../escape\r\n";
                rejected=false;try{await FtpFileDeletion.DeleteRemoteAsync(client,"/workspace",[keep],()=>deleted++,default);}catch(IOException){rejected=true;}
                server.InjectedListing=null;Check("Incomplete remote listings stop deletion before any mutation",rejected&&deleted==0&&server.FileExists(keep.FullPath));
                rejected=false;try{await client.RemoveDirectoryAsync("/");}catch(ArgumentException){rejected=true;}catch(IOException){rejected=true;}
                Check("The FTP root cannot be removed",rejected&&server.DirectoryExists("/"));
            }
            string outside=Path.Combine(AppPaths.Root,"outside.txt");File.WriteAllText(outside,"preserve");bool localRejected=false;
            try{FtpFileDeletion.DeleteLocal(local,[outside],()=>{},default);}catch(IOException){localRejected=true;}
            Check("PC deletion rejects paths outside the displayed folder",localRejected&&File.Exists(outside));
            await using(var linkServer=new FtpTestServer())
            {
                linkServer.SetFile("/keep.txt",[4,5,6]);linkServer.InjectedListing="type=file;size=3; keep.txt\r\ntype=OS.unix=slink; outside-link\r\n";
                await using var client=await FtpClient.ConnectAsync(linkServer.Site(),linkServer.Password);
                bool rejected=false;try{await FtpFileDeletion.DeleteRemoteAsync(client,"/",(await client.ListAsync("/")).ToArray(),()=>{},default);}catch(IOException){rejected=true;}
                Check("A selected remote symlink aborts the entire delete preflight",rejected&&linkServer.FileExists("/keep.txt")&&linkServer.CommandCount("DELE")==0);
            }
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();bool stopped=false;try{FtpFileDeletion.DeleteLocal(local,[Path.Combine(local,"keep.txt")],()=>{},cancelled.Token);}catch(OperationCanceledException){stopped=true;}
                Check("Cancellation stops deletion before touching local files",stopped&&File.Exists(Path.Combine(local,"keep.txt")));
            }

            server.SetFile("/workspace/slow.bin",new byte[65536]);server.TransferDelayMs=40;server.TransferChunkSize=256;
            remote.SelectedItem=Items(remote).Single(x=>Name(x)=="keep.txt");int deletesBefore=server.CommandCount("DELE"),prompts=0;
            queue.EnqueueDownload(site,server.Password,"/workspace/slow.bin",Path.Combine(local,"slow.bin"),65536);await Settle(owner);
            FormDialog.TestLoaded=d=>{prompts++;d.DialogResult=false;};await workspace.DeleteForTestAsync(true);FormDialog.TestLoaded=null;
            Check("Active transfers block deletion without showing a misleading popup",queue.HasPending&&prompts==0&&server.CommandCount("DELE")==deletesBefore&&!Find<Button>(workspace,"FtpRemoteDelete").IsEnabled);
            queue.CancelAll();await queue.WaitForIdleAsync();server.TransferDelayMs=0;

            var source=Find<ComboBox>(workspace,"FtpSourceChoice");source.SelectedIndex=1;await Settle(owner);
            Check("An open terminal can be selected without opening another FTP connection",!pc.IsVisible&&Find<TerminalFtpHelper>(workspace,"TerminalFtpHelper").IsVisible&&prepared.Count==0);
            int connectionCommands=server.CommandCount("USER"),queueCount=queue.Items.Count;
            Click(workspace,"TerminalFtpOpen");
            Check("FTP helper prepares an explicit connect command without sending it",prepared.Count==1&&prepared[0]==$"ftp -i '127.0.0.1' {site.Port}"&&server.CommandCount("USER")==connectionCommands);
            Find<TextBox>(workspace,"TerminalFtpEquipmentPath").Text="/home/test results";Click(workspace,"TerminalFtpLcd");
            Check("Equipment path uses lcd with a quoted space-containing directory",prepared.Last()=="lcd \"/home/test results\"");
            remote.UnselectAll();foreach(var item in Items(remote).Where(x=>Name(x) is "keep.txt" or "보고서.csv"))item.IsSelected=true;
            Click(workspace,"FtpDownload");
            Check("Selecting server files prepares get/mget without a PC download or terminal send",Find<TextBox>(workspace,"TerminalFtpFiles").Text.Contains("보고서.csv")&&queue.Items.Count==queueCount&&prepared.Count==2);
            Click(workspace,"TerminalFtpCd");Check("The helper prepares the actual server folder separately before get",prepared.Last()=="cd \"/workspace\"");
            var helper=Find<TerminalFtpHelper>(workspace,"TerminalFtpHelper");int beforePrepared=prepared.Count;
            await Dialog(()=>{helper.PrepareTransfer(false);return Task.CompletedTask;},d=>Click(d,"TerminalFtpCancel"));
            Check("Cancelling a terminal transfer does not prepare or send a command",prepared.Count==beforePrepared);
            await Dialog(()=>{helper.PrepareTransfer(false);return Task.CompletedTask;},d=>Click(d,"TerminalFtpConfirm"));
            Check("Confirmed terminal transfer prepares mget but never enters the GUI queue",prepared.Last().StartsWith("mget ")&&prepared.Last().Contains("\"보고서.csv\"")&&queue.Items.Count==queueCount&&server.CommandCount("USER")==connectionCommands);
            Find<TextBox>(workspace,"TerminalFtpFiles").Text="*.log";
            await Dialog(()=>{helper.PrepareTransfer(true);return Task.CompletedTask;},d=>Click(d,"TerminalFtpConfirm"));
            Check("A wildcard upload prepares mput for the equipment FTP client",prepared.Last()=="mput \"*.log\"");
            Capture(owner,"terminal-ftp-helper.png");
            targetOpen=false;workspace.RefreshTerminalTargets();Check("Closing the selected terminal returns the source to PC",source.SelectedIndex==0&&pc.IsVisible&&source.Items.Count==1);
            Check("Single literal files generate put and get",TerminalFtpCommands.Transfer(true,"a b.txt")=="put \"a b.txt\""&&TerminalFtpCommands.Transfer(false,"a.txt")=="get \"a.txt\"");
            Check("Default FTP port is omitted from the connect command",TerminalFtpCommands.Open("192.0.2.8","21")=="ftp -i '192.0.2.8'");
            foreach(string invalid in new[]{"|cmd","a;rm","a\"b","a\rb","a\\b","$(cmd)","../a","!cmd","-option"})Reject("Unsafe transfer name rejected: "+invalid.Replace("\r","CR"),()=>TerminalFtpCommands.Transfer(false,invalid));
            Reject("Newlines cannot become another FTP directory command",()=>TerminalFtpCommands.Directory("cd","/data\n!command"));
            Reject("Selected literal wildcard names cannot expand into extra downloads",()=>TerminalFtpCommands.LiteralName("*.txt"));
            Reject("Host arguments cannot inject command options",()=>TerminalFtpCommands.Open("-option","21"));
            Reject("Out-of-range FTP ports are rejected",()=>TerminalFtpCommands.Open("127.0.0.1","65536"));
            Check("Fixture config stores no clear-text FTP password",!File.ReadAllText(AppPaths.Config).Contains(server.Password));
            report.AppendLine("All mutations used synthetic files below the test directory or a loopback FTP fixture. Equipment2 execution requires field validation.");File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-tools-test-report.txt"),report.ToString());
        }
        finally{FormDialog.TestLoaded=null;queue.CancelAll();await queue.WaitForIdleAsync();owner.Close();}
    }
    private static async Task Dialog(Func<Task> action,Action<FormDialog> loaded)
    {
        Exception? failure=null;int count=0;FormDialog.TestLoaded=d=>{try{count++;d.UpdateLayout();loaded(d);}catch(Exception error){failure=error;if(d.IsVisible)d.DialogResult=false;}};
        try{await action();if(failure!=null)throw failure;if(count!=1)throw new InvalidOperationException("Expected exactly one confirmation dialog.");}finally{FormDialog.TestLoaded=null;}
    }
    private static async Task Settle(Window window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);await Task.Delay(100);window.UpdateLayout();}
    private static void Capture(Window window,string filename)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom),96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(AppPaths.Root,filename));encoder.Save(output);
    }
    private static IEnumerable<ListBoxItem> Items(ListBox list)=>list.Items.OfType<ListBoxItem>().Where(x=>x.Tag!=null);
    private static string Name(ListBoxItem item)=>item.Tag is FtpEntry entry?entry.Name:Path.GetFileName(item.ToolTip?.ToString()??"");
    private static void Click(DependencyObject root,string name)=>Find<Button>(root,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root,string name)where T:FrameworkElement=>All(root).OfType<T>().Single(x=>x.Name==name);
    private static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in All(VisualTreeHelper.GetChild(root,i)))yield return item;}
}
