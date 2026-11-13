using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComDataObject=System.Runtime.InteropServices.ComTypes.IDataObject;

namespace EquipLink;

internal static class FtpDragTests
{
    internal static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException("Owned test fixtures only.");
        var report=new StringBuilder();
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException(label);report.AppendLine("PASS: "+label);File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-drag-test-report.txt"),report.ToString());}
        await using var server=new FtpTestServer{RequirePassword=true};server.EnsureDirectory("/workspace/target");
        var site=server.Site("/workspace");var store=new AppStore{Settings=new(){AutoStartX=false},FtpSites=[site]};
        using var session=new FtpSession(store);using var queue=new FtpTransferQueue(store);
        var owner=new Window{Title="EquipLink FTP · local fixture",Width=1300,Height=820,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false,Background=Ui.Brush("#111923")};
        using var workspace=new FtpWorkspace(owner,store,session,queue);owner.Content=workspace;
        string local=Path.Combine(AppPaths.Root,"local");Directory.CreateDirectory(local);
        byte[] oldBytes=Encoding.UTF8.GetBytes("existing fixture data"),newBytes=Enumerable.Range(0,131079).Select(n=>(byte)(n%251)).ToArray();
        try
        {
            owner.Show();await workspace.NavigateLocalForTestAsync(local);await session.ConnectAsync(site,server.Password);await Settle(owner);
            var remote=Find<ListBox>(workspace,"FtpRemoteFiles");
            string first=Path.Combine(local,"한글 sample.bin"),second=Path.Combine(local,"report.csv");File.WriteAllBytes(first,newBytes);File.WriteAllBytes(second,oldBytes);
            Drop(remote,remote,[first,second]);await queue.WaitForIdleAsync();await Settle(owner);
            Check("External Windows FileDrop uploads multiple files to the displayed server folder",server.ReadFile("/workspace/한글 sample.bin").SequenceEqual(newBytes)&&server.ReadFile("/workspace/report.csv").SequenceEqual(oldBytes));
            await session.RefreshAsync();await Settle(owner);
            var target=remote.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry{Name:"target"});
            Find<Button>(workspace,"FtpListView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);
            Drop(remote,target,[second]);await queue.WaitForIdleAsync();
            Check("Dropping on a folder row in list view uploads into that folder",server.ReadFile("/workspace/target/report.csv").SequenceEqual(oldBytes));
            Find<Button>(workspace,"FtpIconView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);
            string tree=Path.Combine(local,"folder upload");Directory.CreateDirectory(Path.Combine(tree,"nested"));File.WriteAllBytes(Path.Combine(tree,"nested","payload.bin"),newBytes);
            Drop(remote,remote,[tree]);await queue.WaitForIdleAsync();
            Check("External folder drop uploads nested contents",server.ReadFile("/workspace/folder upload/nested/payload.bin").SequenceEqual(newBytes));
            await session.RefreshAsync();await Settle(owner);
            var picked=remote.Items.OfType<ListBoxItem>().First(x=>x.Tag is FtpEntry{Name:"report.csv"});picked.IsSelected=true;
            Check("FTP initially renders a wrapping icon view",All(remote).OfType<WrapPanel>().Any());
            Find<Button>(workspace,"FtpListView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);
            Check("Details view preserves selected file and current remote path",picked.IsSelected&&session.CurrentPath=="/workspace"&&picked.Content is Grid);
            Capture(owner,"ftp-details.png");
            var restored=AppStore.Load();
            using(var restoredSession=new FtpSession(restored))
            using(var restoredQueue=new FtpTransferQueue(restored))
            using(var reopened=new FtpWorkspace(owner,restored,restoredSession,restoredQueue))
            {
                await reopened.NavigateLocalForTestAsync(local);
                var restoredFiles=Find<ListBox>(reopened,"FtpLocalFiles");
                Check("Reopening FTP restores the saved list view with row contents",!restored.Settings.FtpIconView&&restoredFiles.Items.Count>0&&restoredFiles.Items.OfType<ListBoxItem>().All(x=>x.Content is Grid));
            }
            Find<Button>(workspace,"FtpIconView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);
            Check("Icon view can be restored without reconnecting",picked.IsSelected&&picked.Content is StackPanel&&session.IsConnected);
            Capture(owner,"ftp-icons.png");

            foreach(bool icons in new[]{true,false})
            {
                string view=icons?"Icon":"List",name="double-click-"+view+"-한글.bin",clickDestination=Path.Combine(local,"double-click-"+view);
                Directory.CreateDirectory(clickDestination);server.SetFile("/workspace/"+name,newBytes);
                await workspace.NavigateLocalForTestAsync(clickDestination);await session.RefreshAsync();
                Find<Button>(workspace,icons?"FtpIconView":"FtpListView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);
                var clicked=remote.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry entry&&entry.Name==name);
                var other=remote.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry{Name:"report.csv"});
                remote.UnselectAll();other.IsSelected=true;clicked.IsSelected=true;
                int before=queue.Items.Count;
                DoubleClick(remote,remote);DoubleClick(remote,(DependencyObject)clicked.Content,MouseButton.Right);
                Check(view+" empty-space and right-button double-clicks do not download selected files",queue.Items.Count==before);
                DoubleClick(remote,All((DependencyObject)clicked.Content).OfType<TextBlock>().Last());await queue.WaitForIdleAsync();await Settle(owner);
                Check(view+" file double-click downloads only the clicked file to the current PC folder",queue.Items.Count==before+1&&File.ReadAllBytes(Path.Combine(clickDestination,name)).SequenceEqual(newBytes)&&!File.Exists(Path.Combine(clickDestination,"report.csv")));
                Check(view+" double-click download uses the transfer queue and persistent history",queue.Items.First().Status=="완료"&&queue.Items.First().LocalPath==Path.Combine(clickDestination,name)&&store.TransferHistory.Any(x=>x.Direction=="다운로드"&&x.Status=="완료"&&x.LocalPath==Path.Combine(clickDestination,name)));
                foreach(string decision in new[]{"Skip","Replace","Cancel"})
                {
                    // A synthetic routed click can run while WPF is still
                    // re-enabling the owner of the previous modal dialog.
                    // Wait for the same enabled state a real mouse needs.
                    await Wait(()=>owner.IsEnabled&&remote.IsEnabled&&!session.IsBusy);
                    File.WriteAllBytes(Path.Combine(clickDestination,name),oldBytes);int prompts=0;Exception? dialogError=null;
                    FormDialog.TestLoaded=dialog=>
                    {
                        try{prompts++;Find<Button>(dialog,"FtpConflict"+decision).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                        catch(Exception error){dialogError=error;if(dialog.IsVisible)dialog.DialogResult=false;}
                    };
                    try
                    {
                        clicked=remote.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry entry&&entry.Name==name);
                        DoubleClick(remote,(DependencyObject)clicked.Content);await queue.WaitForIdleAsync();if(dialogError!=null)throw dialogError;
                    }
                    finally{FormDialog.TestLoaded=null;}
                    bool respected=prompts==1&&File.ReadAllBytes(Path.Combine(clickDestination,name)).SequenceEqual(decision=="Replace"?newBytes:oldBytes);
                    Check(view+" double-click collision "+decision+" asks once and respects the choice"+(respected?"":$" (dialogs={prompts}, ownerEnabled={owner.IsEnabled}, transfer={queue.Items.First().Status})"),respected);
                    await Settle(owner);
                }
                before=queue.Items.Count;target=remote.Items.OfType<ListBoxItem>().Single(x=>x.Tag is FtpEntry{Name:"target"});
                DoubleClick(remote,(DependencyObject)target.Content);await Wait(()=>session.CurrentPath=="/workspace/target"&&!session.IsBusy);await Settle(owner);
                Check(view+" folder double-click still navigates without queuing a download",queue.Items.Count==before&&session.CurrentPath=="/workspace/target");
                await session.NavigateAsync("/workspace");await Settle(owner);
            }
            await workspace.NavigateLocalForTestAsync(local);
            Find<Button>(workspace,"FtpIconView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(owner);

            foreach(bool upload in new[]{true,false})
            foreach(string decision in new[]{"Replace","Skip","Cancel"})
            {
                await Wait(()=>owner.IsEnabled);
                string name=(upload?"upload-":"download-")+decision+".bin",path=Path.Combine(local,name),remotePath="/workspace/"+name;
                File.WriteAllBytes(path,upload?newBytes:oldBytes);server.SetFile(remotePath,upload?oldBytes:newBytes);
                int dialogs=0;Exception? dialogError=null;
                FormDialog.TestLoaded=dialog=>
                {
                    try
                    {
                        dialogs++;dialog.UpdateLayout();
                        if(upload&&decision=="Replace")Capture(dialog,"ftp-overwrite.png");
                        Check((upload?"Upload":"Download")+" collision dialog uses the selected dark theme",dialog.Background.ToString()==Ui.Brush("#111923").ToString());
                        Find<Button>(dialog,"FtpConflict"+decision).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception error){dialogError=error;if(dialog.IsVisible)dialog.DialogResult=false;}
                };
                FtpTransferItem item;
                try
                {
                    item=upload?queue.EnqueueUpload(site,server.Password,path,remotePath):queue.EnqueueDownload(site,server.Password,remotePath,path,newBytes.Length);
                    await queue.WaitForIdleAsync();if(dialogError!=null)throw dialogError;
                }
                finally{FormDialog.TestLoaded=null;}
                byte[] result=upload?server.ReadFile(remotePath):File.ReadAllBytes(path);
                Check((upload?"Upload":"Download")+" "+decision+" prompts exactly once and preserves/replaces the right bytes",dialogs==1&&result.SequenceEqual(decision=="Replace"?newBytes:oldBytes));
                Check((upload?"Upload":"Download")+" "+decision+" records its outcome",item.Status==(decision=="Replace"?"완료":decision=="Skip"?"건너뜀":"취소"));
            }
            Check("Collision decisions leave no upload or download temporary files",!server.FilePaths.Any(x=>x.EndsWith(".part"))&&!Directory.EnumerateFiles(local,"*.part",SearchOption.AllDirectories).Any());
            Check("Durable transfer history never contains the fixture password",!File.ReadAllText(AppPaths.Config).Contains(server.Password));

            // Verify collisions that appear after preflight as well as a mixed folder job.
            queue.ConfirmOverwrite=(_,_)=>Task.FromResult(FtpOverwriteDecision.Skip);
            string mixed=Path.Combine(local,"mixed");Directory.CreateDirectory(mixed);File.WriteAllBytes(Path.Combine(mixed,"exists.bin"),newBytes);File.WriteAllBytes(Path.Combine(mixed,"new.bin"),newBytes);
            server.SetFile("/workspace/mixed/exists.bin",oldBytes);
            var mixedItem=queue.EnqueueUploadDirectory(site,server.Password,mixed,"/workspace/mixed");await queue.WaitForIdleAsync();
            Check("Skipping a folder collision still transfers its other files",mixedItem.Status=="완료"&&server.ReadFile("/workspace/mixed/exists.bin").SequenceEqual(oldBytes)&&server.ReadFile("/workspace/mixed/new.bin").SequenceEqual(newBytes));
            server.TransferDelayMs=20;server.TransferChunkSize=1024;int stor=server.CommandCount("STOR"),latePrompts=0;
            queue.ConfirmOverwrite=(_,_)=>{latePrompts++;return Task.FromResult(FtpOverwriteDecision.Skip);};
            var late=queue.EnqueueUpload(site,server.Password,first,"/workspace/late.bin");await server.WaitForCommandAsync("STOR",stor+1);server.SetFile("/workspace/late.bin",oldBytes);await queue.WaitForIdleAsync();
            Check("A new collision during upload is confirmed before rename and can be skipped",latePrompts==1&&late.Status=="건너뜀"&&server.ReadFile("/workspace/late.bin").SequenceEqual(oldBytes));
            server.TransferDelayMs=0;

            var files=new[]{new FtpEntry("한글 sample.bin","/workspace/한글 sample.bin",false,newBytes.Length,DateTimeOffset.UtcNow),new FtpEntry("report.csv","/workspace/report.csv",false,oldBytes.Length,null)};
            int reads=server.CommandCount("RETR");
            using(var data=new FtpVirtualFiles(site,server.Password,files))
            {
                var format=FtpVirtualFiles.Format(FtpVirtualFiles.DescriptorFormat,TYMED.TYMED_HGLOBAL);data.GetData(ref format,out var medium);
                try
                {
                    var pointer=FtpVirtualFiles.GlobalLock(medium.unionmember);
                    try
                    {
                        Check("Windows FILEGROUPDESCRIPTORW contains both selected files",Marshal.ReadInt32(pointer)==2);
                        Check("Windows descriptor preserves Unicode names and file sizes",Marshal.PtrToStringUni(pointer+76)==files[0].Name&&Marshal.ReadInt32(pointer+72)==newBytes.Length&&Marshal.PtrToStringUni(pointer+668)==files[1].Name);
                    }
                    finally{FtpVirtualFiles.GlobalUnlock(medium.unionmember);}
                }
                finally{FtpVirtualFiles.ReleaseStgMedium(ref medium);}
                Check("Offering drag metadata alone does not download files",server.CommandCount("RETR")==reads);
                byte[] received=ReadVirtual(data,0);Check("Indexed virtual IStream downloads exact binary contents",received.SequenceEqual(newBytes));
                Check("Second virtual file stream has the matching contents",ReadVirtual(data,1).SequenceEqual(oldBytes));
                Check("Repeated stream requests reuse the completed local cache",ReadVirtual(data,0).SequenceEqual(newBytes)&&server.CommandCount("RETR")==reads+2);
                Check("Remote originals survive every drag stream request",server.ReadFile(files[0].FullPath).SequenceEqual(newBytes)&&server.ReadFile(files[1].FullPath).SequenceEqual(oldBytes));
                var invalid=FtpVirtualFiles.Format(FtpVirtualFiles.ContentsFormat,TYMED.TYMED_ISTREAM,99);
                Check("Invalid FileContents indexes are rejected",data.QueryGetData(ref invalid)!=0);
            }
            await Wait(()=>!Directory.EnumerateFiles(Path.Combine(AppPaths.Root,"drag-cache")).Any());
            Check("Completed drag caches are cleaned up",!Directory.EnumerateFiles(Path.Combine(AppPaths.Root,"drag-cache")).Any());
            server.FailDownloadPath=files[0].FullPath;
            using(var failed=new FtpVirtualFiles(site,server.Password,[files[0]]))
            {
                bool rejected=false;try{ReadVirtual(failed,0);}catch(COMException){rejected=true;}
                Check("Failed FTP retrieval never hands a partial stream to Windows",rejected&&failed.Delivered.Count==0);
            }
            server.FailDownloadPath=null;

            server.TransferDelayMs=25;server.TransferChunkSize=1024;reads=server.CommandCount("RETR");
            using(var canceled=new FtpVirtualFiles(site,server.Password,[files[0]]))
            {
                var stop=Task.Run(async()=>{await server.WaitForCommandAsync("RETR",reads+1);canceled.Dispose();});
                bool rejected=false;try{ReadVirtual(canceled,0);}catch(COMException){rejected=true;}
                await stop;await Wait(()=>!Directory.EnumerateFiles(Path.Combine(AppPaths.Root,"drag-cache")).Any());
                Check("Canceling a virtual download releases its connection and removes partial cache",rejected&&canceled.Delivered.Count==0&&!Directory.EnumerateFiles(Path.Combine(AppPaths.Root,"drag-cache")).Any());
            }
            server.TransferDelayMs=0;

            string destination=Path.Combine(AppPaths.Root,"windows-shell-destination");Directory.CreateDirectory(destination);
            using(var shellData=new FtpVirtualFiles(site,server.Password,files))
            {
                uint effect=CopyThroughWindowsShell(shellData,destination);
                await Wait(()=>files.All(f=>File.Exists(Path.Combine(destination,f.Name))));
                Check("Actual Windows Shell folder drop target accepts a virtual FTP copy",effect==1);
                Check("Windows Shell writes both Unicode and binary files intact",File.ReadAllBytes(Path.Combine(destination,files[0].Name)).SequenceEqual(newBytes)&&File.ReadAllBytes(Path.Combine(destination,files[1].Name)).SequenceEqual(oldBytes));
                Check("Windows Shell leaves remote files intact",server.ReadFile(files[0].FullPath).SequenceEqual(newBytes));
                foreach(var file in shellData.Delivered)queue.RecordExternalDownload(site,file.Entry,"Windows 탐색기",file.Bytes);
                Check("External downloads record Windows handoff without inventing a destination path",store.TransferHistory.Count(x=>x.Direction=="드래그 다운로드"&&x.Status=="전달 완료"&&x.LocalPath.StartsWith("Windows 탐색기\\"))==2);
            }
            var source=new CopyDropSource();Check("Native drag supports Escape cancel, mouse-release drop and copy feedback",source.QueryContinueDrag(true,1)==0x40101&&source.QueryContinueDrag(false,0)==0x40100&&source.GiveFeedback(1)==0x40102);
            report.AppendLine("Owned loopback FTP and actual Windows Shell IDropTarget only. Physical mouse dragging between foreground windows and the Explorer collision dialog require interactive field validation.");
        }
        catch(Exception error){report.AppendLine("FAIL: "+error);throw;}
        finally{FormDialog.TestLoaded=null;queue.CancelAll();await queue.WaitForIdleAsync();owner.Close();File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-drag-test-report.txt"),report.ToString());}
    }
    static void Drop(ListBox target,UIElement source,string[] paths)
    {
        var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[new DataObject(DataFormats.FileDrop,paths),DragDropKeyStates.LeftMouseButton,DragDropEffects.Copy,target,new Point(5,5)],null)!;
        args.RoutedEvent=DragDrop.DropEvent;source.RaiseEvent(args);
    }
    static byte[] ReadVirtual(FtpVirtualFiles data,int index)
    {
        var format=FtpVirtualFiles.Format(FtpVirtualFiles.ContentsFormat,TYMED.TYMED_ISTREAM,index);data.GetData(ref format,out var medium);
        try
        {
            var stream=(IStream)Marshal.GetObjectForIUnknown(medium.unionmember);stream.Stat(out var stat,1);
            using var result=new MemoryStream();byte[] buffer=new byte[8093];IntPtr count=Marshal.AllocCoTaskMem(8);
            try{stream.Seek(0,0,IntPtr.Zero);while(true){stream.Read(buffer,buffer.Length,count);int bytes=Marshal.ReadInt32(count);if(bytes==0)break;result.Write(buffer,0,bytes);}if(result.Length!=stat.cbSize)throw new IOException("IStream length mismatch");return result.ToArray();}
            finally{Marshal.FreeCoTaskMem(count);if(Marshal.IsComObject(stream))Marshal.ReleaseComObject(stream);}
        }
        finally{FtpVirtualFiles.ReleaseStgMedium(ref medium);}
    }
    static uint CopyThroughWindowsShell(ComDataObject data,string destination)
    {
        Guid itemId=new("43826D1E-E718-42EE-BC55-A1E261C37BFE"),handler=new("3981E225-F559-11D3-8E3A-00C04F6837D5"),dropId=new("00000122-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination,IntPtr.Zero,ref itemId,out var item));
        object? target=null;
        try
        {
            item.BindToHandler(IntPtr.Zero,ref handler,ref dropId,out target);var drop=(IShellDropTarget)target;uint effect=1;
            Marshal.ThrowExceptionForHR(drop.DragEnter(data,1,new(),ref effect));if(effect!=1){drop.DragLeave();throw new IOException("Windows Shell refused copy");}
            Marshal.ThrowExceptionForHR(drop.Drop(data,0,new(),ref effect));return effect;
        }
        finally{if(target!=null)Marshal.ReleaseComObject(target);Marshal.ReleaseComObject(item);}
    }
    static void DoubleClick(ListBox list,DependencyObject source,MouseButton button=MouseButton.Left)=>list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button){RoutedEvent=Control.MouseDoubleClickEvent,Source=source});
    static async Task Settle(Window window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    static async Task Wait(Func<bool> test){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!test())await Task.Delay(30,timeout.Token);}
    static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>All(root).OfType<T>().Single(x=>x.Name==name);
    static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in All(VisualTreeHelper.GetChild(root,i)))yield return item;}
    static void Capture(Window window,string name)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(AppPaths.Root,name));png.Save(file);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellFolderItem item);
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellFolderItem
    {void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);}
    [StructLayout(LayoutKind.Sequential)]struct PointL{public int X,Y;}
    [ComImport,Guid("00000122-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellDropTarget
    {
        [PreserveSig]int DragEnter([MarshalAs(UnmanagedType.Interface)]ComDataObject data,uint keys,PointL point,ref uint effect);
        [PreserveSig]int DragOver(uint keys,PointL point,ref uint effect);
        [PreserveSig]int DragLeave();
        [PreserveSig]int Drop([MarshalAs(UnmanagedType.Interface)]ComDataObject data,uint keys,PointL point,ref uint effect);
    }
}
