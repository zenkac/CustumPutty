using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace EquipLink;

// All credentials and endpoints below are synthetic fixtures. Reports contain
// only assertions, never credential values or protected blobs.
public static class CredentialTests
{
    public static void Run(Action<string,bool> check)
    {
        const string secret="  EquipLink-OnlyFixture!암호 £7  ";
        var target=new Equipment{Name="Credential fixture",Host="fixture.example.invalid",Username="test-user"};
        target.Validate();
        string first=CredentialProtector.Protect(target,secret);
        string second=CredentialProtector.Protect(target,secret);
        target.EncryptedPassword=first;
        check("Saved password preserves Unicode and leading/trailing spaces",CredentialProtector.Unprotect(target,first)==secret);
        check("Repeated saves use different protected blobs",first!=second);
        check("Saved-password indicator recognizes encrypted storage",target.HasSavedPassword);

        var corrupt=Convert.FromBase64String(first);corrupt[corrupt.Length/2]^=0x5A;
        check("Tampered protected password fails closed",RejectsCryptographic(()=>CredentialProtector.Unprotect(target,Convert.ToBase64String(corrupt))));
        check("Invalid protected password fails closed",RejectsCryptographic(()=>CredentialProtector.Unprotect(target,"not valid base64")));
        var changed=target.Copy();changed.Host="other.example.invalid";
        check("Password cannot be decrypted for another host",!CredentialProtector.MatchesTarget(target,changed) && RejectsCryptographic(()=>CredentialProtector.Unprotect(changed,first)));
        changed=target.Copy();changed.Port=2323;
        check("Password cannot be decrypted for another port",!CredentialProtector.MatchesTarget(target,changed) && RejectsCryptographic(()=>CredentialProtector.Unprotect(changed,first)));
        changed=target.Copy();changed.Protocol="SSH";
        check("Password cannot be decrypted for another protocol",!CredentialProtector.MatchesTarget(target,changed) && RejectsCryptographic(()=>CredentialProtector.Unprotect(changed,first)));
        changed=target.Copy();changed.Username="TEST-USER";
        check("Linux username case changes require a new saved password",!CredentialProtector.MatchesTarget(target,changed) && RejectsCryptographic(()=>CredentialProtector.Unprotect(changed,first)));
        changed=target.Copy();changed.Name="Renamed fixture";changed.Group="Moved group";changed.Id=Guid.NewGuid().ToString("N");changed.Host="FIXTURE.EXAMPLE.INVALID";
        check("Rename, group and hostname case changes preserve saved password",CredentialProtector.MatchesTarget(target,changed) && CredentialProtector.Unprotect(changed,first)==secret);

        bool rejectsControls=new[]{"bad\rvalue","bad\nvalue","bad\tvalue","bad\0value","bad\u001bvalue","bad\u007fvalue","bad\u2028value","bad\u2029value","bad\ud800value"}
            .All(value=>RejectsArgument(()=>CredentialProtector.ValidateSecret(value)));
        check("Credential input rejects terminal controls and invalid Unicode",rejectsControls);
        check("Credential input enforces its length limit",RejectsArgument(()=>CredentialProtector.ValidateSecret(new string('x',CredentialProtector.MaxSecretLength+1))));

        var store=new AppStore{Equipment=[target,target.Copy()]};
        store.Equipment[1].Id=Guid.NewGuid().ToString("N");store.Equipment[1].EncryptedPassword=second;
        string? previousRoot=AppPaths.OverrideRoot;
        AppPaths.OverrideRoot=Path.Combine(AppPaths.Root,"credential-fixture-"+Guid.NewGuid().ToString("N"));
        try
        {
            store.Save();store.Equipment[0].Name="Updated fixture";store.Save();
            var loaded=AppStore.Load();
            check("Local saved password survives atomic save and reload",loaded.Equipment.All(item=>item.HasSavedPassword && CredentialProtector.Unprotect(item,item.EncryptedPassword!)==secret));
            check("Local settings and backup contain no plaintext password",new[]{AppPaths.Config,AppPaths.Config+".bak"}.All(path=>File.Exists(path) && !ContainsSecret(File.ReadAllText(path),secret)));
            string exported=store.ExportJson();var exportedData=AppStore.Read(exported);
            check("Export removes protected passwords from every profile",exportedData.Equipment.Count==2 && exportedData.Equipment.All(item=>!item.HasSavedPassword) && !exported.Contains(first,StringComparison.Ordinal) && !exported.Contains(second,StringComparison.Ordinal) && !ContainsSecret(exported,secret));
            check("Export does not mutate saved credentials",store.Equipment[0].EncryptedPassword==first && store.Equipment[1].EncryptedPassword==second && AppStore.Load().Equipment.All(item=>item.HasSavedPassword));
            string supplied=JsonSerializer.Serialize(store,AppStore.Json);
            var imported=AppStore.ReadImport(supplied);
            check("Import clears even valid locally encrypted password fields",imported.Equipment.Count==2 && imported.Equipment.All(item=>!item.HasSavedPassword));
            string legacy="{\"Version\":1,\"Equipment\":[{\"Name\":\"Legacy fixture\",\"Host\":\"192.0.2.22\",\"Username\":\"legacy-user\"}]}";
            var old=AppStore.Read(legacy);
            check("Existing version-one profiles still load without passwords",old.Equipment.Count==1 && old.Equipment[0].Username=="legacy-user" && !old.Equipment[0].HasSavedPassword);
        }
        finally { AppPaths.OverrideRoot=previousRoot; }

        using var echoProfile=new ForcedEchoProfile();
        target.BasePuttySession=echoProfile.Name;
        string? session=null;
        try
        {
            session=PuttyService.Prepare(target,new Preferences(),out _);
            using var key=Registry.CurrentUser.OpenSubKey(PuttyService.SessionsKey+"\\"+session)!;
            check("Imported force-on local echo is overridden to Auto before credential entry",Convert.ToInt32(key.GetValue("LocalEcho")??-1)==2);
            check("PuTTY session registry contains no plaintext or encrypted password",key.GetValueNames().All(name=>key.GetValue(name) is not string value || (!ContainsSecret(value,secret) && !value.Contains(first,StringComparison.Ordinal))));
            var start=PuttyService.StartInfo(PuttyService.Executable(),session,false);
            check("PuTTY launch arguments contain no password material",start.ArgumentList.All(value=>!ContainsSecret(value,secret) && !value.Contains(first,StringComparison.Ordinal)));
        }
        finally { PuttyService.Cleanup(session); }
    }

