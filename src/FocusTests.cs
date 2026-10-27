using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EquipLink;

/// <summary>
/// Uses real, offscreen WPF/PuTTY HWNDs and loopback sockets. The synthetic
/// notifications are sent only to these HWNDs: no SendInput, cursor movement,
/// global mouse hooks, or typed data is used. GetGUIThreadInfo is the focus
/// oracle rather than WPF's logical focus property alone.
/// </summary>
public static class FocusTests
{
    public static async Task RunAsync()
    {
        var report=new StringBuilder();
        void Check(string name,bool value)
        {
            report.AppendLine((value?"PASS: ":"FAIL: ")+name);
            if(!value)throw new InvalidOperationException("FAIL: "+name);
        }
        Directory.CreateDirectory(AppPaths.Root);
        IntPtr previousForeground=ShortcutNative.GetForegroundWindow();
        var data=AppStore.Demo();data.Settings.AutoStartX=false;data.Settings.ExternalTerminal=false;
        var window=new MainWindow(data)
        {
            Width=1280,Height=820,Left=-30000,Top=-30000,
            WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false
        };
        var panes=new List<SessionPane>();var clients=new List<TcpClient>();
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        Window? other=null;IntPtr root=IntPtr.Zero;IntPtr otherRoot=IntPtr.Zero;
        try
        {
            Check("Native descendant notifications recognize all button-down and pointer-down events",
                new[]{0x0201,0x0204,0x0207,0x020B,0x0246}.All(m=>TerminalHost.IsMouseDownNotification(new IntPtr(m))));
            Check("Native descendant notifications ignore creation, destruction, movement and wheel events",
                new[]{0x0001,0x0002,0x0200,0x0202,0x020A}.All(m=>!TerminalHost.IsMouseDownNotification(new IntPtr(m))));
            window.Show();window.UpdateLayout();root=new WindowInteropHelper(window).Handle;
            for(int index=0;index<2;index++)
            {
                var accept=listener.AcceptTcpClientAsync();
                var equipment=new Equipment{Name="Focus loopback "+(index+1),Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,Protocol="Telnet",UseX11=false};
                var pane=await window.ConnectForTestAsync(equipment);panes.Add(pane);
                var client=await accept.WaitAsync(TimeSpan.FromSeconds(10));clients.Add(client);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("LOCAL FOCUS FIXTURE\r\nlogin: "));
            }
            await Settle();
            Check("Two actual PuTTY child windows are embedded",panes.All(p=>p.Host.Embedded));
            Check("Embedded PuTTY does not suppress parent mouse notifications",panes.All(p=>(Native.GetWindowLongPtr(p.Host.TerminalHandle,-20).ToInt64()&4)==0));
            Native.SetForegroundWindow(root);await Settle();
            if(ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2)!=root)
            {
                report.AppendLine("NOT TESTED: Windows did not allow the offscreen fixture to become foreground. Foreground keyboard restoration and foreground client-click focus remain unverified. Background and modal lifecycle checks continue below.");
                await CheckBackgroundAsync(window,panes,root,Check);
                return;
            }
            Check("Only the owned offscreen fixture was activated",true);
            var first=panes[0];var second=panes[1];
            window.ShowSessionForTest(first);await Settle();
            var search=Descendants<TextBox>(window).First(x=>x.IsVisible && x.IsEnabled);
            void FocusShell()
            {
                Keyboard.Focus(search);
                if(Keyboard.FocusedElement!=search)throw new InvalidOperationException("Cannot focus the fixture's shell input.");
            }
            FocusShell();await Settle();
            Check("Shell focus can be separated from the native terminal",FocusOf(first.Host.TerminalHandle)!=first.Host.TerminalHandle);

            Exception? callbackFailure=null;bool quickOpened=false,blockedDuringModal=false;
            FormDialog.TestLoaded=async dialog=>
            {
                try
                {
                    quickOpened=dialog.Title.StartsWith("빠른 접속",StringComparison.Ordinal);
                    ParentClick(first.Host);
                    await Settle();
                    blockedDuringModal=!ShortcutNative.IsWindowEnabled(root) && FocusOf(first.Host.TerminalHandle)!=first.Host.TerminalHandle;
                }
                catch(Exception exception){callbackFailure=exception;}
                finally{dialog.DialogResult=false;}
            };
            try{window.QuickConnectForTest();}finally{FormDialog.TestLoaded=null;}
            if(callbackFailure!=null)throw callbackFailure;
            await Settle();
            Check("The actual + quick-connect dialog opened and cancelled",quickOpened);
            Check("Native click notifications cannot steal focus while the modal owner is disabled",blockedDuringModal);
            Check("Cancelling quick connect restores the existing terminal's native keyboard focus",FocusOf(first.Host.TerminalHandle)==first.Host.TerminalHandle);

