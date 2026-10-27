using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EquipLink;

/// <summary>Real controls and owned loopback FTP/PuTTY fixtures only.</summary>
public static class FtpUiTests
{
    public static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException("FTP UI checks require an explicit test run.");
        Directory.CreateDirectory(AppPaths.Root);var report=new StringBuilder();string originalTheme=ThemeService.Current.Id;
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);report.AppendLine("PASS: "+label);}
        await using var ftp=new FtpTestServer{RequirePassword=true};ftp.EnsureDirectory("/workspace/recipes");ftp.EnsureDirectory("/workspace/reports");ftp.EnsureDirectory("/archive");
        ftp.SetFile("/workspace/calibration.csv",Encoding.UTF8.GetBytes("fixture,value\nSAMPLE,100\n"));ftp.SetFile("/workspace/test_recipe.ini",Encoding.UTF8.GetBytes("[SAMPLE]\nfixture=true\n"));ftp.SetFile("/workspace/recipes/base.ini",Encoding.UTF8.GetBytes("sample=true"));
        try
        {
            foreach(var theme in ThemeService.All)
            {
                ThemeService.Apply(theme.Id);var site=ftp.Site("/workspace");site.Name="TEST FLOOR · LOCAL SAMPLE";site.EncryptedPassword=CredentialProtector.Protect(site.CredentialTarget(),ftp.Password);
                string local=Path.Combine(AppPaths.Root,"sample-local",theme.Id),uploadName="upload-"+theme.Id+".txt";Directory.CreateDirectory(local);Directory.CreateDirectory(Path.Combine(local,"results"));await File.WriteAllTextAsync(Path.Combine(local,uploadName),"Owned UI transfer fixture. No production data.");
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var equipment=new Equipment{Name="LOCAL TESTER · SAMPLE",Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,UseX11=false};
                var store=new AppStore{Settings=new(){ThemeId=theme.Id,AutoStartX=false},Equipment=[equipment],FtpSites=[site,new(){Name="ENGINEERING · SAMPLE",Host="192.0.2.80",Username="sample",InitialPath="/home/test"}]};
                var window=new MainWindow(store){Width=1440,Height=940,WindowStartupLocation=WindowStartupLocation.Manual,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false};SessionPane? pane=null;TcpClient? client=null;
                try
                {
                    // Establish a fixture-only local path before the first Loaded event.
                    window.ShowFtpForTest(true);await window.FtpWorkspaceForTest!.NavigateLocalForTestAsync(local);window.ShowFtpForTest(false);
                    window.Show();var accept=listener.AcceptTcpClientAsync();pane=await window.ConnectForTestAsync(equipment);client=await accept.WaitAsync(TimeSpan.FromSeconds(10));
                    await client.GetStream().WriteAsync(new byte[]{255,251,1,255,251,3,255,253,31});
                    await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("\u001b[2J\u001b[H\u001b[1;36mEQUIPLINK / LOCAL SIMULATION\u001b[0m\r\nOwned loopback fixture only. No equipment contacted.\r\n\r\n\u001b[32mfixture$\u001b[0m pwd\r\n/home/operator\r\n\r\nFTP folders are navigated independently.\r\nTerminal remains connected when switching views.\r\n\r\n\u001b[32mfixture$\u001b[0m "));
                    await Settle(window);double terminalWidth=pane.Host.ActualWidth,terminalHeight=pane.Host.ActualHeight;int? processId=pane.Host.ProcessId;
                    window.ShowFtpForTest(true);await Settle(window);var workspace=window.FtpWorkspaceForTest!;
                    var waitingList=Find<ListBox>(workspace,"FtpRemoteFiles");
                    Check(theme.Id+" disconnected remote list stays dark when disabled",!waitingList.IsEnabled&&HasDarkCanvas(waitingList));
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-waiting-"+theme.Id+".png"));
                    try{await window.FtpSessionForTest.ConnectAsync(site,"incorrect-fixture-password");throw new InvalidOperationException("Expected failed login");}catch(FtpException){}
                    await Settle(window);
                    Check(theme.Id+" failed connection state stays dark with a visible error",window.FtpSessionForTest.Error.Length>0&&!waitingList.IsEnabled&&HasDarkCanvas(waitingList));
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-error-"+theme.Id+".png"));
                    ftp.ListDelayMs=700;int beforeListing=ftp.CommandCount("MLSD");var connecting=window.FtpSessionForTest.ConnectAsync(site,ftp.Password);
                    await ftp.WaitForCommandAsync("MLSD",beforeListing+1);await Settle(window);
                    Check(theme.Id+" loading remote list stays dark while input is disabled",window.FtpSessionForTest.IsBusy&&!waitingList.IsEnabled&&HasDarkCanvas(waitingList));
                    await connecting;ftp.ListDelayMs=0;await workspace.NavigateLocalForTestAsync(local);await Settle(window);
                    Check(theme.Id+" FTP view spans the workspace and retains live terminal process",window.SidebarWidthForTest==0&&workspace.ActualWidth>1350&&pane.Host.HasProcess&&pane.Host.ProcessId==processId);
                    Check(theme.Id+" full FTP workspace background uses the selected theme",workspace.Background.ToString()==Ui.Brush("#111923").ToString());
                    Check(theme.Id+" site map lists saved servers",Find<ListBox>(workspace,"FtpSites").Items.OfType<ListBoxItem>().Count(x=>x.Tag is FtpSite)==2);
                    Check(theme.Id+" FTP address bar reflects the server canonical path",Find<TextBox>(workspace,"FtpRemoteAddress").Text=="/workspace");
                    Check(theme.Id+" upload and download collisions require confirmation",window.FtpQueueForTest.ConfirmOverwrite!=null);
                    Check(theme.Id+" both file lists allow selecting multiple files",Find<ListBox>(workspace,"FtpLocalFiles").SelectionMode==SelectionMode.Extended&&Find<ListBox>(workspace,"FtpRemoteFiles").SelectionMode==SelectionMode.Extended);

                    var saved=RunDialog(()=>FtpDialogs.EditSite(window,site,true),dialog=>
                    {
                        Check(theme.Id+" actual FTP dialog is dark and masks saved passwords",dialog.Background.ToString()==Ui.Brush("#111923").ToString()&&Find<PasswordBox>(dialog,"FtpPassword").Password.Length==0&&Find<PasswordBox>(dialog,"FtpPassword").PasswordChar=='●');
                        Check(theme.Id+" actual FTP dialog restores IP, ID, port and initial path",Find<TextBox>(dialog,"FtpHost").Text==site.Host&&Find<TextBox>(dialog,"FtpUsername").Text==site.Username&&Find<TextBox>(dialog,"FtpPort").Text==site.Port.ToString()&&Find<TextBox>(dialog,"FtpInitialPath").Text=="/workspace");
                        Check(theme.Id+" charset defaults to auto with custom field disabled",Find<ComboBox>(dialog,"FtpCharsetMode").SelectedIndex==0&&!Find<TextBox>(dialog,"FtpCustomCharset").IsEnabled);
                        CaptureDialog(dialog,"ftp-connect-"+theme.Id+".png");Find<Button>(dialog,"FtpSubmit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    Check(theme.Id+" dialog resolves saved credentials only on connect submit",saved!=null&&saved.Password==ftp.Password&&saved.Site.EncryptedPassword==site.EncryptedPassword);
                    var cancelled=RunDialog(()=>FtpDialogs.EditSite(window,site),dialog=>{Find<TextBox>(dialog,"FtpHost").Text="192.0.2.99";dialog.DialogResult=false;});
                    Check(theme.Id+" cancelling FTP editor leaves the site unchanged",cancelled==null&&site.Host=="127.0.0.1");
                    var cleared=RunDialog(()=>FtpDialogs.EditSite(window,site),dialog=>{Find<CheckBox>(dialog,"FtpRememberPassword").IsChecked=false;Find<Button>(dialog,"FtpSubmit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));});
                    Check(theme.Id+" saved FTP password can be removed without exposing it",cleared!=null&&cleared.Site.EncryptedPassword==null&&cleared.Password=="");
                    var manual=RunDialog(()=>FtpDialogs.EditSite(window,site),dialog=>{Find<ComboBox>(dialog,"FtpCharsetMode").SelectedIndex=2;var input=Find<TextBox>(dialog,"FtpCustomCharset");Check(theme.Id+" choosing custom charset enables its text field",input.IsEnabled);input.Text="windows-1252";Find<Button>(dialog,"FtpSubmit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));});
                    Check(theme.Id+" custom charset saves without altering credentials",manual?.Site.Encoding=="windows-1252"&&manual.Site.EncryptedPassword==site.EncryptedPassword);
                    var forced=RunDialog(()=>FtpDialogs.EditSite(window,site),dialog=>{Find<ComboBox>(dialog,"FtpCharsetMode").SelectedIndex=1;Find<Button>(dialog,"FtpSubmit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));});
                    Check(theme.Id+" force UTF8 mode saves independently of automatic mode",forced?.Site.Encoding=="UTF-8");

                    var address=Find<TextBox>(workspace,"FtpRemoteAddress");address.Text="/workspace/recipes";Enter(address);await WaitUntil(()=>window.FtpSessionForTest.CurrentPath=="/workspace/recipes"&&!window.FtpSessionForTest.IsBusy);await Settle(window);
                    Check(theme.Id+" Enter in remote path field navigates the FTP server",window.FtpSessionForTest.CurrentPath=="/workspace/recipes");
                    await WaitUntil(()=>Find<TreeView>(workspace,"FtpDirectoryTree").Items.OfType<TreeViewItem>().SelectMany(TreeItems).Any(x=>x.ToolTip?.ToString()=="/archive"));
                    var tree=Find<TreeView>(workspace,"FtpDirectoryTree");var archive=tree.Items.OfType<TreeViewItem>().SelectMany(TreeItems).First(x=>x.ToolTip?.ToString()=="/archive");archive.IsSelected=true;await WaitUntil(()=>window.FtpSessionForTest.CurrentPath=="/archive"&&!window.FtpSessionForTest.IsBusy);
                    Check(theme.Id+" folder tree retains ancestor siblings and click navigates",window.FtpSessionForTest.CurrentPath=="/archive");
                    var treeNodes=tree.Items.OfType<TreeViewItem>().SelectMany(TreeItems).Where(x=>x.Tag!=null).ToArray();
                    Check(theme.Id+" nested folder nodes use the custom dark selection and expander template",treeNodes.Length>1&&treeNodes.All(x=>ReferenceEquals(x.Style,tree.ItemContainerStyle)&&ReferenceEquals(x.ItemContainerStyle,tree.ItemContainerStyle)));
                    await window.FtpSessionForTest.NavigateAsync("/workspace");await Settle(window);
                    ftp.InjectedListing="type=file;size=25; calibration.csv\r\ntype=file;size=1; ../unsafe-row\r\n";
                    await window.FtpSessionForTest.RefreshAsync();await Settle(window);
                    Check(theme.Id+" unsupported row does not hide ordinary files or disconnect",window.FtpSessionForTest.IsConnected&&Find<ListBox>(workspace,"FtpRemoteFiles").Items.OfType<ListBoxItem>().Count(x=>x.Tag is FtpEntry)==1&&window.FtpSessionForTest.ListingNotice.Contains("1개"));
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-partial-"+theme.Id+".png"));
                    ftp.InjectedListing=null;await window.FtpSessionForTest.RefreshAsync();await Settle(window);
                    var localList=Find<ListBox>(workspace,"FtpLocalFiles");localList.SelectedItem=localList.Items.OfType<ListBoxItem>().Single(x=>x.ToolTip?.ToString()==Path.Combine(local,uploadName));Find<Button>(workspace,"FtpUpload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.FtpQueueForTest.WaitForIdleAsync();await Settle(window);
                    Check(theme.Id+" real Upload button queues and completes a loopback file transfer",ftp.FileExists("/workspace/"+uploadName)&&window.FtpQueueForTest.Items.First().Status=="완료");
                    await window.FtpSessionForTest.NavigateAsync("/workspace");var remote=Find<ListBox>(workspace,"FtpRemoteFiles");remote.SelectedItem=remote.Items.OfType<ListBoxItem>().First(x=>x.Tag is FtpEntry{Name:"calibration.csv"});Find<Button>(workspace,"FtpDownload").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.FtpQueueForTest.WaitForIdleAsync();await Settle(window);
                    Check(theme.Id+" real Download button writes the selected file to the local path",File.Exists(Path.Combine(local,"calibration.csv"))&&File.ReadAllBytes(Path.Combine(local,"calibration.csv")).SequenceEqual(ftp.ReadFile("/workspace/calibration.csv")));
                    Check(theme.Id+" transfer completion populates persistent history",store.TransferHistory.Any(x=>x.Direction=="다운로드"&&x.Status=="완료"));
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-"+theme.Id+".png"));
                    Find<Button>(workspace,"FtpListView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-list-"+theme.Id+".png"));
                    window.Width=900;window.Height=650;await Settle(window);
                    Check(theme.Id+" minimum window keeps both FTP file panes arranged",Find<ListBox>(workspace,"FtpLocalFiles").ActualWidth>140&&Find<ListBox>(workspace,"FtpRemoteFiles").ActualWidth>140&&Find<ListBox>(workspace,"FtpRemoteFiles").ActualHeight>90);
                    Check(theme.Id+" compact window keeps remote folder navigation visible with transfers open",tree.IsVisible&&tree.ActualHeight>50);
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-compact-list-"+theme.Id+".png"));
                    Find<Button>(workspace,"FtpIconView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"ftp-compact-"+theme.Id+".png"));window.Width=1440;window.Height=940;window.ShowFtpForTest(false);await Settle(window);
                    window.ShowFtpForTest(true);await Settle(window);
                    var sourceChoice=Find<ComboBox>(workspace,"FtpSourceChoice");
                    Check(theme.Id+" live terminal appears as an FTP helper target",sourceChoice.Items.Count==2);
                    sourceChoice.SelectedIndex=1;await Settle(window);
                    Find<Button>(workspace,"TerminalFtpOpen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
                    string expectedCommand=$"ftp -i '127.0.0.1' {site.Port}";
                    Check(theme.Id+" helper opens the existing terminal command composer",!workspace.IsVisible&&Descendants(pane).OfType<TextBox>().Any(x=>x.IsVisible&&x.Text==expectedCommand)&&pane.Host.ProcessId==processId);
                    var received=new List<byte>();var stream=client.GetStream();while(stream.DataAvailable){var buffer=new byte[4096];int count=await stream.ReadAsync(buffer);received.AddRange(buffer.Take(count));}
                    Check(theme.Id+" preparing FTP never sends the command to the Telnet connection",!Encoding.ASCII.GetString(received.ToArray()).Contains("ftp -i"));
                    pane.ToggleComposer();sourceChoice.SelectedIndex=0;await Settle(window);
                    var folderButton=Descendants(window).OfType<Button>().Single(x=>x.Content is TextBlock label&&label.Text=="SSH Browser");folderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
                    Check(theme.Id+" terminal sidebar shows SSH Browser independently of FTP",Descendants(window).OfType<SftpBrowserPanel>().Any(x=>x.IsVisible)&&window.SidebarWidthForTest==224);
                    Check(theme.Id+" FTP and folder sidebar keep terminal dimensions and process",Math.Abs(pane.Host.ActualWidth-terminalWidth)<1&&Math.Abs(pane.Host.ActualHeight-terminalHeight)<1&&pane.Host.ProcessId==processId&&pane.Host.HasProcess);
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"terminal-ftp-folders-"+theme.Id+".png"));
                    report.AppendLine($"GEOMETRY: {theme.Id}: terminal {terminalWidth:F0} × {terminalHeight:F0} DIP unchanged.");
                }
                finally{FormDialog.TestLoaded=null;window.FtpQueueForTest.CancelAll();await window.FtpQueueForTest.WaitForIdleAsync();pane?.Host.CloseSession();window.Close();client?.Dispose();listener.Stop();}
            }
            report.AppendLine("Owned loopback FTP and PuTTY only; all data and credentials are synthetic. Interactive real equipment/network behavior still requires field validation.");
            File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-ui-test-report.txt"),report.ToString());
        }
        catch(Exception error){File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-ui-test-report.txt"),report+"\nFAIL: "+error);throw;}
        finally{FormDialog.TestLoaded=null;ThemeService.Apply(originalTheme);}
    }
    private static async Task Settle(MainWindow window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);foreach(var pane in window.VisiblePanesForTest)pane.Host.RefreshViewport();await Task.Delay(150);}
    private static bool HasDarkCanvas(FrameworkElement element)
    {
        element.UpdateLayout();int width=(int)element.ActualWidth,height=(int)element.ActualHeight;
        if(width<2||height<2)return false;
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(width/2,height/2,1,1),pixel,4,0);
        var expected=((SolidColorBrush)Ui.Brush("#111923")).Color;
        return pixel[0]==expected.B&&pixel[1]==expected.G&&pixel[2]==expected.R&&pixel[3]==255;
    }
    private static async Task WaitUntil(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(25,timeout.Token);}
    private static void Enter(UIElement input)=>input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(input)!,Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
    private static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>Descendants(root).OfType<T>().Single(x=>x.Name==name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static IEnumerable<TreeViewItem> TreeItems(TreeViewItem root){yield return root;foreach(var item in root.Items.OfType<TreeViewItem>())foreach(var child in TreeItems(item))yield return child;}
    private static T RunDialog<T>(Func<T> open,Action<FormDialog> action)
    {
        Exception? failure=null;bool loaded=false;var previous=FormDialog.TestLoaded;
        FormDialog.TestLoaded=dialog=>{try{loaded=true;dialog.UpdateLayout();action(dialog);}catch(Exception error){failure=error;}finally{if(dialog.IsVisible)dialog.DialogResult=false;}};
        try{T result=open();if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();if(!loaded)throw new InvalidOperationException("FTP dialog did not load.");return result;}finally{FormDialog.TestLoaded=previous;}
    }
    private static void CaptureDialog(FormDialog dialog,string file)
    {
        dialog.UpdateLayout();var content=(FrameworkElement)dialog.Content;var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom),96,96,PixelFormats.Pbgra32);image.Render(dialog);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(AppPaths.Root,file));encoder.Save(output);
    }
}
