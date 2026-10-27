using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EquipLink;

/// <summary>
/// Routes a small set of workspace shortcuts while a PuTTY child owns keyboard
/// focus. RegisterHotKey would reserve these keys system-wide; this handler
/// instead checks the foreground HWND for every event and passes other input on.
/// </summary>
public sealed class AppShortcuts : IDisposable
{
    private readonly Window owner;
    private readonly Action<string> dispatch;
    private volatile bool disposed;
    private volatile bool ownerEnabled;
    private IntPtr ownerHandle;
    private IntPtr hook;
    private Thread? worker;
    private uint workerId;
    private ShortcutNative.HookProc? callback;
    // Only five shortcut-key identities can be present, and only until key-up.
    // No text conversion, input history, clipboard access, or input logging.
    private readonly HashSet<uint> held=[];
    private readonly HashSet<uint> consumed=[];
    private static readonly uint[] ShortcutKeys=[0x7A,0x42,0x4B,0x4A,0x0D];

    internal bool Installed => Volatile.Read(ref hook)!=IntPtr.Zero;
    public event Action<IntPtr>? TerminalInput;
    internal bool IsForegroundEligible => ScopeAllows();

    public AppShortcuts(Window owner,Action<string> dispatch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(dispatch);
        owner.Dispatcher.VerifyAccess();
        this.owner=owner;this.dispatch=dispatch;ownerEnabled=owner.IsEnabled;
        owner.SourceInitialized+=OnSourceInitialized;
        owner.IsEnabledChanged+=OnEnabledChanged;
        owner.Closed+=OnClosed;
        if(new WindowInteropHelper(owner).Handle!=IntPtr.Zero) Initialize();
    }

    private void OnSourceInitialized(object? sender,EventArgs e)=>Initialize();
    private void OnEnabledChanged(object sender,DependencyPropertyChangedEventArgs e)=>ownerEnabled=owner.IsEnabled;
    private void OnClosed(object? sender,EventArgs e)=>Dispose();

    private void Initialize()
    {
        if(disposed || worker!=null) return;
        ownerHandle=new WindowInteropHelper(owner).Handle;
        if(ownerHandle==IntPtr.Zero) return;
        ApplyDarkTitlebar(ownerHandle);
        // A dedicated message loop keeps the callback responsive even while the
        // WPF thread is resizing panes or waiting briefly for a process to close.
        worker=new Thread(RunHook){IsBackground=true,Name="EquipLink workspace shortcuts"};
        worker.Start();
    }

    private void RunHook()
    {
        ShortcutNative.PeekMessage(out _,IntPtr.Zero,0,0,0); // Create the thread queue before publishing its ID.
        Volatile.Write(ref workerId,ShortcutNative.GetCurrentThreadId());
        if(disposed) return;
        callback=OnKeyboard;
        try
        {
            foreach(uint key in ShortcutKeys) if(KeyDown((int)key)) held.Add(key);
            Volatile.Write(ref hook,ShortcutNative.SetWindowsHookEx(13,callback,ShortcutNative.GetModuleHandle(null),0));
            if(hook==IntPtr.Zero) return; // Workspace buttons remain available if the OS refuses a hook.
            while(!disposed && ShortcutNative.GetMessage(out var message,IntPtr.Zero,0,0)>0)
            {
                ShortcutNative.TranslateMessage(ref message);
                ShortcutNative.DispatchMessage(ref message);
            }
        }
        finally
        {
            var installed=Interlocked.Exchange(ref hook,IntPtr.Zero);
            if(installed!=IntPtr.Zero) ShortcutNative.UnhookWindowsHookEx(installed);
            held.Clear();consumed.Clear();callback=null;
        }
    }

