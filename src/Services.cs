using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace EquipLink;

public static class PuttyService
{
    public const string OfficialHash = "D01FDB5AAE8F112526040A39B0BFB9E27D813003178645E65F8D1CFDB2A26C87";
    public const string SessionsKey = @"Software\SimonTatham\PuTTY\Sessions";
    public static string Executable()
    {
        var directory=Path.Combine(AppPaths.Root,"bin","putty-0.85"); Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"putty.exe");
        if(!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=OfficialHash)
        {
            using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("EquipLink.putty.exe") ?? throw new FileNotFoundException("내장 PuTTY를 찾을 수 없습니다.");
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            using(var output=File.Create(temp)) source.CopyTo(output);
            if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temp)))!=OfficialHash) throw new InvalidDataException("내장 PuTTY 무결성 검사에 실패했습니다.");
            File.Move(temp,path,true);
        }
        var licence=Path.Combine(directory,"PuTTY-LICENCE.txt");
        if(!File.Exists(licence))
        {
            using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("EquipLink.PuTTY-LICENCE.txt")!;
            using var reader=new StreamReader(resource); File.WriteAllText(licence,reader.ReadToEnd());
        }
        return path;
    }
    public static string EncodeSessionName(string name)
    {
        var builder=new StringBuilder();
        foreach(var value in Encoding.UTF8.GetBytes(name))
        {
            if(value<=32 || value>=127 || value=='%' || value=='\\' || value=='*' || value=='?' || (value=='.' && builder.Length==0)) builder.Append('%').Append(value.ToString("X2"));
            else builder.Append((char)value);
        }
        return builder.ToString();
    }
    public static string Prepare(Equipment equipment,Preferences settings,out string? logFile,string? outputPipe=null)
    {
        equipment.Validate(); settings.Validate();
        var session="EquipLink_"+Environment.ProcessId+"_"+Guid.NewGuid().ToString("N");
        using var root=Registry.CurrentUser.CreateSubKey(SessionsKey);
        using var key=root.CreateSubKey(session);
        try
        {
            if(!string.IsNullOrWhiteSpace(equipment.BasePuttySession))
            {
                using var original=root.OpenSubKey(EncodeSessionName(equipment.BasePuttySession));
                if(original!=null) foreach(var name in original.GetValueNames())
                {
                    var value=original.GetValue(name);
                    if(value!=null) key.SetValue(name,value,original.GetValueKind(name));
                }
            }
            var values=new Dictionary<string,object>
            {
                ["Present"]=1,["HostName"]=equipment.Host,["Protocol"]=equipment.IsSsh?"ssh":"telnet",["PortNumber"]=equipment.Port,
                ["UserName"]=equipment.Username,["UserNameFromEnvironment"]=0,["TerminalType"]="xterm",["LineCodePage"]=equipment.Encoding,
                ["Font"]="Consolas",["FontHeight"]=settings.FontSize,["FontCharSet"]=0,["FontIsBold"]=0,["FontQuality"]=3,["ScrollbackLines"]=settings.Scrollback,
                ["TermWidth"]=100,["TermHeight"]=30,["WindowBorder"]=6,["SunkenEdge"]=0,["LockSize"]=0,["HideMousePtr"]=0,["CursorType"]=0,["BlinkCur"]=0,
                ["ScrollBar"]=0,["ScrollBarFullScreen"]=0,
                ["WinTitle"]=equipment.Name+" — EquipLink",["WinNameAlways"]=1,["NoRemoteWinTitle"]=1,["NoRemoteResize"]=1,
                ["CloseOnExit"]=0,["WarnOnClose"]=0,["Beep"]=0,["TCPKeepalives"]=1,["PingInterval"]=0,["PingIntervalSecs"]=30,
                ["X11Forward"]=equipment.ForwardX11?1:0,["X11Display"]="127.0.0.1:"+settings.DisplayNumber+".0",
                ["X11AuthType"]=1,["X11AuthFile"]=settings.XAuthorityFile??"",
                ["AgentFwd"]=0,["SshProt"]=3,["RemoteCommand"]="",["RemoteCommand2"]="",["SSHNoShell"]=0,["NoPTY"]=0,
                // Imported Force-on echo would expose password input on screen and
                // in output logs even when the remote password prompt disables echo.
                ["PreConnectCommand"]="",["TelnetRet"]=1,["LocalEcho"]=2,
                ["UseSystemColours"]=0,["TryPalette"]=0,["ANSIColour"]=1,["Xterm256Colour"]=1,["TrueColour"]=1,["BoldAsColour"]=2,
                ["LogType"]=equipment.LogSession?1:0,["LogFileClash"]=0,["LogFlush"]=1,["LogHeader"]=1
            };
            // PuTTY's stored colours interleave normal/bright ANSI pairs after
            // default foreground/background and cursor colours (settings.c).
            // Apply after importing a session so its appearance cannot replace
            // the workspace palette or disable terminal resizing in split views.
            // The selected palette is applied at app startup. Saving a different
            // preference does not mix new terminals with the current workspace.
            var colours=ThemeService.PuttyColors;
            for(int i=0;i<colours.Count;i++) values["Colour"+i]=colours[i];
            if(!string.IsNullOrWhiteSpace(equipment.PrivateKeyPath)) values["PublicKeyFile"]=equipment.PrivateKeyPath;
            logFile=null;
            if(equipment.LogSession)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                logFile=Path.Combine(AppPaths.Logs,Rules.SafeFilename(equipment.Name)+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]+".log");
                values["LogFileName"]=logFile;
            }
            if(outputPipe!=null){values["LogType"]=1;values["LogFileName"]=outputPipe;values["LogFileClash"]=1;values["LogHeader"]=0;}
            foreach(var item in values) key.SetValue(item.Key,item.Value,item.Value is int?RegistryValueKind.DWord:RegistryValueKind.String);
            return session;
        }
        catch { key.Close(); root.DeleteSubKey(session,false); throw; }
    }
    public static ProcessStartInfo StartInfo(string executable,string session,bool external,string? passwordPipe=null)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!,WindowStyle=external?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden};
        info.ArgumentList.Add("-load"); info.ArgumentList.Add(session);
        if(passwordPipe!=null){info.ArgumentList.Add("-pwfile");info.ArgumentList.Add(passwordPipe);}
        return info;
    }
    public static void Cleanup(string? session)
    {
        if(session==null || !session.StartsWith("EquipLink_"+Environment.ProcessId+"_",StringComparison.Ordinal)) return;
        try { using var root=Registry.CurrentUser.OpenSubKey(SessionsKey,true); root?.DeleteSubKey(session,false); } catch { }
    }
    public static List<Equipment> ImportSessions()
    {
        var result=new List<Equipment>();
        using var root=Registry.CurrentUser.OpenSubKey(SessionsKey);
        if(root==null) return result;
        foreach(var encoded in root.GetSubKeyNames())
        {
            if(encoded=="Default%20Settings" || encoded.StartsWith("EquipLink_")) continue;
            using var key=root.OpenSubKey(encoded); if(key==null) continue;
            var host=key.GetValue("HostName") as string ?? "";
            var protocol=key.GetValue("Protocol") as string;
            if(!Rules.ValidHost(host) || (protocol!="ssh" && protocol!="telnet")) continue;
            var name=Uri.UnescapeDataString(encoded);
            var item=new Equipment{Name=name,Host=host,Group="PuTTY에서 가져옴",Protocol=protocol=="ssh"?"SSH":"Telnet",Port=Convert.ToInt32(key.GetValue("PortNumber")??(protocol=="ssh"?22:23)),Username=key.GetValue("UserName") as string??"",Encoding=key.GetValue("LineCodePage") as string??"UTF-8",BasePuttySession=name};
            item.Validate(); result.Add(item);
        }
        return result;
    }
}

