using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace EquipLink;

public enum FtpOverwriteDecision { Replace, Skip, Cancel }
public sealed record FtpConflict(string Direction,string LocalPath,string RemotePath);

public sealed class FtpTransferItem : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public string SiteName { get; init; } = "";
    public string Host { get; init; } = "";
    public string Name => Path.GetFileName(LocalPath.TrimEnd(Path.DirectorySeparatorChar));
    public string Direction { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public bool IsDirectory { get; init; }
    private string status="대기", error="";
    private long bytes;
    private long? total;
    public string Status { get=>status; internal set{status=value;Notify();Notify(nameof(IsPending));Notify(nameof(Percent));Notify(nameof(Progress));} }
    public string Error { get=>error; internal set{error=value;Notify();} }
    public long Bytes { get=>bytes; internal set{bytes=value;Notify();Notify(nameof(Percent));Notify(nameof(Progress));} }
    public long? Total { get=>total; internal set{total=value;Notify();Notify(nameof(Percent));Notify(nameof(Progress));} }
    public double Percent => Status is "완료" or "전달 완료"?100:Total>0?Math.Clamp((double)Bytes/Total.Value*100,0,100):0;
    public double Progress => Percent;
    public bool IsPending => Status is "대기" or "전송 중";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    internal FtpTransferRecord Record()=>new(){Id=Id,At=At,SiteName=SiteName,Host=Host,Direction=Direction,LocalPath=LocalPath,RemotePath=RemotePath,Bytes=Bytes,Status=Status,Error=Error};
    internal static FtpTransferItem FromRecord(FtpTransferRecord r)=>new(){Id=r.Id,At=r.At,SiteName=r.SiteName,Host=r.Host,Direction=r.Direction,LocalPath=r.LocalPath,RemotePath=r.RemotePath,Bytes=r.Bytes,Total=r.Bytes,Status=r.Status,Error=r.Error};
}

