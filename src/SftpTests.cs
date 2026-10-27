using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EquipLink;

internal static class SftpTests
{
    const string Secret="local-sftp-fixture-only";
    internal static async Task RunAsync()
    {
        var report=new StringBuilder();void Check(string name,bool value){if(!value)throw new InvalidOperationException("FAIL: "+name);report.AppendLine("PASS: "+name);}
        Directory.CreateDirectory(AppPaths.Root);
        try
        {
            await using var fixture=await Fixture.StartAsync();
            string remote=Path.Combine(fixture.Root,"remote");
            foreach(string name in new[]{"recipes","reports","한글 폴더","special","denied"})Directory.CreateDirectory(Path.Combine(remote,name));
            File.WriteAllText(Path.Combine(remote,"calibration.csv"),"fixture-only,1\r\n");
            File.WriteAllText(Path.Combine(remote,"special","a ' $() ! file.txt"),"literal filename");
            var target=new Equipment{Name="ENGINEERING · LOCAL SSH",Host="127.0.0.1",Port=fixture.Port,Protocol="SSH",Username="fixture",UseX11=false};
            SftpLogin Login(string password=Secret,string path="")=>new(){Target=target.Copy(),Password=password,InitialPath=path};
            using var browser=new SftpSession(Application.Current.Dispatcher);int approvals=0;
            await browser.ConnectAsync(Login(),_=>{approvals++;return true;});
            Check("Real SSH/SFTP password authentication and directory enumeration",browser.IsConnected&&browser.Entries.Any(x=>x.Name=="한글 폴더"));
            Check("First server key is approved and persisted",approvals==1&&File.Exists(Path.Combine(AppPaths.Root,"sftp-host-keys.json")));
            Check("SFTP requires no shell or exec request",!fixture.Events.Contains("shell-request")&&!fixture.Events.Contains("exec-request"));
            Check("SFTP starts in the server-reported home directory",browser.CurrentPath=="/");
            await browser.NavigateAsync("한글 폴더");Check("Unicode and relative paths navigate through SFTP",browser.CurrentPath=="/한글 폴더");
            await browser.NavigateAsync("../special");Check("Parent paths and shell metacharacters remain file data",browser.Entries.Any(x=>x.Name=="a ' $() ! file.txt"));
            Check("One control-character filename does not suppress valid entries",browser.Entries.Count==1&&browser.Notice.Contains("1개 제외"));
            await browser.NavigateAsync("/");
            bool failed=false;try{await browser.NavigateAsync("/denied");}catch{failed=true;}
            Check("Permission errors preserve the last displayed path",failed&&browser.CurrentPath=="/");
            await browser.CreateDirectoryAsync("새 폴더");Check("SFTP mkdir and refresh expose the new folder",browser.Entries.Any(x=>x.Name=="새 폴더")&&Directory.Exists(Path.Combine(remote,"새 폴더")));
            string local=Path.Combine(AppPaths.Root,"upload.bin");byte[] bytes=Enumerable.Range(0,262147).Select(x=>(byte)(x%251)).ToArray();File.WriteAllBytes(local,bytes);
            await browser.UploadAsync(local);Check("Binary upload commits the complete file",File.ReadAllBytes(Path.Combine(remote,"upload.bin")).SequenceEqual(bytes));
            failed=false;try{await browser.UploadAsync(local);}catch(IOException){failed=true;}
            Check("Duplicate uploads preserve the original",failed&&File.ReadAllBytes(Path.Combine(remote,"upload.bin")).SequenceEqual(bytes));
            string downloaded=Path.Combine(AppPaths.Root,"download.bin");var file=browser.Entries.Single(x=>x.Name=="upload.bin");await browser.DownloadAsync(file,downloaded);
            Check("Binary download matches the original",File.ReadAllBytes(downloaded).SequenceEqual(bytes));
            failed=false;try{await browser.DownloadAsync(file,downloaded);}catch(IOException){failed=true;}
            Check("Duplicate downloads preserve local files",failed&&File.ReadAllBytes(downloaded).SequenceEqual(bytes));
            Check("Completed transfers leave no partial files",!Directory.EnumerateFiles(remote,"*.part").Any()&&!Directory.EnumerateFiles(AppPaths.Root,"*.part").Any());
            Check("Per-tab transfer history includes success and failure",browser.History.Count==4&&browser.History.Any(x=>x.Status=="실패")&&browser.History.Any(x=>x.Status=="완료"));
            browser.Disconnect();await browser.ConnectAsync(Login(),_=>{approvals++;return true;});
            Check("Known server keys reconnect without repeated approval",approvals==1&&browser.IsConnected);
            using var second=new SftpSession(Application.Current.Dispatcher);await second.ConnectAsync(Login(path:"/recipes"),_=>throw new InvalidOperationException("Unexpected trust prompt"));
            await browser.NavigateAsync("/reports");Check("Two sessions keep independent SFTP paths",browser.CurrentPath=="/reports"&&second.CurrentPath=="/recipes");
            second.Disconnect();
            target.PrivateKeyPath=Path.Combine(fixture.Root,"client-encrypted.pem");var keyLogin=Login(password:"");keyLogin.KeyPassphrase="fixture-key-passphrase";
            await second.ConnectAsync(keyLogin,_=>false);Check("Encrypted private key authentication works",second.IsConnected&&fixture.Events.Contains("auth-key:True"));second.Disconnect();target.PrivateKeyPath="";
            failed=false;try{await second.ConnectAsync(Login(password:"wrong"),_=>false);}catch{failed=true;}
            Check("Incorrect credentials fail without a connected browser",failed&&!second.IsConnected);
            File.WriteAllText(Path.Combine(fixture.Root,"disable-sftp"),"");failed=false;try{await second.ConnectAsync(Login(),_=>false);}catch{failed=true;}
            Check("SSH without an SFTP subsystem fails clearly",failed&&!second.IsConnected);File.Delete(Path.Combine(fixture.Root,"disable-sftp"));
            File.WriteAllText(Path.Combine(fixture.Root,"rotate-host-key"),"");int authBefore=fixture.Events.Split("auth-password:").Length;
            failed=false;try{await second.ConnectAsync(Login(),_=>true);}catch(Exception e){failed=e.Message.Contains("서버 키");}
            Check("Changed host keys are rejected before password authentication",failed&&fixture.Events.Split("auth-password:").Length==authBefore);
            File.Delete(Path.Combine(fixture.Root,"rotate-host-key"));
            var telnet=target.Copy();telnet.Protocol="Telnet";failed=false;try{await second.ConnectAsync(new(){Target=telnet,Password=Secret},_=>true);}catch(InvalidOperationException){failed=true;}
            Check("Telnet cannot create an SFTP browser connection",failed&&!second.IsConnected);
            foreach(string bad in new[]{"a\nb","a\rb","a\0b"}){failed=false;try{await browser.NavigateAsync(bad);}catch(ArgumentException){failed=true;}Check("Control path rejected before network operation",failed);}
            string canceledPath=Path.Combine(AppPaths.Root,"canceled-download.bin");
            var canceledDownload=browser.DownloadAsync(file,canceledPath);browser.Disconnect();failed=false;
            try{await canceledDownload;}catch(OperationCanceledException){failed=true;}
            Check("Disconnect cancels pending SFTP work without a partial destination",failed&&!File.Exists(canceledPath)&&!Directory.EnumerateFiles(AppPaths.Root,"*.part").Any()&&!browser.IsConnected);
            await browser.ConnectAsync(Login(),_=>false);
            Check("SFTP reconnects after cancellation with an independent path",browser.IsConnected&&browser.CurrentPath=="/");
            foreach(string theme in new[]{"midnight","graphite","nord"})await TestUi(fixture,target,theme,Check);
            await TestRejectedTerminalLogin(fixture,target,Check);
            report.AppendLine("Real loopback SSH/SFTP with synthetic credentials only. Company equipment, actual firewall configuration, PuTTY proxy/jump-host routing and physical foreground input were not tested.");
        }
        catch(Exception error){report.AppendLine("FAIL: "+error);throw;}
        finally{File.WriteAllText(Path.Combine(AppPaths.Root,"sftp-test-report.txt"),report.ToString());}
    }
    static async Task TestUi(Fixture fixture,Equipment target,string theme,Action<string,bool> check)
    {
        string originalTheme=ThemeService.Current.Id;ThemeService.Apply(theme);var store=new AppStore{Settings=new(){AutoStartX=false,ThemeId=theme}};
        target.EncryptedPassword=CredentialProtector.Protect(target,Secret);store.Equipment.Add(target.Copy());
        using var keys=Registry.CurrentUser.CreateSubKey(@"Software\SimonTatham\PuTTY\SshHostKeys");string cacheKey="rsa2@"+fixture.Port+":127.0.0.1";keys.SetValue(cacheKey,fixture.PuttyKey);
        var window=new MainWindow(store){Width=1440,Height=940,Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false};SessionPane? pane=null;
        try
        {
            int authBefore=fixture.Events.Split("auth-none").Length,shellBefore=fixture.Events.Split("shell-request").Length;
            window.Show();pane=await window.ConnectForTestAsync(target);await Wait(()=>fixture.Events.Split("auth-none").Length>authBefore);await Task.Delay(350);
            await Wait(()=>fixture.Events.Split("shell-request").Length>shellBefore);
            check("Actual PuTTY SSH logs in with the saved password without manual input",fixture.Events.Contains("auth-password:True",StringComparison.Ordinal));
            var dimensions=pane.Host.ClientSizePixels;var pid=pane.Host.ProcessId;
            All(window).OfType<Button>().Single(x=>x.Content is TextBlock{Text:"SSH Browser"}).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
            var panel=All(window).OfType<SftpBrowserPanel>().Single();
            FormDialog.TestLoaded=dialog=>
            {
                if(dialog.Title.StartsWith("SSH Browser"))Find<Button>(dialog,"SftpConnectSubmit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if(dialog.Title.StartsWith("SFTP 서버 키"))Find<Button>(dialog,"SftpTrustHost").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.DialogResult=false;
            };
            Find<Button>(panel,"SftpConnect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>pane.Sftp.IsConnected&&!pane.Sftp.IsBusy);await Settle(window);FormDialog.TestLoaded=null;
            check("Actual SFTP Connect button uses the saved equipment password",pane.Sftp.Entries.Any(e=>e.Name=="recipes"));
            check("Saved passwords remain encrypted in the settings file",!File.ReadAllText(AppPaths.Config).Contains(Secret));
            int inputBefore=fixture.Events.Split("shell-input:").Length;
            var item=Find<ListBox>(panel,"SftpEntries").Items.OfType<ListBoxItem>().Single(x=>x.Tag is SftpEntry{Name:"한글 폴더"});
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            await Wait(()=>pane.Sftp.CurrentPath=="/한글 폴더"&&!pane.Sftp.IsBusy);
            check("Folder clicks navigate the SFTP browser",pane.Sftp.CurrentPath=="/한글 폴더");
            var address=Find<TextBox>(panel,"SftpAddress");address.Text="/recipes";
            address.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(address)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
            await Wait(()=>pane.Sftp.CurrentPath=="/recipes"&&!pane.Sftp.IsBusy);
            check("Address Enter navigates without shell commands",fixture.Events.Split("shell-input:").Length==inputBefore);
            Find<Button>(panel,"SftpUp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>pane.Sftp.CurrentPath=="/"&&!pane.Sftp.IsBusy);
            Find<Button>(panel,"SftpRefresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!pane.Sftp.IsBusy);await Settle(window);
            check("Browsing preserves the terminal process and dimensions",pane.Host.ProcessId==pid&&pane.Host.ClientSizePixels==dimensions);
            check("Legacy shell-integration controls are absent",!All(window).OfType<Button>().Any(x=>x.Name.StartsWith("TerminalFolder")));
            panel.Refresh();await Settle(window);PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"sftp-"+theme+".png"));check(theme+" SFTP sidebar retains terminal space",window.SidebarWidthForTest==224&&panel.ActualHeight>300);
            window.ShowHomeForTest();await Settle(window);check("Home view releases the selected SFTP browser",Find<TextBox>(panel,"SftpAddress").Text=="");
            window.ShowSessionForTest(pane);await Settle(window);check("Returning to a tab restores its SFTP path",Find<TextBox>(panel,"SftpAddress").Text=="/");
        }
        finally{FormDialog.TestLoaded=null;if(pane!=null)window.CloseForTest(pane);window.Close();keys.DeleteValue(cacheKey,false);ThemeService.Apply(originalTheme);}
    }
    static async Task TestRejectedTerminalLogin(Fixture fixture,Equipment original,Action<string,bool> check)
    {
        var target=original.Copy();target.EncryptedPassword=CredentialProtector.Protect(target,"wrong-fixture-password");
        var store=new AppStore{Settings=new(){AutoStartX=false},Equipment=[target]};
        using var keys=Registry.CurrentUser.CreateSubKey(@"Software\SimonTatham\PuTTY\SshHostKeys");string cacheKey="rsa2@"+fixture.Port+":127.0.0.1";keys.SetValue(cacheKey,fixture.PuttyKey);
        var window=new MainWindow(store){Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false};SessionPane? pane=null;
        try
        {
            int before=fixture.Events.Split("auth-password:False").Length;window.Show();pane=await window.ConnectForTestAsync(target);
            await Wait(()=>fixture.Events.Split("auth-password:False").Length>before);await Task.Delay(800);
            check("Rejected SSH password is attempted once without an authentication retry loop",fixture.Events.Split("auth-password:False").Length==before+1);
            int shellBefore=fixture.Events.Split("shell-request").Length;
            target.EncryptedPassword=CredentialProtector.Protect(target,Secret);store.Save();
            pane.Host.CloseSession();await pane.ConnectAsync();await Wait(()=>fixture.Events.Split("shell-request").Length>shellBefore);
            check("Reconnecting an existing SSH tab uses the corrected saved password",fixture.Events.Split("shell-request").Length==shellBefore+1&&pane.Equipment.EncryptedPassword!=target.EncryptedPassword);
        }
        finally{if(pane!=null)window.CloseForTest(pane);window.Close();keys.DeleteValue(cacheKey,false);}
    }
    static T Find<T>(DependencyObject root,string name)where T:FrameworkElement=>All(root).OfType<T>().Single(x=>x.Name==name);
    static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in All(VisualTreeHelper.GetChild(root,i)))yield return child;}
    static async Task Wait(Func<bool> ready){var until=DateTime.UtcNow.AddSeconds(20);while(!ready()){if(DateTime.UtcNow>until)throw new TimeoutException("SFTP test fixture timed out");await Task.Delay(40);}}
    static async Task Settle(MainWindow window){await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);foreach(var pane in window.VisiblePanesForTest)pane.Host.RefreshViewport();await Task.Delay(100);}
    sealed class Fixture:IAsyncDisposable
    {
        public required Process Process;public required string Root;public int Port;public required string PuttyKey;
        public string Events
        {
            get
            {
                string path=Path.Combine(Root,"events.txt");if(!File.Exists(path))return "";
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(input);return reader.ReadToEnd();
            }
        }
        public static async Task<Fixture> StartAsync()
        {
            string root=Path.Combine(AppPaths.Root,"server");Directory.CreateDirectory(root);
            string python=Environment.GetEnvironmentVariable("EQUIPLINK_TEST_PYTHON")??"python";
            string fixture=Environment.GetEnvironmentVariable("EQUIPLINK_SFTP_FIXTURE")??Path.GetFullPath(Path.Combine("tests","sftp-fixture.py"));
            if(!File.Exists(fixture))throw new FileNotFoundException("Run tests/test.ps1 from the repository or set EQUIPLINK_SFTP_FIXTURE.",fixture);
            var info=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add(fixture);info.ArgumentList.Add(root);
            var process=Process.Start(info)!;_=process.StandardError.ReadToEndAsync();
            try{string line=(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!;using var json=JsonDocument.Parse(line);return new(){Process=process,Root=root,Port=json.RootElement.GetProperty("port").GetInt32(),PuttyKey=json.RootElement.GetProperty("puttyKey").GetString()!};}
            catch{if(!process.HasExited)process.Kill(true);process.Dispose();throw;}
        }
        public async ValueTask DisposeAsync(){if(!Process.HasExited)Process.Kill(true);await Process.WaitForExitAsync();Process.Dispose();}
    }
}
