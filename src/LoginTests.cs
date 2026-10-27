using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace EquipLink;

internal static class LoginTests
{
    internal const string ReloadSecret="  Test-Only!암호-42 £  ";
    public static async Task RunAsync()
    {
        var report=new StringBuilder();
        void Check(string label,bool ok){if(!ok)throw new InvalidOperationException("FAIL: "+label);report.AppendLine("PASS: "+label);File.WriteAllText(Path.Combine(AppPaths.Root,"login-test-report.txt"),report.ToString());}
        foreach(string prompt in new[]{"login: ","equip01 login: ","Login as: ","Username: ","User name: ","User: "})
        {var p=new TelnetLoginPrompts();p.Feed(prompt);Check("Recognizes initial "+prompt.Trim()+" prompt",p.Pending()==LoginInput.Username);}
        foreach(string text in new[]{"Please enter password:","[sudo] password for fixture:","New password:","Current password:","Verification code:","Password: example", "Password:\r\nWelcome\r\n"})
        {var p=new TelnetLoginPrompts();p.Feed(text);Check("Does not answer banner, extra authentication or completed prompt: "+text.Replace('\r',' ').Replace('\n',' '),p.Pending()==LoginInput.None);}
        var parsed=new TelnetLoginPrompts();parsed.Feed("Pass");Check("Fragmented password prompt waits for completion",parsed.Pending()==LoginInput.None);parsed.Feed("word: ");Check("Complete password prompt is recognized",parsed.Pending()==LoginInput.Password);parsed.Sent(LoginInput.Password);parsed.Feed("\r\nPassword: ");Check("A rejected password is never retried automatically",!parsed.Active&&parsed.Pending()==LoginInput.None);
        parsed=new();parsed.Feed("fixture@host:~$ \r\nPassword: ");Check("Shell activity permanently disarms initial login",!parsed.Active&&parsed.Pending()==LoginInput.None);
        parsed=new();parsed.Feed("login: ");parsed.Sent(LoginInput.Username);parsed.Feed("login: ");Check("Repeated username prompt does not cause a retry loop",parsed.Pending()==LoginInput.None&&!parsed.Active);

        const string secret=ReloadSecret;
        using var fixture=new TelnetFixture();
        var target=new Equipment{Name="AUTOMATIC LOGIN LOCAL FIXTURE",Host="127.0.0.1",Port=fixture.Port,Username="fixture-user",UseX11=false,LogSession=true};
        target.EncryptedPassword=CredentialProtector.Protect(target,secret);
        var saved=new AppStore{Settings=new(){AutoStartX=false},Equipment=[target]};saved.Save();saved.Record(target);
        var store=AppStore.Load();target=store.Equipment.Single();
        Check("Saved credentials and automatic-login preference survive save, history and reload",target.CanAutoLogin&&CredentialProtector.Unprotect(target,target.EncryptedPassword!)==secret);
        var restart=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden};restart.ArgumentList.Add("--login-reload-test");restart.ArgumentList.Add(AppPaths.Root);
        using(var child=System.Diagnostics.Process.Start(restart)!)
        {
            try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Check("A separate new app process restores the saved password and automatic-login preference",child.ExitCode==0);}
            finally{if(!child.HasExited)child.Kill();}
        }
        var window=new MainWindow(store){Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false};SessionPane? pane=null;
        try
        {
            window.Show();var accepted=fixture.AcceptAsync();pane=await window.ConnectForTestAsync(target);await accepted;
            await fixture.WriteAsync("Fixture login service\r\n");await Task.Delay(400);
            Check("No password is sent merely because the terminal opened",!fixture.HasInput);
            await fixture.WriteAsync("\u001b[32mequip01 log");await Task.Delay(400);Check("Partial login prompt sends no credential",!fixture.HasInput);
            await fixture.WriteAsync("in: \u001b[0m");
            Check("Opening a saved equipment session automatically enters ID and Enter",await fixture.LineAsync()==target.Username);
            await fixture.WriteAsync("\r\nPass");await Task.Delay(400);Check("Password is not sent before the complete prompt",!fixture.HasInput);
            await fixture.WriteAsync("word: ");Check("Actual PuTTY Telnet automatically sends saved Unicode password with spaces and Enter",await fixture.LineAsync()==secret);
            await fixture.WriteAsync("\r\nLogin incorrect\r\nPassword: ");await Task.Delay(800);
            Check("Actual PuTTY does not repeat password after authentication failure",!fixture.HasInput);
            await fixture.WriteAsync("\r\nfixture@host:~$ \r\nPassword: ");await Task.Delay(400);
            Check("Later shell password prompts cannot reuse the initial password",!fixture.HasInput);
            string log=pane.Host.LogFile!;window.CloseForTest(pane);pane=null;await Task.Delay(200);
            Check("Optional session output log remains available and excludes credentials",File.Exists(log)&&File.ReadAllText(log).Contains("Login incorrect")&&!File.ReadAllText(log).Contains(secret));
            Check("Settings retain only protected credentials after the real connection",!File.ReadAllText(AppPaths.Config).Contains(secret)&&AppStore.Load().Equipment.Single().HasSavedPassword);

            using var disabled=new TelnetFixture();target=NewTarget(disabled,secret);target.AutoLogin=false;store.Equipment.Add(target);
            accepted=disabled.AcceptAsync();pane=await window.ConnectForTestAsync(target);await accepted;await disabled.WriteAsync("login: ");await Task.Delay(650);
            Check("Disabling automatic login preserves password without sending input",target.HasSavedPassword&&!disabled.HasInput);window.CloseForTest(pane);pane=null;

            using var cancelled=new TelnetFixture();target=NewTarget(cancelled,secret);store.Equipment.Add(target);
            accepted=cancelled.AcceptAsync();pane=await window.ConnectForTestAsync(target);await accepted;pane.Host.CancelAutoLogin();await cancelled.WriteAsync("Password: ");await Task.Delay(650);
            Check("Cancelling login prevents delayed password input",!cancelled.HasInput);window.CloseForTest(pane);pane=null;

            using var invalid=new TelnetFixture();target=NewTarget(invalid,secret);target.EncryptedPassword="not-valid-protected-data";store.Equipment.Add(target);
            accepted=invalid.AcceptAsync();pane=await window.ConnectForTestAsync(target);await accepted;await invalid.WriteAsync("Password: ");await Task.Delay(650);
            Check("Unreadable stored passwords leave the terminal usable and send no credential",pane.Host.HasProcess&&!invalid.HasInput);window.CloseForTest(pane);pane=null;

            using var direct=new TelnetFixture();target=NewTarget(direct,secret);store.Equipment.Add(target);
            accepted=direct.AcceptAsync();pane=await window.ConnectForTestAsync(target);await accepted;await direct.WriteAsync("Password: ");
            Check("Servers that already know the user can start at the password prompt",await direct.LineAsync()==secret);
            window.CloseForTest(pane);pane=null;
            Check("Automatic login with output logging disabled creates no session log",!Directory.EnumerateFiles(AppPaths.Logs).Any(p=>Path.GetFileName(p).StartsWith("DIRECT",StringComparison.Ordinal)));
            report.AppendLine("Real embedded PuTTY with loopback Telnet only; no company systems or real credentials accessed.");
            File.WriteAllText(Path.Combine(AppPaths.Root,"login-test-report.txt"),report.ToString());
        }
        finally{if(pane!=null)window.CloseForTest(pane);window.Close();}
    }
    private static Equipment NewTarget(TelnetFixture fixture,string secret)
    {
        var target=new Equipment{Name="DIRECT LOCAL FIXTURE",Host="127.0.0.1",Port=fixture.Port,Username="fixture-user",UseX11=false};
        target.EncryptedPassword=CredentialProtector.Protect(target,secret);return target;
    }
    private sealed class TelnetFixture:IDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private TcpClient? client;private NetworkStream? stream;
        private int state;private readonly List<byte> line=[];private bool afterCr;
        public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        public TelnetFixture(){listener.Start();}
        public bool HasInput
        {
            get
            {
                bool application=line.Count>0;
                while(client?.Available>0){string? result=Consume((byte)stream!.ReadByte());application|=line.Count>0||!string.IsNullOrEmpty(result);}
                return application;
            }
        }
        public async Task AcceptAsync()
        {
            client=await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));stream=client.GetStream();
            await stream.WriteAsync(new byte[]{255,251,1,255,251,3});
            // Drain only the initial Telnet negotiation, before any prompts.
            await Task.Delay(150);while(client.Available>0){byte[] bytes=new byte[client.Available];int n=await stream.ReadAsync(bytes);for(int i=0;i<n;i++)Consume(bytes[i]);}
        }
        public async Task WriteAsync(string value)=>await stream!.WriteAsync(Encoding.UTF8.GetBytes(value));
        public async Task<string> LineAsync()
        {
            using var timeout=new CancellationTokenSource(10000);byte[] b=new byte[1];
            while(true){int n=await stream!.ReadAsync(b,timeout.Token);if(n==0)throw new IOException("Fixture disconnected.");var value=Consume(b[0]);if(value!=null)return value;}
        }
        private string? Consume(byte b)
        {
            if(state==1){state=b==250?3:b is 251 or 252 or 253 or 254?2:0;return null;}
            if(state==2){state=0;return null;}
            if(state==3){if(b==255)state=4;return null;}
            if(state==4){state=b==240?0:3;return null;}
            if(b==255){state=1;return null;}
            if(afterCr){afterCr=false;if(b is 0 or 10)return null;}
            if(b==13){afterCr=true;string result=Encoding.UTF8.GetString(line.ToArray());line.Clear();return result;}
            if(b==10){string result=Encoding.UTF8.GetString(line.ToArray());line.Clear();return result;}
            line.Add(b);return null;
        }
        public void Dispose(){stream?.Dispose();client?.Dispose();listener.Stop();}
    }
}
