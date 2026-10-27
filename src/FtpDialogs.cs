using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

// Password exists only for the connection attempt; it is never part of a site's
// serialized model or the transfer/history display.
public sealed class FtpConnectionRequest(FtpSite site,string password)
{
    public FtpSite Site { get; }=site;
    public string Password { get; }=password;
    public override string ToString()=>Site.Name;
}

public static class FtpDialogs
{
    public static FtpConnectionRequest? EditSite(Window owner,FtpSite original,bool connecting=false)
    {
        var site=original.Copy();var dialog=new FormDialog(owner,connecting?"FTP 접속":"FTP 사이트 설정",660,660);
        var name=Ui.Input(site.Name);name.Name="FtpSiteName";
        var host=Ui.Input(site.Host);host.Name="FtpHost";
        var port=Ui.Input(site.Port.ToString());port.Name="FtpPort";
        var username=Ui.Input(site.Username);username.Name="FtpUsername";
        var password=new PasswordBox{Name="FtpPassword",MaxLength=CredentialProtector.MaxSecretLength,MinHeight=32};
        var initial=Ui.Input(site.InitialPath);initial.Name="FtpInitialPath";
        var modes=new[]{"자동 감지 (권장)","UTF-8로 강제 설정","문자셋 직접 설정"};
        var encoding=Ui.Select(modes,site.Encoding=="Auto"?modes[0]:site.Encoding=="UTF-8"?modes[1]:modes[2]);encoding.Name="FtpCharsetMode";
        var custom=Ui.Input(site.Encoding is "Auto" or "UTF-8"?"CP949":site.Encoding);custom.Name="FtpCustomCharset";custom.IsEnabled=encoding.SelectedIndex==2;
        encoding.SelectionChanged+=(_,_)=>custom.IsEnabled=encoding.SelectedIndex==2;
        var remember=new CheckBox{Name="FtpRememberPassword",Content="이 Windows 계정에 비밀번호 저장",IsChecked=true};
        bool existing=!string.IsNullOrEmpty(original.EncryptedPassword);
        dialog.Body.Children.Add(Ui.Field("사이트 이름",name));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("서버 IP / 호스트",host),Ui.Field("포트",port),4,1));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("로그인 ID",username),Ui.Field("비밀번호",password)));
        dialog.Body.Children.Add(remember);
        var hint=Ui.Text(existing?"비밀번호가 저장되어 있습니다. 비워 두면 기존 값을 사용합니다.":"최근 접속 정보와 사이트의 비밀번호는 Windows 계정에 묶어 암호화합니다.",11,"#8797AA");hint.Margin=new Thickness(0,0,0,14);dialog.Body.Children.Add(hint);
        dialog.Body.Children.Add(Ui.Field("기본 경로",initial,"연결할 때 열 폴더입니다. 예: /home/test"));
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("파일명 문자셋",encoding),Ui.Field("직접 지정",custom,"예: CP949, windows-1252, shift_jis"),3,2));
        dialog.Body.Children.Add(Ui.Text("일반 FTP (Use plain FTP) · 기본 포트 21 · 패시브 모드",11,"#8797AA"));
        FtpConnectionRequest? result=null;
        dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        var submit=Ui.Button(connecting?"연결":"사이트 저장",()=>
        {
            try
            {
                if(!int.TryParse(port.Text,out int parsed))throw new ArgumentException("포트에 숫자를 입력해 주세요.");
                site.Name=name.Text;site.Host=host.Text;site.Port=parsed;site.Username=username.Text.Trim();site.InitialPath=initial.Text;site.Encoding=encoding.SelectedIndex switch{0=>"Auto",1=>"UTF-8",_=>custom.Text};site.Validate();
                string secret=password.Password;
                try
                {
                    if(secret.Length==0&&existing&&(connecting||remember.IsChecked==true))
                    {
                        if(!CredentialProtector.MatchesTarget(original.CredentialTarget(),site.CredentialTarget()))throw new ArgumentException("IP, 포트 또는 ID를 변경했습니다. 비밀번호를 다시 입력해 주세요.");
                        if(connecting)secret=CredentialProtector.Unprotect(original.CredentialTarget(),original.EncryptedPassword!);
                    }
                    CredentialProtector.ValidateSecret(secret);
                    if(remember.IsChecked==true)
                    {
                        if(password.Password.Length==0&&existing)site.EncryptedPassword=original.EncryptedPassword;
                        else site.EncryptedPassword=CredentialProtector.Protect(site.CredentialTarget(),secret);
                    }
                    else site.EncryptedPassword=null;
                    result=new(site,connecting?secret:"");password.Clear();dialog.DialogResult=true;
                }
                finally{secret="";}
            }
            catch(Exception error){Ui.Error(error);}
        },true);submit.Name="FtpSubmit";dialog.Actions.Children.Add(submit);
        dialog.Closed+=(_,_)=>password.Clear();
        return dialog.ShowDialog()==true?result:null;
    }

    public static string? NewFolder(Window owner,string location)
    {
        var dialog=new FormDialog(owner,"새 폴더",560,440);var name=Ui.Input();name.Name="FtpNewFolderName";
        dialog.Body.Children.Add(Ui.Field("폴더 이름",name));dialog.Body.Children.Add(Ui.Text(location,11,"#8797AA"));
        string? result=null;dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        dialog.Actions.Children.Add(Ui.Button("만들기",()=>
        {
            try
            {
                var value=name.Text.Trim();
                if(value.Length==0||value is "." or ".."||value.Any(c=>char.IsControl(c)||c is '/' or '\\'))throw new ArgumentException("슬래시와 제어 문자가 없는 폴더 이름을 입력해 주세요.");
                result=value;dialog.DialogResult=true;
            }
            catch(Exception error){Ui.Error(error);}
        },true));
        return dialog.ShowDialog()==true?result:null;
    }
}