/// <summary>One dedicated FTP connection per queued job, separate from the file browser.</summary>
public sealed class FtpTransferQueue : IDisposable
{
    private sealed class Work(FtpTransferItem item,FtpSite site,string protectedPassword,bool overwrite,Func<FtpConflict,CancellationToken,Task<FtpOverwriteDecision>>? resolver)
    {
        internal readonly FtpTransferItem Item=item;
        internal readonly FtpSite Site=site;
        internal string ProtectedPassword=protectedPassword;
        internal readonly bool Overwrite=overwrite;
        internal readonly Func<FtpConflict,CancellationToken,Task<FtpOverwriteDecision>>? Resolver=resolver;
        internal readonly HashSet<string> Approved=[];
        internal readonly CancellationTokenSource Cancel=new();
    }
    private sealed record TransferFile(string Local,string Remote,long? Size);
    private sealed class Plan
    {
        internal readonly List<string> Directories=[];
        internal readonly List<TransferFile> Files=[];
        internal int Skipped;
        internal void Limit(){if(Directories.Count+Files.Count+Skipped>10000)throw new IOException("한 번에 전송할 폴더 항목은 10,000개 이내여야 합니다.");}
    }
    private readonly AppStore store;
    private readonly Dispatcher? dispatcher;
    private readonly object gate=new();
    private readonly Queue<Work> pending=new();
    private readonly Dictionary<string,Work> workById=[];
    private Task pump=Task.CompletedTask;
    private bool pumping,disposed;
    public ObservableCollection<FtpTransferItem> Items { get; }=[];
    public bool HasPending { get {lock(gate)return workById.Count>0;} }
    public event Action? Changed;
    public Func<FtpConflict,CancellationToken,Task<FtpOverwriteDecision>>? ConfirmOverwrite { get; set; }
    public FtpTransferQueue(AppStore store)
    {
        this.store=store;
        dispatcher=Application.Current?.Dispatcher;
        foreach(var record in store.TransferHistory.Take(500))Items.Add(FtpTransferItem.FromRecord(record));
    }
    public FtpTransferItem EnqueueUpload(FtpSite site,string password,string localFile,string remotePath,bool overwrite=false)=>Enqueue(site,password,localFile,remotePath,true,false,null,overwrite);
    public FtpTransferItem EnqueueDownload(FtpSite site,string password,string remotePath,string localFile,long? size=null,bool overwrite=false)=>Enqueue(site,password,localFile,remotePath,false,false,size,overwrite);
    public FtpTransferItem EnqueueUploadDirectory(FtpSite site,string password,string localDir,string remoteDir,bool overwrite=false)=>Enqueue(site,password,localDir,remoteDir,true,true,null,overwrite);
    public FtpTransferItem EnqueueDownloadDirectory(FtpSite site,string password,string remoteDir,string localDir,bool overwrite=false)=>Enqueue(site,password,localDir,remoteDir,false,true,null,overwrite);
    private FtpTransferItem Enqueue(FtpSite site,string password,string local,string remote,bool upload,bool directory,long? size,bool overwrite)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var snapshot=site.Copy();snapshot.Validate();
        string fullLocal=Path.GetFullPath(local);
        if(string.IsNullOrWhiteSpace(Path.GetFileName(fullLocal.TrimEnd(Path.DirectorySeparatorChar))))throw new ArgumentException("파일 또는 폴더 경로를 선택해 주세요.");
        if(upload && (directory?!Directory.Exists(fullLocal):!File.Exists(fullLocal)))throw new IOException("업로드할 파일 또는 폴더를 찾을 수 없습니다.");
        var item=new FtpTransferItem{SiteName=snapshot.Name,Host=snapshot.Host,Direction=upload?"업로드":"다운로드",LocalPath=fullLocal,RemotePath=FtpPaths.Normalize(remote),Total=size,IsDirectory=directory};
        var work=new Work(item,snapshot,CredentialProtector.Protect(snapshot.CredentialTarget(),password),overwrite,ConfirmOverwrite);
        // Never retain the caller's saved cipher on the profile snapshot. The queue owns
        // a short-lived, separately protected password and clears it on completion.
        snapshot.EncryptedPassword=null;
        OnUi(()=>{Items.Insert(0,item);TrimItems();Changed?.Invoke();});
        lock(gate)
        {
            pending.Enqueue(work);workById.Add(item.Id,work);
            if(!pumping){pumping=true;pump=Task.Run(ProcessAsync);}
        }
        OnUi(()=>Changed?.Invoke());
        return item;
    }
    public void Cancel(FtpTransferItem item){lock(gate)if(workById.TryGetValue(item.Id,out var work))work.Cancel.Cancel();}
    public void CancelAll(){lock(gate)foreach(var work in workById.Values)work.Cancel.Cancel();}
    public void RecordExternalDownload(FtpSite site,FtpEntry entry,string destination,long bytes)
    {
        var record=new FtpTransferRecord{SiteName=site.Name,Host=site.Host,Direction="드래그 다운로드",LocalPath=destination+"\\"+entry.Name,RemotePath=entry.FullPath,Bytes=bytes,Status="전달 완료"};
        OnUi(()=>{Items.Insert(0,FtpTransferItem.FromRecord(record));store.TransferHistory.Insert(0,record);if(store.TransferHistory.Count>500)store.TransferHistory.RemoveRange(500,store.TransferHistory.Count-500);store.Save();TrimItems();Changed?.Invoke();});
    }
    public async Task WaitForIdleAsync()
    {
        while(true){Task current;lock(gate){if(!pumping)return;current=pump;}await current.ConfigureAwait(false);}
    }
    private async Task ProcessAsync()
    {
        while(true)
        {
            Work work;
            lock(gate){if(pending.Count==0){pumping=false;return;}work=pending.Dequeue();}
            string password="";
            try
            {
                work.Cancel.Token.ThrowIfCancellationRequested();
                await OnUiAsync(()=>{work.Item.Status="전송 중";Changed?.Invoke();});
                password=CredentialProtector.Unprotect(work.Site.CredentialTarget(),work.ProtectedPassword);
                await using var client=await FtpClient.ConnectAsync(work.Site,password,work.Cancel.Token);
                var plan=await BuildPlanAsync(work,client,work.Cancel.Token);
                await PreflightAsync(work,client,plan,work.Cancel.Token);
                await OnUiAsync(()=>work.Item.Total=plan.Files.All(f=>f.Size.HasValue)?plan.Files.Sum(f=>f.Size!.Value):null);
                if(work.Item.Direction=="업로드")
                    foreach(string dir in plan.Directories)await EnsureRemoteDirectoryAsync(client,dir,work.Cancel.Token);
                else foreach(string dir in plan.Directories){work.Cancel.Token.ThrowIfCancellationRequested();EnsureNoLocalLinks(dir);Directory.CreateDirectory(dir);}
                long completed=0;int transferred=0;
                foreach(var file in plan.Files)
                {
                    work.Cancel.Token.ThrowIfCancellationRequested();
                    long offset=completed;
                    long lastProgress=0;
                    var progress=new DirectProgress<FtpProgress>(p=>
                    {
                        long now=Environment.TickCount64;
                        if(p.Bytes!=0&&now-lastProgress<100&&p.Bytes!=p.Total)return;
                        lastProgress=now;
                        OnUi(()=>{work.Item.Bytes=offset+p.Bytes;if(plan.Files.Count==1&&p.Total.HasValue)work.Item.Total=p.Total;Changed?.Invoke();});
                    });
                    long count=work.Item.Direction=="업로드"
                        ?await UploadOneAsync(work,client,password,file,progress,work.Cancel.Token)
                        :await DownloadOneAsync(work,client,file,progress,work.Cancel.Token);
                    if(count<0){plan.Skipped++;continue;}
                    completed+=count;transferred++;
                    await OnUiAsync(()=>work.Item.Bytes=completed);
                }
                await OnUiAsync(()=>{work.Item.Status=transferred==0&&plan.Skipped>0?"건너뜀":"완료";if(plan.Skipped>0)work.Item.Error=$"건너뛴 파일·링크·연결점 또는 해석할 수 없는 목록 항목 {plan.Skipped}개는 전송하지 않았습니다.";});
            }
            catch(OperationCanceledException){await OnUiAsync(()=>work.Item.Status="취소");}
            catch(Exception ex)
            {
                // A server may echo credentials in replies. Store only our classification,
                // never raw remote error text or exception details in durable history.
                await OnUiAsync(()=>{work.Item.Status=work.Cancel.IsCancellationRequested?"취소":"실패";work.Item.Error=SafeError(ex,work.Cancel.IsCancellationRequested);});
            }
            finally
            {
                password="";work.ProtectedPassword="";
                await OnUiAsync(()=>
                {
                    store.TransferHistory.RemoveAll(x=>x.Id==work.Item.Id);
                    store.TransferHistory.Insert(0,work.Item.Record());
                    if(store.TransferHistory.Count>500)store.TransferHistory.RemoveRange(500,store.TransferHistory.Count-500);
                    try{store.Save();}catch{work.Item.Error+=(work.Item.Error.Length>0?" ":"")+"전송 기록 저장에 실패했습니다. 저장 공간과 권한을 확인해 주세요.";}
                    // Keep HasPending true through durable history persistence so
                    // window shutdown cannot overtake the last completed record.
                    lock(gate)workById.Remove(work.Item.Id);
                    work.Cancel.Dispose();
                    TrimItems();Changed?.Invoke();
                });
                // A dispatcher already shutting down skips OnUiAsync; release its
                // canceled job resources even though no further UI can be updated.
                lock(gate)if(workById.Remove(work.Item.Id))work.Cancel.Dispose();
            }
        }
    }
    private static string SafeError(Exception error,bool canceled)=>canceled?"전송이 취소되었습니다. 완료된 파일은 유지됩니다.":error switch
    {
        UnauthorizedAccessException=>"파일 접근 권한을 확인해 주세요.",
        System.Security.Cryptography.CryptographicException=>"저장된 로그인 정보를 읽지 못했습니다. 다시 연결해 주세요.",
        FtpException ftp=>$"FTP 서버가 요청을 완료하지 못했습니다 (응답 {ftp.ReplyCode}). 경로와 권한을 확인해 주세요.",
        System.Net.Sockets.SocketException=>"FTP 서버와 연결하지 못했습니다. 주소와 네트워크를 확인해 주세요.",
        _=>"전송을 완료하지 못했습니다. 대상 파일의 중복, 경로, 권한 및 연결 상태를 확인해 주세요."
    };
    private static async Task<Plan> BuildPlanAsync(Work work,FtpClient client,CancellationToken ct)
    {
        var plan=new Plan();
        if(work.Item.Direction=="다운로드")await CheckRemotePathAsync(client,work.Item.RemotePath,false,ct);
        if(!work.Item.IsDirectory)
        {
            if(work.Item.Direction=="업로드")EnsureNoLocalLinks(work.Item.LocalPath);
            plan.Files.Add(new(work.Item.LocalPath,work.Item.RemotePath,work.Item.Direction=="업로드"?new FileInfo(work.Item.LocalPath).Length:work.Item.Total));
            return plan;
        }
        if(work.Item.Direction=="업로드")
        {
            EnsureNoLocalLinks(work.Item.LocalPath);
            var directories=new Stack<(string Local,string Remote)>();directories.Push((work.Item.LocalPath,work.Item.RemotePath));
            while(directories.TryPop(out var folder))
            {
                ct.ThrowIfCancellationRequested();plan.Directories.Add(folder.Remote);plan.Limit();
                foreach(string path in Directory.EnumerateFileSystemEntries(folder.Local))
                {
                    ct.ThrowIfCancellationRequested();var attributes=File.GetAttributes(path);
                    if(attributes.HasFlag(FileAttributes.ReparsePoint)){plan.Skipped++;plan.Limit();continue;}
                    string remote=FtpPaths.Join(folder.Remote,Path.GetFileName(path));
                    if(attributes.HasFlag(FileAttributes.Directory))directories.Push((path,remote));
                    else {plan.Files.Add(new(path,remote,new FileInfo(path).Length));plan.Limit();}
                }
            }
        }
        else
        {
            var directories=new Stack<(string Remote,string Local)>();directories.Push((work.Item.RemotePath,work.Item.LocalPath));
            while(directories.TryPop(out var folder))
            {
                ct.ThrowIfCancellationRequested();EnsureNoLocalLinks(folder.Local);plan.Directories.Add(folder.Local);plan.Limit();
                var listing=await client.ListAsync(folder.Remote,ct);
                if(listing is FtpListing details){plan.Skipped+=details.OmittedCount;plan.Limit();}
                foreach(var entry in listing)
                {
                    if(entry.IsLink){plan.Skipped++;plan.Limit();continue;}
                    string local=SafeLocalChild(folder.Local,entry.Name),remote=FtpPaths.Join(folder.Remote,entry.Name);
                    if(entry.IsDirectory)directories.Push((remote,local));
                    else {plan.Files.Add(new(local,remote,entry.Size));plan.Limit();}
                }
            }
        }
        return plan;
    }
    private static async Task PreflightAsync(Work work,FtpClient client,Plan plan,CancellationToken ct)
    {
        if(work.Item.Direction=="다운로드")
        {
            var destinations=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string directory in plan.Directories)
                if(!destinations.Add(Path.GetFullPath(directory)))throw new IOException("Windows에서 같은 이름으로 저장되는 폴더가 있습니다.");
            foreach(var file in plan.Files.ToArray())
            {
                ct.ThrowIfCancellationRequested();EnsureNoLocalLinks(file.Local);
                if(!destinations.Add(Path.GetFullPath(file.Local)))throw new IOException("Windows에서 같은 이름으로 저장되는 파일이 있습니다.");
                if(Directory.Exists(file.Local))throw new IOException("대상 경로에 폴더가 이미 있습니다.");
                if(File.Exists(file.Local)&&!await ApproveOverwriteAsync(work,file,ct)){plan.Files.Remove(file);plan.Skipped++;}
            }
            return;
        }
        var listings=new Dictionary<string,IReadOnlyList<FtpEntry>>(StringComparer.Ordinal);
        foreach(var file in plan.Files.ToArray())
        {
            string parent=FtpPaths.Parent(file.Remote);
            if(!listings.TryGetValue(parent,out var listing))
            {
                await CheckRemotePathAsync(client,parent,work.Item.IsDirectory,ct);
                try{listing=FtpListing.RequireComplete(await client.ListAsync(parent,ct));}
                catch(FtpException ex) when(ex.ReplyCode==550 && plan.Directories.Contains(parent)){listing=[];}
                listings[parent]=listing;
            }
            var existing=listing.FirstOrDefault(e=>string.Equals(e.Name,RemoteName(file.Remote),StringComparison.OrdinalIgnoreCase));
            if(existing!=null)
            {
                if(existing.IsDirectory||existing.IsLink)throw new IOException("대상에 폴더 또는 링크가 이미 있습니다.");
                if(!await ApproveOverwriteAsync(work,file,ct)){plan.Files.Remove(file);plan.Skipped++;}
            }
        }
    }
    private static async Task<bool> ApproveOverwriteAsync(Work work,TransferFile file,CancellationToken ct)
    {
        if(work.Overwrite||work.Approved.Contains(file.Remote))return true;
        if(work.Resolver==null)throw new IOException("대상 파일이 이미 있습니다.");
        ct.ThrowIfCancellationRequested();var choice=await work.Resolver(new(work.Item.Direction,file.Local,file.Remote),ct);ct.ThrowIfCancellationRequested();
        if(choice==FtpOverwriteDecision.Cancel)throw new OperationCanceledException(ct);
        if(choice==FtpOverwriteDecision.Skip)return false;work.Approved.Add(file.Remote);return true;
    }
    private static async Task CheckRemotePathAsync(FtpClient client,string path,bool allowMissing,CancellationToken ct)
    {
        string current="/";
        foreach(string part in FtpPaths.Normalize(path).Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            var entry=(await client.ListAsync(current,ct)).FirstOrDefault(e=>string.Equals(e.Name,part,StringComparison.OrdinalIgnoreCase));
            if(entry==null){if(allowMissing)return;throw new IOException("전송 경로를 찾을 수 없습니다.");}
            if(entry.IsLink)throw new IOException("심볼릭 링크 경로는 전송하지 않습니다.");
            current=FtpPaths.Join(current,part);
        }
    }
    private static async Task EnsureRemoteDirectoryAsync(FtpClient client,string path,CancellationToken ct)
    {
        // Walk from the root so a requested initial folder may itself be new.
        string current="/";
        foreach(string part in FtpPaths.Normalize(path).Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            var existing=FtpListing.RequireComplete(await client.ListAsync(current,ct)).FirstOrDefault(e=>string.Equals(e.Name,part,StringComparison.OrdinalIgnoreCase));
            current=FtpPaths.Join(current,part);
            if(existing!=null)
            {
                if(existing.IsLink||!existing.IsDirectory)throw new IOException("업로드 대상 폴더가 링크 또는 파일입니다.");
                continue;
            }
            await client.MakeDirectoryAsync(current,ct);
        }
    }
    private static async Task<long> DownloadOneAsync(Work work,FtpClient client,TransferFile file,IProgress<FtpProgress> progress,CancellationToken ct)
    {
        string parent=Path.GetDirectoryName(file.Local)!;
        EnsureNoLocalLinks(parent);
        if(!Directory.Exists(parent))throw new DirectoryNotFoundException();
        string temp=Path.Combine(parent,".equiplink-"+Guid.NewGuid().ToString("N")+".part");
        try
        {
            long length;
            await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                await client.DownloadAsync(file.Remote,stream,progress,ct);
                ct.ThrowIfCancellationRequested();await stream.FlushAsync(ct);stream.Flush(true);length=stream.Length;
                if(file.Size.HasValue&&length!=file.Size.Value)throw new IOException("서버 파일 크기와 수신 크기가 다릅니다.");
            }
            ct.ThrowIfCancellationRequested();EnsureNoLocalLinks(file.Local);
            if(File.Exists(file.Local)&&!await ApproveOverwriteAsync(work,file,ct))return -1;
            File.Move(temp,file.Local,work.Overwrite||work.Approved.Contains(file.Remote));
            return length;
        }
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
    }
    private static async Task<long> UploadOneAsync(Work work,FtpClient client,string password,TransferFile file,IProgress<FtpProgress> progress,CancellationToken ct)
    {
        string temp=FtpPaths.Join(FtpPaths.Parent(file.Remote),".equiplink-"+Guid.NewGuid().ToString("N")+".part");
        bool promoted=false;
        try
        {
            EnsureNoLocalLinks(file.Local);
            long length;
            await using(var stream=new FileStream(file.Local,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                length=stream.Length;await client.UploadAsync(stream,temp,progress,ct);
                if(stream.Position!=length)throw new IOException("파일을 끝까지 전송하지 못했습니다.");
            }
            ct.ThrowIfCancellationRequested();
            // Recheck immediately before promotion. FTP has no portable atomic
            // no-replace rename, so a separate client racing this check is a server limitation.
            var existing=FtpListing.RequireComplete(await client.ListAsync(FtpPaths.Parent(file.Remote),ct)).FirstOrDefault(e=>string.Equals(e.Name,RemoteName(file.Remote),StringComparison.OrdinalIgnoreCase));
            if(existing!=null)
            {
                if(existing.IsDirectory||existing.IsLink)throw new IOException("대상에 폴더 또는 링크가 이미 있습니다.");
                if(!await ApproveOverwriteAsync(work,file,ct))return -1;
            }
            await client.RenameAsync(temp,file.Remote,ct);promoted=true;
            return length;
        }
        finally
        {
            if(!promoted)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    // Cancellation aborts the data/control socket. Use a fresh connection
                    // only to remove this exact generated temporary file, never the target.
                    await using var cleaner=await FtpClient.ConnectAsync(work.Site,password,cleanup.Token);
                    await cleaner.DeleteFileAsync(temp,cleanup.Token);
                }
                catch { /* A disconnected server may retain this identifiable .part file. */ }
            }
        }
    }
    internal static string SafeLocalChild(string parent,string leaf)
    {
        string name=FtpPaths.SafeLocalName(leaf);
        string directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string root=Path.EndsInDirectorySeparator(directory)?directory:directory+Path.DirectorySeparatorChar;
        string candidate=Path.GetFullPath(Path.Combine(root,name));
        if(!candidate.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(candidate)!),directory,StringComparison.OrdinalIgnoreCase))throw new IOException("서버 파일 이름이 올바르지 않습니다.");
        EnsureNoLocalLinks(candidate);return candidate;
    }
    private static void EnsureNoLocalLinks(string path)
    {
        var current=Path.GetFullPath(path);
        while(!string.IsNullOrEmpty(current))
        {
            if((File.Exists(current)||Directory.Exists(current))&&File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))throw new IOException("심볼릭 링크 또는 연결점 경로에는 전송하지 않습니다.");
            current=Path.GetDirectoryName(current);
        }
    }
    private static string RemoteName(string remote)=>remote[(remote.LastIndexOf('/')+1)..];
    private void TrimItems(){for(int i=Items.Count-1;i>=500;i--)if(!Items[i].IsPending)Items.RemoveAt(i);}
    private void OnUi(Action action)
    {
        if(dispatcher==null||dispatcher.CheckAccess())action();
        else if(!dispatcher.HasShutdownStarted)dispatcher.BeginInvoke(action,DispatcherPriority.Background);
    }
    private Task OnUiAsync(Action action)
    {
        if(dispatcher==null||dispatcher.CheckAccess()){action();return Task.CompletedTask;}
        if(dispatcher.HasShutdownStarted)return Task.CompletedTask;
        return dispatcher.InvokeAsync(action,DispatcherPriority.Background).Task;
    }
    public void Dispose(){if(disposed)return;disposed=true;CancelAll();}
    private sealed class DirectProgress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
}
