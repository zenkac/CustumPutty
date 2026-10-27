using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EquipLink;

public static class FtpTests
{
    public static async Task<string> RunAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        string local=Path.Combine(AppPaths.Root,"ftp-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(local);
        var results=new List<string>();
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);results.Add("PASS: "+label);}
        void Reject(string label,Action action){try{action();}catch(Exception ex) when(ex is ArgumentException or IOException or InvalidDataException){Check(label,true);return;}Check(label,false);}
        async Task RejectAsync(string label,Func<Task> action){try{await action();}catch(Exception ex) when(ex is ArgumentException or IOException or InvalidDataException or OperationCanceledException){Check(label,true);return;}Check(label,false);}
        byte[] binary=Enumerable.Range(0,32769).Select(i=>(byte)((i*31)%256)).ToArray();
        byte[] original=Encoding.UTF8.GetBytes("Original target must survive failed or cancelled transfers.");
        string upload=Path.Combine(local,"binary-upload.bin");await File.WriteAllBytesAsync(upload,binary);
        await using var server=new FtpTestServer{RequirePassword=true};
        server.SetFile("/data/한국어 file.bin",binary);server.SetFile("/data/empty.txt",[]);server.EnsureDirectory("/data/nested");
        var site=server.Site("/data");site.Validate();
        Check("FTP defaults to port 21",new FtpSite().Port==21);
        Reject("FTP profile rejects CRLF command injection",()=>new FtpSite{Host="127.0.0.1",Username="id\r\nDELE file"}.Validate());
        Reject("FTP path rejects CRLF",()=>FtpPaths.Normalize("/folder\r\nDELE file"));
        Check("Remote paths normalize relative navigation",FtpPaths.Normalize("../folder/./file","/data/nested")=="/data/folder/file");
        Check("Remote path parent stops at root",FtpPaths.Parent("/")=="/"&&FtpPaths.Normalize("../../..","/")=="/");
        foreach(string unsafeName in new[]{"../escape.txt","..\\escape.txt","C:\\escape.txt","a:b","CON","NUL.txt","trailing.","trailing ","a/b"})
            Reject("Reject unsafe local filename "+unsafeName.Replace('\\','/'),()=>FtpTransferQueue.SafeLocalChild(local,unsafeName));
        Check("Unicode file name stays inside chosen local directory",Path.GetDirectoryName(FtpTransferQueue.SafeLocalChild(local,"한글 file.txt"))==local);
        string driveRoot=Path.GetPathRoot(local)!;
        Check("Drive root is a valid download folder without writing a file",FtpTransferQueue.SafeLocalChild(driveRoot,"equiplink-root-check.txt")==Path.Combine(driveRoot,"equiplink-root-check.txt"));
        await RejectAsync("Login rejection is reported without raw server reply",async()=>{await using var invalid=await FtpClient.ConnectAsync(site,"wrong");});
        await using(var client=await FtpClient.ConnectAsync(site,server.Password))
        {
            Check("Login starts at configured default folder",client.WorkingDirectory=="/data");
            var listing=await client.ListAsync(".");
            Check("MLSD lists Unicode names and directories",listing.Any(x=>x.Name=="한국어 file.bin"&&x.Size==binary.Length&&!x.IsDirectory)&&listing.Any(x=>x.Name=="nested"&&x.IsDirectory));
            Check("MLSD modification time parsed",listing.First(x=>x.Name=="한국어 file.bin").Modified.HasValue);
            Check("EPSV is used by modern server",server.CommandCount("EPSV")>0&&server.CommandCount("PASV")==0);
            using var downloaded=new MemoryStream();await client.DownloadAsync("한국어 file.bin",downloaded);
            Check("Binary download matches SHA256",SHA256.HashData(downloaded.ToArray()).SequenceEqual(SHA256.HashData(binary)));
            Check("Download keeps caller stream open",downloaded.CanWrite);
            using var source=new MemoryStream(binary);await client.UploadAsync(source,"/data/direct-upload.bin");
            Check("Binary upload matches SHA256",server.ReadFile("/data/direct-upload.bin").SequenceEqual(binary));
            Check("Upload keeps caller stream open",source.CanRead);
            await client.MakeDirectoryAsync("new folder");Check("New folder is created",server.DirectoryExists("/data/new folder"));
            Check("Path bar CWD reports server path",await client.ChangeDirectoryAsync("new folder")=="/data/new folder");
            await client.RenameAsync("/data/direct-upload.bin","/data/renamed.bin");
            Check("Remote rename preserves file",server.FileExists("/data/renamed.bin")&&!server.FileExists("/data/direct-upload.bin"));
            await client.DeleteFileAsync("/data/renamed.bin");Check("Temporary-file cleanup command works",!server.FileExists("/data/renamed.bin"));
            await RejectAsync("Protocol command injection rejected before send",()=>client.ChangeDirectoryAsync("/\r\nDELE /data/empty.txt"));
            Check("Rejected path leaves session usable",(await client.ListAsync("/data")).Any(e=>e.Name=="empty.txt"));
        }
        await using(var legacy=new FtpTestServer{SupportEpsv=false,SupportMlsd=false,PassiveAdvertisedAddress="203.0.113.99"})
        {
            legacy.SetFile("/file with spaces.txt",binary);legacy.EnsureDirectory("/a folder");
            await using var client=await FtpClient.ConnectAsync(legacy.Site(),legacy.Password);
            var listing=await client.ListAsync("/");
            Check("Legacy LIST fallback retains spaces",listing.Any(e=>e.Name=="file with spaces.txt"&&e.Size==binary.Length));
            Check("Legacy LIST fallback recognizes folders",listing.Any(e=>e.Name=="a folder"&&e.IsDirectory));
            Check("EPSV unsupported falls back to PASV",legacy.CommandCount("PASV")>0&&legacy.CommandCount("LIST")>0);
            using var target=new MemoryStream();await client.DownloadAsync("/file with spaces.txt",target);
            Check("PASV pins data socket to control peer despite advertised foreign IP",target.ToArray().SequenceEqual(binary));
        }
        var store=new AppStore();
        site.EncryptedPassword=CredentialProtector.Protect(site.CredentialTarget(),server.Password);
        store.FtpSites.Add(site.Copy());store.LastFtpConnection=site.Copy();store.Save();
        var stored=AppStore.Load();
        Check("FTP sitemap round-trips ID and default path",stored.FtpSites.Single().Username==site.Username&&stored.FtpSites.Single().InitialPath=="/data");
        Check("FTP password round-trips through Windows encryption",CredentialProtector.Unprotect(stored.FtpSites.Single().CredentialTarget(),stored.FtpSites.Single().EncryptedPassword!)==server.Password);
        Check("Last FTP connection is persisted",stored.LastFtpConnection?.Host==site.Host&&stored.LastFtpConnection.Port==site.Port);
        string storedJson=File.ReadAllText(AppPaths.Config);
        Check("Settings contain no plaintext FTP password",!storedJson.Contains(server.Password,StringComparison.Ordinal));
        var exported=store.ExportJson();Check("Export strips sitemap and recent FTP encrypted passwords",!exported.Contains(site.EncryptedPassword!,StringComparison.Ordinal)&&!exported.Contains(server.Password,StringComparison.Ordinal));
        var imported=AppStore.ReadImport(storedJson);Check("Import clears encrypted FTP secrets from both profile locations",!imported.FtpSites.Single().HasSavedPassword&&imported.LastFtpConnection?.HasSavedPassword==false);
        Check("Export does not mutate saved FTP password",store.FtpSites.Single().HasSavedPassword&&store.LastFtpConnection.HasSavedPassword);
        Check("Old v1.3 settings load with empty FTP state",AppStore.Read("{\"Version\":1,\"Equipment\":[]}").FtpSites.Count==0);
        using(var queue=new FtpTransferQueue(store))
        {
            var up=queue.EnqueueUpload(site,server.Password,upload,"/data/queued.bin");
            await queue.WaitForIdleAsync();
            Check("Queued upload completes with exact bytes",up.Status=="완료"&&up.Bytes==binary.Length&&server.ReadFile("/data/queued.bin").SequenceEqual(binary));
            Check("Queued upload promotes a temporary remote file",server.CommandCount("RNTO")>=2&&!server.FilePaths.Any(p=>p.Contains(".equiplink-",StringComparison.Ordinal)));
            string destination=Path.Combine(local,"queued-download.bin");
            var down=queue.EnqueueDownload(site,server.Password,"/data/queued.bin",destination,binary.Length);await queue.WaitForIdleAsync();
            Check("Queued download completes with exact bytes",down.Status=="완료"&&File.ReadAllBytes(destination).SequenceEqual(binary));
            Check("Completed transfer progress is 100 percent",down.Percent==100&&up.Percent==100);
            Check("Download removes local part files",!Directory.EnumerateFiles(local,"*.part").Any());
            await File.WriteAllBytesAsync(destination,original);
            var duplicate=queue.EnqueueDownload(site,server.Password,"/data/queued.bin",destination,binary.Length);await queue.WaitForIdleAsync();
            Check("Unconfirmed local overwrite is rejected without changing existing file",duplicate.Status=="실패"&&File.ReadAllBytes(destination).SequenceEqual(original));
            server.SetFile("/data/existing.bin",original);
            var remoteDuplicate=queue.EnqueueUpload(site,server.Password,upload,"/data/existing.bin");await queue.WaitForIdleAsync();
            Check("Unconfirmed remote overwrite preserves existing file",remoteDuplicate.Status=="실패"&&server.ReadFile("/data/existing.bin").SequenceEqual(original));
            var replace=queue.EnqueueUpload(site,server.Password,upload,"/data/existing.bin",true);await queue.WaitForIdleAsync();
            Check("Explicit remote overwrite promotes completed upload",replace.Status=="완료"&&server.ReadFile("/data/existing.bin").SequenceEqual(binary));
            var replaceLocal=queue.EnqueueDownload(site,server.Password,"/data/queued.bin",destination,binary.Length,true);await queue.WaitForIdleAsync();
            Check("Explicit local overwrite replaces only after successful transfer",replaceLocal.Status=="완료"&&File.ReadAllBytes(destination).SequenceEqual(binary));
            server.SetFile("/data/existing.bin",original);server.FailUpload=true;
            var failedUp=queue.EnqueueUpload(site,server.Password,upload,"/data/existing.bin",true);await queue.WaitForIdleAsync();server.FailUpload=false;
            Check("426 upload failure leaves original remote file intact",failedUp.Status=="실패"&&server.ReadFile("/data/existing.bin").SequenceEqual(original));
            Check("Failed upload cleans generated remote part file",!server.FilePaths.Any(p=>p.Contains(".equiplink-",StringComparison.Ordinal)));
            await File.WriteAllBytesAsync(destination,original);server.FailDownloadPath="/data/queued.bin";
            var failedDown=queue.EnqueueDownload(site,server.Password,"/data/queued.bin",destination,binary.Length,true);await queue.WaitForIdleAsync();server.FailDownloadPath=null;
            Check("426 download failure leaves original local file intact",failedDown.Status=="실패"&&File.ReadAllBytes(destination).SequenceEqual(original));
            Check("Failed download cleans local part file",!Directory.EnumerateFiles(local,"*.part").Any());
            server.RejectRename=true;
            var failedRename=queue.EnqueueUpload(site,server.Password,upload,"/data/existing.bin",true);await queue.WaitForIdleAsync();server.RejectRename=false;
            Check("Server rename rejection never deletes original target",failedRename.Status=="실패"&&server.ReadFile("/data/existing.bin").SequenceEqual(original));
            Check("Rejected rename removes temporary upload",!server.FilePaths.Any(p=>p.Contains(".equiplink-",StringComparison.Ordinal)));
            server.SetFile("/data/slow.bin",new byte[1024*1024]);server.TransferChunkSize=1024;server.TransferDelayMs=5;
            int retrBefore=server.CommandCount("RETR");
            var canceled=queue.EnqueueDownload(site,server.Password,"/data/slow.bin",destination,1024*1024,true);
            await server.WaitForCommandAsync("RETR",retrBefore+1);queue.Cancel(canceled);await queue.WaitForIdleAsync();server.TransferDelayMs=0;server.TransferChunkSize=32768;
            Check("Cancellation marks transfer canceled",canceled.Status=="취소");
            Check("Canceled download preserves original local file",File.ReadAllBytes(destination).SequenceEqual(original));
            Check("Canceled download removes local part file",!Directory.EnumerateFiles(local,"*.part").Any());
            Check("Queue is idle after cancellation",!queue.HasPending);
            var afterCancel=queue.EnqueueDownload(site,server.Password,"/data/empty.txt",Path.Combine(local,"empty.txt"),0);await queue.WaitForIdleAsync();
            Check("New transfer runs after previous connection cancellation",afterCancel.Status=="완료"&&new FileInfo(Path.Combine(local,"empty.txt")).Length==0);
            server.TransferChunkSize=1024;server.TransferDelayMs=5;retrBefore=server.CommandCount("RETR");
            var cancelActive=queue.EnqueueDownload(site,server.Password,"/data/slow.bin",Path.Combine(local,"cancel-active.bin"),1024*1024);
            var cancelPending=queue.EnqueueDownload(site,server.Password,"/data/empty.txt",Path.Combine(local,"cancel-pending.txt"),0);
            await server.WaitForCommandAsync("RETR",retrBefore+1);queue.CancelAll();await queue.WaitForIdleAsync();server.TransferDelayMs=0;server.TransferChunkSize=32768;
            Check("Cancel All cancels active and queued jobs",cancelActive.Status=="취소"&&cancelPending.Status=="취소"&&!queue.HasPending);
            Check("Canceled queued job never opens a data transfer",server.CommandCount("RETR")==retrBefore+1&&!File.Exists(Path.Combine(local,"cancel-pending.txt")));
            string largeUpload=Path.Combine(local,"large-upload.bin");await File.WriteAllBytesAsync(largeUpload,new byte[8*1024*1024]);
            server.TransferChunkSize=1024;server.TransferDelayMs=10;int storBefore=server.CommandCount("STOR");
            var canceledUpload=queue.EnqueueUpload(site,server.Password,largeUpload,"/data/existing.bin",true);
            await server.WaitForCommandAsync("STOR",storBefore+1);queue.Cancel(canceledUpload);await queue.WaitForIdleAsync();server.TransferDelayMs=0;server.TransferChunkSize=32768;
            Check("Canceled upload never replaces original remote file",canceledUpload.Status=="취소"&&server.ReadFile("/data/existing.bin").SequenceEqual(original));
            string folder=Path.Combine(local,"tree-upload");Directory.CreateDirectory(Path.Combine(folder,"하위 폴더"));Directory.CreateDirectory(Path.Combine(folder,"empty"));
            await File.WriteAllBytesAsync(Path.Combine(folder,"first.bin"),binary);await File.WriteAllBytesAsync(Path.Combine(folder,"하위 폴더","nested.txt"),original);
            var folderUpload=queue.EnqueueUploadDirectory(site,server.Password,folder,"/data/new tree");await queue.WaitForIdleAsync();
            Check("Recursive upload preserves Unicode directory structure",folderUpload.Status=="완료"&&server.ReadFile("/data/new tree/하위 폴더/nested.txt").SequenceEqual(original));
            Check("Recursive upload preserves empty folders",server.DirectoryExists("/data/new tree/empty"));
            Check("Recursive upload accounts total bytes",folderUpload.Bytes==binary.Length+original.Length&&folderUpload.Total==folderUpload.Bytes);
            string folderDestination=Path.Combine(local,"tree-download");
            var folderDownload=queue.EnqueueDownloadDirectory(site,server.Password,"/data/new tree",folderDestination);await queue.WaitForIdleAsync();
            Check("Recursive download preserves file bytes and nested names",folderDownload.Status=="완료"&&File.ReadAllBytes(Path.Combine(folderDestination,"하위 폴더","nested.txt")).SequenceEqual(original));
            Check("Recursive download preserves empty folders",Directory.Exists(Path.Combine(folderDestination,"empty")));
            server.SetFile("/data/partial/a-good.bin",binary);server.SetFile("/data/partial/z-failed.bin",binary);
            string partialDestination=Path.Combine(local,"partial");Directory.CreateDirectory(partialDestination);
            await File.WriteAllBytesAsync(Path.Combine(partialDestination,"z-failed.bin"),original);server.FailDownloadPath="/data/partial/z-failed.bin";
            var partial=queue.EnqueueDownloadDirectory(site,server.Password,"/data/partial",partialDestination,true);await queue.WaitForIdleAsync();server.FailDownloadPath=null;
            Check("Directory failure preserves already completed files",partial.Status=="실패"&&File.ReadAllBytes(Path.Combine(partialDestination,"a-good.bin")).SequenceEqual(binary));
            Check("Directory failure leaves failing target original intact",File.ReadAllBytes(Path.Combine(partialDestination,"z-failed.bin")).SequenceEqual(original));
            Check("Directory failure removes in-progress local part",!Directory.EnumerateFiles(partialDestination,"*.part").Any());
            server.SetFile("/data/collision/File.txt",binary);server.SetFile("/data/collision/file.txt",original);
            string collisionDestination=Path.Combine(local,"collision");
            var collision=queue.EnqueueDownloadDirectory(site,server.Password,"/data/collision",collisionDestination,true);await queue.WaitForIdleAsync();
            Check("Case-colliding remote names fail before writing to Windows",collision.Status=="실패"&&!Directory.Exists(collisionDestination));
            server.SetFile("/data/unsafe/CON.txt",binary);
            string unsafeDestination=Path.Combine(local,"unsafe");
            var unsafeDownload=queue.EnqueueDownloadDirectory(site,server.Password,"/data/unsafe",unsafeDestination,true);await queue.WaitForIdleAsync();
            Check("Reserved Windows remote name fails before creating local tree",unsafeDownload.Status=="실패"&&!Directory.Exists(unsafeDestination));
            var queuedStore=AppStore.Load();
            Check("Transfer history persists terminal states",queuedStore.TransferHistory.Any(r=>r.Status=="완료")&&queuedStore.TransferHistory.Any(r=>r.Status=="실패")&&queuedStore.TransferHistory.Any(r=>r.Status=="취소"));
            Check("Transfer history records paths and byte counts",queuedStore.TransferHistory.Any(r=>r.RemotePath=="/data/queued.bin"&&r.Bytes==binary.Length));
            string historyJson=JsonSerializer.Serialize(queuedStore.TransferHistory);
            Check("Transfer history excludes credentials and encrypted secrets",!historyJson.Contains(server.Password,StringComparison.Ordinal)&&!historyJson.Contains(site.EncryptedPassword!,StringComparison.Ordinal)&&!historyJson.Contains("Username",StringComparison.OrdinalIgnoreCase));
            Check("Queue snapshots leave profile password unchanged",site.HasSavedPassword&&store.FtpSites.Single().HasSavedPassword);
            using var restored=new FtpTransferQueue(queuedStore);
            Check("Previous transfer history appears after restarting queue",restored.Items.Count==queuedStore.TransferHistory.Count&&!restored.HasPending);
        }
        await using(var linkServer=new FtpTestServer())
        {
            linkServer.InjectedListing="type=file;size=3; real.txt\r\ntype=OS.unix=slink; outside-link\r\n";linkServer.SetFile("/real.txt",[1,2,3]);
            var linkStore=new AppStore();using var queue=new FtpTransferQueue(linkStore);
            string linkDestination=Path.Combine(local,"link-tree");
            var item=queue.EnqueueDownloadDirectory(linkServer.Site(),linkServer.Password,"/",linkDestination);
            await queue.WaitForIdleAsync();
            Check("Recursive download skips symbolic links and reports count",item.Status=="완료"&&item.Error.Contains("1개",StringComparison.Ordinal)&&!File.Exists(Path.Combine(linkDestination,"outside-link")));
            Check("Skipping link keeps ordinary sibling file",File.ReadAllBytes(Path.Combine(linkDestination,"real.txt")).SequenceEqual(new byte[]{1,2,3}));
        }
        var capped=new AppStore{TransferHistory=Enumerable.Range(0,501).Select(_=>new FtpTransferRecord{Status="완료"}).ToList()};
        Check("Loaded transfer history is capped at 500 rows",AppStore.Read(JsonSerializer.Serialize(capped)).TransferHistory.Count==500);
        var partialListing=FtpClient.ParseListing("type=file;size=1; ../outside\r\ntype=file;size=1; good.txt\r\n","/",true);
        Check("Listing parser excludes unsafe traversal without hiding normal files",partialListing.Count==1&&partialListing[0].Name=="good.txt"&&partialListing is FtpListing{OmittedCount:1});
        Check("Listing parser accepts safe leaf entry",FtpClient.ParseListing("type=file;size=1; good.txt\r\n","/",true).Single().Name=="good.txt");
        var linked=FtpClient.ParseListing("type=OS.unix=slink; link\r\n","/",true);
        Check("MLSD symbolic links are marked for skip",linked.Count==1&&linked[0].IsLink);
        var legacyLink=FtpClient.ParseListing("lrwxrwxrwx 1 owner group 9 Oct 21 12:00 shortcut -> /outside\r\n","/",false);
        Check("Legacy symbolic links are marked for skip",legacyLink.Count==1&&legacyLink[0].IsLink&&legacyLink[0].Name=="shortcut");
        results.Add("All FTP tests used owned loopback fixture servers. No real equipment/server connections were made.");
        results.Add("Not tested: company firewall/passive port configuration, actual FTP server variants, interactive Windows file dialogs, cross-process same-name race during FTP rename.");
        return string.Join(Environment.NewLine,results);
    }
}
