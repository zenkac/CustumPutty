using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EquipLink;

public sealed class TerminalHost : HwndHost
{
    private IntPtr container;
    private IntPtr terminal;
    private Process? process;
    private string? savedSession;
    private readonly TaskCompletionSource loaded=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    private bool closing;
    private long originalStyle;
    private long originalExStyle;
    private IntPtr backgroundBrush;
    private IntPtr focusHook;
    private Native.WinEventProc? focusCallback;
    private long launchGeneration;
    private bool nativeFocusQueued;
    private bool settingFocus;
    private PuttyLoginPipe? loginPipe;
    private TelnetLoginPrompts? loginPrompts;
    private DispatcherTimer? loginTimer;
    private Equipment? loginTarget;
    private DateTime loginDeadline,lastLoginOutput;
    private volatile bool autoLoginActive;
    public bool External { get; private set; }
    public bool HasProcess => process is {HasExited:false};
    public bool Embedded => terminal!=IntPtr.Zero && !External && HasProcess;
    public string? LogFile { get; private set; }
    public int? ProcessId => HasProcess?process!.Id:null;
    public IntPtr TerminalHandle => terminal;
    public (int Width,int Height) ClientSizePixels
    {
        get
        {
            if(terminal==IntPtr.Zero || !Native.GetClientRect(terminal,out var rect)) return (0,0);
            return (Math.Max(0,rect.Right-rect.Left),Math.Max(0,rect.Bottom-rect.Top));
        }
    }
    public event Action<string>? StateChanged;
    public event Action? Activated;
    public TerminalHost()
    {
        Focusable=true;
        IsVisibleChanged+=(_,_)=>RefreshViewport();
    }
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        backgroundBrush=Native.CreateSolidBrush(unchecked((uint)ThemeService.ColorRef("#111923")));
        container=Native.CreateWindowEx(0,"STATIC","EquipLink Terminal",0x40000000|0x10000000|0x02000000|0x04000000,0,0,1,1,hwndParent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(container==IntPtr.Zero)
        {
            var error=Marshal.GetLastWin32Error();
            if(backgroundBrush!=IntPtr.Zero) { Native.DeleteObject(backgroundBrush); backgroundBrush=IntPtr.Zero; }
            throw new Win32Exception(error);
        }
        loaded.TrySetResult();
        return new HandleRef(this,container);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // The shell keeps all session hosts in the visual tree when switching tabs.
        CloseSession(); Native.DestroyWindow(hwnd.Handle); container=IntPtr.Zero;
        if(backgroundBrush!=IntPtr.Zero) { Native.DeleteObject(backgroundBrush); backgroundBrush=IntPtr.Zero; }
    }
    public async Task StartAsync(Equipment equipment,Preferences settings)
    {
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(disposed) return;
        if(HasProcess) throw new InvalidOperationException("이미 열린 세션입니다.");
        if(process!=null) CloseSession();
        long generation=++launchGeneration;
        string path=PuttyService.Executable();
        External=settings.ExternalTerminal;
        try
        {
            string? loginNotice=null;
            bool useLogin=equipment.CanAutoLogin;
            if(useLogin)
            {
                try{_ = CredentialProtector.Unprotect(equipment,equipment.EncryptedPassword!);}
                catch(System.Security.Cryptography.CryptographicException){useLogin=false;loginNotice="저장된 비밀번호를 읽을 수 없습니다 · 설비 편집에서 다시 저장하세요";}
            }
            if(useLogin)loginPipe=new PuttyLoginPipe(equipment.IsSsh);
            savedSession=PuttyService.Prepare(equipment,settings,out var logFile,useLogin&&!equipment.IsSsh?loginPipe!.Path:null);LogFile=logFile;
            if(useLogin)
            {
                void Failed()=>Dispatcher.BeginInvoke(()=>{if(generation==launchGeneration){CancelAutoLogin();StateChanged?.Invoke("자동 로그인 준비 실패 · 터미널에서 로그인해 주세요");}});
                if(equipment.IsSsh)_=loginPipe!.SupplyPasswordAsync(equipment.Copy(),Failed);
                else
                {
                    loginTarget=equipment.Copy();loginPrompts=new();autoLoginActive=true;loginDeadline=DateTime.UtcNow.AddSeconds(90);
                    loginTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};loginTimer.Tick+=AutoLoginTick;loginTimer.Start();
                    _=loginPipe!.ReadOutputAsync(text=>
                    {
                        if(autoLoginActive)Dispatcher.BeginInvoke(()=>{if(generation==launchGeneration&&autoLoginActive){loginPrompts?.Feed(text);lastLoginOutput=DateTime.UtcNow;}});
                    },LogFile,Failed);
                }
            }
            process=Process.Start(PuttyService.StartInfo(path,savedSession,External,useLogin&&equipment.IsSsh?loginPipe!.Path:null)) ?? throw new InvalidOperationException("터미널을 실행하지 못했습니다.");
            loginPipe?.SetProcess(process.Id);
            var launchedProcess=process;
            var launchedSession=savedSession;
            process.EnableRaisingEvents=true;
            process.Exited+=(_,_)=>Dispatcher.BeginInvoke(()=>
            {
                if(!closing && ReferenceEquals(process,launchedProcess))
                {
                    RemoveActivationHook();
                    DisposeLogin();
                    StateChanged?.Invoke("세션 프로세스 종료 · 다시 연결할 수 있습니다");
                }
                PuttyService.Cleanup(launchedSession);
            });
            var discovered=await FindTerminalAsync(launchedProcess);
            if(disposed || generation!=launchGeneration) return;
            terminal=discovered;
            InstallActivationHook(launchedProcess.Id);
            originalStyle=Native.GetWindowLongPtr(terminal,-16).ToInt64();
            originalExStyle=Native.GetWindowLongPtr(terminal,-20).ToInt64();
            string openedState=loginNotice??(useLogin?"저장된 정보로 자동 로그인 · 인증 결과는 터미널에서 확인하세요":"세션 창 열림 · 로그인은 터미널에서 진행하세요");
            if(External) { Native.ShowWindow(terminal,5); StateChanged?.Invoke(openedState); return; }
            // PuTTY restores its caption and resize frame on every SIZE_RESTORED
            // (windows-window.c: clear_full_screen). Keep those native styles and
            // crop the non-client furniture outside our clipping container instead
            // of repeatedly fighting its resize handler. Show only after sizing.
            Native.ShowWindow(terminal,0);
            long childStyle=(originalStyle & ~(0x80000000L|0x00080000L|0x00020000L|0x00010000L|0x10000000L|0x20000000L|0x01000000L)) | 0x40000000L;
            Native.SetWindowLongPtr(terminal,-16,new IntPtr(childStyle));
            // Ancestor mouse notifications are how the host observes clicks in
            // PuTTY's native client area, even when the shell is already active.
            Native.SetWindowLongPtr(terminal,-20,new IntPtr(originalExStyle & ~(0x00040000L|0x00000100L|0x00000200L|0x00000004L)));
            var oldParent=Native.SetParent(terminal,container);
            int error=Marshal.GetLastWin32Error();
            if(oldParent==IntPtr.Zero && error!=0)
            {
                External=true; Native.SetWindowLongPtr(terminal,-16,new IntPtr(originalStyle)); Native.SetWindowLongPtr(terminal,-20,new IntPtr(originalExStyle)); Native.ShowWindow(terminal,5);
                StateChanged?.Invoke("탭 삽입을 지원하지 않아 별도 터미널 창으로 열었습니다"); return;
            }
            Native.SetWindowPos(terminal,IntPtr.Zero,0,0,0,0,0x0020|0x0001|0x0002|0x0004|0x0010);
            RefreshViewport();
            FocusTerminal(); StateChanged?.Invoke(openedState);
        }
        catch
        {
            // An earlier async launch must never tear down a replacement session.
            if(disposed || generation!=launchGeneration) return;
            CloseSession(); throw;
        }
    }
    private static async Task<IntPtr> FindTerminalAsync(Process process)
    {
        for(int i=0;i<100;i++)
        {
            if(process.HasExited) throw new InvalidOperationException("PuTTY가 종료되었습니다. 접속 주소와 설정을 확인해 주세요.");
            IntPtr found=IntPtr.Zero;
            Native.EnumWindows((window,_)=>
            {
                Native.GetWindowThreadProcessId(window,out uint id);
                if(id==(uint)process.Id)
                {
                    var name=new StringBuilder(100); Native.GetClassName(window,name,name.Capacity);
                    if(name.ToString()=="PuTTY") { found=window; return false; }
                }
                return true;
            },IntPtr.Zero);
            if(found!=IntPtr.Zero) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException("터미널 창 생성 시간이 초과되었습니다. 설정에서 '별도 창 사용'을 선택해 다시 연결해 주세요.");
    }
    protected override void OnWindowPositionChanged(Rect rcBoundingBox) { base.OnWindowPositionChanged(rcBoundingBox); ResizeChild(); }
    /// <summary>Refresh a retained host after changing its split cell or visibility.</summary>
    public void RefreshViewport()
    {
        if(!Embedded) return;
        ResizeChild();
        Native.ShowWindow(terminal,IsVisible?4:0); // SW_SHOWNOACTIVATE / SW_HIDE
    }
    private void ResizeChild()
    {
        if(container==IntPtr.Zero || terminal==IntPtr.Zero || External || !Native.IsWindow(terminal)) return;
        if(!Native.GetClientRect(container,out var rect)) return;
        int width=Math.Max(1,rect.Right-rect.Left),height=Math.Max(1,rect.Bottom-rect.Top);
        // Read actual physical-pixel margins; caption/frame dimensions vary with
        // DPI and Windows version. The container clips all four non-client edges.
        // A second pass covers PuTTY changing those styles during the first resize.
        for(int pass=0;pass<3;pass++)
        {
            if(!Native.GetWindowRect(terminal,out var window) || !Native.GetClientRect(terminal,out var client)) return;
            var clientOrigin=new Native.POINT();var containerOrigin=new Native.POINT();
            if(!Native.ClientToScreen(terminal,ref clientOrigin) || !Native.ClientToScreen(container,ref containerOrigin)) return;
            int clientWidth=client.Right-client.Left,clientHeight=client.Bottom-client.Top;
            if(clientWidth==width && clientHeight==height && clientOrigin.X==containerOrigin.X && clientOrigin.Y==containerOrigin.Y) return;
            int left=Math.Max(0,clientOrigin.X-window.Left),top=Math.Max(0,clientOrigin.Y-window.Top);
            int right=Math.Max(0,window.Right-clientOrigin.X-clientWidth),bottom=Math.Max(0,window.Bottom-clientOrigin.Y-clientHeight);
            if(!Native.MoveWindow(terminal,-left,-top,width+left+right,height+top+bottom,true)) return;
        }
    }
    protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        // WM_PARENTNOTIFY arrives for native descendant clicks; WPF mouse
        // events do not cross the HwndHost boundary. Unlike WM_MOUSEACTIVATE,
        // it is also delivered when our top-level window is already active.
        // Defer to avoid calling back into PuTTY while it is notifying us.
        if(msg==0x0210 && IsMouseDownNotification(wParam))
        {
            QueueNativeFocus();
        }
        else if(msg is 0x0021 or 0x0007) QueueNativeFocus(); // mouse activation / container focus
        // Leave the message unhandled: PuTTY must retain its normal selection,
        // copy/paste, drag and mouse-reporting behavior. Never eat the click.
        if(msg==0x0014 && backgroundBrush!=IntPtr.Zero)
        {
            Native.GetClientRect(hwnd,out var rect); Native.FillRect(wParam,ref rect,backgroundBrush);
            handled=true; return new IntPtr(1);
        }
        if(msg==0x000F && backgroundBrush!=IntPtr.Zero)
        {
            var context=Native.BeginPaint(hwnd,out var paint);
            if(context!=IntPtr.Zero) Native.FillRect(context,ref paint.Paint,backgroundBrush);
            Native.EndPaint(hwnd,ref paint); handled=true; return IntPtr.Zero;
        }
        return base.WndProc(hwnd,msg,wParam,lParam,ref handled);
    }
    internal static bool IsMouseDownNotification(IntPtr value)=>unchecked((ushort)value.ToInt64()) is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x0246;
    private void QueueNativeFocus()
    {
        if(disposed || nativeFocusQueued || External || !IsVisible || terminal==IntPtr.Zero) return;
        long generation=launchGeneration;IntPtr target=terminal;
        nativeFocusQueued=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
        {
            nativeFocusQueued=false;
            if(generation!=launchGeneration || target!=terminal || !CanFocusEmbedded()) return;
            Activated?.Invoke();
            FocusEmbedded();
        }));
    }
    private bool CanFocusEmbedded()
    {
        if(disposed || !Embedded || !IsVisible || !IsEnabled || ComponentDispatcher.IsThreadModal) return false;
        var owner=Window.GetWindow(this);
        if(owner is not {IsVisible:true,IsEnabled:true}) return false;
        var root=new WindowInteropHelper(owner).Handle;
        if(root==IntPtr.Zero || !ShortcutNative.IsWindowEnabled(root)) return false;
        var foreground=ShortcutNative.GetForegroundWindow();
        // GA_ROOT excludes owned modal windows and detached PuTTY windows.
        // A delayed click must not move focus after another app becomes active.
        if(foreground==IntPtr.Zero || ShortcutNative.GetAncestor(foreground,2)!=root) return false;
        uint thread=ShortcutNative.GetWindowThreadProcessId(foreground,out _);
        var info=new ShortcutNative.GUITHREADINFO{Size=(uint)Marshal.SizeOf<ShortcutNative.GUITHREADINFO>()};
        return ShortcutNative.GetGUIThreadInfo(thread,ref info) && (info.Flags&0x1C)==0;
    }
    private bool FocusEmbedded()
    {
        if(!CanFocusEmbedded()) return false;
        if(settingFocus) return FocusNativeChild();
        settingFocus=true;
        try
        {
            // Keep WPF's remembered focus in the terminal, not on the '+' or
            // closed dialog's control. The final native focus call is required
            // because PuTTY belongs to a separate process/input sink.
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this),this);
            return FocusNativeChild();
        }
        finally { settingFocus=false; }
    }
    private bool FocusNativeChild()
    {
        Native.SetFocus(terminal);
        return HasFocusWithinCore();
    }
    protected override bool TabIntoCore(TraversalRequest request)=>CanFocusEmbedded() && FocusNativeChild();
    protected override bool HasFocusWithinCore()
    {
        if(disposed || External || terminal==IntPtr.Zero || !Native.IsWindow(terminal)) return false;
        uint thread=Native.GetWindowThreadProcessId(terminal,out _);
        var info=new ShortcutNative.GUITHREADINFO{Size=(uint)Marshal.SizeOf<ShortcutNative.GUITHREADINFO>()};
        return ShortcutNative.GetGUIThreadInfo(thread,ref info) && (info.Focus==terminal || Native.IsChild(terminal,info.Focus));
    }
    private void InstallActivationHook(int processId)
    {
        RemoveActivationHook();
        long generation=launchGeneration;
        focusCallback=(_,_,window,_,_,_,_)=>
        {
            if(disposed || Dispatcher.HasShutdownStarted || External || !IsVisible || terminal==IntPtr.Zero) return;
            if(window!=terminal && !Native.IsChild(terminal,window)) return;
            // Keep UI handlers outside the unmanaged event callback.
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
            {
                if(generation==launchGeneration && CanFocusEmbedded() && HasFocusWithinCore()) Activated?.Invoke();
            }));
        };
        // Accessibility focus notifications are restricted to this PuTTY process.
        // This observes pane selection without intercepting keyboard input.
        focusHook=Native.SetWinEventHook(0x8005,0x8005,IntPtr.Zero,focusCallback,(uint)processId,0,0);
    }
    private void RemoveActivationHook()
    {
        if(focusHook!=IntPtr.Zero) { Native.UnhookWinEvent(focusHook); focusHook=IntPtr.Zero; }
        focusCallback=null;
    }
    public void FocusTerminal()
    {
        if(!HasProcess || terminal==IntPtr.Zero) return;
        if(External) Native.SetForegroundWindow(terminal);
        else FocusEmbedded();
    }
    public void Send(string text,bool execute)
    {
        CancelAutoLogin();
        SendCore(text,execute);
        FocusTerminal();
    }
    private void SendCore(string text,bool execute)
    {
        if(!HasProcess || terminal==IntPtr.Zero || !Native.IsWindow(terminal)) throw new InvalidOperationException("열린 터미널이 없습니다. 먼저 설비에 연결해 주세요.");
        var command=Commands.NormalizeInput(text,execute);
        foreach(char character in command)
            if(Native.SendMessageTimeout(terminal,0x0102,new IntPtr(character),IntPtr.Zero,0x0002,1500,out _)==IntPtr.Zero)
                throw new InvalidOperationException("터미널이 응답하지 않습니다. 일부 문자가 입력됐을 수 있으니 터미널을 확인해 주세요.");
    }
    public void CancelAutoLogin()
    {
        autoLoginActive=false;loginPrompts?.Stop();loginTimer?.Stop();loginTarget=null;
    }
    private void DisposeLogin(){CancelAutoLogin();loginPipe?.Dispose();loginPipe=null;loginTimer=null;loginPrompts=null;}
    private void AutoLoginTick(object? sender,EventArgs e)
    {
        if(!autoLoginActive||loginPrompts?.Active!=true||DateTime.UtcNow>loginDeadline){CancelAutoLogin();return;}
        if(terminal==IntPtr.Zero||!HasProcess||DateTime.UtcNow-lastLoginOutput<TimeSpan.FromMilliseconds(250))return;
        var input=loginPrompts.Pending();
        if(input==LoginInput.None)return;
        try
        {
            var target=loginTarget!;
            string value=input==LoginInput.Username?target.Username:CredentialProtector.Unprotect(target,target.EncryptedPassword!);
            try
            {
                CredentialProtector.ValidateSecret(value);
                loginPrompts.Sent(input);
                SendCore(value,true);
            }
            finally{value=string.Empty;}
            StateChanged?.Invoke(input==LoginInput.Username?"저장된 ID 입력됨 · 비밀번호 안내 대기":"저장된 비밀번호 전송됨 · 인증 결과는 터미널에서 확인하세요");
            if(input==LoginInput.Password)CancelAutoLogin();
        }
        catch{CancelAutoLogin();StateChanged?.Invoke("자동 로그인 중단 · 설비 설정과 터미널을 확인해 주세요");}
    }
    public void SendCredential(string value)
    {
        CredentialProtector.ValidateSecret(value);
        // Explicit user action only: no newline, shell command, clipboard,
        // command composer, process argument, or diagnostic contains this value.
        Send(value,false);
    }
    public void Detach()
    {
        if(!Embedded) { FocusTerminal(); return; }
        Native.SetParent(terminal,IntPtr.Zero); Native.SetWindowLongPtr(terminal,-16,new IntPtr(originalStyle)); Native.SetWindowLongPtr(terminal,-20,new IntPtr(originalExStyle));
        External=true;
        Native.SetWindowPos(terminal,IntPtr.Zero,120,100,1000,700,0x0020|0x0004); Native.ShowWindow(terminal,5); Native.SetForegroundWindow(terminal);
        StateChanged?.Invoke("별도 창으로 분리됨 · 탭을 닫으면 세션도 종료됩니다");
    }
    public void CloseSession()
    {
        closing=true;
        launchGeneration++;
        CancelAutoLogin();
        RemoveActivationHook();
        try
        {
            if(process is {HasExited:false})
            {
                if(terminal!=IntPtr.Zero) Native.PostMessage(terminal,0x0010,IntPtr.Zero,IntPtr.Zero);
                if(!process.WaitForExit(500)) process.Kill();
            }
        }
        catch { }
        finally { DisposeLogin();process?.Dispose(); process=null; terminal=IntPtr.Zero; PuttyService.Cleanup(savedSession); savedSession=null; closing=false; }
    }
    protected override void Dispose(bool disposing) { disposed=true; CloseSession(); base.Dispose(disposing); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public int Erase;
        public RECT Paint;
        public int Restore,IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Reserved;
    }
    internal delegate bool EnumWindowsProc(IntPtr hwnd,IntPtr lParam);
    internal delegate void WinEventProc(IntPtr hook,uint eventType,IntPtr hwnd,int objectId,int childId,uint eventThread,uint eventTime);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] internal static extern IntPtr CreateWindowEx(int exStyle,string className,string name,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd,ref POINT point);
    [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr hwnd,int x,int y,int width,int height,bool repaint);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback,IntPtr param);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd,StringBuilder text,int maximum);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent,IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint eventMin,uint eventMax,IntPtr module,WinEventProc callback,uint processId,uint threadId,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] internal static extern int FillRect(IntPtr context,ref RECT rect,IntPtr brush);
    [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hwnd,out PAINTSTRUCT paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hwnd,ref PAINTSTRUCT paint);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
}
