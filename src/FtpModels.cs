using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EquipLink;

public sealed class FtpSite
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Name { get; set; }="새 FTP 서버";
    public string Host { get; set; }="";
    public int Port { get; set; }=21;
    public string Username { get; set; }="";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? EncryptedPassword { get; set; }
    [JsonIgnore] public bool HasSavedPassword=>!string.IsNullOrEmpty(EncryptedPassword);
    public string InitialPath { get; set; }="/";
    public string Encoding { get; set; }="Auto";
    public FtpSite Copy()=>JsonSerializer.Deserialize<FtpSite>(JsonSerializer.Serialize(this))!;
    public Equipment CredentialTarget()=>new(){Host=Host,Port=Port,Username=Username,Protocol="FTP"};
    public void Validate()
    {
        Host=(Host??"").Trim().Trim('[',']');Name=(Name??"").Trim();Username??="";
        if(!Rules.ValidHost(Host))throw new ArgumentException("올바른 FTP 서버 IP 또는 호스트를 입력해 주세요.");
        if(Port is <1 or >65535)throw new ArgumentException("FTP 포트는 1~65535 사이여야 합니다.");
        if(Name.Length==0)Name=Host;
        if(Name.Length>100)throw new ArgumentException("서버 이름은 100자 이내로 입력해 주세요.");
        if(Username.Length==0 || Username.Length>256 || Username.Any(char.IsControl))throw new ArgumentException("FTP ID를 제어 문자 없이 256자 이내로 입력해 주세요.");
        if(EncryptedPassword?.Length>32768)throw new ArgumentException("저장된 FTP 비밀번호 형식이 올바르지 않습니다.");
        if(EncryptedPassword=="")EncryptedPassword=null;
        InitialPath=string.IsNullOrWhiteSpace(InitialPath)?"/":InitialPath;
        FtpPaths.Validate(InitialPath);
        Encoding=FtpEncoding.Normalize(Encoding);
        if(!Guid.TryParseExact(Id,"N",out _))Id=Guid.NewGuid().ToString("N");
    }
}

public sealed record FtpEntry(string Name,string FullPath,bool IsDirectory,long? Size,DateTimeOffset? Modified,bool IsLink=false);
public sealed record FtpProgress(long Bytes,long? Total);
public sealed class FtpTransferRecord
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; }=DateTimeOffset.Now;
    public string SiteName { get; set; }="";
    public string Host { get; set; }="";
    public string Direction { get; set; }="";
    public string LocalPath { get; set; }="";
    public string RemotePath { get; set; }="";
    public long Bytes { get; set; }
    public string Status { get; set; }="";
    public string Error { get; set; }="";
}

public static class FtpPaths
{
    public static void Validate(string path)
    {
        if(path==null || path.Length>4096 || path.Any(char.IsControl))throw new ArgumentException("FTP 경로는 제어 문자 없이 4,096자 이내로 입력해 주세요.");
    }
    public static string Normalize(string path,string current="/")
    {
        Validate(path);Validate(current);
        if(path.Length==0)path=current;
        if(!path.StartsWith('/'))path=current.TrimEnd('/')+"/"+path;
        var parts=new List<string>();
        foreach(var part in path.Split('/'))
        {
            if(part is "" or ".")continue;
            if(part==".."){if(parts.Count>0)parts.RemoveAt(parts.Count-1);}
            else parts.Add(part);
        }
        return "/"+string.Join('/',parts);
    }
    public static string Join(string directory,string leaf)
    {
        Validate(leaf);
        if(leaf is "" or "." or ".." || leaf.Contains('/'))throw new ArgumentException("폴더 또는 파일 이름을 확인해 주세요.");
        return Normalize(directory).TrimEnd('/')+"/"+leaf;
    }
    public static string Parent(string path)
    {
        path=Normalize(path);int index=path.LastIndexOf('/');return index<=0?"/":path[..index];
    }
    public static string SafeLocalName(string leaf)
    {
        Validate(leaf);
        if(leaf is "" or "." or ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || leaf.EndsWith(' ') || leaf.EndsWith('.'))
            throw new ArgumentException("Windows에 저장할 수 없는 파일 이름입니다: "+leaf);
        var stem=leaf.Split('.')[0].ToUpperInvariant();
        if(stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length==4 && (stem.StartsWith("COM")||stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
            throw new ArgumentException("Windows 예약 이름은 저장할 수 없습니다: "+leaf);
        return leaf;
    }
}
