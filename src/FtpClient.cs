using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace EquipLink;

/// <summary>A server rejection. Server-provided text is deliberately never exposed: some servers echo credentials.</summary>
public sealed class FtpException : IOException
{
    public int ReplyCode { get; }
    internal FtpException(string action, int code) : base($"{action}: {Describe(code)} (FTP {code})") => ReplyCode = code;
    static string Describe(int code) => code switch
    {
        421 => "서버가 연결을 종료했습니다. 다시 연결해 주세요.",
        425 => "데이터 연결을 열 수 없습니다. 서버의 패시브 FTP 포트 설정을 확인해 주세요.",
        426 => "파일 전송이 중단되었습니다.",
        450 or 451 or 452 => "서버가 요청을 처리할 수 없습니다. 잠시 후 다시 시도해 주세요.",
        530 => "로그인 정보 또는 접속 권한을 확인해 주세요.",
        550 => "파일·폴더가 없거나 접근 권한이 없습니다.",
        552 => "서버 저장 공간이 부족하거나 파일 크기 제한을 초과했습니다.",
        553 => "서버에서 허용하지 않는 파일 이름 또는 경로입니다.",
        500 or 501 or 502 or 504 => "서버가 이 명령 또는 옵션을 지원하지 않습니다.",
        _ => "서버가 요청을 거절했습니다."
    };
}

/// <summary>One serialized, passive-mode FTP connection. Never reconnects or retries a file transfer implicitly.</summary>
public sealed class FtpClient : IAsyncDisposable
{
    const int MaxReplyBytes = 64 * 1024;
    const int MaxLineBytes = 16 * 1024;
    const int MaxListingBytes = 16 * 1024 * 1024;
    const int MaxEntries = 100_000;
    static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(60);
    readonly TcpClient control;
    readonly NetworkStream stream;
    Encoding encoding;
    readonly bool autoEncoding;
    bool encodingDetected;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly byte[] replyBuffer = new byte[4096];
    int replyPosition, replyLength, disposed;
    public string WorkingDirectory { get; private set; } = "/";
    public bool IsConnected => Volatile.Read(ref disposed) == 0;
    public string EncodingName=>FtpEncoding.Label(encoding);
    public string EncodingDisplay=>autoEncoding?"자동 감지 · "+EncodingName:EncodingName+" · 지정";

    FtpClient(TcpClient control, Encoding encoding,bool autoEncoding)
    {
        this.control = control;
        this.encoding = encoding;
        this.autoEncoding=autoEncoding;
        stream = control.GetStream();
    }