            // Deliberately take focus away again so modal restoration cannot
            // accidentally make the client-click regression pass.
            FocusShell();await Settle();ParentClick(first.Host);await Settle();
            Check("A native client parent notification restores focus without clicking a tab",FocusOf(first.Host.TerminalHandle)==first.Host.TerminalHandle);
            Check("WPF keyboard-sink focus agrees with the actual native child",((IKeyboardInputSink)first.Host).HasFocusWithin());
            Check("WPF remembers the terminal after native client focus",FocusManager.GetFocusedElement(FocusManager.GetFocusScope(first.Host))==first.Host);

            FocusShell();await Settle();
            Native.SendMessageTimeout(first.Host.TerminalHandle,0x0021,root,new IntPtr((0x0201<<16)|1),0x0002,1500,out _);
            await Settle();
            Check("Mouse activation propagates from the real PuTTY HWND through its host",FocusOf(first.Host.TerminalHandle)==first.Host.TerminalHandle);

            window.SetLayoutForTest("columns");await Settle();
            var initialSize=window.VisiblePanesForTest.Select(p=>p.Host.ClientSizePixels).ToArray();
            ParentClick(second.Host);await Settle();
            Check("Clicking another split pane gives that PuTTY child native focus",FocusOf(second.Host.TerminalHandle)==second.Host.TerminalHandle);
            Check("Native client click also selects the correct split pane",window.ActiveForTest==second && window.VisiblePanesForTest.Count==2);
            Check("Focus changes preserve both terminal viewport sizes",window.VisiblePanesForTest.Select((p,i)=>p.Host.ClientSizePixels==initialSize[i]).All(same=>same));

