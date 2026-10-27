using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace EquipLink;

// PuTTY's supported password-file input and output log are carried through
// private, current-user pipes. No password file or shell integration is needed.
internal sealed class PuttyLoginPipe : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly NamedPipeServerStream? spare;
    private readonly CancellationTokenSource lifetime=new();
    private readonly TaskCompletionSource<int> clientProcess=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string name="EquipLink_Login_"+Guid.NewGuid().ToString("N");
    public string Path => @"\\.\pipe\"+name;
    public PuttyLoginPipe(bool password)
    {
        pipe=new(name,password?PipeDirection.Out:PipeDirection.In,password?1:2,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        // PuTTY probes file attributes before opening the log. Windows can
        // connect that zero-byte probe to a named pipe; keep a second listener
        // ready so its real fopen cannot race a disconnect/re-arm.
        if(!password)spare=new(name,PipeDirection.In,2,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
    }
    public void SetProcess(int id)=>clientProcess.TrySetResult(id);
    private async Task ConnectAsync(NamedPipeServerStream channel)
    {
        await channel.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
        int expected=await clientProcess.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
        if(!GetNamedPipeClientProcessId(channel.SafePipeHandle,out uint actual) || actual!=expected)
            throw new IOException("로그인 통신 대상이 일치하지 않습니다.");
    }
    public async Task SupplyPasswordAsync(Equipment equipment,Action failed)
    {
        byte[]? bytes=null;
        try
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            await ConnectAsync(pipe).ConfigureAwait(false);
            string secret=CredentialProtector.Unprotect(equipment,equipment.EncryptedPassword!);
            try { bytes=Encoding.UTF8.GetBytes(secret+"\n"); }
            finally { secret=string.Empty; }
            await pipe.WriteAsync(bytes,lifetime.Token).ConfigureAwait(false);
            await pipe.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException or CryptographicException)
        { if(!lifetime.IsCancellationRequested)failed(); }
        finally { if(bytes!=null)CryptographicOperations.ZeroMemory(bytes);pipe.Dispose();lifetime.Dispose(); }
    }
    public async Task ReadOutputAsync(Action<string> received,string? logFile,Action failed)
    {
        FileStream? log=null;
        using var logGate=new SemaphoreSlim(1);
        try
        {
            async Task Pump(NamedPipeServerStream channel)
            {
                byte[] buffer=new byte[8192];
                try
                {
                    while(!lifetime.IsCancellationRequested)
                    {
                        await ConnectAsync(channel).ConfigureAwait(false);
                        while(true)
                        {
                            int count=await channel.ReadAsync(buffer,lifetime.Token).ConfigureAwait(false);
                            if(count==0)break;
                            received(Encoding.Latin1.GetString(buffer,0,count));
                            if(logFile!=null)
                            {
                                await logGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                                try{log??=new FileStream(logFile,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite,8192,true);await log.WriteAsync(buffer.AsMemory(0,count),lifetime.Token).ConfigureAwait(false);await log.FlushAsync(lifetime.Token).ConfigureAwait(false);}
                                finally{logGate.Release();}
                            }
                        }
                        channel.Disconnect();
                    }
                }
                catch(Exception e) when(e is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {if(!lifetime.IsCancellationRequested)failed();lifetime.Cancel();}
                finally{CryptographicOperations.ZeroMemory(buffer);}
            }
            await Task.WhenAll(Pump(pipe),Pump(spare!)).ConfigureAwait(false);
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        { if(!lifetime.IsCancellationRequested)failed(); }
        finally { log?.Dispose();pipe.Dispose();spare?.Dispose();lifetime.Dispose(); }
    }
    public void Dispose(){try{lifetime.Cancel();}catch(ObjectDisposedException){}pipe.Dispose();spare?.Dispose();}
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
}

internal enum LoginInput { None, Username, Password }

// A one-attempt initial login recognizer, not a shell automation engine.
// Anchored prompts prevent banner text, sudo, password-change and OTP prompts
// from receiving the stored password. A shell prompt permanently disarms it.
internal sealed class TelnetLoginPrompts
{
    private static readonly Regex UsernamePrompt=new(@"^(?:[a-z0-9_.-]+\s+)?(?:login(?:\s+as)?|user\s*name|user)\s*:\s*$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex PasswordPrompt=new(@"^password\s*:\s*$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex ShellPrompt=new(@"[\$#>%]\s*$",RegexOptions.CultureInvariant);
    private readonly StringBuilder line=new();
    private bool usernameSent;
    public bool Active { get; private set; }=true;
    public void Stop(){Active=false;line.Clear();}
    public void Feed(string text)
    {
        if(!Active)return;
        foreach(char c in text)
        {
            if(c is '\r' or '\n')
            {
                // A completed line containing a prompt is no longer waiting for
                // input. It can never be acted upon after later shell output.
                if(ShellPrompt.IsMatch(line.ToString().Trim())){Stop();return;}
                line.Clear();
            }
            else if(c=='\b'){if(line.Length>0)line.Length--;}
            else if(!char.IsControl(c)){line.Append(c);if(line.Length>512){Stop();return;}}
        }
    }
    public LoginInput Pending()
    {
        if(!Active)return LoginInput.None;
        string value=line.ToString().Trim();
        if(ShellPrompt.IsMatch(value)){Stop();return LoginInput.None;}
        if(PasswordPrompt.IsMatch(value))return LoginInput.Password;
        if(UsernamePrompt.IsMatch(value))
        {
            if(usernameSent){Stop();return LoginInput.None;}
            return LoginInput.Username;
        }
        return LoginInput.None;
    }
    public void Sent(LoginInput input)
    {
        line.Clear();
        if(input==LoginInput.Username)usernameSent=true;
        else if(input==LoginInput.Password)Stop();
    }
}
