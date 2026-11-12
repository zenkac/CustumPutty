using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EquipLink;

public sealed class Equipment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 설비";
    public string Host { get; set; } = "";
    public string Group { get; set; } = "기본 그룹";
    public string Protocol { get; set; } = "Telnet";
    public int Port { get; set; } = 23;
    public string Username { get; set; } = "";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedPassword { get; set; }
    [JsonIgnore] public bool HasSavedPassword => !string.IsNullOrEmpty(EncryptedPassword);
    public bool AutoLogin { get; set; } = true;
    [JsonIgnore] public bool CanAutoLogin => AutoLogin && HasSavedPassword && !string.IsNullOrEmpty(Username);
    public string Encoding { get; set; } = "UTF-8";
    public bool Favorite { get; set; }
    public bool LogSession { get; set; }
    public bool UseX11 { get; set; } = true;
    public string X11Mode { get; set; } = "Auto";
    public string DisplayAddress { get; set; } = "";
    public string Shell { get; set; } = "sh";
    public string GuiCommand { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string SftpInitialPath { get; set; } = "";
    public string BasePuttySession { get; set; } = "";
    public DateTimeOffset? LastOpened { get; set; }
    [JsonIgnore] public bool IsSsh => Protocol == "SSH";
    [JsonIgnore] public bool ForwardX11 => IsSsh && UseX11 && X11Mode != "Direct";
    public Equipment Copy() => JsonSerializer.Deserialize<Equipment>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        Host = (Host ?? "").Trim().Trim('[',']');
        Name = (Name ?? "").Trim(); Group = (Group ?? "").Trim();
        if (!Rules.ValidHost(Host)) throw new ArgumentException("올바른 IP 주소 또는 호스트 이름을 입력해 주세요.");
        if (Port is < 1 or > 65535) throw new ArgumentException("포트는 1~65535 사이의 숫자여야 합니다.");
        if (Protocol != "Telnet" && Protocol != "SSH") throw new ArgumentException("Telnet 또는 SSH를 선택해 주세요.");
        if (string.IsNullOrWhiteSpace(Name)) Name = Host;
        if (string.IsNullOrWhiteSpace(Group)) Group = "기본 그룹";
        if (Name.Length > 100 || Group.Length > 60) throw new ArgumentException("설비 이름은 100자, 그룹은 60자 이내로 입력해 주세요.");
        if (!string.IsNullOrWhiteSpace(DisplayAddress) && !IPAddress.TryParse(DisplayAddress, out var address)) throw new ArgumentException("DISPLAY 주소는 Windows PC의 IP 주소를 입력해 주세요.");
        if (GuiCommand?.Length > 16000 || Notes?.Length > 16000) throw new ArgumentException("명령과 메모는 각각 16,000자 이내로 입력해 주세요.");
        if (Id == null || !Regex.IsMatch(Id,"^[a-fA-F0-9]{32}$")) Id = Guid.NewGuid().ToString("N");
        Username ??= ""; Encoding ??= "UTF-8"; DisplayAddress ??= ""; GuiCommand ??= ""; Notes ??= ""; PrivateKeyPath ??= ""; BasePuttySession ??= "";
        if(Username.Length>256 || Username.Any(char.IsControl))throw new ArgumentException("ID는 제어 문자 없이 256자 이내로 입력해 주세요.");
        if(EncryptedPassword?.Length>32768)throw new ArgumentException("저장된 비밀번호 데이터 형식을 확인해 주세요.");
        if(EncryptedPassword=="")EncryptedPassword=null;
        if (Shell != "csh") Shell = "sh";
        SftpInitialPath??="";SftpSession.ValidatePath(SftpInitialPath,true);
        if (X11Mode != "Direct") X11Mode = "Auto";
    }
}