    public static async Task SmokeAsync(StringBuilder report)
    {
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);report.AppendLine("PASS: "+label);}
        const string secret="  LoopbackCredential!42  ";
        const string updatedSecret="  UpdatedLoopbackCredential!73  ";
        const string sentinel="CREDENTIAL-END-SENTINEL";
        using var echoProfile=new ForcedEchoProfile();
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        SessionPane? pane=null;
        var window=new Window{Title="EquipLink credential loopback fixture",Width=800,Height=600,Left=-30000,Top=-30000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            var target=new Equipment{Name="CREDENTIAL LOCAL TEST ONLY",Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,Username="fixture-user",UseX11=false,LogSession=true,BasePuttySession=echoProfile.Name,AutoLogin=false};
            target.EncryptedPassword=CredentialProtector.Protect(target,secret);
            var store=new AppStore{Equipment=[target]};var statuses=new List<string>();
            pane=new SessionPane(window,target.Copy(),store,statuses.Add,()=>Task.CompletedTask,new XServerService());
            var host=pane.Host;window.Content=pane;
            var accept=listener.AcceptTcpClientAsync();window.Show();await pane.ConnectAsync();
            using var client=await accept.WaitAsync(TimeSpan.FromSeconds(5));using var stream=client.GetStream();
            // Tell PuTTY the server is responsible for echo. This fixture deliberately
            // never echoes credential input, as an ordinary password prompt would not.
            await stream.WriteAsync(new byte[]{255,251,1,255,251,3});
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\nCredential fixture password: "));
            await Task.Delay(300);
            bool rejected=RejectsArgument(()=>host.SendCredential("injected\rcommand"));
            pane.FillCredential(false);
            pane.FillCredential(true);
            // The open pane retains its connection snapshot. Replacing the saved
            // password should immediately affect the menu's next input action.
            target.EncryptedPassword=CredentialProtector.Protect(target,updatedSecret);
            pane.FillCredential(true);
            host.SendCredential(sentinel);
            var received=new List<byte>();var buffer=new byte[4096];using var timeout=new CancellationTokenSource(7000);
            while(!Encoding.ASCII.GetString(received.ToArray()).Contains(sentinel,StringComparison.Ordinal))
            {
                int count=await stream.ReadAsync(buffer,timeout.Token);if(count==0)break;received.AddRange(buffer[..count]);
            }
            string text=Encoding.ASCII.GetString(received.ToArray());int start=text.IndexOf(target.Username,StringComparison.Ordinal);
            Check("Session menu ID and saved password reach the intended terminal without trimming or Enter",start>=0 && text[start..]==target.Username+secret+updatedSecret+sentinel);
            Check("Already-open session uses the latest saved password",pane.Equipment.EncryptedPassword!=target.EncryptedPassword && text.Contains(updatedSecret,StringComparison.Ordinal));
            Check("Credential control-character rejection sends no partial input",rejected && !text.Contains("injected",StringComparison.Ordinal) && !text.Contains("command",StringComparison.Ordinal));
            Check("Credential UI status does not expose passwords",statuses.All(value=>!ContainsSecret(value,secret) && !ContainsSecret(value,updatedSecret)));
            string? log=host.LogFile;host.CloseSession();
            Check("Imported force-on local echo cannot expose a non-echoed credential in the session output log",log!=null && File.Exists(log) && File.ReadAllText(log).Contains("Credential fixture password:",StringComparison.Ordinal) && !ContainsSecret(File.ReadAllText(log),secret) && !ContainsSecret(File.ReadAllText(log),updatedSecret));
        }
        finally { pane?.Host.CloseSession();window.Close();pane?.Host.Dispose();listener.Stop(); }
    }

    private static bool ContainsSecret(string text,string secret) => text.Contains(secret,StringComparison.Ordinal) || text.Contains(JsonSerializer.Serialize(secret)[1..^1],StringComparison.Ordinal);
    private static bool RejectsCryptographic(Action action){try{action();return false;}catch(CryptographicException){return true;}}
    private static bool RejectsArgument(Action action){try{action();return false;}catch(ArgumentException){return true;}}

    private sealed class ForcedEchoProfile : IDisposable
    {
        public string Name { get; }="EquipLink_CredentialEchoFixture_"+Guid.NewGuid().ToString("N");
        public ForcedEchoProfile()
        {
            try
            {
                using var root=Registry.CurrentUser.CreateSubKey(PuttyService.SessionsKey);
                using var key=root.CreateSubKey(PuttyService.EncodeSessionName(Name));
                key.SetValue("LocalEcho",0,RegistryValueKind.DWord); // PuTTY Force on.
            }
            catch { Dispose();throw; }
        }
        public void Dispose()
        {
            using var root=Registry.CurrentUser.OpenSubKey(PuttyService.SessionsKey,true);
            root?.DeleteSubKey(PuttyService.EncodeSessionName(Name),false);
        }
    }
}
