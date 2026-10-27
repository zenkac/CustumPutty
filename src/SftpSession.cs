using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Renci.SshNet;

namespace EquipLink;

public sealed record SftpEntry(string Name,string FullPath,bool IsDirectory,bool IsLink,long Size);
public sealed record SftpHostKey(string Host,int Port,string Algorithm,string Fingerprint);
public sealed record SftpTransfer(string Direction,string Name,string Status,long Bytes,DateTimeOffset At);
public sealed class SftpLogin
{
    public required Equipment Target { get; init; }
    public string Password { get; set; }="";
    public string KeyPassphrase { get; set; }="";
    public string InitialPath { get; init; }="";
    public bool Remember { get; init; }
    public override string ToString()=>Target.Name;
    public void Clear(){Password="";KeyPassphrase="";}
}

// Independent SFTP connection per terminal tab. This class cannot send shell
// commands and never reads terminal output, prompts, keyboard input or passwords.
public sealed class SftpSession : IDisposable
{
    readonly Dispatcher dispatcher;
    readonly SemaphoreSlim gate=new(1,1);
    CancellationTokenSource lifetime=new();
    Connection? connection;
    int generation;
    bool disposed;
    public bool IsConnected=>connection?.Client.IsConnected==true;
    public bool IsBusy { get; private set; }
    public string CurrentPath { get; private set; }="";
    public string Notice { get; private set; }="SSH 서버의 SFTP 기능으로 파일을 탐색합니다.";
    public string Error { get; private set; }="";
    public IReadOnlyList<SftpEntry> Entries { get; private set; }=[];
    public List<SftpTransfer> History { get; }=[];
    public event Action? Changed;
    public SftpSession(Dispatcher dispatcher)=>this.dispatcher=dispatcher;
    void Notify(){if(dispatcher.HasShutdownStarted)return;if(dispatcher.CheckAccess())Changed?.Invoke();else dispatcher.BeginInvoke(()=>Changed?.Invoke());}