public sealed class Preferences
{
    public string ThemeId { get; set; } = "midnight";
    public bool FtpIconView { get; set; } = true;
    public bool FtpSwapPanes { get; set; }
    public bool FtpSinglePane { get; set; }
    public string XServerPath { get; set; } = "";
    public string XLaunchPath { get; set; } = "";
    public string XLaunchConfig { get; set; } = "";
    public string XAuthorityFile { get; set; } = "";
    public bool AutoStartX { get; set; } = true;
    public int DisplayNumber { get; set; }
    public string PcAddress { get; set; } = "";
    public int FontSize { get; set; } = 11;
    public int Scrollback { get; set; } = 20000;
    public bool ExternalTerminal { get; set; }
    public void Validate()
    {
        ThemeId=ThemeService.NormalizeId(ThemeId);
        if (DisplayNumber is < 0 or > 99) throw new ArgumentException("디스플레이 번호는 0~99로 설정해 주세요.");
        if (FontSize is < 8 or > 24) throw new ArgumentException("터미널 글자 크기는 8~24로 설정해 주세요.");
        if (Scrollback is < 1000 or > 200000) throw new ArgumentException("스크롤 보관 줄 수는 1,000~200,000으로 설정해 주세요.");
        if (!string.IsNullOrWhiteSpace(PcAddress) && !IPAddress.TryParse(PcAddress,out _)) throw new ArgumentException("PC 주소는 IP 형식으로 입력해 주세요.");
    }
}

public sealed class Snippet
{
    public string Name { get; set; } = "명령";
    public string Command { get; set; } = "";
}
public sealed class RecentConnection
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "Telnet";
}

public static class AppPaths
{
    public static string? OverrideRoot { get; set; }
    public static string Root => OverrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EquipLink");
    public static string Logs => Path.Combine(Root,"logs");
    public static string Config => Path.Combine(Root,"settings.json");
}

public sealed class AppStore
{
    public int Version { get; set; } = 1;
    public int FtpSettingsVersion { get; set; } = 1;
    public Preferences Settings { get; set; } = new();
    public List<Equipment> Equipment { get; set; } = [];
    public List<Snippet> Snippets { get; set; } = [new(){Name="현재 위치",Command="pwd"},new(){Name="디스크 확인",Command="df -h"},new(){Name="DISPLAY 확인",Command="printenv DISPLAY"}];
    public List<RecentConnection> Recent { get; set; } = [];
    public List<FtpSite> FtpSites { get; set; } = [];
    public FtpSite? LastFtpConnection { get; set; }
    public List<FtpTransferRecord> TransferHistory { get; set; } = [];
    [JsonIgnore] public string Notice { get; set; } = "";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented=true, PropertyNameCaseInsensitive=true };
    public static AppStore Load()
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (!File.Exists(AppPaths.Config)) return new();
        try { return Read(File.ReadAllText(AppPaths.Config)); }
        catch (Exception exception)
        {
            var backup = AppPaths.Config + ".bak";
            if (File.Exists(backup))
            {
                try { var recovered=Read(File.ReadAllText(backup)); recovered.Notice="설정 파일을 읽지 못해 최근 백업을 불러왔습니다. 원본은 보존했습니다."; return recovered; } catch { }
            }
            // A damaged file is never silently replaced with an empty configuration.
            throw new InvalidDataException("설정 파일을 읽을 수 없습니다. 파일을 보존했습니다: " + AppPaths.Config,exception);
        }
    }
    public static AppStore Read(string json)
    {
        var data=JsonSerializer.Deserialize<AppStore>(json,Json) ?? throw new InvalidDataException("빈 설정 파일입니다.");
        if(data.Version!=1) throw new InvalidDataException("지원하지 않는 설정 파일 버전입니다.");
        data.Settings ??= new(); data.Equipment ??= []; data.Snippets ??= []; data.Recent ??= [];
        data.FtpSites??=[];data.TransferHistory??=[];
        using(var document=JsonDocument.Parse(json))
        {
            bool previous=!document.RootElement.EnumerateObject().Any(p=>p.Name.Equals(nameof(FtpSettingsVersion),StringComparison.OrdinalIgnoreCase));
            if(previous)
            {
                // v1.4 offered no automatic mode and defaulted every site to UTF-8.
                // Explicit legacy charsets and DPAPI credentials remain unchanged.
                foreach(var site in data.FtpSites)if(site.Encoding=="UTF-8")site.Encoding="Auto";
                if(data.LastFtpConnection?.Encoding=="UTF-8")data.LastFtpConnection.Encoding="Auto";
            }
        }
        if(data.FtpSites.Count>5000)throw new InvalidDataException("FTP 서버 목록이 너무 큽니다.");
        foreach(var site in data.FtpSites)site.Validate();
        data.LastFtpConnection?.Validate();
        data.TransferHistory=data.TransferHistory.Where(r=>r!=null).Take(500).ToList();
        data.Settings.Validate();
        if(data.Equipment.Count>5000 || data.Snippets.Count>1000) throw new InvalidDataException("가져올 항목 수가 너무 많습니다.");
        foreach(var item in data.Equipment) item.Validate();
        foreach(var snippet in data.Snippets)
            if(snippet is null || string.IsNullOrWhiteSpace(snippet.Name) || string.IsNullOrWhiteSpace(snippet.Command) || snippet.Command.Length>16000) throw new InvalidDataException("명령 모음 형식을 확인해 주세요.");
        return data;
    }
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temp=AppPaths.Config+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,Json));
        if(File.Exists(AppPaths.Config)) File.Replace(temp,AppPaths.Config,AppPaths.Config+".bak",true);
        else File.Move(temp,AppPaths.Config);
    }
    public string ExportJson()
    {
        var exported=Read(JsonSerializer.Serialize(this,Json));
        foreach(var item in exported.Equipment)item.EncryptedPassword=null;
        foreach(var site in exported.FtpSites)site.EncryptedPassword=null;
        if(exported.LastFtpConnection!=null)exported.LastFtpConnection.EncryptedPassword=null;
        return JsonSerializer.Serialize(exported,Json);
    }
    public static AppStore ReadImport(string json)
    {
        var imported=Read(json);
        foreach(var item in imported.Equipment)item.EncryptedPassword=null;
        foreach(var site in imported.FtpSites)site.EncryptedPassword=null;
        if(imported.LastFtpConnection!=null)imported.LastFtpConnection.EncryptedPassword=null;
        return imported;
    }
    public void Record(Equipment equipment)
    {
        var saved=Equipment.FirstOrDefault(p=>p.Id==equipment.Id);
        if(saved!=null) saved.LastOpened=DateTimeOffset.Now;
        Recent.Insert(0,new(){Name=equipment.Name,Host=equipment.Host,Port=equipment.Port,Protocol=equipment.Protocol});
        if(Recent.Count>100) Recent.RemoveRange(100,Recent.Count-100);
        Save();
    }
    public static AppStore Demo() => new()
    {
        Equipment = [
            new(){Name="TESTER 01",Host="192.0.2.21",Group="TEST FLOOR",Favorite=true,LastOpened=DateTimeOffset.Now},
            new(){Name="TESTER 02",Host="192.0.2.22",Group="TEST FLOOR",Favorite=true},
            new(){Name="ENGINEERING",Host="192.0.2.80",Group="ENGINEERING",Protocol="SSH",Port=22},
            new(){Name="HANDLER 03",Host="192.0.2.33",Group="TEST FLOOR"}
        ],
        Notice="미리보기 · 예시 설비이며 실제로 접속하지 않았습니다."
    };
}

