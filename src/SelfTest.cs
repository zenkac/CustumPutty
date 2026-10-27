using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace EquipLink;

public static class SelfTest
{
    public static async Task<string> RunAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);var results=new List<string>();
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);results.Add("PASS: "+label);}
        void Throws(string label,Action action){try{action();}catch(ArgumentException){results.Add("PASS: "+label);return;}throw new InvalidOperationException("FAIL: "+label);}
        var preferences=new Preferences();var telnet=new Equipment{Name="테스트 설비",Host="192.0.2.10"};telnet.Validate();
        Check("Telnet default port",telnet.Port==23 && !telnet.ForwardX11);
        Check("Exact requested xterm command",Commands.Xterm(telnet,preferences,"192.0.2.100")=="xterm -display 192.0.2.100:0.0 &");
        Check("POSIX DISPLAY syntax",Commands.SetDisplay(telnet,preferences,"192.0.2.100")=="export DISPLAY=192.0.2.100:0.0");
        telnet.Shell="csh";Check("csh DISPLAY syntax",Commands.SetDisplay(telnet,preferences,"192.0.2.100")=="setenv DISPLAY 192.0.2.100:0.0");
        var ssh=new Equipment{Host="192.0.2.20",Protocol="SSH",Port=22};ssh.Validate();
        Check("SSH enables forwarding",ssh.ForwardX11);
        Check("SSH preserves forwarded DISPLAY",Commands.Xterm(ssh,preferences,"192.0.2.100")=="xterm &" && Commands.SetDisplay(ssh,preferences,"192.0.2.100")=="printenv DISPLAY");
        ssh.X11Mode="Direct";Check("SSH explicit direct mode",!ssh.ForwardX11 && Commands.Xterm(ssh,preferences,"192.0.2.100").Contains("-display"));
        preferences.DisplayNumber=2;Check("Non-default display number",Commands.Xterm(telnet,preferences,"192.0.2.100").Contains(":2.0"));
        Throws("Reject command injection in host",()=>new Equipment{Host="-proxycmd calc"}.Validate());
        Throws("Reject invalid port",()=>new Equipment{Host="192.0.2.1",Port=65536}.Validate());
        Throws("Reject DISPLAY shell injection",()=>Commands.Display("192.0.2.1;touch bad",0));
        Check("Normalize command newlines once",Commands.NormalizeInput("echo first\r\necho second\n",true)=="echo first\recho second\r");
        Check("No-execute single line has no return",Commands.NormalizeInput("pwd",false)=="pwd");
        var store=new AppStore{Equipment=[telnet,ssh]};store.Save();var loaded=AppStore.Load();Check("Profiles saved and loaded",loaded.Equipment.Count==2 && loaded.Equipment[0].Name=="테스트 설비");
        loaded.Equipment[0].Name="갱신된 설비";loaded.Save();Check("Atomic save keeps backup",File.Exists(AppPaths.Config+".bak") && AppStore.Load().Equipment[0].Name=="갱신된 설비");
        CredentialTests.Run(Check);
        var executable=PuttyService.Executable();Check("Bundled official PuTTY SHA-256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)))==PuttyService.OfficialHash);
        var start=PuttyService.StartInfo(executable,"a session with spaces",false);Check("Arguments are not shell-concatenated",!start.UseShellExecute && start.ArgumentList.Count==2 && start.ArgumentList[1]=="a session with spaces");
        CheckTerminalTheme(Check);
        Check("F11 toggles focus without consuming modified F11",AppShortcuts.MatchShortcut(0x7A,false,false,false,false)=="focus" && AppShortcuts.MatchShortcut(0x7A,true,false,false,false)==null && AppShortcuts.MatchShortcut(0x7A,false,true,false,false)==null);
        Check("Workspace shortcut mapping",AppShortcuts.MatchShortcut(0x42,true,true,false,false)=="sidebar" && AppShortcuts.MatchShortcut(0x4B,true,true,false,false)=="quick" && AppShortcuts.MatchShortcut(0x4A,true,true,false,false)=="composer" && AppShortcuts.MatchShortcut(0x0D,true,true,false,false)=="zoom");
        Check("Terminal control keys and normal typing remain unclaimed",new uint[]{0x42,0x4B,0x4A,0x0D,0x43,0x44}.All(key=>AppShortcuts.MatchShortcut(key,true,false,false,false)==null && AppShortcuts.MatchShortcut(key,false,false,false,false)==null));
        Check("Alt and Windows modifiers do not trigger workspace shortcuts",new uint[]{0x7A,0x42,0x4B,0x4A,0x0D}.All(key=>AppShortcuts.MatchShortcut(key,true,true,true,false)==null && AppShortcuts.MatchShortcut(key,true,true,false,true)==null));
        var config=Path.Combine(AppPaths.Root,"test.xlaunch");File.WriteAllText(config,"<XLaunch Display=\"2\" WindowMode=\"MultiWindow\" />");Check("Read XLaunch display",XServerService.ConfigDisplay(config)==2);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{Check("Port probe accepts local listener",await NetworkTools.PortOpenAsync("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port));}finally{listener.Stop();}
        results.Add("All core checks passed. No real equipment connections were made.");return string.Join(Environment.NewLine,results);
    }

    private static void CheckTerminalTheme(Action<string,bool> check)
    {
        // A temporary imported profile deliberately has a clashing palette, a locked
        // viewport and scrollbars. Preparing a session must override only the app's
        // presentation settings while preserving unrelated protocol preferences.
        string sourceName="EquipLink_ThemeFixture_"+Guid.NewGuid().ToString("N");
        string sourceKey=PuttyService.EncodeSessionName(sourceName);string? prepared=null;
        using var root=Registry.CurrentUser.CreateSubKey(PuttyService.SessionsKey);
        try
        {
            using(var imported=root.CreateSubKey(sourceKey))
            {
                for(int index=0;index<22;index++)imported.SetValue("Colour"+index,"255,0,255",RegistryValueKind.String);
                foreach(string name in new[]{"UseSystemColours","TryPalette","ScrollBar","ScrollBarFullScreen","SunkenEdge"})imported.SetValue(name,1,RegistryValueKind.DWord);
                imported.SetValue("LockSize",2,RegistryValueKind.DWord);imported.SetValue("WindowBorder",50,RegistryValueKind.DWord);
                imported.SetValue("ProxyExcludeList","fixture.example.invalid",RegistryValueKind.String);
            }
            prepared=PuttyService.Prepare(new Equipment{Name="Theme fixture",Host="127.0.0.1",BasePuttySession=sourceName},new Preferences(),out _);
            using var actual=root.OpenSubKey(prepared)!;
            string[] palette=["220,229,240","244,248,253","17,25,35","17,25,35","17,25,35","93,214,190","17,25,35","94,110,131","231,132,148","255,164,179","126,207,161","159,231,188","222,192,135","247,216,161","125,174,234","161,201,255","184,159,232","212,184,255","108,203,208","148,231,231","201,214,230","244,248,253"];
            for(int index=0;index<palette.Length;index++)
                check("PuTTY Colour"+index+" overrides an imported mismatched theme",actual.GetValue("Colour"+index) as string==palette[index]);
            check("Native background matches the workspace theme",actual.GetValue("Colour2") as string=="17,25,35" && actual.GetValue("Colour3") as string=="17,25,35");
            var expected=new Dictionary<string,int>{{"LockSize",0},{"WindowBorder",6},{"ScrollBar",0},{"ScrollBarFullScreen",0},{"SunkenEdge",0},{"FontQuality",3},{"UseSystemColours",0},{"TryPalette",0},{"ANSIColour",1},{"Xterm256Colour",1},{"TrueColour",1},{"BoldAsColour",2}};
            foreach(var value in expected)check("PuTTY "+value.Key+" is enforced after profile import",Convert.ToInt32(actual.GetValue(value.Key)??-1)==value.Value);
            check("Theme preparation preserves unrelated imported settings",actual.GetValue("ProxyExcludeList") as string=="fixture.example.invalid");
        }
        finally{PuttyService.Cleanup(prepared);root.DeleteSubKey(sourceKey,false);}
    }

    public static async Task SmokeAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var report=new StringBuilder();
        void Check(string text,bool condition){if(!condition)throw new InvalidOperationException("FAIL: "+text);report.AppendLine("PASS: "+text);}
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var host=new TerminalHost();
        var window=new Window{Title="EquipLink local integration check",Width=800,Height=600,Left=-30000,Top=-30000,ShowInTaskbar=false,Content=host};
        Application.Current.MainWindow=window;
        try
        {
            var accept=listener.AcceptTcpClientAsync();
            var equipment=new Equipment{Name="LOCAL TEST ONLY",Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,UseX11=false,LogSession=true};
            window.Show();await host.StartAsync(equipment,new Preferences());
            using var client=await accept.WaitAsync(TimeSpan.FromSeconds(5));using var stream=client.GetStream();
            Check("PuTTY established real loopback Telnet TCP connection",client.Connected);
            Check("PuTTY window embedded as child",host.Embedded && Native.GetParent(host.TerminalHandle)!=IntPtr.Zero);
            await stream.WriteAsync(new byte[]{255,251,1,255,251,3,255,253,31});
            await stream.WriteAsync(Encoding.UTF8.GetBytes("\r\n\u001b[32mEquipLink integration fixture\u001b[0m\r\nfixture$ "));
            await Task.Delay(350);
            host.Send("printf EquipLink_SMOKE",true);
            var received=new List<byte>();var buffer=new byte[4096];using var cancellation=new CancellationTokenSource(7000);
            while(!Encoding.UTF8.GetString(received.ToArray()).Contains("EquipLink_SMOKE"))
            {int length=await stream.ReadAsync(buffer,cancellation.Token);if(length==0)break;received.AddRange(buffer[..length]);}
            Check("Command reached actual Telnet socket",Encoding.UTF8.GetString(received.ToArray()).Contains("printf EquipLink_SMOKE"));
            var handle=host.TerminalHandle;window.Width=960;window.Height=700;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(150);
            Native.GetClientRect(handle,out var rect);Check("Embedded terminal resizes",rect.Right>850 && rect.Bottom>500);
            host.Visibility=Visibility.Collapsed;await Task.Delay(100);host.Visibility=Visibility.Visible;await Task.Delay(100);Check("Tab hide/show preserves terminal process",host.HasProcess && host.TerminalHandle==handle);
            host.Detach();Check("Detach restores a top-level window",host.External && Native.GetParent(handle)==IntPtr.Zero);Native.ShowWindow(handle,0);
            var log=host.LogFile;host.CloseSession();Check("Close stops only owned child",!host.HasProcess);
            Check("Session output log created",log!=null && File.Exists(log) && File.ReadAllText(log).Contains("EquipLink integration fixture"));
            var secondAccept=listener.AcceptTcpClientAsync();await host.StartAsync(equipment,new Preferences());using var second=await secondAccept.WaitAsync(TimeSpan.FromSeconds(5));Check("Reconnect starts a new embedded terminal",host.Embedded && second.Connected);host.CloseSession();
            await WorkspaceSmokeAsync(report);
            await CredentialTests.SmokeAsync(report);
            report.AppendLine("No real equipment was contacted. SSH authentication and a real remote X11 GUI require the user's network.");
            File.WriteAllText(Path.Combine(AppPaths.Root,"smoke-test-report.txt"),report.ToString());
        }
        catch(Exception exception){File.WriteAllText(Path.Combine(AppPaths.Root,"smoke-test-report.txt"),report+"\nFAIL: "+exception);throw;}
        finally{host.CloseSession();window.Close();host.Dispose();listener.Stop();}
    }
    private static async Task WorkspaceSmokeAsync(StringBuilder report)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var equipment=new Equipment{Name="WORKSPACE TEST",Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,UseX11=true};
        var data=new AppStore{Settings=new(){AutoStartX=false,PcAddress="192.0.2.100"},Equipment=[equipment]};
        var window=new MainWindow(data){Width=1440,Height=940,Left=-30000,Top=-30000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};
        var fixtures=new List<WorkspaceFixture>();
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);report.AppendLine("PASS: "+label);}
        async Task Settle()
        {
            await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            foreach(var current in window.VisiblePanesForTest)current.Host.RefreshViewport();
            await Task.Delay(200);
        }
        void CheckAlive(string label)
        {
            Check(label,fixtures.Where(f=>!f.Closed).All(f=>f.Pane.Host.HasProcess && f.Pane.Host.Embedded && f.Pane.Host.TerminalHandle==f.Handle));
        }
        void CheckGeometry(string label,int count,int columns,int rows)
        {
            var panes=window.VisiblePanesForTest.ToArray();
            Check(label+" has the expected visible pane count",panes.Length==count && panes.All(p=>p.IsVisible));
            var bounds=panes.Select(p=>new Rect(p.TranslatePoint(new Point(0,0),window),p.RenderSize)).ToArray();
            bool nonoverlap=true;
            for(int a=0;a<bounds.Length;a++)for(int b=a+1;b<bounds.Length;b++)
            {
                var intersection=Rect.Intersect(bounds[a],bounds[b]);
                if(!intersection.IsEmpty && intersection.Width>1 && intersection.Height>1)nonoverlap=false;
            }
            Check(label+" pane rectangles do not overlap",nonoverlap);
            // Group positions within two device-independent pixels to tolerate fractional grid layout.
            int Groups(IEnumerable<double> positions)
            {
                var sorted=positions.Order().ToArray();int groups=0;double last=double.NegativeInfinity;
                foreach(double value in sorted)if(value-last>2){groups++;last=value;}
                return groups;
            }
            Check(label+" lays out the requested rows and columns",Groups(bounds.Select(b=>b.X))==columns && Groups(bounds.Select(b=>b.Y))==rows);
            bool fills=panes.All(p=>
            {
                var size=p.Host.ClientSizePixels;var dpi=VisualTreeHelper.GetDpi(p.Host);
                double expectedWidth=p.Host.ActualWidth*dpi.DpiScaleX,expectedHeight=p.Host.ActualHeight*dpi.DpiScaleY;
                return size.Width>200 && size.Height>120 && Math.Abs(size.Width-expectedWidth)<=40 && Math.Abs(size.Height-expectedHeight)<=4;
            });
            if(!fills)
            {
                report.AppendLine($"DIAGNOSTIC: {label}; window Actual={window.ActualWidth:F2}x{window.ActualHeight:F2}; window position={window.Left:F2},{window.Top:F2}; focus={window.FocusModeForTest}; sidebar={window.SidebarWidthForTest:F2}");
                for(int index=0;index<panes.Length;index++)
                {
                    var pane=panes[index];var host=pane.Host;var dpi=VisualTreeHelper.GetDpi(host);var size=host.ClientSizePixels;
                    var origin=host.TranslatePoint(new Point(0,0),window);Native.GetClientRect(host.Handle,out var container);
                    report.AppendLine($"DIAGNOSTIC: pane[{index}] '{pane.Equipment.Name}' Actual={pane.ActualWidth:F2}x{pane.ActualHeight:F2} Render={pane.RenderSize.Width:F2}x{pane.RenderSize.Height:F2} bounds={bounds[index]}; Host Actual={host.ActualWidth:F2}x{host.ActualHeight:F2} Render={host.RenderSize.Width:F2}x{host.RenderSize.Height:F2} origin={origin}; DPI={dpi.DpiScaleX:F3},{dpi.DpiScaleY:F3}; expected pixels={host.ActualWidth*dpi.DpiScaleX:F2}x{host.ActualHeight*dpi.DpiScaleY:F2}; native terminal={size.Width}x{size.Height}; native container={container.Right-container.Left}x{container.Bottom-container.Top}; visibility={host.Visibility}/{host.IsVisible}; embedded={host.Embedded}; hwnd={host.TerminalHandle}");
                }
            }
            Check(label+" native terminal fills each pane",fills);
        }
        async Task DragDivider(GridResizeDirection direction,double delta)
        {
            var grid=(Grid)window.VisiblePanesForTest[0].Parent;
            var divider=grid.Children.OfType<GridSplitter>().Single(s=>s.ResizeDirection==direction);
            double x=direction==GridResizeDirection.Columns?delta:0,y=direction==GridResizeDirection.Rows?delta:0;
            // Invoke the same public routed drag events handled by WPF's GridSplitter.
            // No system mouse input, foreground activation or private reflection is used.
            divider.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            divider.RaiseEvent(new DragDeltaEventArgs(x,y){RoutedEvent=Thumb.DragDeltaEvent});
            divider.RaiseEvent(new DragCompletedEventArgs(x,y,false){RoutedEvent=Thumb.DragCompletedEvent});
            await Settle();
        }
        try
        {
            window.Show();
            async Task<WorkspaceFixture> Connect(int index)
            {
                var item=equipment.Copy();item.Id=Guid.NewGuid().ToString("N");item.Name="LOCAL LAYOUT "+index;
                var accept=listener.AcceptTcpClientAsync();var pane=await window.ConnectForTestAsync(item);
                var client=await accept.WaitAsync(TimeSpan.FromSeconds(5));var fixture=new WorkspaceFixture(pane,client);fixtures.Add(fixture);
                await fixture.InitializeAsync();await Settle();return fixture;
            }
            var first=await Connect(1);
            await first.Pane.OpenXtermForTestAsync();await first.ExpectAsync("xterm -display 192.0.2.100:0.0 &");
            Check("Full workspace xterm action sends the requested command through PuTTY",true);
            window.ShowHomeForTest();await Settle();window.ShowSessionForTest(first.Pane);await Settle();
            CheckAlive("Full workspace home/session switching retains the connection");
            data.Settings.PcAddress="192.0.2.101";await first.Pane.OpenXtermForTestAsync();await first.ExpectAsync("xterm -display 192.0.2.101:0.0 &");
            Check("PC IP changes apply to an already-open Telnet session",true);

            for(int index=2;index<=5;index++)await Connect(index);
            Check("Five independent PuTTY processes and sockets are open",fixtures.Select(f=>f.Pane.Host.ProcessId).Distinct().Count()==5 && fixtures.All(f=>f.Connected));
            window.SetLayoutForTest("single");await Settle();CheckGeometry("Single view",1,1,1);
            var fullHeight=window.VisiblePanesForTest[0].Host.ActualHeight;
            Check("Single terminal keeps at least 70 percent of window height",fullHeight>=window.ActualHeight*.70);
            window.SetLayoutForTest("columns");await Settle();CheckGeometry("Side-by-side split",2,2,1);CheckAlive("Two-column split retains all five sessions");
            window.SetLayoutForTest("rows");await Settle();CheckGeometry("Top-bottom split",2,1,2);CheckAlive("Two-row split retains all five sessions");
            window.SetLayoutForTest("grid");await Settle();CheckGeometry("Four-pane split",4,2,2);CheckAlive("Four-pane split retains all five sessions");

            var beforeDrag=window.VisiblePanesForTest.Select(p=>p.Host.ClientSizePixels).ToArray();
            await DragDivider(GridResizeDirection.Columns,80);
            var afterColumns=window.VisiblePanesForTest.Select(p=>p.Host.ClientSizePixels).ToArray();
            Check("Dragging a vertical divider resizes both live terminal columns",afterColumns[0].Width>beforeDrag[0].Width+40 && afterColumns[2].Width>beforeDrag[2].Width+40 && afterColumns[1].Width<beforeDrag[1].Width-40 && afterColumns[3].Width<beforeDrag[3].Width-40);
            await DragDivider(GridResizeDirection.Rows,55);
            var afterRows=window.VisiblePanesForTest.Select(p=>p.Host.ClientSizePixels).ToArray();
            Check("Dragging a horizontal divider resizes both live terminal rows",afterRows[0].Height>afterColumns[0].Height+30 && afterRows[1].Height>afterColumns[1].Height+30 && afterRows[2].Height<afterColumns[2].Height-30 && afterRows[3].Height<afterColumns[3].Height-30);
            CheckGeometry("Dragged four-pane split",4,2,2);CheckAlive("Dragging split dividers preserves every connection");
            PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"workspace-4panes.png"));

            var nativeTarget=window.VisiblePanesForTest[1];bool activationObserved=false;
            void OnNativeActivated()=>activationObserved=true;
            nativeTarget.Host.Activated+=OnNativeActivated;
            try
            {
                nativeTarget.Host.FocusTerminal();await Settle();
                if(activationObserved)Check("Native PuTTY focus selects its pane without replacing slots",window.ActiveForTest==nativeTarget && window.VisiblePanesForTest.Count==4);
                else report.AppendLine("NOT TESTED: Native focus notification was unavailable in the offscreen test window; no foreground activation was forced.");
            }
            finally{nativeTarget.Host.Activated-=OnNativeActivated;}

            var original=window.VisiblePanesForTest.ToArray();var zoomTarget=original[2];
            window.ToggleZoomForTest(zoomTarget);await Settle();
            Check("Zoom shows only the selected pane",window.VisiblePanesForTest.Count==1 && window.VisiblePanesForTest[0]==zoomTarget);
            Check("Zoom increases actual terminal height",zoomTarget.Host.ActualHeight>fullHeight*.85);
            CheckAlive("Zoom leaves hidden sessions alive");
            window.ToggleZoomForTest(zoomTarget);await Settle();
            Check("Unzoom restores the same four pane slots",window.VisiblePanesForTest.SequenceEqual(original));CheckGeometry("Restored four-pane split",4,2,2);
            Check("Unzoom preserves the dragged split proportions",window.VisiblePanesForTest.Select((p,i)=>Math.Abs(p.Host.ClientSizePixels.Width-afterRows[i].Width)<=3 && Math.Abs(p.Host.ClientSizePixels.Height-afterRows[i].Height)<=3).All(same=>same));

            var beforeHome=window.VisiblePanesForTest.ToArray();var current=window.ActiveForTest!;
            window.ShowHomeForTest();await Settle();CheckAlive("Home view retains all split session processes");
            window.ShowSessionForTest(current);await Settle();
            Check("Returning from home restores the split",window.VisiblePanesForTest.SequenceEqual(beforeHome));
            Check("Home return preserves the dragged split proportions",window.VisiblePanesForTest.Select((p,i)=>Math.Abs(p.Host.ClientSizePixels.Width-afterRows[i].Width)<=3 && Math.Abs(p.Host.ClientSizePixels.Height-afterRows[i].Height)<=3).All(same=>same));

            var originalSidebar=window.SidebarWidthForTest;var beforeCollapse=window.VisiblePanesForTest.Sum(p=>p.Host.ActualWidth);
            window.ToggleSidebarForTest();await Settle();
            Check("Collapsing sidebar gives native terminals more width",window.SidebarWidthForTest<originalSidebar && window.VisiblePanesForTest.Sum(p=>p.Host.ActualWidth)>beforeCollapse+100);
            CheckAlive("Sidebar collapse retains all sessions");window.ToggleSidebarForTest();await Settle();
            Check("Sidebar width restores",Math.Abs(window.SidebarWidthForTest-originalSidebar)<2);
            var beforeFocusHeight=window.VisiblePanesForTest[0].Host.ActualHeight;
            window.ToggleFocusForTest();await Settle();
            Check("Focus mode increases available terminal height",window.FocusModeForTest && window.VisiblePanesForTest[0].Host.ActualHeight>beforeFocusHeight);
            PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"workspace-focus.png"));
            window.ToggleFocusForTest();await Settle();
            Check("Exiting focus restores the workspace",!window.FocusModeForTest && window.VisiblePanesForTest.SequenceEqual(beforeHome) && Math.Abs(window.SidebarWidthForTest-originalSidebar)<2);

            var selected=window.VisiblePanesForTest[1];window.ShowSessionForTest(selected);await Settle();
            Check("Selecting an already-visible tab keeps every split slot",window.ActiveForTest==selected && window.VisiblePanesForTest.SequenceEqual(beforeHome));
            var selectedFixture=fixtures.Single(f=>f.Pane==selected);string marker="ONLY_ACTIVE_"+Guid.NewGuid().ToString("N");
            window.ActiveForTest!.Host.Send("echo "+marker,true);await selectedFixture.ExpectAsync(marker);await Task.Delay(150);
            Check("Active-pane command reaches only its own Telnet socket",fixtures.Where(f=>f!=selectedFixture).All(f=>!f.ReceivedContains(marker)));

            var hidden=fixtures.Single(f=>!window.VisiblePanesForTest.Contains(f.Pane));var activeSlot=Array.IndexOf(beforeHome,selected);
            window.ShowSessionForTest(hidden.Pane);await Settle();var replaced=window.VisiblePanesForTest.ToArray();
            Check("Hidden tab replaces only the active pane slot",window.ActiveForTest==hidden.Pane && replaced.Length==4 && replaced[activeSlot]==hidden.Pane && replaced.Where((p,i)=>i!=activeSlot).SequenceEqual(beforeHome.Where((p,i)=>i!=activeSlot)));
            CheckAlive("Hidden-tab replacement preserves background terminals");

            window.CloseForTest(hidden.Pane);hidden.Closed=true;await Settle();
            Check("Closing one pane terminates only that terminal",!hidden.Pane.Host.HasProcess && window.VisiblePanesForTest.Count==4 && !window.VisiblePanesForTest.Contains(hidden.Pane));
            CheckAlive("Closing a pane retains all four remaining sessions");
            Check("Closing a pane fills the gap from a background session",window.VisiblePanesForTest.Contains(selected));
            CheckGeometry("Four-pane split after closing a tab",4,2,2);

            window.SetLayoutForTest("single");await Settle();CheckGeometry("Return to single view",1,1,1);CheckAlive("Returning to single view retains background connections");
            PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"workspace-single.png"));
            foreach(var fixture in fixtures.Where(f=>!f.Closed))
            {
                window.ShowSessionForTest(fixture.Pane);await Settle();string reconnectMarker="STILL_LIVE_"+Guid.NewGuid().ToString("N");
                fixture.Pane.Host.Send("echo "+reconnectMarker,true);await fixture.ExpectAsync(reconnectMarker);
            }
            Check("Every surviving session still exchanges data after all layout transitions",true);
        }
        finally
        {
            foreach(var fixture in fixtures)fixture.Pane.Host.CloseSession();
            window.Close();foreach(var fixture in fixtures)await fixture.DisposeAsync();listener.Stop();
        }
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        public SessionPane Pane { get; }
        public IntPtr Handle { get; }
        public bool Closed { get; set; }
        public bool Connected=>client.Connected;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly CancellationTokenSource cancellation=new();
        private readonly StringBuilder received=new();
        private readonly Task reader;
        public WorkspaceFixture(SessionPane pane,TcpClient client)
        {
            Pane=pane;Handle=pane.Host.TerminalHandle;this.client=client;stream=client.GetStream();reader=ReadAsync();
        }
        public async Task InitializeAsync()
        {
            await stream.WriteAsync(new byte[]{255,251,1,255,251,3,255,253,31});
            var screen="\u001b[2J\u001b[H\u001b[36m"+Pane.Equipment.Name+"  |  LOCAL SIMULATION\u001b[0m\r\n"+
                "Loopback fixture only - no equipment contacted\r\n"+
                "\r\n\u001b[32mfixture$\u001b[0m inspect --sample\r\n"+
                "  Target      127.0.0.1 (local fixture)\r\n"+
                "  Transport   Telnet / embedded PuTTY\r\n"+
                "  Uptime      12d 04h              [SAMPLE]\r\n"+
                "  CPU         18%                 [SAMPLE]\r\n"+
                "  Disk        42% used            [SAMPLE]\r\n"+
                "\r\n\u001b[33mTEST STATUS  Sample output - not equipment results\u001b[0m\r\n"+
                "  01  sensor check              sample ready\r\n"+
                "  02  GUI environment           sample idle\r\n"+
                "\r\n\u001b[32mfixture$\u001b[0m ";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(screen));
            await Task.Delay(150);
        }
        private async Task ReadAsync()
        {
            var buffer=new byte[4096];
            try
            {
                while(!cancellation.IsCancellationRequested)
                {
                    int count=await stream.ReadAsync(buffer,cancellation.Token);if(count==0)break;
                    lock(received)received.Append(Encoding.UTF8.GetString(buffer,0,count));
                }
            }
            catch(OperationCanceledException){}catch(IOException){}catch(ObjectDisposedException){}
        }
        public bool ReceivedContains(string expected){lock(received)return received.ToString().Contains(expected,StringComparison.Ordinal);}
        public async Task ExpectAsync(string expected)
        {
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(DateTime.UtcNow<deadline)
            {
                if(ReceivedContains(expected))return;
                if(reader.IsCompleted)throw new IOException("Loopback fixture disconnected before receiving: "+expected);
                await Task.Delay(30);
            }
            throw new TimeoutException("Loopback fixture did not receive: "+expected);
        }
        public async ValueTask DisposeAsync(){cancellation.Cancel();client.Dispose();await reader;cancellation.Dispose();}
    }
}
