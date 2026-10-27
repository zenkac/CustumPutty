using System.Windows;
using System.Windows.Controls;

namespace EquipLink;

public static class SftpDialogs
{
    public static SftpLogin? Login(Window owner,Equipment original)
    {
        var dialog=new FormDialog(owner,"SSH Browser · SFTP 연결",610,610);
        dialog.Body.Children.Add(Ui.Text(original.Name+" · "+original.Host+":"+original.Port,13,"#5DD6BE",true));
        var user=Ui.Input(original.Username);user.Name="SftpUsername";
        var password=new PasswordBox{Name="SftpPassword",MaxLength=CredentialProtector.MaxSecretLength,MinHeight=32};
        var path=Ui.Input(original.SftpInitialPath);path.Name="SftpInitialPath";
        var remember=new CheckBox{Name="SftpRemember",Content="설비 목록에 ID·비밀번호 저장",IsChecked=original.HasSavedPassword};
        dialog.Body.Children.Add(Ui.Columns(Ui.Field("로그인 ID",user),Ui.Field("비밀번호",password)));
        dialog.Body.Children.Add(Ui.Text(original.HasSavedPassword?"비워 두면 이 설비에 저장된 비밀번호를 사용합니다.":"현재 SSH 서버의 로그인 정보를 입력하세요.",11,"#8797AA"));
        dialog.Body.Children.Add(remember);
        var useKey=new CheckBox{Content="설비에 설정한 개인 키 사용",IsChecked=original.PrivateKeyPath.Length>0,IsEnabled=original.PrivateKeyPath.Length>0};
        var phrase=new PasswordBox{Name="SftpKeyPassphrase",MaxLength=CredentialProtector.MaxSecretLength,MinHeight=32};
        if(original.PrivateKeyPath.Length>0)
        {
            dialog.Body.Children.Add(useKey);dialog.Body.Children.Add(Ui.Text(original.PrivateKeyPath,11,"#8797AA"));
            dialog.Body.Children.Add(Ui.Field("개인 키 암호 (저장하지 않음)",phrase));
        }
        dialog.Body.Children.Add(Ui.Field("시작 경로",path,"비워 두면 로그인 계정의 홈 폴더를 엽니다."));
        dialog.Body.Children.Add(Ui.Text("SFTP 탐색 경로와 터미널 작업 경로는 각각 유지됩니다. SSH 서버에서 SFTP를 허용해야 합니다.",11,"#8797AA"));
        SftpLogin? result=null;dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        var connect=Ui.Button("연결",()=>
        {
            try
            {
                var target=original.Copy();target.Username=user.Text.Trim();target.Validate();
                if(target.Username.Length==0)throw new ArgumentException("로그인 ID를 입력하세요.");
                if(useKey.IsChecked!=true)target.PrivateKeyPath="";
                SftpSession.ValidatePath(path.Text,true);string secret=password.Password;
                if(secret.Length==0&&original.HasSavedPassword)
                {
                    if(!CredentialProtector.MatchesTarget(target,original))throw new ArgumentException("ID를 변경했습니다. 비밀번호를 다시 입력하세요.");
                    secret=CredentialProtector.Unprotect(original,original.EncryptedPassword!);
                }
                CredentialProtector.ValidateSecret(secret);
                result=new(){Target=target,Password=secret,KeyPassphrase=phrase.Password,InitialPath=path.Text,Remember=remember.IsChecked==true};
                dialog.DialogResult=true;
            }
            catch(Exception error){Ui.Error(error);}
        },true);connect.Name="SftpConnectSubmit";dialog.Actions.Children.Add(connect);
        dialog.Closed+=(_,_)=>{password.Clear();phrase.Clear();};
        return dialog.ShowDialog()==true?result:null;
    }
    public static bool ConfirmHost(Window owner,SftpHostKey key)
    {
        var dialog=new FormDialog(owner,"SFTP 서버 키 확인",650,460);
        dialog.Body.Children.Add(Ui.Text(key.Host+":"+key.Port,16,"#5DD6BE",true));
        dialog.Body.Children.Add(Ui.Text("처음 연결하는 서버입니다. 서버 관리자가 제공한 지문과 일치하는지 확인하세요.",13,"#DCE5F0"));
        dialog.Body.Children.Add(Ui.Field(key.Algorithm, new TextBox{Text=key.Fingerprint,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)}));
        dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        var accept=Ui.Button("확인 · 이 서버 키 저장",()=>dialog.DialogResult=true,true);accept.Name="SftpTrustHost";dialog.Actions.Children.Add(accept);
        return dialog.ShowDialog()==true;
    }
    public static string? Authentication(Window owner,string request,bool echo)
    {
        var dialog=new FormDialog(owner,"SFTP 추가 인증",580,440);
        var text=Ui.Input();var secret=new PasswordBox{MaxLength=1024,MinHeight=32};
        dialog.Body.Children.Add(Ui.Field(request,echo?text:secret));string? result=null;
        dialog.Actions.Children.Add(Ui.Button("취소",()=>dialog.DialogResult=false));
        dialog.Actions.Children.Add(Ui.Button("확인",()=>{result=echo?text.Text:secret.Password;dialog.DialogResult=true;},true));
        dialog.Closed+=(_,_)=>secret.Clear();return dialog.ShowDialog()==true?result:null;
    }
}