public static class Rules
{
    public static bool ValidHost(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length<=253 &&
        (IPAddress.TryParse(host,out _) || Regex.IsMatch(host,@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$"));
    public static string SafeFilename(string name) => Regex.Replace(name,@"[^\p{L}\p{N}._-]","_")[..Math.Min(50,name.Length)];
    public static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

public static class Commands
{
    public static string Display(string address,int number)
    {
        if(!IPAddress.TryParse(address,out var ip)) throw new ArgumentException("PC의 DISPLAY IP를 선택해 주세요.");
        if(number is < 0 or > 99) throw new ArgumentException("올바르지 않은 디스플레이 번호입니다.");
        return (ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?"["+address+"]":address)+":"+number+".0";
    }
    public static string Xterm(Equipment equipment,Preferences preferences,string localAddress) => equipment.ForwardX11
        ? "xterm &"
        : "xterm -display " + Display(localAddress,preferences.DisplayNumber) + " &";
    public static string SetDisplay(Equipment equipment,Preferences preferences,string localAddress)
    {
        if(equipment.ForwardX11) return "printenv DISPLAY";
        string display=Display(localAddress,preferences.DisplayNumber);
        return equipment.Shell=="csh"?"setenv DISPLAY "+display:"export DISPLAY="+display;
    }
    public static string Gui(Equipment equipment,Preferences preferences,string localAddress)
    {
        if(string.IsNullOrWhiteSpace(equipment.GuiCommand)) return Xterm(equipment,preferences,localAddress);
        return equipment.ForwardX11?equipment.GuiCommand:SetDisplay(equipment,preferences,localAddress)+"; "+equipment.GuiCommand;
    }
    public static string NormalizeInput(string value,bool execute)
    {
        if(value.Length>16000) throw new ArgumentException("한 번에 보낼 명령은 16,000자 이내여야 합니다.");
        var normalized=value.Replace("\r\n","\n").Replace('\r','\n').TrimEnd('\n').Replace('\n','\r');
        return normalized+(execute?"\r":"");
    }
}