    public async Task ConnectAsync(SftpLogin login,Func<SftpHostKey,bool> confirmHost,Func<string,bool,string?>? prompt=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var target=login.Target.Copy();target.Validate();
        if(!target.IsSsh)throw new InvalidOperationException("SSH Browser는 SSH 설비에서만 사용할 수 있습니다.");
        if(string.IsNullOrWhiteSpace(target.Username))throw new ArgumentException("SFTP 로그인 ID를 입력하세요.");
        ValidatePath(login.InitialPath,true);CredentialProtector.ValidateSecret(login.Password);
        Disconnect();int version=generation;var token=lifetime.Token;IsBusy=true;Error="";Notice="SFTP 연결 중…";Notify();
        Connection? candidate=null;
        try
        {
            candidate=new Connection(target,login.Password,login.KeyPassphrase,prompt,token);
            connection=candidate;string? trustError=null;
            candidate.Client.HostKeyReceived+=(_,e)=>
            {
                e.CanTrust=false;
                try{token.ThrowIfCancellationRequested();e.CanTrust=SftpKnownHosts.Verify(target,e.HostKeyName,e.HostKey,confirmHost,token);}
                catch(Exception error){trustError=error.Message;}
            };
            var snapshot=await Task.Run(async()=>
            {
                try{await candidate.Client.ConnectAsync(token).ConfigureAwait(false);}
                catch when(trustError!=null){throw new InvalidOperationException(trustError);}
                return Read(candidate.Client,login.InitialPath,token);
            },token);
            if(version!=generation){candidate.Dispose();return;}
            Apply(snapshot);Notice="SFTP 연결됨 · "+Entries.Count+"개 항목"+snapshot.Note;
        }
        catch(Exception error)
        {
            candidate?.Dispose();
            if(version!=generation)return;
            connection=null;Error=Friendly(error);Notice="SFTP 연결 실패";throw new InvalidOperationException(Error,error);
        }
        finally{if(version==generation){IsBusy=false;Notify();}}
    }
    public Task NavigateAsync(string path)=>RunAsync("폴더 읽는 중…",(client,ct)=>{var result=Read(client,Resolve(path,CurrentPath),ct);return ()=>{Apply(result);Notice=Entries.Count+"개 항목"+result.Note;};});
    public Task RefreshAsync()=>NavigateAsync(CurrentPath);
    public Task CreateDirectoryAsync(string name)
    {
        ValidateName(name);string target=Join(CurrentPath,name);
        return RunAsync("새 폴더 만드는 중…",(client,ct)=>{ct.ThrowIfCancellationRequested();client.CreateDirectory(target);var result=Read(client,CurrentPath,ct);return ()=>{Apply(result);Notice="폴더 생성 완료";};});
    }
    public async Task UploadAsync(string localPath)
    {
        string local=Path.GetFullPath(localPath),name=Path.GetFileName(local);ValidateName(name);
        string remote=Join(CurrentPath,name);long bytes=0;
        try
        {
            await RunAsync("업로드 준비 중…",(client,ct)=>
            {
                if(client.Exists(remote))throw new IOException("같은 이름이 이미 있습니다. 기존 파일은 덮어쓰지 않습니다.");
                string temp=Join(CurrentPath,".equiplink-"+Guid.NewGuid().ToString("N")+".part");bool committed=false;
                try
                {
                    using var input=new FileStream(local,FileMode.Open,FileAccess.Read,FileShare.Read);long total=input.Length;long last=0;
                    client.UploadFile(input,temp,false,count=>{ct.ThrowIfCancellationRequested();bytes=checked((long)count);Progress("업로드",name,bytes,total,ref last);});
                    ct.ThrowIfCancellationRequested();if(client.Exists(remote))throw new IOException("전송 중 같은 이름의 파일이 생겨 완료하지 않았습니다.");
                    client.RenameFile(temp,remote);committed=true;var result=Read(client,CurrentPath,ct);
                    return ()=>{Apply(result);Notice="업로드 완료 · "+name;};
                }
                finally{if(!committed&&client.IsConnected){try{client.DeleteFile(temp);}catch{}}}
            });
            Record("업로드",name,"완료",bytes);
        }
        catch(Exception e){Record("업로드",name,e is OperationCanceledException?"취소":"실패",bytes);throw;}
    }
    public async Task DownloadAsync(SftpEntry entry,string localPath)
    {
        if(entry.IsDirectory||entry.IsLink)throw new ArgumentException("다운로드할 일반 파일을 선택하세요.");
        ValidatePath(entry.FullPath);string destination=Path.GetFullPath(localPath);long bytes=0;
        try
        {
            await RunAsync("다운로드 준비 중…",(client,ct)=>
            {
                if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("같은 이름이 이미 있습니다. 다른 저장 이름을 선택하세요.");
                string temp=destination+".equiplink-"+Guid.NewGuid().ToString("N")+".part";bool committed=false;
                try
                {
                    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {long last=0;client.DownloadFile(entry.FullPath,output,count=>{ct.ThrowIfCancellationRequested();bytes=checked((long)count);Progress("다운로드",entry.Name,bytes,entry.Size,ref last);});output.Flush(true);}
                    ct.ThrowIfCancellationRequested();File.Move(temp,destination,false);committed=true;
                    return ()=>Notice="다운로드 완료 · "+entry.Name;
                }
                finally{if(!committed&&File.Exists(temp))File.Delete(temp);}
            });
            Record("다운로드",entry.Name,"완료",bytes);
        }
        catch(Exception e){Record("다운로드",entry.Name,e is OperationCanceledException?"취소":"실패",bytes);throw;}
    }
    void Record(string direction,string name,string status,long bytes){History.Insert(0,new(direction,name,status,bytes,DateTimeOffset.Now));if(History.Count>50)History.RemoveAt(History.Count-1);Notify();}
    void Progress(string direction,string name,long bytes,long total,ref long last)
    {
        long now=Environment.TickCount64;if(now-last<150&&bytes<total)return;last=now;
        int version=generation;dispatcher.BeginInvoke(()=>{if(version!=generation)return;Notice=$"{direction} · {(total>0?Math.Min(100,bytes*100d/total):100):0}%\n{name}";Notify();});
    }
    async Task RunAsync(string notice,Func<SftpClient,CancellationToken,Action> work)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(IsBusy)throw new InvalidOperationException("현재 작업을 마친 뒤 다시 시도하세요.");
        var current=connection;if(current?.Client.IsConnected!=true)throw new InvalidOperationException("먼저 SFTP에 연결하세요.");
        int version=generation;var token=lifetime.Token;IsBusy=true;Error="";Notice=notice;Notify();
        bool entered=false;
        try
        {
            await gate.WaitAsync(token);entered=true;
            var apply=await Task.Run(()=>{token.ThrowIfCancellationRequested();return work(current.Client,token);},token);
            if(version!=generation)throw new OperationCanceledException();apply();
        }
        catch(Exception error)
        {if(version!=generation)throw new OperationCanceledException();Error=Friendly(error);Notice="작업을 완료하지 못했습니다.";throw;}
        finally{if(entered)gate.Release();if(version==generation){IsBusy=false;Notify();}}
    }
    sealed record Snapshot(string Path,IReadOnlyList<SftpEntry> Entries,string Note);
    static Snapshot Read(SftpClient client,string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(path.Length>0)client.ChangeDirectory(path);
        string current=client.WorkingDirectory;ValidatePath(current);var entries=new List<SftpEntry>();int omitted=0;bool truncated=false;
        foreach(var entry in client.ListDirectory(current))
        {
            token.ThrowIfCancellationRequested();if(entry.Name is "." or "..")continue;
            try{ValidateName(entry.Name);}catch(ArgumentException){omitted++;continue;}
            if(entries.Count>=10000){truncated=true;break;}
            entries.Add(new(entry.Name,Join(current,entry.Name),entry.IsDirectory,entry.IsSymbolicLink,entry.Length));
        }
        string note=(truncated?" · 처음 10,000개 표시":"")+(omitted>0?$" · 표시할 수 없는 이름 {omitted}개 제외":"");
        return new(current,entries.OrderByDescending(e=>e.IsDirectory||e.IsLink).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ToArray(),note);
    }
    void Apply(Snapshot snapshot){CurrentPath=snapshot.Path;Entries=snapshot.Entries;}
    public void Disconnect()
    {
        if(disposed)return;
        generation++;lifetime.Cancel();lifetime.Dispose();lifetime=new();
        var previous=connection;connection=null;if(previous!=null)_=Task.Run(previous.Dispose);
        IsBusy=false;CurrentPath="";Entries=[];Error="";Notice="SFTP 연결 대기";Notify();
    }
    public void Dispose(){if(disposed)return;Disconnect();disposed=true;lifetime.Dispose();}
    public static void ValidatePath(string path,bool empty=false){if(path==null||(!empty&&path.Length==0)||path.Length>4096||path.Any(char.IsControl))throw new ArgumentException("경로는 제어 문자 없이 4,096자 이내로 입력하세요.");}
    public static void ValidateName(string name){ValidatePath(name);if(name is "." or ".."||name.Contains('/'))throw new ArgumentException("파일 또는 폴더 이름을 확인하세요.");}
    public static string Join(string path,string name){ValidateName(name);ValidatePath(path);return path.TrimEnd('/')+"/"+name;}
    public static string Resolve(string path,string current){ValidatePath(path);return path.StartsWith('/')?path:current.TrimEnd('/')+"/"+path;}
    public static string Parent(string path){string value=path.TrimEnd('/');int slash=value.LastIndexOf('/');return slash<=0?"/":value[..slash];}
    static string Friendly(Exception error)=>error switch
    {
        Renci.SshNet.Common.SshAuthenticationException=>"SFTP 인증에 실패했습니다. ID, 비밀번호 또는 개인 키를 확인하세요.",
        Renci.SshNet.Common.SshOperationTimeoutException=>"SFTP 서버 응답 시간이 초과되었습니다.",
        Renci.SshNet.Common.SftpPermissionDeniedException=>"이 경로에 접근할 권한이 없습니다.",
        Renci.SshNet.Common.SftpPathNotFoundException=>"서버에 해당 경로가 없습니다.",
        OperationCanceledException=>"작업을 취소했습니다.",
        _=>error.Message
    };
    sealed class Connection : IDisposable
    {
        public SftpClient Client { get; }
        readonly List<IDisposable> resources=[];int disposed;
        public Connection(Equipment target,string password,string passphrase,Func<string,bool,string?>? prompt,CancellationToken token)
        {
            try
            {
                var methods=new List<AuthenticationMethod>();
                if(target.PrivateKeyPath.Length>0){var key=new PrivateKeyFile(target.PrivateKeyPath,passphrase);resources.Add(key);methods.Add(new PrivateKeyAuthenticationMethod(target.Username,key));}
                if(password.Length>0||methods.Count==0)methods.Add(new PasswordAuthenticationMethod(target.Username,password));
                var interactive=new KeyboardInteractiveAuthenticationMethod(target.Username);
                interactive.AuthenticationPrompt+=(_,e)=>
                {
                    foreach(var request in e.Prompts)
                    {
                        token.ThrowIfCancellationRequested();
                        string? answer=password.Length>0&&e.Prompts.Count()==1&&request.Request.Trim().TrimEnd(':').Equals("Password",StringComparison.OrdinalIgnoreCase)
                            ?password:prompt?.Invoke(request.Request,request.IsEchoed);
                        if(answer==null)throw new OperationCanceledException("인증을 취소했습니다.");request.Response=answer;
                    }
                };
                methods.Add(interactive);resources.AddRange(methods);
                var info=new ConnectionInfo(target.Host,target.Port,target.Username,methods.ToArray()){Timeout=TimeSpan.FromSeconds(15),Encoding=new UTF8Encoding(false,true)};
                Client=new SftpClient(info){OperationTimeout=TimeSpan.FromSeconds(20),KeepAliveInterval=TimeSpan.FromSeconds(30)};
            }
            catch{foreach(var resource in resources)resource.Dispose();throw;}
        }
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;try{Client.Dispose();}finally{foreach(var resource in resources)resource.Dispose();}}
    }
}

