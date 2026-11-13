using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EquipLink;

internal static class FtpDeletionTests
{
    internal static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException();
        var report=new StringBuilder();
        void Check(string name,bool value){if(!value)throw new InvalidOperationException(name);report.AppendLine("PASS: "+name);}
        try
        {
            await using(var server=new FtpTestServer{ListingAccessRoot="/private/home",RequirePassword=true})
            {
                server.SetFile("/private/home/result.txt",[1,2,3]);server.SetFile("/private/home/keep.txt",[4]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/private/home"),server.Password);
                var entry=(await client.ListAsync("/private/home")).Single(x=>x.Name=="result.txt");int deleted=0;
                await FtpFileDeletion.DeleteRemoteAsync(client,"/private/home",[entry],()=>deleted++,default);
                Check("An authorized home-directory file can be deleted when all parent listings are denied",deleted==1&&!server.FileExists(entry.FullPath)&&server.FileExists("/private/home/keep.txt"));
                Check("Deletion never requests listings outside the selected working directory",server.DeniedListings==0);
            }
            await using(var server=new FtpTestServer{RelativeDeleteOnly=true})
            {
                server.SetFile("/data/folder/nested/검사 결과.txt",[5]);server.SetFile("/data/-leading.txt",[6]);server.SetFile("/data/keep.txt",[7]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);
                var chosen=(await client.ListAsync("/data")).Where(x=>x.Name!="keep.txt").ToArray();int deleted=0;
                await FtpFileDeletion.DeleteRemoteAsync(client,"/data",chosen,()=>deleted++,default);
                Check("Legacy relative-only DELE and RMD remove selected files and nested directories",deleted==4&&!server.DirectoryExists("/data/folder")&&!server.FileExists("/data/-leading.txt")&&server.FileExists("/data/keep.txt"));
                Check("Every delete names a child of a confirmed directory without absolute paths",server.DeleteRequests.All(x=>!x.Argument.StartsWith('/')&&x.Directory.StartsWith("/data",StringComparison.Ordinal)));
                Check("Deletion restores the browsing connection working directory",client.WorkingDirectory=="/data"&&client.IsConnected);
            }
            await using(var server=new FtpTestServer{SupportMlsd=false,SupportUtf8=false,WireEncoding="CP949",RelativeDeleteOnly=true,ListingAccessRoot="/data"})
            {
                string[] names=["한글 결과 [1].txt"," space.txt",".hidden.txt"];
                foreach(string name in names)server.SetFile("/data/"+name,[8]);server.SetFile("/data/keep.txt",[9]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);
                var entries=await client.ListAsync("/data");int deleted=0;
                Check("Legacy LIST filenames auto-detect CP949 before deletion",client.EncodingName.Contains("949")&&names.All(name=>entries.Any(x=>x.Name==name)));
                await FtpFileDeletion.DeleteRemoteAsync(client,"/data",entries.Where(x=>names.Contains(x.Name)).ToArray(),()=>deleted++,default);
                Check("Korean, spaces, brackets and hidden filenames are deleted literally",deleted==3&&names.All(name=>!server.FileExists("/data/"+name))&&server.FileExists("/data/keep.txt"));
                Check("No additional parent-list permissions are required for legacy LIST",server.DeniedListings==0&&client.WorkingDirectory=="/data");
            }
            await using(var server=new FtpTestServer())
            {
                server.SetFile("/data/keep.txt",[1]);server.SetFile("/data/remove.txt",[2]);server.SetFile("/data/folder/child.txt",[3]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);
                var original=await client.ListAsync("/data");int deleted=0;
                server.DirectoryListings["/data"]="type=file;size=2; remove.txt\r\ntype=file;size=1; keep.txt\r\ntype=dir; folder\r\nunsupported listing row\r\n";
                await FtpFileDeletion.DeleteRemoteAsync(client,"/data",[original.Single(x=>x.Name=="remove.txt")],()=>deleted++,default);
                Check("Unreadable unrelated siblings do not block an exact selected file",deleted==1&&!server.FileExists("/data/remove.txt")&&server.FileExists("/data/keep.txt"));
                server.DirectoryListings["/data/folder"]="type=file;size=3; child.txt\r\nunsupported child row\r\n";
                int before=server.CommandCount("DELE");var error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",[original.Single(x=>x.Name=="folder")],()=>deleted++,default));
                Check("An incomplete selected subtree is rejected before deleting any child",error!=null&&server.CommandCount("DELE")==before&&server.FileExists("/data/folder/child.txt"));
                server.DirectoryListings["/data/folder"]="type=file;size=3; child.txt\r\ntype=OS.unix=slink; link\r\n";
                error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",[original.Single(x=>x.Name=="folder")],()=>deleted++,default));
                Check("A nested symbolic link stops the complete deletion plan",error!=null&&server.CommandCount("DELE")==before);
                server.DirectoryListings["/data"]="type=file;size=1; keep.txt\r\ntype=file;size=2; keep.txt\r\n";
                error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",[original.Single(x=>x.Name=="keep.txt")],()=>deleted++,default));
                Check("Duplicate ambiguous selected names remain blocked",error!=null&&server.FileExists("/data/keep.txt")&&server.CommandCount("DELE")==before);
                server.DirectoryListings.Clear();
                var invalid=original.Single(x=>x.Name=="keep.txt") with{FullPath="/outside/keep.txt"};
                error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",[invalid],()=>deleted++,default));
                Check("A selected file cannot escape its displayed folder",error!=null&&server.CommandCount("DELE")==before);
                error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",[new("..","/",true,null,null)],()=>deleted++,default));
                Check("A parent-directory or root selection cannot be removed",error!=null&&server.DirectoryExists("/"));
                using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool cancelled=false;
                try{await FtpFileDeletion.DeleteRemoteAsync(client,"/data",[original.Single(x=>x.Name=="keep.txt")],()=>deleted++,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
                Check("Cancelled deletion sends no destructive command",cancelled&&server.CommandCount("DELE")==before);
            }
            await using(var server=new FtpTestServer())
            {
                server.SetFile("/data/folder/file.txt",[1]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);
                var chosen=(await client.ListAsync("/data")).ToArray();
                server.BeforeListing=path=>{if(path=="/data/folder")server.DirectoryListings["/data"]="type=OS.unix=slink; folder\r\n";};
                var error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",chosen,()=>{},default));
                Check("A selected directory replaced by a link during planning is rejected",error!=null&&server.CommandCount("DELE")==0&&server.FileExists("/data/folder/file.txt"));
            }
            await using(var server=new FtpTestServer{DenyDeletePath="/data/folder/locked.txt",RelativeDeleteOnly=true})
            {
                server.SetFile(server.DenyDeletePath,[1]);server.SetFile("/data/folder/ok.txt",[2]);
                await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);int deleted=0;
                var chosen=(await client.ListAsync("/data")).ToArray();
                var error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",chosen,()=>deleted++,default));
                Check("Server permission denial remains a failure with a useful operation and target",error is FtpDeletionException{Stage:"파일 삭제",TargetPath:"/data/folder/locked.txt"}&&error.GetBaseException() is FtpException{ReplyCode:550});
                Check("Confirmed deletions are counted and the locked file is preserved",deleted==1&&server.FileExists(server.DenyDeletePath)&&!server.FileExists("/data/folder/ok.txt"));
                Check("Failed relative deletion restores the directory and leaves the connection usable",client.WorkingDirectory=="/data"&&client.IsConnected);
            }
            await using(var server=new FtpTestServer{DenyCwdAfterDeletePath="/data"})
            {
                server.SetFile("/data/folder/file.txt",[1]);await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);int deleted=0;
                var chosen=(await client.ListAsync("/data")).ToArray();var error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",chosen,()=>deleted++,default));
                Check("A restore failure never reports zero after a confirmed file deletion",error!=null&&deleted==1&&!server.FileExists("/data/folder/file.txt"));
                Check("A connection with an un-restored working directory is closed",!client.IsConnected&&server.DirectoryExists("/data/folder"));
            }
            await using(var server=new FtpTestServer{CloseAfterDelete=true})
            {
                server.SetFile("/data/file.txt",[1]);await using var client=await FtpClient.ConnectAsync(server.Site("/data"),server.Password);int confirmed=0;
                var chosen=(await client.ListAsync("/data")).ToArray();var error=await Reject(()=>FtpFileDeletion.DeleteRemoteAsync(client,"/data",chosen,()=>confirmed++,default));
                Check("Losing a delete reply neither invents confirmation nor retries deletion",error!=null&&confirmed==0&&!client.IsConnected&&server.CommandCount("DELE")==1&&!server.FileExists("/data/file.txt"));
            }
            await TestDialogAsync(Check);
            report.AppendLine("Owned loopback servers only. Real company-server replies are not available; restricted parent listings, legacy relative commands, CP949 and denial cases are covered.");
        }
        catch(Exception error){report.AppendLine("FAIL: "+error);throw;}
        finally{File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-delete-test-report.txt"),report.ToString());}
    }
    private static async Task<IOException?> Reject(Func<Task> action){try{await action();return null;}catch(IOException error){return error;}}
    private static async Task TestDialogAsync(Action<string,bool> check)
    {
        await using var server=new FtpTestServer{ListingAccessRoot="/private/home",RelativeDeleteOnly=true,DenyDeletePath="/private/home/locked.txt"};server.SetFile(server.DenyDeletePath,[1]);
        var site=server.Site("/private/home");var store=new AppStore{Settings=new(){AutoStartX=false}};using var session=new FtpSession(store);using var queue=new FtpTransferQueue(store);
        var window=new Window{Title="Deletion fixture",Width=1200,Height=800,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false};using var workspace=new FtpWorkspace(window,store,session,queue);window.Content=workspace;
        string local=Path.Combine(AppPaths.Root,"local");Directory.CreateDirectory(local);Exception? modalFailure=null;int confirmations=0,errors=0;
        try
        {
            await workspace.NavigateLocalForTestAsync(local);window.Show();await session.ConnectAsync(site,server.Password);await Settle(window);
            var list=Find<ListBox>(workspace,"FtpRemoteFiles");list.SelectedItem=list.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry{Name:"locked.txt"});
            FormDialog.TestLoaded=dialog=>
            {
                try
                {
                    dialog.UpdateLayout();var confirm=All(dialog).OfType<Button>().FirstOrDefault(x=>x.Name=="FtpDeleteConfirm");
                    if(confirm!=null){confirmations++;confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return;}
                    errors++;check("Delete failure uses a compact themed popup",dialog.ActualWidth<=510&&dialog.ActualHeight<490&&dialog.Background.ToString()==Ui.Brush("#111923").ToString());
                    check("Zero confirmed items do not falsely claim that the server kept every file",Find<TextBlock>(dialog,"FtpDeleteErrorOutcome").Text=="삭제 완료가 확인된 항목이 없습니다.");
                    string details=Find<TextBox>(dialog,"FtpDeleteErrorDetails").Text;
                    check("Failure identifies the exact stage and file plus sanitized FTP code",details.Contains("파일 삭제")&&details.Contains("locked.txt")&&details.Contains("550")&&!details.Contains(server.Password));
                    check("Copyable failure details and close action are available",Find<Button>(dialog,"FtpDeleteErrorCopy").IsEnabled&&Find<Button>(dialog,"FtpDeleteErrorClose").IsDefault);
                    Capture(dialog,"ftp-delete-error.png");Find<Button>(dialog,"FtpDeleteErrorClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception error){modalFailure=error;dialog.DialogResult=false;}
            };
            await workspace.DeleteForTestAsync(true);if(modalFailure!=null)throw modalFailure;
            check("Confirmed denied deletion shows one failure and keeps the file selected",confirmations==1&&errors==1&&server.FileExists(server.DenyDeletePath)&&!workspace.IsDeletingForTest);
            server.DenyDeletePath=null;await workspace.DeleteForTestAsync(true);if(modalFailure!=null)throw modalFailure;await Settle(window);
            check("Confirmed UI deletion succeeds with restricted ancestors and relative-only DELE",confirmations==2&&errors==1&&!server.FileExists("/private/home/locked.txt")&&session.IsConnected);
            check("The visible server list refreshes after successful deletion",list.Items.OfType<ListBoxItem>().All(x=>x.Tag==null)&&Find<TextBlock>(workspace,"FtpRemoteCount").Text.Contains("삭제 완료 1개"));
        }
        finally{FormDialog.TestLoaded=null;window.Close();}
    }
    private static async Task Settle(Window window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);await Task.Delay(150);}
    private static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>All(root).OfType<T>().Single(x=>x.Name==name);
    private static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in All(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void Capture(Window window,string name){window.UpdateLayout();var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom),96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(AppPaths.Root,name));png.Save(file);}
}
