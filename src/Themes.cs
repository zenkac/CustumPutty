using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EquipLink;

/// <summary>A small, asset-free palette shared by WPF chrome and embedded PuTTY.</summary>
public sealed class ThemeDefinition
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    private readonly IReadOnlyDictionary<string,string> palette;
    /// <summary>PuTTY registry Colour0..Colour21 values, in r,g,b form.</summary>
    public IReadOnlyList<string> PuttyColors { get; }
    internal ThemeDefinition(string id,string name,string description,IReadOnlyDictionary<string,string> colors,string[] terminal)
    {
        Id=id;Name=name;Description=description;palette=colors;
        PuttyColors=Array.AsReadOnly(terminal.Select(Rgb).ToArray());
    }
    public string Resolve(string canonicalHex)=>palette.TryGetValue(canonicalHex,out var result)?result:canonicalHex;
    public override string ToString()=>Name;
    private static string Rgb(string hex)
    {
        var value=uint.Parse(hex.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        return $"{(value>>16)&255},{(value>>8)&255},{value&255}";
    }
}

public static class ThemeService
{
    // Existing canonical colors are deliberately preserved for Midnight, so old
    // settings keep their appearance. All new palettes cover the same UI roles.
    private static readonly (string Canonical,string Graphite,string Nord)[] Colors=
    [
        ("#111923","#191A1D","#202A39"), // terminal and window canvas
        ("#141E29","#1D1E22","#243043"), // tab rail
        ("#17212D","#212226","#283548"), // panels
        ("#1B2936","#26282D","#2E3D51"), // visible tab
        ("#1D2A38","#292B31","#304157"), // raised surface
        ("#2A394A","#3C3D43","#42556C"), // divider
        ("#273747","#37383E","#40536A"), // split handle
        ("#203039","#34302A","#334B61"), // active pane header
        ("#22353E","#3C3429","#36526D"), // selected tab
        ("#25413F","#443827","#365671"), // selected layout
        ("#4A8D81","#9F7C44","#6C9DC6"),
        ("#DCE5F0","#E6E3DD","#E1EAF5"), // primary text
        ("#ECF3FC","#F4F0E9","#F1F6FC"),
        ("#E6EFFB","#EFECE6","#EBF2FA"),
        ("#BDCCDD","#C9C4BC","#C5D3E3"),
        ("#AAB8C8","#BEB9B1","#B8C9DD"),
        ("#A8BAD0","#BDB7AF","#B5C7DC"),
        ("#A8B7C9","#BBB6AE","#B5C6DB"),
        ("#98ACC4","#B0AAA3","#ABBDD3"),
        ("#8EA6C1","#AAA49C","#A6B9D0"),
        ("#8DA6C2","#AAA49C","#A6B9D0"),
        ("#8797AA","#A49F98","#9AACBF"), // secondary text
        ("#687B91","#8E8983","#8196AE"), // placeholder
        ("#5DD6BE","#E7B971","#86BDE8"), // accent
        ("#69DDC0","#B6D59A","#A1D3BA"), // healthy status
        ("#B8A6EE","#C7AFDE","#C1AFEB"), // SSH tag
        ("#E9C786","#E8C37F","#E3C68F"),
        ("#D2B479","#D6B67E","#D5BB87"),
        ("#102D29","#2D2415","#172A3D"), // text on accent
        ("#536577","#6D6860","#637C97"), // hover border
        ("#415367","#59564F","#546D89"),
        ("#35685F","#705B39","#466F92"), // text selection
        ("#2A3C4E","#3A3938","#3C526B"), // menu hover
        ("#25423F","#4A3D2A","#385D7B"), // selected dropdown
        ("#20303F","#303136","#34485F"), // list hover
        ("#203A3C","#3D352A","#324F6A"), // selected equipment
        ("#315851","#715B3A","#4E7B9F"),
        ("#263545","#33353A","#374D66"), // tooltip
        ("#3B4B5D","#505057","#536A84"), // scrollbar
        ("#596E84","#6B6864","#7089A5"),
        ("#71889E","#8A8174","#8FA6BE")
    ];
    internal static readonly IReadOnlyDictionary<string,string> ResourceColors=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>
    {
        ["Bg"]="#111923",["Panel"]="#17212D",["Card"]="#1D2A38",["Line"]="#2A394A",["Ink"]="#DCE5F0",["Muted"]="#8797AA",["Accent"]="#5DD6BE",
        ["AccentInk"]="#102D29",["HoverLine"]="#536577",["InputHover"]="#415367",["Selection"]="#35685F",["Hover"]="#2A3C4E",["Selected"]="#25423F",
        ["ListHover"]="#20303F",["ListSelected"]="#203A3C",["SelectedLine"]="#315851",["Tooltip"]="#263545",["Scrollbar"]="#3B4B5D",["ScrollbarHover"]="#596E84",["ScrollbarPressed"]="#71889E"
    });
    public static IReadOnlyList<ThemeDefinition> All { get; }=Array.AsReadOnly(new[]
    {
        Create("midnight","Midnight · 민트","깊은 네이비와 차분한 민트",0,
        ["#DCE5F0","#F4F8FD","#111923","#111923","#111923","#5DD6BE","#111923","#5E6E83","#E78494","#FFA4B3","#7ECFA1","#9FE7BC","#DEC087","#F7D8A1","#7DAEEA","#A1C9FF","#B89FE8","#D4B8FF","#6CCBD0","#94E7E7","#C9D6E6","#F4F8FD"]),
        Create("graphite","Graphite · 앰버","따뜻한 차콜과 부드러운 앰버",1,
        ["#E6E3DD","#FAF6EF","#191A1D","#191A1D","#191A1D","#E7B971","#191A1D","#787571","#E48C91","#FFAFB3","#A7CA8C","#C4E6AC","#DEC08A","#F5D7A3","#93B6D7","#B7D5F3","#C2A6D7","#DCC0F2","#8FC7C1","#AFE4DB","#D2CEC6","#FAF6EF"]),
        Create("nord","Nord · 블루","푸른 슬레이트와 맑은 아이스 블루",2,
        ["#E1EAF5","#F6F9FE","#202A39","#202A39","#202A39","#86BDE8","#202A39","#73869F","#E691A2","#FFB4C1","#A5D2AE","#C3E8CB","#E3C68F","#F5DFB2","#8FBDEA","#B8DAFF","#B8AFE6","#D5CDFF","#91CFD4","#B5E9ED","#D1DEED","#F6F9FE"])
    });
    public static ThemeDefinition Current { get; private set; }=All[0];
    private static readonly Dictionary<string,SolidColorBrush> brushes=new(StringComparer.OrdinalIgnoreCase);
    private static ThemeDefinition Create(string id,string name,string description,int index,string[] terminal)
    {
        var map=Colors.ToDictionary(c=>c.Canonical,c=>index==1?c.Graphite:index==2?c.Nord:c.Canonical,StringComparer.OrdinalIgnoreCase);
        return new(id,name,description,new ReadOnlyDictionary<string,string>(map),terminal);
    }
    public static string NormalizeId(string? id)=>All.FirstOrDefault(t=>string.Equals(t.Id,id,StringComparison.OrdinalIgnoreCase))?.Id??"midnight";
    public static ThemeDefinition Get(string? id)=>All.First(t=>t.Id==NormalizeId(id));
    public static string Resolve(string canonicalHex)=>Current.Resolve(canonicalHex);
    /// <summary>PuTTY Colour0..Colour21, as comma-separated RGB components.</summary>
    public static IReadOnlyList<string> PuttyColors=>Current.PuttyColors;
    /// <summary>Apply before constructing windows. Existing PuTTY sessions keep their palette.</summary>
    public static void Apply(string? id)
    {
        Current=Get(id);brushes.Clear();
        if(Application.Current is not Application app)return;
        foreach(var pair in ResourceColors)app.Resources[pair.Key]=Brush(pair.Value);
    }
    public static SolidColorBrush Brush(string canonicalHex)
    {
        string resolved=Resolve(canonicalHex);
        if(brushes.TryGetValue(resolved,out var brush))return brush;
        brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(resolved));brush.Freeze();brushes[resolved]=brush;return brush;
    }
    /// <summary>Win32 COLORREF (0x00BBGGRR) for the current theme.</summary>
    public static int ColorRef(string canonicalHex)
    {
        var color=Brush(canonicalHex).Color;return color.R|(color.G<<8)|(color.B<<16);
    }
}