            // Another offscreen top-level window represents a different active
            // root. The repair must never activate the terminal behind it.
            var otherInput=new TextBox{Text="Focus isolation fixture"};
            other=new Window{Title="EquipLink focus isolation fixture",Content=otherInput,Width=600,Height=440,Left=-30000,Top=-30000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false};
            other.Show();otherRoot=new WindowInteropHelper(other).Handle;
            Native.SetForegroundWindow(otherRoot);await Settle();Keyboard.Focus(otherInput);await Settle();
            Check("The isolated foreground root is active",ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2)==otherRoot);
            ParentClick(first.Host);await Settle();
            Check("Delayed native notifications never steal focus from another foreground root",ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2)==otherRoot && FocusOf(otherRoot)==otherRoot && window.ActiveForTest==second);
            other.Close();other=null;Native.SetForegroundWindow(root);await Settle();

            window.SetLayoutForTest("single");window.ShowSessionForTest(first);await Settle();
            ParentClick(second.Host);await Settle();
            Check("Hidden pane notifications do not change the visible terminal or its native focus",window.ActiveForTest==first && FocusOf(first.Host.TerminalHandle)==first.Host.TerminalHandle);
            Check("Focus tests keep both local sessions alive",panes.All(p=>p.Host.HasProcess));
            report.AppendLine("All native focus checks passed. Messages were targeted to actual owned HWNDs; physical mouse clicks and real equipment connections were not simulated.");
        }
        finally
        {
            FormDialog.TestLoaded=null;
            IntPtr foreground=ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2);
            bool restore=foreground==root || foreground==otherRoot;
            other?.Close();
            foreach(var pane in panes)window.CloseForTest(pane);
            foreach(var client in clients)client.Dispose();listener.Stop();window.Close();
            if(restore && previousForeground!=IntPtr.Zero && Native.IsWindow(previousForeground))Native.SetForegroundWindow(previousForeground);
            File.WriteAllText(Path.Combine(AppPaths.Root,"focus-test-report.txt"),report.ToString());
        }
    }
    private static async Task CheckBackgroundAsync(MainWindow window,IReadOnlyList<SessionPane> panes,IntPtr root,Action<string,bool> check)
    {
        var first=panes[0];var second=panes[1];
        window.ShowSessionForTest(first);window.SetLayoutForTest("columns");await Settle();
        var handles=panes.Select(p=>p.Host.TerminalHandle).ToArray();
        var processes=panes.Select(p=>p.Host.ProcessId).ToArray();
        var sizes=panes.Select(p=>p.Host.ClientSizePixels).ToArray();
        var focusBefore=FocusOf(root);var activeBefore=window.ActiveForTest;
        check("Both real PuTTY clients retain positive split viewport sizes",sizes.All(s=>s.Width>100 && s.Height>100));
        check("The background test starts outside the application's foreground root",ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2)!=root);
        ParentClick(second.Host);
        Native.SendMessageTimeout(second.Host.TerminalHandle,0x0021,root,new IntPtr((0x0201<<16)|1),0x0002,1500,out _);
        await Settle();
        check("Native client click notifications cannot activate a background shell",ShortcutNative.GetAncestor(ShortcutNative.GetForegroundWindow(),2)!=root);
        check("Background native notifications preserve existing thread keyboard focus",FocusOf(root)==focusBefore);
        check("Background native notifications cannot switch the selected pane",window.ActiveForTest==activeBefore);

        Exception? callbackFailure=null;bool opened=false,disabled=false,modalPreserved=false;
        FormDialog.TestLoaded=async dialog=>
        {
            try
            {
                opened=dialog.Title.StartsWith("빠른 접속",StringComparison.Ordinal);
                disabled=!ShortcutNative.IsWindowEnabled(root);
                // QuickConnect's own Loaded handler focuses its address box.
                // Finish that normal initialization before measuring stability.
                await Settle();
                var modalFocus=FocusOf(root);
                ParentClick(second.Host);await Settle();
                modalPreserved=FocusOf(root)==modalFocus && window.ActiveForTest==activeBefore;
            }
            catch(Exception exception){callbackFailure=exception;}
            finally{dialog.DialogResult=false;}
        };
        try{window.QuickConnectForTest();}finally{FormDialog.TestLoaded=null;}
        if(callbackFailure!=null)throw callbackFailure;
        await Settle();
        check("The actual + quick-connect dialog opens with its native owner disabled",opened && disabled);
        check("Native client notifications leave the modal's input focus and pane selection intact",modalPreserved);
        check("Cancelling quick connect re-enables the original native shell",ShortcutNative.IsWindowEnabled(root));
        check("Cancelling quick connect retains both native PuTTY handles and processes",panes.Select((p,i)=>p.Host.HasProcess && p.Host.TerminalHandle==handles[i] && p.Host.ProcessId==processes[i]).All(same=>same));
        check("Cancelling quick connect preserves both terminal viewport sizes",panes.Select((p,i)=>p.Host.ClientSizePixels==sizes[i]).All(same=>same));
        check("Cancelling quick connect preserves the selected split pane",window.ActiveForTest==activeBefore && window.VisiblePanesForTest.Count==2);

        window.SetLayoutForTest("single");window.ShowSessionForTest(first);await Settle();
        var hiddenFocus=FocusOf(root);ParentClick(second.Host);await Settle();
        check("A hidden native pane cannot change selection or thread focus",window.ActiveForTest==first && FocusOf(root)==hiddenFocus);
        check("Background/modal regression checks leave both local connections alive",panes.All(p=>p.Host.HasProcess));
    }
    private static void ParentClick(TerminalHost host)
    {
        // Windows' documented WM_PARENTNOTIFY contract: low word is the child
        // event, lParam contains client coordinates relative to this ancestor.
        Native.SendMessageTimeout(host.Handle,0x0210,new IntPtr(0x0201),new IntPtr((20<<16)|20),0x0002,1500,out _);
    }
    private static IntPtr FocusOf(IntPtr handle)
    {
        uint thread=Native.GetWindowThreadProcessId(handle,out _);
        var info=new ShortcutNative.GUITHREADINFO{Size=(uint)Marshal.SizeOf<ShortcutNative.GUITHREADINFO>()};
        return ShortcutNative.GetGUIThreadInfo(thread,ref info)?info.Focus:IntPtr.Zero;
    }
    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Task.Delay(160);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)yield return match;
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
            foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(parent,index)))yield return child;
    }
}
