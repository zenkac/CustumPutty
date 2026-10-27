using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EquipLink;

internal static class FtpCompatibilityTests
{
    internal static async Task<string> RunAsync()
    {
        var report=new List<string>();
        void Check(string name,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+name);report.Add("PASS: "+name);}
        async Task Reject(string name,Func<Task> action){try{await action();}catch(Exception e) when(e is ArgumentException or InvalidDataException or FtpException){Check(name,true);return;}Check(name,false);}
        var entries=FtpClient.ParseListing("type=cdir; .\r\ntype=pdir; ..\r\ntype=file;size=1; ./normal.txt\r\ntype=file;size=2; /work/absolute.txt\r\ntype=file;size=3; name\\with\\backslash.txt\r\ntype=file;size=1; ../outside\r\ntype=file;size=1; /other/outside\r\nunrecognized server row\r\n","/work",true);
        Check("Legacy path prefixes and Linux backslashes no longer abort the folder",entries.Count==3&&entries.All(x=>x.FullPath.StartsWith("/work/"))&&entries.Any(x=>x.Name=="name\\with\\backslash.txt"));
        Check("Malformed and traversal rows are omitted with a visible count",entries is FtpListing{OmittedCount:3});
        var duplicates=FtpClient.ParseListing("type=file; same\r\ntype=dir; same\r\ntype=file; valid\r\n","/",true);
        Check("Ambiguous duplicates cannot be used as a transfer target",duplicates.Count==1&&duplicates[0].Name=="valid"&&duplicates is FtpListing{OmittedCount:2});
        var unix=FtpClient.ParseListing("total 2\r\n-rw-r--r-- 1 user group 2 Jan 21 10:00 /home/test/a file.txt\r\ndrwxr-xr-x 2 user group 0 Jan 21 10:00 .\r\n-rw-r--r-- 1 user group 3 Jan 21 10:00 a\\b\r\n","/home/test",false);
        Check("Legacy UNIX absolute child names spaces and backslashes are readable",unix.Count==2&&unix.Any(x=>x.Name=="a file.txt")&&unix.Any(x=>x.Name=="a\\b"));
        Check("New FTP profiles default to automatic charset detection",new FtpSite().Encoding=="Auto");
        foreach(var setup in new[]{("UTF-8",true,true,"데이터","한글.txt"),("CP949",false,false,"데이터","결과.txt"),("UTF-8",false,false,"데이터","한글.txt"),("CP949",true,true,"데이터","결과.txt"),("windows-1252",false,false,"data","café.txt"),("shift_jis",false,false,"data","結果.txt")})
        {
            var (charset,features,utf8,folder,filename)=setup;
            await using var server=new FtpTestServer{RequirePassword=true,WireEncoding=charset,SupportFeatures=features,SupportUtf8=utf8,SupportMlsd=charset!="CP949"};
            if(charset=="CP949")server.Greeting="한글 FTP 서버";
            string path="/"+folder;byte[] content=[0,1,2,127,128,255];server.SetFile(path+"/"+filename,content);
            var site=server.Site(path);if(charset is "windows-1252" or "shift_jis")site.Encoding=charset;
            await using var client=await FtpClient.ConnectAsync(site,server.Password);
            var listed=await client.ListAsync(path);
            Check($"{charset} features={features}: filename and default folder decode correctly",client.WorkingDirectory==path&&listed.Single().Name==filename&&client.EncodingName==FtpEncoding.Label(FtpEncoding.Get(charset)));
            using var received=new MemoryStream();await client.DownloadAsync(listed[0].FullPath,received);
            Check($"{charset} features={features}: RETR uses the same charset as the listed filename",received.ToArray().SequenceEqual(content));
            string added=charset=="shift_jis"?"新規.txt":charset=="windows-1252"?"résumé.txt":"새파일.txt";
            using var source=new MemoryStream(content);await client.UploadAsync(source,path+"/"+added);
            Check($"{charset} features={features}: STOR preserves the requested Unicode filename",server.FileExists(path+"/"+added)&&server.ReadFile(path+"/"+added).SequenceEqual(content));
            Check($"{charset} features={features}: connection remains plain FTP",server.CommandCount("AUTH")==0&&server.CommandCount("PBSZ")==0&&server.CommandCount("PROT")==0);
            if(!utf8&&site.Encoding=="Auto")Check("Automatic legacy mode does not force UTF8 ON",server.CommandCount("OPTS UTF8 ON")==0);
        }
        await using(var server=new FtpTestServer{WireEncoding="CP949",SupportUtf8=false,SupportFeatures=false})
        {
            server.SetFile("/한글.txt",[3]);var forced=server.Site();forced.Encoding="UTF-8";
            await Reject("Forced UTF-8 never silently changes a CP949 filename",async()=>{await using var client=await FtpClient.ConnectAsync(forced,server.Password);await client.ListAsync("/");});
        }
        foreach(string name in new[]{"UTF-16","UTF-32","not-a-charset"})
            await Reject("Invalid or non-ASCII-compatible custom charset is rejected: "+name,()=>{FtpEncoding.Get(name);return Task.CompletedTask;});
        var store=new AppStore();var old=new FtpSite{Name="legacy",Host="127.0.0.1",Username="id",Encoding="UTF-8"};old.EncryptedPassword=CredentialProtector.Protect(old.CredentialTarget(),"fixture-secret");
        var manual=old.Copy();manual.Id=Guid.NewGuid().ToString("N");manual.Encoding="CP949";store.FtpSites=[old,manual];store.LastFtpConnection=old.Copy();
        var json=JsonNode.Parse(JsonSerializer.Serialize(store,AppStore.Json))!.AsObject();json.Remove("FtpSettingsVersion");var migrated=AppStore.Read(json.ToJsonString());
        Check("v1.4 default UTF8 sites and recent connection migrate to automatic",migrated.FtpSites[0].Encoding=="Auto"&&migrated.LastFtpConnection?.Encoding=="Auto");
        Check("Legacy explicit CP949 remains unchanged",migrated.FtpSites[1].Encoding=="CP949");
        Check("Charset migration preserves saved credentials",CredentialProtector.Unprotect(migrated.FtpSites[0].CredentialTarget(),migrated.FtpSites[0].EncryptedPassword!)=="fixture-secret");
        Check("New explicitly forced UTF8 survives reload",AppStore.Read(JsonSerializer.Serialize(store,AppStore.Json)).FtpSites[0].Encoding=="UTF-8");
        await using(var server=new FtpTestServer{InjectedListing="type=file;size=3; good.txt\r\ntype=file;size=1; ../escape\r\n"})
        {
            server.SetFile("/good.txt",[1,2,3]);var site=server.Site();var partialStore=new AppStore();using var session=new FtpSession(partialStore);
            await session.ConnectAsync(site,server.Password);
            Check("Partial listing keeps authenticated session and normal files visible",session.IsConnected&&session.Entries.Count==1&&session.ListingNotice.Contains("1개"));
            string local=Path.Combine(AppPaths.Root,"compat-files");Directory.CreateDirectory(local);
            using var queue=new FtpTransferQueue(partialStore);
            var download=queue.EnqueueDownload(site,server.Password,"/good.txt",Path.Combine(local,"good.txt"),3);await queue.WaitForIdleAsync();
            Check("Normal file remains downloadable beside an unsupported listing entry",download.Status=="완료"&&File.ReadAllBytes(download.LocalPath).SequenceEqual(new byte[]{1,2,3}));
            var upload=queue.EnqueueUpload(site,server.Password,download.LocalPath,"/good.txt",true);await queue.WaitForIdleAsync();
            Check("Incomplete listing cannot conceal an upload collision",upload.Status=="실패"&&server.CommandCount("STOR")==0&&server.ReadFile("/good.txt").SequenceEqual(new byte[]{1,2,3}));
            server.InjectedListing="unknown row\r\n";await session.RefreshAsync();
            Check("Unrecognized listing is reported as incomplete rather than an empty folder",session.IsConnected&&session.Entries.Count==0&&session.ListingNotice.Length>0);
        }
        report.Add("Compatibility fixtures cover actual UTF-8, CP949, Windows-1252 and Shift-JIS bytes. Actual company server listings were not available.");
        return string.Join(Environment.NewLine,report);
    }
}
