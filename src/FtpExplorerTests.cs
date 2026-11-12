using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EquipLink;

internal static class FtpExplorerTests
{
    internal static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException();
        var report=new StringBuilder();
        void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);report.AppendLine("PASS: "+name);File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-explorer-test-report.txt"),report.ToString());}
        await using var server=new FtpTestServer{RequirePassword=true};server.EnsureDirectory("/workspace/results");server.EnsureDirectory("/workspace/download folder/nested");
        byte[] payload=Enumerable.Range(0,131079).Select(n=>(byte)(n%251)).ToArray(),original=Encoding.UTF8.GetBytes("keep existing fixture");
        server.SetFile("/workspace/검사 결과.csv",payload);server.SetFile("/workspace/manual.txt",original);server.SetFile("/workspace/download folder/nested/한글.bin",payload);
        foreach(var pair in new[]{("order-a.zip",2),("order-b.txt",9),("order-c.csv",5)})server.SetFile("/workspace/"+pair.Item1,new byte[pair.Item2]);
        var site=server.Site("/workspace");site.Name="TEST LAB · FTP";
        var store=new AppStore{Settings=new(){AutoStartX=false,FtpIconView=false},FtpSites=[site]};
        using var session=new FtpSession(store);using var queue=new FtpTransferQueue(store);
        var owner=new Window{Title="EquipLink · FTP Explorer",Width=1440,Height=900,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false,Background=Ui.Brush("#111923")};
        using var workspace=new FtpWorkspace(owner,store,session,queue);owner.Content=workspace;
        string local=Path.Combine(AppPaths.Root,"my-files"),inbox=Path.Combine(local,"받은 파일");Directory.CreateDirectory(inbox);Directory.CreateDirectory(Path.Combine(local,"upload folder","nested"));
        File.WriteAllBytes(Path.Combine(local,"upload folder","nested","data.bin"),payload);
        File.WriteAllBytes(Path.Combine(local,"upload 한글.bin"),payload);File.WriteAllBytes(Path.Combine(local,"notes.txt"),original);
        foreach(var pair in new[]{("order-a.zip",2),("order-b.txt",9),("order-c.csv",5)}){string path=Path.Combine(local,pair.Item1);File.WriteAllBytes(path,new byte[pair.Item2]);File.SetLastWriteTime(path,new DateTime(2026,1,pair.Item2));}
        try
        {
            owner.Show();await workspace.NavigateLocalForTestAsync(local);await session.ConnectAsync(site,server.Password);await Settle(owner);
            var pc=Find<ListBox>(workspace,"FtpLocalFiles");var ftp=Find<ListBox>(workspace,"FtpRemoteFiles");
            void Click(string name)=>Find<Button>(workspace,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Select(ListBox list,params string[] names){list.UnselectAll();foreach(var name in names)Item(list,name).IsSelected=true;}
            void Drag(bool remote,ListBox target,UIElement hit,bool accept=true)
            {
                int called=0;workspace.DragLoopForTest=(_,data)=>
                {
                    called++;
                    if(remote&&Item(ftp,Names(ftp).First(n=>Item(ftp,n).IsSelected)).Tag is FtpEntry{IsDirectory:false}&&ftp.SelectedItems.Count==1)
                        Check("Native virtual-file source is understood by WPF without fetching contents",data.GetDataPresent("FileGroupDescriptorW"));
                    var over=Event(target,hit,data,DragDrop.DragOverEvent);
                    Check("Drop feedback reports "+(accept?"a copy destination":"a rejected target"),over.Effects==(accept?DragDropEffects.Copy:DragDropEffects.None));
                    if(accept)Check("Destination preview identifies transfer direction and preserves originals",workspace.DropHintForTest.Contains(remote?"다운로드":"업로드")&&workspace.DropHintForTest.Contains("원본 유지"));
                    Event(target,hit,data,DragDrop.DropEvent);
                };
                workspace.StartPaneDragForTest(remote);workspace.DragLoopForTest=null;
                Check("Drag completes once and clears destination feedback",called==1&&workspace.DropHintForTest.Length==0);
            }
            Check("Both panes accept drops and initial PC position is left",pc.AllowDrop&&ftp.AllowDrop&&pc.TranslatePoint(new(),owner).X<ftp.TranslatePoint(new(),owner).X);
            await Wait(()=>!session.IsBusy);await Settle(owner);
            Check("Lazy ancestor loading keeps the current FTP folder highlighted",Find<FtpFolderTree>(workspace,"FtpDirectoryTree").SelectedItem is TreeViewItem current&&Equals(current.ToolTip,"/workspace"));
            Select(pc,"notes.txt");Select(ftp,"manual.txt");double beforeWidth=ftp.ActualWidth;
            Click("FtpSwapPanes");await Settle(owner);
            Check("Swapping places the server left without changing paths or selections",ftp.TranslatePoint(new(),owner).X<pc.TranslatePoint(new(),owner).X&&session.CurrentPath=="/workspace"&&Find<TextBox>(workspace,"FtpLocalAddress").Text==local&&Item(pc,"notes.txt").IsSelected&&Item(ftp,"manual.txt").IsSelected);
            Check("Swapped positions persist across app settings reload",AppStore.Load().Settings.FtpSwapPanes);
            Check("Upload and download arrows follow physical pane positions",((TextBlock)Find<Button>(workspace,"FtpUpload").Content).Text.StartsWith("←")&&((TextBlock)Find<Button>(workspace,"FtpDownload").Content).Text.EndsWith("→"));
            Click("FtpRemoteTreeChoice");Click("FtpSinglePane");await Settle(owner);
            Check("Single-pane server view expands without disconnecting",!pc.IsVisible&&ftp.IsVisible&&ftp.ActualWidth>beforeWidth*1.7&&session.IsConnected);
            Click("FtpLocalTreeChoice");await Settle(owner);
            Check("Single-pane location switch exposes PC tree and PC files",pc.IsVisible&&!ftp.IsVisible&&Find<LocalFolderTree>(workspace,"FtpLocalDirectoryTree").IsVisible);
            var tree=Find<LocalFolderTree>(workspace,"FtpLocalDirectoryTree");var node=TreeNodes(tree).Single(x=>Equals(x.ToolTip,inbox));node.IsSelected=true;
            await Wait(()=>Find<TextBox>(workspace,"FtpLocalAddress").Text==inbox);Check("Clicking a PC tree folder navigates the file pane",pc.Items.OfType<ListBoxItem>().All(x=>x.Tag==null));
            await workspace.NavigateLocalForTestAsync(local);Click("FtpSinglePane");await Settle(owner);
            Check("Returning to two panes restores the saved swapped order",pc.IsVisible&&ftp.IsVisible&&ftp.TranslatePoint(new(),owner).X<pc.TranslatePoint(new(),owner).X);
            foreach(bool remote in new[]{false,true})
            {
                var list=remote?ftp:pc;string prefix=remote?"FtpRemote":"FtpLocal";var search=Find<TextBox>(workspace,prefix+"Search");search.Text="order-";Select(list,"order-b.txt");
                Click(prefix+"SizeColumn");await Settle(owner);Check(prefix+" sorts sizes ascending with selection retained",Names(list).SequenceEqual(new[]{"order-a.zip","order-c.csv","order-b.txt"})&&Item(list,"order-b.txt").IsSelected);
                Click(prefix+"SizeColumn");Check(prefix+" sorts sizes descending",Names(list).SequenceEqual(new[]{"order-b.txt","order-c.csv","order-a.zip"}));
                Click(prefix+"TypeColumn");Check(prefix+" sorts by file type",Names(list).SequenceEqual(new[]{"order-c.csv","order-b.txt","order-a.zip"}));
                Click(prefix+"ModifiedColumn");if(!remote)Check("PC dates sort chronologically",Names(list).SequenceEqual(new[]{"order-a.zip","order-c.csv","order-b.txt"}));
                search.Clear();Click(prefix+"NameColumn");Check(prefix+" keeps folders before ordinary files",Item(list,Names(list)[0]).Tag is FtpEntry{IsDirectory:true}||Directory.Exists((string)Item(list,Names(list)[0]).ToolTip));
                var heading=(Grid)Find<Button>(workspace,prefix+"NameColumn").Parent;
                double width=heading.ColumnDefinitions[0].Width.Value;var grip=heading.Children.OfType<Thumb>().First();grip.RaiseEvent(new DragDeltaEventArgs(75,0){RoutedEvent=Thumb.DragDeltaEvent});await Settle(owner);
                var row=(Grid)list.Items.OfType<ListBoxItem>().First(x=>x.Tag!=null).Content;
                Check(prefix+" resizing a column keeps header and rows aligned",heading.ColumnDefinitions[0].Width.Value==width+75&&row.ColumnDefinitions[0].Width==heading.ColumnDefinitions[0].Width&&row.ColumnDefinitions.Count==4);
                var scroll=All(list).OfType<ScrollViewer>().First();scroll.ScrollToHorizontalOffset(50);await Settle(owner);
                Check(prefix+" horizontal scrolling keeps headings aligned",Math.Abs(((ScrollViewer)heading.Parent).HorizontalOffset-scroll.HorizontalOffset)<1);
                scroll.ScrollToHorizontalOffset(0);
            }
            await Task.Delay(250);Check("Details and folder tree render cached Windows file icons",All(pc).OfType<Image>().Any(x=>x.Source!=null)&&All(tree).OfType<Image>().Any(x=>x.Source!=null));
            foreach(bool swapped in new[]{true,false})
            {
                if(store.Settings.FtpSwapPanes!=swapped){Click("FtpSwapPanes");await Settle(owner);}
                string destination=Path.Combine(local,swapped?"swapped":"normal");Directory.CreateDirectory(destination);await workspace.NavigateLocalForTestAsync(local);await session.RefreshAsync();await Settle(owner);
                Select(ftp,"검사 결과.csv","manual.txt");Drag(true,pc,Item(pc,Path.GetFileName(destination)));await queue.WaitForIdleAsync();await Settle(owner);
                Check("Remote multi-selection downloads intact into PC child folder in "+(swapped?"swapped":"normal")+" layout",File.ReadAllBytes(Path.Combine(destination,"검사 결과.csv")).SequenceEqual(payload)&&File.ReadAllBytes(Path.Combine(destination,"manual.txt")).SequenceEqual(original));
            }
            Select(ftp,"download folder");Drag(true,pc,pc);await queue.WaitForIdleAsync();await Settle(owner);
            Check("Remote folder drag copies nested files into the displayed PC directory",File.ReadAllBytes(Path.Combine(local,"download folder","nested","한글.bin")).SequenceEqual(payload));
            Select(pc,"upload 한글.bin","upload folder");Drag(false,ftp,Item(ftp,"results"));await queue.WaitForIdleAsync();await Settle(owner);
            Check("PC file and directory multi-drag uploads into a remote child folder",server.ReadFile("/workspace/results/upload 한글.bin").SequenceEqual(payload)&&server.ReadFile("/workspace/results/upload folder/nested/data.bin").SequenceEqual(payload));
            Select(pc,"notes.txt");Drag(false,ftp,ftp);await queue.WaitForIdleAsync();await Settle(owner);
            Check("PC drag on remote blank space uses the displayed server path",server.ReadFile("/workspace/notes.txt").SequenceEqual(original));
            foreach(bool icons in new[]{true,false})
            {
                Click(icons?"FtpIconView":"FtpListView");await Settle(owner);
                foreach(string decision in new[]{"Cancel","Replace"})
                {
                    File.WriteAllBytes(Path.Combine(inbox,"manual.txt"),payload);int prompts=0;Exception? modalFailure=null;
                    FormDialog.TestLoaded=dialog=>{try{prompts++;dialog.UpdateLayout();Check("Drag overwrite popup remains compact",dialog.ActualWidth<=500&&dialog.ActualHeight<450);Find<Button>(dialog,"FtpConflict"+decision).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception error){modalFailure=error;dialog.DialogResult=false;}};
                    try{Select(ftp,"manual.txt");Drag(true,pc,Item(pc,"받은 파일"));await queue.WaitForIdleAsync();if(modalFailure!=null)throw modalFailure;}
                    finally{FormDialog.TestLoaded=null;}
                    Check((icons?"Icons":"Details")+" drag overwrite "+decision+" respects user choice",prompts==1&&File.ReadAllBytes(Path.Combine(inbox,"manual.txt")).SequenceEqual(decision=="Replace"?original:payload));await Settle(owner);
                }
            }
            int before=queue.Items.Count;Select(ftp,"manual.txt");Drag(true,ftp,ftp,false);Select(pc,"notes.txt");Drag(false,pc,pc,false);
            Check("Same-pane drops never enqueue a transfer",queue.Items.Count==before);
            Select(ftp,"manual.txt");workspace.DragLoopForTest=(_,data)=>{session.Disconnect();var drop=Event(pc,pc,data,DragDrop.DropEvent);Check("Disconnect during a drag rejects its stale payload",drop.Effects==DragDropEffects.None);};workspace.StartPaneDragForTest(true);workspace.DragLoopForTest=null;
            Check("Rejected disconnected drag preserves queue",queue.Items.Count==before);
            await session.ConnectAsync(site,server.Password);await Settle(owner);
            Check("All copies preserve both source trees",server.ReadFile("/workspace/검사 결과.csv").SequenceEqual(payload)&&server.ReadFile("/workspace/download folder/nested/한글.bin").SequenceEqual(payload)&&File.ReadAllBytes(Path.Combine(local,"upload 한글.bin")).SequenceEqual(payload));
            Check("In-app transfers retain queue history without extra Windows handoff records",store.TransferHistory.Any(x=>x.Direction=="다운로드"&&x.Status=="완료")&&!store.TransferHistory.Any(x=>x.Direction=="드래그 다운로드"));
            foreach(var theme in ThemeService.All)
            {
                ThemeService.Apply(theme.Id);owner.Background=Ui.Brush("#111923");
                // Recreate the controls so all directly assigned palette colors follow the theme.
                using var previewSession=new FtpSession(store);using var previewQueue=new FtpTransferQueue(store);using var preview=new FtpWorkspace(owner,store,previewSession,previewQueue);owner.Content=preview;
                await preview.NavigateLocalForTestAsync(local);await previewSession.ConnectAsync(site,server.Password);await Settle(owner);
                Check(theme.Id+" first Loaded does not replace an explicitly requested local directory",Find<TextBox>(preview,"FtpLocalAddress").Text==local);
                Capture(owner,"ftp-explorer-"+theme.Id+".png");
                Find<Button>(preview,"FtpIconView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);Capture(owner,"ftp-explorer-icons-"+theme.Id+".png");
                Find<Button>(preview,"FtpSinglePane").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);Capture(owner,"ftp-explorer-single-"+theme.Id+".png");
                Find<Button>(preview,"FtpSinglePane").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Find<Button>(preview,"FtpListView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));owner.Width=920;owner.Height=650;await Settle(owner);
                Check(theme.Id+" compact two-pane explorer retains usable file areas",Find<ListBox>(preview,"FtpLocalFiles").ActualWidth>140&&Find<ListBox>(preview,"FtpRemoteFiles").ActualHeight>200);Capture(owner,"ftp-explorer-compact-"+theme.Id+".png");owner.Width=1440;owner.Height=900;
            }
            owner.Content=workspace;ThemeService.Apply("midnight");
            report.AppendLine("Owned loopback FTP, real WPF routed drag events, and native virtual-file format adapter. Physical mouse gestures and company servers require field validation.");
        }
        catch(Exception error){report.AppendLine("FAIL: "+error);throw;}
        finally{workspace.DragLoopForTest=null;FormDialog.TestLoaded=null;queue.CancelAll();await queue.WaitForIdleAsync();owner.Close();ThemeService.Apply("midnight");File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-explorer-test-report.txt"),report.ToString());}
    }
    private static DragEventArgs Event(ListBox target,UIElement hit,IDataObject data,RoutedEvent routed)
    {
        var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Copy,target,new Point(30,80)],null)!;args.RoutedEvent=routed;hit.RaiseEvent(args);return args;
    }
    private static string[] Names(ListBox list)=>list.Items.OfType<ListBoxItem>().Where(x=>x.Tag!=null).Select(x=>x.Tag is FtpEntry entry?entry.Name:Path.GetFileName((string)x.ToolTip)).ToArray();
    private static ListBoxItem Item(ListBox list,string name)=>list.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry entry?entry.Name==name:x.Tag!=null&&Path.GetFileName((string)x.ToolTip)==name);
    private static async Task Settle(Window window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    private static async Task Wait(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(30,timeout.Token);}
    private static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>All(root).OfType<T>().Single(x=>x.Name==name);
    private static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in All(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static IEnumerable<TreeViewItem> TreeNodes(ItemsControl parent){foreach(var item in parent.Items.OfType<TreeViewItem>()){yield return item;foreach(var child in TreeNodes(item))yield return child;}}
    private static void Capture(Window window,string name){window.UpdateLayout();var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(AppPaths.Root,name));png.Save(file);}
}