    public static async Task<FtpClient> ConnectAsync(FtpSite site, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        string host = (site.Host ?? "").Trim();
        if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) || host.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("FTP 서버 IP 또는 호스트 이름을 확인해 주세요.");
        if (site.Port is < 1 or > 65535) throw new ArgumentException("FTP 포트는 1~65535 사이여야 합니다.");
        ValidateArgument(site.Username, "ID", allowEmpty: false);
        ValidateArgument(password, "비밀번호", allowEmpty: true);
        bool auto=FtpEncoding.Normalize(site.Encoding)=="Auto";
        var encoding = FtpEncoding.Get(auto?"UTF-8":site.Encoding);
        // Validate encodability before any login command is sent.
        try { _ = encoding.GetByteCount(site.Username); _ = encoding.GetByteCount(password); }
        catch (EncoderFallbackException) { throw new ArgumentException("ID 또는 비밀번호가 선택한 문자 인코딩으로 표현되지 않습니다."); }
        var tcp = new TcpClient { NoDelay = true };
        FtpClient? client = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ControlTimeout);
                try { await tcp.ConnectAsync(host, site.Port, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("FTP 서버 연결 시간이 초과되었습니다."); }
            }
            client = new FtpClient(tcp, encoding,auto);
            var hello = await client.ReadReplyAsync(ct).ConfigureAwait(false);
            if (hello.Code == 120) hello = await client.ReadReplyAsync(ct).ConfigureAwait(false);
            client.Ensure(hello, "서버 연결", 220);
            var login = await client.CommandAsync("USER " + site.Username, ct).ConfigureAwait(false);
            if (login.Code == 331) login = await client.CommandAsync("PASS " + password, ct).ConfigureAwait(false);
            client.Ensure(login, "로그인", 230);
            client.Ensure(await client.CommandAsync("TYPE I", ct).ConfigureAwait(false), "바이너리 전송 설정", 200);
            await client.ConfigureEncodingAsync(ct).ConfigureAwait(false);
            await client.ReadWorkingDirectoryAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(site.InitialPath))
            {
                ValidateArgument(site.InitialPath, "기본 경로", false);
                if(auto&&!client.encodingDetected&&site.InitialPath.Any(c=>c>127))
                {
                    // An ASCII PWD cannot identify legacy encodings. Read the home
                    // listing before sending a non-ASCII initial path when possible.
                    try{await client.ListAsync(client.WorkingDirectory,ct).ConfigureAwait(false);}catch(FtpException) when(client.IsConnected){}
                }
                client.Ensure(await client.CommandAsync("CWD " + site.InitialPath, ct).ConfigureAwait(false), "기본 폴더 열기", 250);
                await client.ReadWorkingDirectoryAsync(ct).ConfigureAwait(false);
            }
            return client;
        }
        catch (Exception error)
        {
            if (client != null) await client.DisposeAsync().ConfigureAwait(false);
            else tcp.Dispose();
            if (ct.IsCancellationRequested && error is not OperationCanceledException) throw new OperationCanceledException(ct);
            throw;
        }
    }

    public Task<string> ChangeDirectoryAsync(string path, CancellationToken ct = default)
    {
        ValidateArgument(path, "폴더 경로", false);
        return OperationAsync(async token =>
        {
            Ensure(await CommandAsync("CWD " + path, token).ConfigureAwait(false), "폴더 열기", 250);
            return await ReadWorkingDirectoryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        ValidateArgument(path, "폴더 경로", false);
        return OperationAsync<IReadOnlyList<FtpEntry>>(async token =>
        {
            var fullPath = ResolvePath(path);
            string listing;
            bool machine = true;
            try { listing = await ReadListingAsync("MLSD " + fullPath, token).ConfigureAwait(false); }
            catch (FtpException error) when (error.ReplyCode is 500 or 501 or 502 or 504 or 550)
            {
                machine = false;
                listing = await ReadListingAsync("LIST " + fullPath, token).ConfigureAwait(false);
            }
            var parsed=ParseListing(listing,fullPath,machine);
            if(machine&&parsed.Count==0&&parsed is FtpListing{OmittedCount:>0})
            {
                try
                {
                    var legacy=ParseListing(await ReadListingAsync("LIST "+fullPath,token).ConfigureAwait(false),fullPath,false);
                    if(legacy.Count>0)return legacy;
                }
                catch(FtpException) when(IsConnected){}
            }
            return parsed;
        }, ct);
    }

    public Task MakeDirectoryAsync(string path, CancellationToken ct = default)
    {
        ValidateArgument(path, "폴더 경로", false);
        return OperationAsync(async token =>
        {
            Ensure(await CommandAsync("MKD " + ResolvePath(path), token).ConfigureAwait(false), "새 폴더 만들기", 257, 250);
        }, ct);
    }

    public Task<long?> SizeAsync(string path, CancellationToken ct = default)
    {
        ValidateArgument(path, "파일 경로", false);
        return OperationAsync(token => SizeCoreAsync(ResolvePath(path), token), ct);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ValidateArgument(path, "파일 경로", false);
        return OperationAsync(async token => Ensure(await CommandAsync("DELE " + ResolvePath(path), token).ConfigureAwait(false), "파일 삭제", 250), ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default) => DeleteFileAsync(path, ct);

    internal Task VerifyDeletionDirectoryAsync(string directory,CancellationToken ct)=>OperationAsync(async token=>
    {
        string actual=await ReadWorkingDirectoryAsync(token).ConfigureAwait(false);
        if(FtpPaths.Normalize(actual)!=FtpPaths.Normalize(directory))throw new IOException("현재 서버 폴더가 변경되었습니다. 새로고침한 뒤 다시 선택해 주세요.");
    },ct);

    // User-approved deletion uses CWD + a literal child name. Some legacy FTP
    // servers allow browsing absolute paths but reject absolute DELE/RMD paths.
    // Keep the switch, deletion, and restoration in one serialized operation.
    internal Task DeleteListedEntryAsync(FtpEntry entry,Action deleted,CancellationToken ct)
    {
        ValidateArgument(entry.Name,"삭제할 파일 이름",false);
        string parent=FtpPaths.Parent(entry.FullPath);
        if(entry.IsLink||entry.Name.Contains('\\')||entry.FullPath!=FtpPaths.Join(parent,entry.Name))throw new IOException("삭제할 항목의 이름 또는 경로가 올바르지 않습니다.");
        return OperationAsync(async token=>
        {
            string original=WorkingDirectory;Exception? failure=null;
            try
            {
                Ensure(await CommandAsync("CWD "+parent,token).ConfigureAwait(false),"삭제 대상 폴더 열기",250);
                string actual=await ReadWorkingDirectoryAsync(token).ConfigureAwait(false);
                if(FtpPaths.Normalize(actual)!=parent)throw new IOException("삭제 대상 폴더가 다른 위치로 연결되어 있어 중단했습니다.");
                string name=entry.Name.StartsWith('-')||char.IsWhiteSpace(entry.Name[0])?"./"+entry.Name:entry.Name;
                Ensure(await CommandAsync((entry.IsDirectory?"RMD ":"DELE ")+name,token).ConfigureAwait(false),entry.IsDirectory?"폴더 삭제":"파일 삭제",250);
                // Count a confirmed deletion even if restoring CWD fails later.
                deleted();
            }
            catch(Exception error){failure=error;throw;}
            finally
            {
                if(IsConnected&&(FtpPaths.Normalize(original)!=parent||WorkingDirectory!=original))
                {
                    try
                    {
                        Ensure(await CommandAsync("CWD "+original,token).ConfigureAwait(false),"작업 폴더 복원",250);
                        if(FtpPaths.Normalize(await ReadWorkingDirectoryAsync(token).ConfigureAwait(false))!=FtpPaths.Normalize(original))throw new IOException("작업 폴더가 변경되었습니다.");
                    }
                    catch(Exception error){Abort();if(failure==null)throw new IOException("삭제 후 작업 폴더로 돌아가지 못했습니다. 서버에 다시 연결해 주세요.",error);}
                }
            }
        },ct);
    }
    public Task RemoveDirectoryAsync(string path,CancellationToken ct=default)
    {
        ValidateArgument(path,"폴더 경로",false);
        if(ResolvePath(path)=="/")throw new ArgumentException("서버 루트 폴더는 삭제할 수 없습니다.");
        return OperationAsync(async token=>Ensure(await CommandAsync("RMD "+ResolvePath(path),token).ConfigureAwait(false),"폴더 삭제",250),ct);
    }

    public Task RenameAsync(string source, string target, CancellationToken ct = default)
    {
        ValidateArgument(source, "원본 경로", false);
        ValidateArgument(target, "대상 경로", false);
        return OperationAsync(async token =>
        {
            Ensure(await CommandAsync("RNFR " + ResolvePath(source), token).ConfigureAwait(false), "파일 이름 변경", 350);
            try { Ensure(await CommandAsync("RNTO " + ResolvePath(target), token).ConfigureAwait(false), "파일 이름 변경", 250); }
            catch { Abort(); throw; } // RNFR can leave a server-specific pending rename state.
        }, ct);
    }

    public Task DownloadAsync(string remotePath, Stream destination, IProgress<FtpProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateArgument(remotePath, "파일 경로", false);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("다운로드 대상 파일에 쓸 수 없습니다.");
        return OperationAsync(async token =>
        {
            string path = ResolvePath(remotePath);
            long? total = await SizeCoreAsync(path, token).ConfigureAwait(false);
            using var data = await OpenPassiveAsync(token).ConfigureAwait(false);
            Ensure(await CommandAsync("RETR " + path, token).ConfigureAwait(false), "다운로드 시작", 125, 150);
            try
            {
                long done = 0;
                progress?.Report(new FtpProgress(0, total));
                var buffer = new byte[64 * 1024];
                using var network = data.GetStream();
                while (true)
                {
                    int count = await ReadDataAsync(network, buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    done += count;
                    progress?.Report(new FtpProgress(done, total));
                }
                data.Dispose();
                Ensure(await ReadReplyAsync(token).ConfigureAwait(false), "다운로드 완료", 226, 250);
                if (total.HasValue && done != total.Value) throw new IOException("다운로드한 파일 크기가 서버의 파일 크기와 다릅니다. 다시 시도해 주세요.");
            }
            catch { Abort(); throw; }
        }, ct);
    }

    public Task UploadAsync(Stream source, string remotePath, IProgress<FtpProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateArgument(remotePath, "파일 경로", false);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("업로드할 파일을 읽을 수 없습니다.");
        return OperationAsync(async token =>
        {
            string path = ResolvePath(remotePath);
            long? total = source.CanSeek ? source.Length - source.Position : null;
            using var data = await OpenPassiveAsync(token).ConfigureAwait(false);
            Ensure(await CommandAsync("STOR " + path, token).ConfigureAwait(false), "업로드 시작", 125, 150);
            try
            {
                long done = 0;
                progress?.Report(new FtpProgress(0, total));
                var buffer = new byte[64 * 1024];
                using var network = data.GetStream();
                while (true)
                {
                    int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    await WriteDataAsync(network, buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    done += count;
                    progress?.Report(new FtpProgress(done, total));
                }
                data.Dispose();
                Ensure(await ReadReplyAsync(token).ConfigureAwait(false), "업로드 완료", 226, 250);
                if (total.HasValue && done != total.Value) throw new IOException("전송 중 로컬 파일 크기가 변경되었습니다. 파일을 확인해 주세요.");
                var remoteSize = await SizeCoreAsync(path, token).ConfigureAwait(false);
                if (remoteSize.HasValue && done != remoteSize.Value) throw new IOException("업로드한 파일 크기가 서버의 파일 크기와 다릅니다. 다시 시도해 주세요.");
            }
            catch { Abort(); throw; }
        }, ct);
    }

    async Task<long?> SizeCoreAsync(string path, CancellationToken ct)
    {
        var reply = await CommandAsync("SIZE " + path, ct).ConfigureAwait(false);
        if (reply.Code is 500 or 501 or 502 or 504 or 550) return null;
        Ensure(reply, "파일 크기 확인", 213);
        return long.TryParse(reply.Text.AsSpan(4).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size) && size >= 0
            ? size : throw new InvalidDataException("FTP 서버가 올바르지 않은 파일 크기를 반환했습니다.");
    }

    async Task<string> ReadListingAsync(string command, CancellationToken ct)
    {
        using var data = await OpenPassiveAsync(ct).ConfigureAwait(false);
        Ensure(await CommandAsync(command, ct).ConfigureAwait(false), "폴더 목록 읽기", 125, 150);
        try
        {
            using var output = new MemoryStream();
            using var network = data.GetStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                int count = await ReadDataAsync(network, buffer, ct).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > MaxListingBytes) throw new InvalidDataException("폴더 목록이 너무 큽니다. 하위 폴더로 나누어 열어 주세요.");
                output.Write(buffer, 0, count);
            }
            data.Dispose();
            Ensure(await ReadReplyAsync(ct).ConfigureAwait(false), "폴더 목록 완료", 226, 250);
            return DecodePathBytes(output.GetBuffer().AsSpan(0,checked((int)output.Length)));
        }
        catch { Abort(); throw; }
    }

    async Task<TcpClient> OpenPassiveAsync(CancellationToken ct)
    {
        var peer = (IPEndPoint?)control.Client.RemoteEndPoint ?? throw new IOException("FTP 연결이 종료되었습니다.");
        // TcpClient's default dual-stack control socket reports IPv4 peers as
        // ::ffff:a.b.c.d. A fresh IPv6-only data socket cannot connect that address.
        var peerAddress = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
        int port;
        var response = await CommandAsync("EPSV", ct).ConfigureAwait(false);
        if (response.Code == 229)
        {
            var match = Regex.Match(response.Text, @"\((?<d>[^\d\s()])\k<d>\k<d>(?<p>\d{1,5})\k<d>\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success || !int.TryParse(match.Groups["p"].Value, out port) || port is < 1 or > 65535)
                throw new InvalidDataException("FTP 서버의 EPSV 포트 응답이 올바르지 않습니다.");
        }
        else
        {
            if (response.Code < 400 || response.Code >= 600 || response.Code == 421) Ensure(response, "패시브 연결 준비", 229);
            if (peerAddress.AddressFamily != AddressFamily.InterNetwork) Ensure(response, "IPv6 패시브 연결 준비", 229);
            response = await CommandAsync("PASV", ct).ConfigureAwait(false);
            Ensure(response, "패시브 연결 준비", 227);
            var match = Regex.Match(response.Text, @"\((\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3})\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new InvalidDataException("FTP 서버의 PASV 응답이 올바르지 않습니다.");
            int[] parts = Enumerable.Range(1, 6).Select(i => int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Any(p => p > 255) || (port = parts[4] * 256 + parts[5]) == 0)
                throw new InvalidDataException("FTP 서버의 PASV 포트 응답이 올바르지 않습니다.");
            // Ignore the advertised address: use the authenticated control peer, including through NAT.
        }
        var data = new TcpClient(peerAddress.AddressFamily) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ControlTimeout);
            try { await data.ConnectAsync(peerAddress, port, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("FTP 데이터 연결 시간이 초과되었습니다. 패시브 포트의 방화벽 설정을 확인해 주세요."); }
            return data;
        }
        catch { data.Dispose(); throw; }
    }

    async Task<string> ReadWorkingDirectoryAsync(CancellationToken ct)
    {
        var reply = await CommandAsync("PWD", ct).ConfigureAwait(false);
        Ensure(reply, "현재 경로 확인", 257);
        int start = reply.Text.IndexOf('"');
        if (start < 0) throw new InvalidDataException("FTP 서버가 현재 폴더 경로를 반환하지 않았습니다.");
        var path = new StringBuilder();
        bool ended = false;
        for (int i = start + 1; i < reply.Text.Length; i++)
        {
            if (reply.Text[i] != '"') path.Append(reply.Text[i]);
            else if (i + 1 < reply.Text.Length && reply.Text[i + 1] == '"') { path.Append('"'); i++; }
            else { ended = true; break; }
        }
        if (!ended || path.Length == 0) throw new InvalidDataException("FTP 서버의 현재 폴더 응답이 올바르지 않습니다.");
        string decoded=DecodePathBytes(Encoding.Latin1.GetBytes(path.ToString()));
        ValidateArgument(decoded, "서버 경로", false);
        WorkingDirectory = decoded;
        return WorkingDirectory;
    }

    readonly record struct Reply(int Code, string Text);
    async Task<Reply> CommandAsync(string command, CancellationToken ct)
    {
        // Every argument has also been checked at the API boundary, including USER/PASS.
        if (command.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("FTP 명령에 줄바꿈이나 제어 문자를 사용할 수 없습니다.");
        byte[] bytes;
        try { bytes = encoding.GetBytes(command + "\r\n"); }
        catch (EncoderFallbackException) { throw new ArgumentException("파일 이름을 선택한 문자 인코딩으로 표현할 수 없습니다."); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ControlTimeout);
            try { await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("FTP 명령 전송 시간이 초과되었습니다."); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    async Task<Reply> ReadReplyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ControlTimeout);
        try
        {
            string first = await ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code) || code is < 100 or > 599 || (first.Length > 3 && first[3] is not (' ' or '-')))
                throw new InvalidDataException("FTP 서버 응답을 해석할 수 없습니다.");
            int length = first.Length;
            if (first.Length > 3 && first[3] == '-')
            {
                var lines=new StringBuilder(first);
                string terminator = code.ToString(CultureInfo.InvariantCulture) + " ";
                while (true)
                {
                    string line = await ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    length += line.Length;
                    if (length > MaxReplyBytes) throw new InvalidDataException("FTP 서버 응답이 허용 길이를 초과했습니다.");
                    lines.Append('\n').Append(line);
                    if (line.StartsWith(terminator, StringComparison.Ordinal)) return new Reply(code, lines.ToString());
                }
            }
            return new Reply(code, first);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("FTP 서버 응답 시간이 초과되었습니다."); }
    }

    async Task<string> ReadLineAsync(CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            if (replyPosition == replyLength)
            {
                replyLength = await stream.ReadAsync(replyBuffer, ct).ConfigureAwait(false);
                replyPosition = 0;
                if (replyLength == 0) throw new IOException("FTP 서버가 연결을 종료했습니다.");
            }
            byte value = replyBuffer[replyPosition++];
            if (value == '\n')
            {
                int count = checked((int)bytes.Length);
                if (count > 0 && bytes.GetBuffer()[count - 1] == '\r') count--;
                // Keep raw reply bytes losslessly. Codes/features are ASCII, while
                // PWD is decoded separately using the selected filename charset.
                return Encoding.Latin1.GetString(bytes.GetBuffer(),0,count);
            }
            bytes.WriteByte(value);
            if (bytes.Length > MaxLineBytes) throw new InvalidDataException("FTP 서버 응답 행이 너무 깁니다.");
        }
    }

    async Task<int> ReadDataAsync(NetworkStream network, Memory<byte> buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TransferIdleTimeout);
        try { return await network.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("60초 동안 FTP 데이터를 받지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요."); }
    }

    static async Task WriteDataAsync(NetworkStream network, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TransferIdleTimeout);
        try { await network.WriteAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("60초 동안 FTP 데이터를 보내지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요."); }
    }

    Task OperationAsync(Func<CancellationToken, Task> operation, CancellationToken ct) =>
        OperationAsync(async token => { await operation(token).ConfigureAwait(false); return true; }, ct);

    async Task<T> OperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!IsConnected) throw new IOException("FTP 연결이 종료되었습니다. 다시 연결해 주세요.");
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (FtpException) { throw; }
        catch (Exception error) when (ct.IsCancellationRequested && error is not OperationCanceledException)
        {
            Abort();
            throw new OperationCanceledException(ct);
        }
        catch { Abort(); throw; }
        finally { gate.Release(); }
    }

    void Ensure(Reply reply, string action, params int[] codes)
    {
        if (codes.Contains(reply.Code)) return;
        if (reply.Code == 421 || reply.Code < 200 || reply.Code is >= 300 and < 400) Abort();
        throw new FtpException(action, reply.Code);
    }

    void Abort()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        control.Dispose();
        Array.Clear(replyBuffer);
    }

    public ValueTask DisposeAsync() { Abort(); return ValueTask.CompletedTask; }

    async Task ConfigureEncodingAsync(CancellationToken ct)
    {
        bool utf8=encoding.CodePage==65001;
        if(autoEncoding)
        {
            var features=await CommandAsync("FEAT",ct).ConfigureAwait(false);
            if(features.Code!=211 && features.Code is not (500 or 501 or 502 or 504 or 550))Ensure(features,"문자셋 지원 확인",211);
            utf8=features.Code==211&&features.Text.Split('\n').Any(line=>line.Trim().Equals("UTF8",StringComparison.OrdinalIgnoreCase)||line.Trim().Equals("UTF-8",StringComparison.OrdinalIgnoreCase));
            // Legacy Korean equipment often has no FEAT/UTF8. ASCII is shared;
            // the first non-ASCII path/listing refines this without lossy decoding.
            encoding=FtpEncoding.Get(utf8?"UTF-8":"CP949");
        }
        if(!autoEncoding||utf8)
        {
            var response=await CommandAsync(utf8?"OPTS UTF8 ON":"OPTS UTF8 OFF",ct).ConfigureAwait(false);
            if(response.Code==421||response.Code<200||response.Code is >=300 and <400)Ensure(response,"문자셋 설정",200);
        }
    }

    string DecodePathBytes(ReadOnlySpan<byte> bytes)
    {
        bool nonAscii=false;foreach(byte value in bytes)if(value>=128){nonAscii=true;break;}
        if(autoEncoding&&!encodingDetected&&nonAscii)
        {
            foreach(string candidate in new[]{"UTF-8","CP949","ISO-8859-1"})
            {
                var codec=FtpEncoding.Get(candidate);
                try
                {
                    string text=codec.GetString(bytes);
                    if(!codec.GetBytes(text).AsSpan().SequenceEqual(bytes))continue;
                    encoding=codec;encodingDetected=true;return text;
                }
                catch(DecoderFallbackException){}
                catch(EncoderFallbackException){}
            }
        }
        try{return encoding.GetString(bytes);}
        catch(DecoderFallbackException){throw new InvalidDataException("서버 파일명 문자셋이 일치하지 않습니다. 접속 설정에서 자동 감지 또는 직접 지정(CP949 등)을 선택해 주세요.");}
    }

    static void ValidateArgument(string? value, string label, bool allowEmpty)
    {
        if (value == null || (!allowEmpty && value.Length == 0) || value.Length > 4096 || value.Any(char.IsControl) || value.Any(c => c is '\u2028' or '\u2029'))
            throw new ArgumentException($"{label}에 빈 값, 제어 문자 또는 너무 긴 값을 사용할 수 없습니다.");
    }

    string ResolvePath(string path)
    {
        // Normalize TVFS/Unix paths without URI escaping; FTP paths are literal protocol arguments.
        if (!WorkingDirectory.StartsWith('/')) return path.StartsWith('/') ? path : WorkingDirectory.TrimEnd('/') + "/" + path;
        var parts = (path.StartsWith('/') ? path : WorkingDirectory.TrimEnd('/') + "/" + path).Split('/');
        var result = new List<string>();
        foreach (string part in parts)
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (result.Count > 0) result.RemoveAt(result.Count - 1); }
            else result.Add(part);
        }
        return "/" + string.Join('/', result);
    }

    static bool SafeLeaf(string name) => name.Length is > 0 and <= 4096 && name is not ("." or "..") &&
        !name.Any(char.IsControl) && name.IndexOfAny(['/', '\u2028', '\u2029']) < 0;

    internal static IReadOnlyList<FtpEntry> ParseListing(string listing, string directory, bool machine)
    {
        var result = new Dictionary<string,FtpEntry>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int omitted=0,lines=0;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || (!machine && line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))) continue;
            if(++lines>MaxEntries)throw new InvalidDataException("폴더에 항목이 너무 많습니다. 하위 폴더로 나누어 열어 주세요.");
            if (line.Length > MaxLineBytes){omitted++;continue;}
            FtpEntry? entry;
            try{entry=machine?ParseMachineLine(line,directory):ParseLegacyLine(line,directory);}
            catch(InvalidDataException){omitted++;continue;}
            if (entry == null) continue;
            string name=entry.Name;
            // Some legacy LIST servers return ./name or the requested absolute
            // child path. Only accept a single direct child; never normalize '..'.
            string prefix=directory.TrimEnd('/')+"/";
            if(name.StartsWith(prefix,StringComparison.Ordinal))name=name[prefix.Length..];
            else if(name.StartsWith("./",StringComparison.Ordinal))name=name[2..];
            if(name is "." or "..")continue;
            if(!SafeLeaf(name)){omitted++;continue;}
            if(!seen.Add(name)){if(result.Remove(name))omitted++;omitted++;continue;}
            result.Add(name,entry with{Name=name,FullPath=prefix+name});
        }
        return new FtpListing(result.Values.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(),omitted);
    }

    static FtpEntry? ParseMachineLine(string line, string directory)
    {
        int space = line.IndexOf(' ');
        if (space < 1) throw new InvalidDataException("서버의 MLSD 폴더 목록 형식을 해석할 수 없습니다.");
        string name = line[(space + 1)..];
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fact in line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = fact.IndexOf('=');
            if (equals > 0) facts[fact[..equals]] = fact[(equals + 1)..];
        }
        string type = facts.GetValueOrDefault("type", "");
        if (type.Equals("cdir", StringComparison.OrdinalIgnoreCase) || type.Equals("pdir", StringComparison.OrdinalIgnoreCase) || name is "." or "..") return null;
        bool isDir = type.Equals("dir", StringComparison.OrdinalIgnoreCase);
        bool isLink = type.Contains("slink", StringComparison.OrdinalIgnoreCase) || type.Contains("symlink", StringComparison.OrdinalIgnoreCase) || facts.ContainsKey("UNIX.slink");
        if (!isDir && !isLink && !type.Equals("file", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("서버의 MLSD 항목 종류를 해석할 수 없습니다.");
        long? size = long.TryParse(facts.GetValueOrDefault("size"), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0 ? parsed : null;
        DateTimeOffset? modified = null;
        if (facts.TryGetValue("modify", out string? stamp))
        {
            int dot = stamp.IndexOf('.');
            if (dot >= 0) stamp = stamp[..dot];
            if (DateTimeOffset.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) modified = date;
        }
        return new FtpEntry(name, directory.TrimEnd('/') + "/" + name, isDir && !isLink, isDir ? null : size, modified, isLink);
    }

    static readonly Regex UnixLine = new(@"^(?<mode>[bcdlps-][rwxStTs-]{9}[+@.]?)\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<when>\d{4}|\d{1,2}:\d{2})\s(?<name>.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex DosLine = new(@"^(?<date>\d{2}[-/]\d{2}[-/]\d{2,4})\s+(?<time>\d{1,2}:\d{2})(?<ampm>AM|PM)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    static FtpEntry? ParseLegacyLine(string line, string directory)
    {
        var unix = UnixLine.Match(line);
        if (unix.Success)
        {
            string name = unix.Groups["name"].Value;
            char kind = unix.Groups["mode"].Value[0];
            if (kind == 'l')
            {
                int arrow = name.LastIndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) name = name[..arrow];
            }
            if (name is "." or "..") return null;
            // Device/FIFO/socket entries are not regular downloadable files.
            if (kind is not ('-' or 'd' or 'l')) return null;
            long? size = long.TryParse(unix.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
            DateTimeOffset? modified = null;
            string when = unix.Groups["when"].Value;
            bool hasTime = when.Contains(':');
            string stamp = unix.Groups["month"].Value + " " + unix.Groups["day"].Value + " " + (hasTime ? DateTime.Now.Year.ToString(CultureInfo.InvariantCulture) + " " + when : when);
            if (DateTime.TryParseExact(stamp, hasTime ? "MMM d yyyy H:mm" : "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                if (hasTime && date > DateTime.Now.AddDays(2)) date = date.AddYears(-1);
                modified = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
            }
            return new FtpEntry(name, directory.TrimEnd('/') + "/" + name, kind == 'd', kind == 'd' ? null : size, modified, kind == 'l');
        }
        var dos = DosLine.Match(line);
        if (dos.Success)
        {
            string name = dos.Groups["name"].Value;
            if (name is "." or "..") return null;
            bool isDir = dos.Groups["size"].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
            long? size = long.TryParse(dos.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
            string stamp = dos.Groups["date"].Value.Replace('/', '-') + " " + dos.Groups["time"].Value + dos.Groups["ampm"].Value.ToUpperInvariant();
            DateTimeOffset? modified = DateTime.TryParseExact(stamp, ["MM-dd-yy h:mmtt", "MM-dd-yyyy h:mmtt"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local)) : null;
            return new FtpEntry(name, directory.TrimEnd('/') + "/" + name, isDir, isDir ? null : size, modified);
        }
        throw new InvalidDataException("서버의 폴더 목록 형식을 지원하지 않습니다. MLSD 또는 UNIX·DOS 형식의 LIST가 필요합니다.");
    }
}
