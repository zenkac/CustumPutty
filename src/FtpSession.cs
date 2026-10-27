namespace EquipLink;

/// <summary>Shared browser session; transfers use their own connections.</summary>
public sealed class FtpSession : IDisposable
{
    private readonly AppStore store;
    private readonly SemaphoreSlim navigation=new(1,1);
    private CancellationTokenSource lifetime=new();
    private FtpClient? client;
    private string protectedPassword="";
    private int generation,busy;
    private bool disposed;
    public bool IsConnected=>client?.IsConnected==true;
    public FtpSite? Site { get; private set; }
    public string CurrentPath { get; private set; }="/";
    public IReadOnlyList<FtpEntry> Entries { get; private set; }=[];
    public bool IsBusy=>busy>0;
    public string Error { get; private set; }="";
    public string ListingNotice=>Entries is FtpListing listing?listing.Notice:"";
    public string EncodingDisplay=>client?.EncodingDisplay??"";
    public event Action? Changed;
    public FtpSession(AppStore store)=>this.store=store;

    public async Task ConnectAsync(FtpSite site,string password,CancellationToken ct=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var snapshot=site.Copy();snapshot.Validate();CredentialProtector.ValidateSecret(password);
        Disconnect();int version=generation;var token=lifetime.Token;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,ct);
        store.LastFtpConnection=snapshot.Copy();store.Save();
        Site=snapshot;busy=1;Error="";Changed?.Invoke();
        FtpClient? connection=null;
        try
        {
            connection=await FtpClient.ConnectAsync(snapshot,password,linked.Token);
            var entries=await connection.ListAsync(connection.WorkingDirectory,linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if(version!=generation)return;
            protectedPassword=CredentialProtector.Protect(snapshot.CredentialTarget(),password);
            client=connection;connection=null;CurrentPath=client.WorkingDirectory;Entries=entries;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception error){if(version==generation)Error=error.Message;throw;}
        finally
        {
            if(connection!=null)await connection.DisposeAsync();
            if(version==generation){busy=0;Changed?.Invoke();}
        }
    }

    public Task NavigateAsync(string path)=>BrowseAsync(async (connection,token)=>
    {
        FtpPaths.Validate(path);
        var canonical=await connection.ChangeDirectoryAsync(path,token);
        token.ThrowIfCancellationRequested();
        // Keep the server's actual CWD even if listing is denied.
        CurrentPath=canonical;Entries=[];
        var entries=await connection.ListAsync(canonical,token);token.ThrowIfCancellationRequested();Entries=entries;
    });
    public Task RefreshAsync()=>BrowseAsync(async (connection,token)=>{var entries=await connection.ListAsync(CurrentPath,token);token.ThrowIfCancellationRequested();Entries=entries;});
    public Task DeleteEntriesAsync(FtpEntry[] entries,string expectedFolder,Action deleted)=>BrowseAsync(async(connection,token)=>
    {
        if(CurrentPath!=expectedFolder)throw new InvalidOperationException("폴더가 변경되었습니다. 삭제할 항목을 다시 선택해 주세요.");
        await FtpFileDeletion.DeleteRemoteAsync(connection,expectedFolder,entries,deleted,token);
        Entries=await connection.ListAsync(CurrentPath,token);
    });
    public Task MakeDirectoryAsync(string name)=>BrowseAsync(async (connection,token)=>
    {
        await connection.MakeDirectoryAsync(FtpPaths.Join(CurrentPath,name),token);
        var entries=await connection.ListAsync(CurrentPath,token);token.ThrowIfCancellationRequested();Entries=entries;
    });
    public async Task<IReadOnlyList<FtpEntry>> ListFoldersAsync(string path)
    {
        IReadOnlyList<FtpEntry> result=[];
        await BrowseAsync(async (connection,token)=>result=(await connection.ListAsync(path,token)).Where(e=>e.IsDirectory&&!e.IsLink).ToArray());
        return result;
    }
    private async Task BrowseAsync(Func<FtpClient,CancellationToken,Task> operation)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        int version=generation;var token=lifetime.Token;
        await navigation.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var connection=client;
            if(connection?.IsConnected!=true)throw new InvalidOperationException("먼저 FTP 서버에 연결해 주세요.");
            busy++;Error="";Changed?.Invoke();
            try{await operation(connection,token);token.ThrowIfCancellationRequested();}
            catch(Exception error){if(version==generation)Error=error.Message;throw;}
            finally{if(version==generation){busy--;Changed?.Invoke();}}
        }
        finally{navigation.Release();}
    }
    public string PasswordForTransfer()
    {
        if(!IsConnected||Site==null)throw new InvalidOperationException("먼저 FTP 서버에 연결해 주세요.");
        return CredentialProtector.Unprotect(Site.CredentialTarget(),protectedPassword);
    }
    public void Disconnect()
    {
        generation++;lifetime.Cancel();lifetime.Dispose();lifetime=new();
        if(client!=null)_=client.DisposeAsync();client=null;protectedPassword="";
        Site=null;Entries=[];CurrentPath="/";busy=0;Error="";Changed?.Invoke();
    }
    public void Dispose(){if(disposed)return;Disconnect();disposed=true;lifetime.Dispose();}
}