    private IntPtr OnKeyboard(int code,IntPtr message,IntPtr data)
    {
        if(code<0 || disposed) return Next(code,message,data);
        uint kind=unchecked((uint)message.ToInt64());
        bool down=kind is 0x0100 or 0x0104;
        bool up=kind is 0x0101 or 0x0105;
        if(!down && !up) return Next(code,message,data);
        try
        {
            if(down && TerminalInput!=null && !owner.Dispatcher.HasShutdownStarted)
            {
                var focus=new ShortcutNative.GUITHREADINFO{Size=(uint)Marshal.SizeOf<ShortcutNative.GUITHREADINFO>()};
                if(ShortcutNative.GetGUIThreadInfo(0,ref focus)&&focus.Focus!=IntPtr.Zero)
                {
                    var name=new System.Text.StringBuilder(32);Native.GetClassName(focus.Focus,name,name.Capacity);
                    if(name.ToString()=="PuTTY")owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>TerminalInput?.Invoke(focus.Focus)));
                }
            }
            uint key=unchecked((uint)Marshal.ReadInt32(data));
            if(key is not (0x7A or 0x42 or 0x4B or 0x4A or 0x0D)) return Next(code,message,data);
            if(up)
            {
                held.Remove(key);
                bool wasConsumed=consumed.Remove(key);
                // Never consume the key-up in another app or in a modal dialog,
                // even if the original key-down was our workspace shortcut.
                return wasConsumed && ScopeAllows()?new IntPtr(1):Next(code,message,data);
            }
            bool firstDown=held.Add(key);
            if(!ScopeAllows()) return Next(code,message,data);
            if(!firstDown) return consumed.Contains(key)?new IntPtr(1):Next(code,message,data);
            uint flags=unchecked((uint)Marshal.ReadInt32(data,8));
            // Modifier keys have already transitioned before this candidate key.
            // We do not use async state to infer the current key's transition.
            string? command=MatchShortcut(key,KeyDown(0x11),KeyDown(0x10),KeyDown(0x12)||(flags&0x20)!=0,KeyDown(0x5B)||KeyDown(0x5C));
            if(command==null || owner.Dispatcher.HasShutdownStarted) return Next(code,message,data);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
            {
                // Focus can change between native input and the queued action.
                if(!disposed && owner.IsEnabled && owner.IsVisible && !ComponentDispatcher.IsThreadModal && ScopeAllows()) dispatch(command);
            }));
            consumed.Add(key);
            return new IntPtr(1);
        }
        catch
        {
            // An exception must not cross the native hook boundary or block input.
            return Next(code,message,data);
        }
    }

    internal static string? MatchShortcut(uint key,bool control,bool shift,bool alt,bool windows)
    {
        if(alt || windows) return null;
        if(key==0x7A && !control && !shift) return "focus";
        if(!control || !shift) return null;
        return key switch {0x42=>"sidebar",0x4B=>"quick",0x4A=>"composer",0x0D=>"zoom",_=>null};
    }

    private bool ScopeAllows()
    {
        if(disposed || !ownerEnabled || ownerHandle==IntPtr.Zero || !ShortcutNative.IsWindowEnabled(ownerHandle) || !ShortcutNative.IsWindowVisible(ownerHandle)) return false;
        IntPtr foreground=ShortcutNative.GetForegroundWindow();
        // GA_ROOT follows parent links, not owner links: owned dialogs and
        // detached PuTTY windows cannot inherit the main window's shortcut scope.
        if(foreground==IntPtr.Zero || ShortcutNative.GetAncestor(foreground,2)!=ownerHandle) return false;
        uint foregroundThread=ShortcutNative.GetWindowThreadProcessId(foreground,out _);
        var info=new ShortcutNative.GUITHREADINFO{Size=(uint)Marshal.SizeOf<ShortcutNative.GUITHREADINFO>()};
        if(ShortcutNative.GetGUIThreadInfo(foregroundThread,ref info) && (info.Flags&0x1C)!=0) return false; // Menu/popup/system-menu loops
        return true;
    }

    private static bool KeyDown(int key)=>(ShortcutNative.GetAsyncKeyState(key)&0x8000)!=0;
    private IntPtr Next(int code,IntPtr message,IntPtr data)=>ShortcutNative.CallNextHookEx(hook,code,message,data);

    private static void ApplyDarkTitlebar(IntPtr handle)
    {
        try
        {
            int dark=1,background=0x00231911,foreground=0x00F0E5DC;
            ShortcutNative.DwmSetWindowAttribute(handle,20,ref dark,4);
            // Caption/text colours are supported on Windows 11. Earlier versions
            // simply return an unsupported-attribute HRESULT and retain dark mode.
            ShortcutNative.DwmSetWindowAttribute(handle,35,ref background,4);
            ShortcutNative.DwmSetWindowAttribute(handle,36,ref foreground,4);
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
    }

    public void Dispose()
    {
        if(disposed) return;
        disposed=true;
        uint id=Volatile.Read(ref workerId);
        if(id!=0) ShortcutNative.PostThreadMessage(id,0x0012,IntPtr.Zero,IntPtr.Zero);
        if(owner.Dispatcher.CheckAccess()) RemoveHandlers();
        else if(!owner.Dispatcher.HasShutdownStarted) owner.Dispatcher.BeginInvoke(new Action(RemoveHandlers));
    }

    private void RemoveHandlers()
    {
        owner.SourceInitialized-=OnSourceInitialized;
        owner.IsEnabledChanged-=OnEnabledChanged;
        owner.Closed-=OnClosed;
    }
}

internal static class ShortcutNative
{
    internal delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct GUITHREADINFO
    {
        public uint Size,Flags;
        public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;
        public RECT CaretRect;
    }
    [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint threadId,ref GUITHREADINFO info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetMessage(out MSG message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool PeekMessage(out MSG message,IntPtr window,uint min,uint max,uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool PostThreadMessage(uint threadId,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window,uint attribute,ref int value,uint size);
}
