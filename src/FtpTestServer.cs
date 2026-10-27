using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EquipLink;

/// <summary>Test-only, loopback-bound FTP fixture. It never connects to real equipment.</summary>
internal sealed class FtpTestServer : IAsyncDisposable
{
    private readonly TcpListener control=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource shutdown=new();
    private readonly ConcurrentBag<Task> clients=[];
    private readonly ConcurrentDictionary<string,byte[]> files=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> directories=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,int> commands=new(StringComparer.Ordinal);
    private readonly Task accepting;
    internal int Port=>((IPEndPoint)control.LocalEndpoint).Port;
    internal bool SupportEpsv { get; set; }=true;
    internal bool SupportMlsd { get; set; }=true;
    internal bool SupportFeatures { get; set; }=true;
    internal bool SupportUtf8 { get; set; }=true;
    internal string WireEncoding { get; set; }="UTF-8";
    internal string Greeting { get; set; }="EquipLink owned local FTP fixture";
    internal bool RequirePassword { get; set; }
    internal bool FailUpload { get; set; }
    internal string? FailDownloadPath { get; set; }
    internal int TransferDelayMs { get; set; }
    internal int LoginDelayMs { get; set; }
    internal int ListDelayMs { get; set; }
    internal int TransferChunkSize { get; set; }=32768;
    internal string PassiveAdvertisedAddress { get; set; }="127.0.0.1";
    internal string? InjectedListing { get; set; }
    internal bool RejectRename { get; set; }
    internal string Password { get; set; }="fixture-password";
    internal string Username { get; set; }="fixture-id";
    internal FtpTestServer(){directories["/"]=0;control.Start();accepting=AcceptAsync();}
    internal FtpSite Site(string initialPath="/")=>new(){Name="LOCAL FTP FIXTURE",Host="127.0.0.1",Port=Port,Username=Username,InitialPath=initialPath};
    internal void SetFile(string path,byte[] data)
    {
        path=FtpPaths.Normalize(path);EnsureDirectory(FtpPaths.Parent(path));files[path]=data.ToArray();
    }
    internal void EnsureDirectory(string path)
    {
        path=FtpPaths.Normalize(path);directories[path]=0;
        if(path!="/")EnsureDirectory(FtpPaths.Parent(path));
    }
    internal bool FileExists(string path)=>files.ContainsKey(FtpPaths.Normalize(path));
    internal bool DirectoryExists(string path)=>directories.ContainsKey(FtpPaths.Normalize(path));
    internal byte[] ReadFile(string path)=>files[FtpPaths.Normalize(path)].ToArray();
    internal string[] FilePaths=>files.Keys.ToArray();
    internal int CommandCount(string command)=>commands.GetValueOrDefault(command);
    internal async Task WaitForCommandAsync(string command,int count=1)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(CommandCount(command)<count)await Task.Delay(10,timeout.Token);
    }
    private async Task AcceptAsync()
    {
        try
        {
            while(!shutdown.IsCancellationRequested)
            {
                var client=await control.AcceptTcpClientAsync(shutdown.Token);
                clients.Add(HandleAsync(client));
            }
        }
        catch(OperationCanceledException){}
        catch(SocketException) when(shutdown.IsCancellationRequested){}
    }
    private async Task HandleAsync(TcpClient client)
    {
        TcpListener? passive=null;
        string working="/",user="",rename="";
        bool authenticated=!RequirePassword;
        var ct=shutdown.Token;
        using(client)
        using(var stream=client.GetStream())
        using(var reader=new System.IO.StreamReader(stream,FtpEncoding.Get(WireEncoding),false,4096,true))
        using(var writer=new System.IO.StreamWriter(stream,FtpEncoding.Get(WireEncoding),4096,true){AutoFlush=true,NewLine="\r\n"})
        {
            async Task Reply(string text)=>await writer.WriteLineAsync(text.AsMemory(),ct);
            string PathFor(string value)=>FtpPaths.Normalize(value,working);
            void StartPassive(){passive?.Stop();passive=new TcpListener(IPAddress.Loopback,0);passive.Start();}
            async Task<TcpClient> DataClient(){if(passive==null)throw new InvalidOperationException();return await passive.AcceptTcpClientAsync(ct);}
            try
            {
                await Reply("220 "+Greeting);
                while(!ct.IsCancellationRequested)
                {
                    string? line=await reader.ReadLineAsync(ct);if(line==null)break;
                    int space=line.IndexOf(' ');string command=(space<0?line:line[..space]).ToUpperInvariant();string argument=space<0?"":line[(space+1)..];
                    commands.AddOrUpdate(command,1,(_,n)=>n+1);
                    if(!authenticated&&command is not ("USER" or "PASS" or "QUIT" or "FEAT" or "OPTS")){await Reply("530 Authentication required");continue;}
                    switch(command)
                    {
                        case "USER":user=argument;await Reply("331 Password required");break;
                        case "PASS":if(LoginDelayMs>0)await Task.Delay(LoginDelayMs,ct);authenticated=!RequirePassword||(user==Username&&argument==Password);await Reply(authenticated?"230 Logged in":"530 Login rejected (fixture reply must not be retained)");break;
                        case "FEAT":await Reply(SupportFeatures?"211-Features\r\n"+(SupportUtf8?" UTF8\r\n":"")+" EPSV\r\n MLST type*;size*;modify*;\r\n SIZE\r\n211 End":"502 FEAT unsupported");break;
                        case "OPTS":commands.AddOrUpdate("OPTS "+argument,1,(_,n)=>n+1);await Reply(argument=="UTF8 ON"&&!SupportUtf8?"502 UTF8 unsupported":"200 Accepted");break;
                        case "TYPE":case "NOOP":await Reply("200 Accepted");break;
                        case "SYST":await Reply("215 UNIX Type: L8");break;
                        case "PWD":await Reply("257 \""+working.Replace("\"","\"\"")+"\" is current directory");break;
                        case "CWD":
                            if(directories.ContainsKey(PathFor(argument))){working=PathFor(argument);await Reply("250 Directory changed");}else await Reply("550 Directory not found");
                            break;
                        case "CDUP":working=FtpPaths.Parent(working);await Reply("250 Directory changed");break;
                        case "EPSV":
                            if(!SupportEpsv){await Reply("502 EPSV not supported");break;}
                            StartPassive();await Reply($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive!.LocalEndpoint).Port}|)");break;
                        case "PASV":
                            StartPassive();int port=((IPEndPoint)passive!.LocalEndpoint).Port;
                            await Reply($"227 Entering Passive Mode ({PassiveAdvertisedAddress.Replace('.',',')},{port/256},{port%256})");break;
                        case "MLSD":case "LIST":
                        {
                            if(command=="MLSD"&&!SupportMlsd){await Reply("500 MLSD not supported");passive?.Stop();passive=null;break;}
                            string path=string.IsNullOrEmpty(argument)?working:PathFor(argument);
                            if(!directories.ContainsKey(path)){await Reply("550 Directory not found");passive?.Stop();passive=null;break;}
                            if(ListDelayMs>0)await Task.Delay(ListDelayMs,ct);
                            await Reply("150 Opening listing");
                            using(var data=await DataClient())
                            {
                                string listing=InjectedListing??Listing(path,command=="MLSD");
                                await data.GetStream().WriteAsync(FtpEncoding.Get(WireEncoding).GetBytes(listing),ct);
                            }
                            passive?.Stop();passive=null;await Reply("226 Listing complete");break;
                        }
                        case "SIZE":
                            if(files.TryGetValue(PathFor(argument),out var sized))await Reply("213 "+sized.Length.ToString(CultureInfo.InvariantCulture));else await Reply("550 File not found");break;
                        case "RETR":
                        {
                            string path=PathFor(argument);
                            if(!files.TryGetValue(path,out var content)){await Reply("550 File not found");passive?.Stop();passive=null;break;}
                            await Reply("150 Opening binary transfer");
                            using(var data=await DataClient())
                            {
                                int end=path==FailDownloadPath?Math.Min(content.Length,7):content.Length;
                                for(int offset=0;offset<end;offset+=TransferChunkSize)
                                {
                                    await data.GetStream().WriteAsync(content.AsMemory(offset,Math.Min(TransferChunkSize,end-offset)),ct);
                                    if(TransferDelayMs>0)await Task.Delay(TransferDelayMs,ct);
                                }
                            }
                            passive?.Stop();passive=null;await Reply(path==FailDownloadPath?"426 Transfer interrupted":"226 Transfer complete");break;
                        }
                        case "STOR":
                        {
                            string path=PathFor(argument);
                            if(!directories.ContainsKey(FtpPaths.Parent(path))){await Reply("550 Directory not found");passive?.Stop();passive=null;break;}
                            await Reply("150 Opening binary transfer");
                            using(var data=await DataClient())
                            using(var output=new System.IO.MemoryStream())
                            {
                                byte[] buffer=new byte[TransferChunkSize];int read;
                                while((read=await data.GetStream().ReadAsync(buffer,ct))>0)
                                {await output.WriteAsync(buffer.AsMemory(0,read),ct);if(TransferDelayMs>0)await Task.Delay(TransferDelayMs,ct);}
                                files[path]=output.ToArray();
                            }
                            passive?.Stop();passive=null;await Reply(FailUpload?"426 Transfer interrupted":"226 Transfer complete");break;
                        }
                        case "MKD":
                        {
                            string path=PathFor(argument);
                            if(directories.ContainsKey(path)||files.ContainsKey(path)||!directories.ContainsKey(FtpPaths.Parent(path)))await Reply("550 Cannot create directory");
                            else {directories[path]=0;await Reply("257 \""+path+"\" created");}break;
                        }
                        case "RNFR":rename=PathFor(argument);await Reply(files.ContainsKey(rename)?"350 Send new name":"550 Source not found");break;
                        case "RNTO":
                        {
                            string path=PathFor(argument);
                            if(RejectRename||!files.TryRemove(rename,out var content)||!directories.ContainsKey(FtpPaths.Parent(path)))await Reply("550 Rename rejected");
                            else{files[path]=content;await Reply("250 Renamed");}rename="";break;
                        }
                        case "DELE":await Reply(files.TryRemove(PathFor(argument),out _)?"250 Deleted":"550 File not found");break;
                        case "RMD":
                        {
                            string path=PathFor(argument);bool empty=path!="/"&&!files.Keys.Any(x=>x.StartsWith(path+"/",StringComparison.Ordinal))&&!directories.Keys.Any(x=>x.StartsWith(path+"/",StringComparison.Ordinal));
                            await Reply(empty&&directories.TryRemove(path,out _)?"250 Removed":"550 Directory not empty or missing");break;
                        }
                        case "QUIT":await Reply("221 Goodbye");return;
                        default:await Reply("502 Unsupported command");break;
                    }
                }
            }
            catch(Exception ex) when(ex is OperationCanceledException or System.IO.IOException or SocketException or ObjectDisposedException){}
            finally{passive?.Stop();}
        }
    }
    private string Listing(string parent,bool machine)
    {
        var lines=new List<string>();
        foreach(string directory in directories.Keys.Where(p=>p!="/"&&FtpPaths.Parent(p)==parent).Order())
        {
            string name=directory[(directory.LastIndexOf('/')+1)..];
            lines.Add(machine?$"type=dir;modify=20261021120000; {name}":$"drwxr-xr-x 2 owner group 4096 Oct 21 12:00 {name}");
        }
        foreach(var file in files.Where(p=>FtpPaths.Parent(p.Key)==parent).OrderBy(p=>p.Key))
        {
            string name=file.Key[(file.Key.LastIndexOf('/')+1)..];
            lines.Add(machine?$"type=file;size={file.Value.Length};modify=20261021120000; {name}":$"-rw-r--r-- 1 owner group {file.Value.Length} Oct 21 12:00 {name}");
        }
        return string.Join("\r\n",lines)+(lines.Count>0?"\r\n":"");
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();control.Stop();try{await accepting;}catch{}
        try{await Task.WhenAll(clients.ToArray()).WaitAsync(TimeSpan.FromSeconds(3));}catch{}
        shutdown.Dispose();
    }
}