public static class NetworkTools
{
    public static IReadOnlyList<string> LocalAddresses()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Distinct().Order().ToList(); }
        catch { return []; }
    }
    public static async Task<string> LocalForAsync(Equipment equipment,Preferences preferences)
    {
        var chosen=string.IsNullOrWhiteSpace(equipment.DisplayAddress)?preferences.PcAddress:equipment.DisplayAddress;
        if(!string.IsNullOrWhiteSpace(chosen)) return chosen;
        // UDP Connect chooses the routing interface; no payload is transmitted.
        try
        {
            var addresses=await Dns.GetHostAddressesAsync(equipment.Host).WaitAsync(TimeSpan.FromSeconds(3));
            var target=addresses.FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork) ?? addresses.First();
            using var socket=new Socket(target.AddressFamily,SocketType.Dgram,ProtocolType.Udp);
            socket.Connect(target,equipment.Port);
            var address=((IPEndPoint)socket.LocalEndPoint!).Address;
            if(!IPAddress.IsLoopback(address)) return address.ToString();
        }
        catch { }
        var candidates=LocalAddresses();
        if(candidates.Count==1) return candidates[0];
        throw new InvalidOperationException("설비에서 접근할 Windows PC의 IP를 설정에서 선택해 주세요.");
    }
    public static async Task<bool> PortOpenAsync(string host,int port,int milliseconds=1500)
    {
        using var client=new TcpClient();
        using var cancellation=new CancellationTokenSource(milliseconds);
        try { await client.ConnectAsync(host,port,cancellation.Token); return true; }
        catch { return false; }
    }
}