internal static class SftpKnownHosts
{
    static readonly object Sync=new();
    static string PathName=>Path.Combine(AppPaths.Root,"sftp-host-keys.json");
    internal static bool Verify(Equipment target,string algorithm,byte[] key,Func<SftpHostKey,bool> confirm,CancellationToken token)
    {
        string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Host.ToLowerInvariant()+":"+target.Port)));
        string pin=algorithm+":"+Convert.ToBase64String(key);
        bool Known(Dictionary<string,string> pins)
        {
            if(!pins.TryGetValue(identity,out string? existing))return false;
            if(existing!=pin)throw new InvalidOperationException("저장된 SFTP 서버 키와 다릅니다. 연결을 중단했습니다. 서버 관리자에게 변경 여부를 확인하세요.");return true;
        }
        lock(Sync){if(Known(Read()))return true;}
        string fingerprint="SHA256:"+Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        if(!confirm(new(target.Host,target.Port,algorithm,fingerprint)))return false;
        token.ThrowIfCancellationRequested();
        lock(Sync)
        {
            var pins=Read();if(Known(pins))return true;pins[identity]=pin;Directory.CreateDirectory(AppPaths.Root);
            string temp=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temp,JsonSerializer.Serialize(pins));File.Move(temp,PathName,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        }
        return true;
    }
    static Dictionary<string,string> Read()=>File.Exists(PathName)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(PathName))??throw new InvalidDataException("SFTP 서버 키 저장 파일이 손상되었습니다."):new();
}
