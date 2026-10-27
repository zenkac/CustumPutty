using System.Text;

namespace EquipLink;

internal static class FtpEncoding
{
    internal static string Normalize(string? name)
    {
        name=(name??"Auto").Trim();
        if(name.Length==0||name.Equals("Auto",StringComparison.OrdinalIgnoreCase))return "Auto";
        var encoding=Get(name);
        return Label(encoding);
    }
    internal static Encoding Get(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try
        {
            if(name.Equals("CP949",StringComparison.OrdinalIgnoreCase)||name.Equals("EUC-KR",StringComparison.OrdinalIgnoreCase))name="949";
            encoding=int.TryParse(name,out int code)
                ?Encoding.GetEncoding(code,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback)
                :Encoding.GetEncoding(name,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            if(encoding.CodePage==65001)encoding=new UTF8Encoding(false,true);
            // FTP framing and commands must retain the ASCII byte values.
            string ascii=new(Enumerable.Range(9,119).Select(i=>(char)i).ToArray());
            if(!encoding.GetBytes(ascii).SequenceEqual(Enumerable.Range(9,119).Select(i=>(byte)i)))throw new ArgumentException();
        }
        catch(ArgumentException){throw new ArgumentException("지원하는 문자셋 이름을 입력하세요. 예: CP949, UTF-8, windows-1252, shift_jis. UTF-16/32는 FTP 파일명에 사용할 수 없습니다.");}
        return encoding;
    }
    internal static string Label(Encoding encoding)=>encoding.CodePage switch{65001=>"UTF-8",949=>"CP949",28591=>"ISO-8859-1",_=>encoding.WebName};
}

/// <summary>Keep readable entries without concealing that a listing was incomplete.</summary>
public sealed class FtpListing(IReadOnlyList<FtpEntry> entries,int omittedCount=0):IReadOnlyList<FtpEntry>
{
    public int OmittedCount { get; }=omittedCount;
    public string Notice=>OmittedCount==0?"":$"표시할 수 없는 목록 항목 {OmittedCount}개를 제외했습니다. 정상 항목은 계속 이용할 수 있습니다.";
    public int Count=>entries.Count;
    public FtpEntry this[int index]=>entries[index];
    public IEnumerator<FtpEntry> GetEnumerator()=>entries.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    internal static IReadOnlyList<FtpEntry> RequireComplete(IReadOnlyList<FtpEntry> listing)
    {
        if(listing is FtpListing{OmittedCount:>0})throw new System.IO.InvalidDataException("목록 일부를 해석하지 못해 기존 파일의 중복 여부를 확인할 수 없습니다. 문자셋을 확인한 뒤 다시 업로드해 주세요.");
        return listing;
    }
}