public sealed class XServerService
{
    private readonly SemaphoreSlim gate=new(1,1);
    public static string FindExecutable(string file)
    {
        var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)};
        foreach(var root in roots) foreach(var folder in new[]{"Xming","VcXsrv"})
        { var candidate=Path.Combine(root,folder,file); if(File.Exists(candidate)) return candidate; }
        return "";
    }
    public static void Discover(Preferences preferences)
    {
        if(string.IsNullOrWhiteSpace(preferences.XServerPath)) preferences.XServerPath=FindExecutable("Xming.exe");
        if(string.IsNullOrWhiteSpace(preferences.XServerPath)) preferences.XServerPath=FindExecutable("vcxsrv.exe");
        if(string.IsNullOrWhiteSpace(preferences.XLaunchPath)) preferences.XLaunchPath=FindExecutable("XLaunch.exe");
    }
    public static int? ConfigDisplay(string path)
    {
        if(!File.Exists(path)) return null;
        var settings=new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1000000};
        using var reader=System.Xml.XmlReader.Create(path,settings);
        var root=XDocument.Load(reader).Root;
        return int.TryParse(root?.Attribute("Display")?.Value,out int display) && display>=0 && display<=99?display:null;
    }
    public async Task<string> EnsureAsync(Preferences preferences)
    {
        await gate.WaitAsync();
        try
        {
            preferences.Validate(); Discover(preferences);
            int port=6000+preferences.DisplayNumber;
            if(await NetworkTools.PortOpenAsync("127.0.0.1",port,500)) return $"X 서버 포트 {port} 사용 중";
            ProcessStartInfo start;
            if(!string.IsNullOrWhiteSpace(preferences.XLaunchConfig))
            {
                if(!File.Exists(preferences.XLaunchConfig)) throw new FileNotFoundException("저장한 XLaunch 설정 파일을 찾을 수 없습니다. 설정에서 경로를 다시 지정해 주세요.");
                if(!File.Exists(preferences.XLaunchPath)) throw new FileNotFoundException("XLaunch.exe 경로를 설정해 주세요.");
                var number=ConfigDisplay(preferences.XLaunchConfig);
                if(number.HasValue && number.Value!=preferences.DisplayNumber) throw new InvalidOperationException("XLaunch 파일의 Display 번호("+number+")와 앱 설정이 다릅니다. 설정에서 번호를 맞춰 주세요.");
                start=new(preferences.XLaunchPath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(preferences.XLaunchConfig)!};
                start.ArgumentList.Add("-run"); start.ArgumentList.Add(preferences.XLaunchConfig);
            }
            else
            {
                if(!File.Exists(preferences.XServerPath)) throw new FileNotFoundException("Xming.exe 또는 VcXsrv.exe를 설정에서 선택해 주세요. 기존 XLaunch 설정 파일도 사용할 수 있습니다.");
                start=new(preferences.XServerPath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(preferences.XServerPath)!};
                start.ArgumentList.Add(":"+preferences.DisplayNumber); start.ArgumentList.Add("-multiwindow"); start.ArgumentList.Add("-clipboard");
            }
            using var process=Process.Start(start) ?? throw new InvalidOperationException("X 서버를 실행하지 못했습니다.");
            for(int i=0;i<20;i++)
            { await Task.Delay(300); if(await NetworkTools.PortOpenAsync("127.0.0.1",port,300)) return "X 서버 시작됨 · 디스플레이 :"+preferences.DisplayNumber; }
            throw new TimeoutException("X 서버의 TCP "+port+" 응답을 확인하지 못했습니다. XLaunch 시작 화면과 설정을 확인해 주세요.");
        }
        finally { gate.Release(); }
    }
    public static async Task<string> AllowHostAsync(Equipment equipment,Preferences preferences)
    {
        equipment.Validate();
        var server=preferences.XServerPath;
        if(string.IsNullOrWhiteSpace(server)) { Discover(preferences); server=preferences.XServerPath; }
        var tool=Path.Combine(Path.GetDirectoryName(server)??"","xhost.exe");
        if(!File.Exists(tool)) tool=FindExecutable("xhost.exe");
        if(!File.Exists(tool)) throw new FileNotFoundException("xhost.exe를 찾을 수 없습니다. 기존 XLaunch 설정의 설비 접근 허용을 확인해 주세요.");
        var addresses=await Dns.GetHostAddressesAsync(equipment.Host).WaitAsync(TimeSpan.FromSeconds(5));
        var address=addresses.FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork) ?? throw new InvalidOperationException("X11 직접 연결에 사용할 설비 IPv4 주소를 찾지 못했습니다.");
        var info=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(tool)!};
        info.ArgumentList.Add("+"+address); info.Environment["DISPLAY"]="127.0.0.1:"+preferences.DisplayNumber+".0";
        using var process=Process.Start(info)!;
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { try{process.Kill();}catch{} throw new TimeoutException("X 서버 접근 설정 응답이 없습니다."); }
        if(process.ExitCode!=0) throw new InvalidOperationException((await error).Trim());
        return "X 서버 접근 허용: "+address+" · "+(await output).Trim();
    }
}
