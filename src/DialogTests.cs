using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

/// <summary>Exercises the real modal editors, using synthetic credentials only.</summary>
public static class DialogTests
{
    public static void Run()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var report=new StringBuilder();
        void Check(string label,bool ok)
        {
            if(!ok)throw new InvalidOperationException("FAIL: "+label);
            report.AppendLine("PASS: "+label);
        }
        var owner=new MainWindow(AppStore.Demo(),true)
        {
            WindowStartupLocation=WindowStartupLocation.Manual,Left=-30000,Top=-30000,
            ShowActivated=false,ShowInTaskbar=false
        };
        try
        {
            owner.Show();
            var settings=RunDialog(()=>Dialogs.Settings(owner,new Preferences()),dialog=>
            {
                Check("Actual settings dialog has explicit dark Window style",dialog.Style!=null && Color(dialog.Background)=="#FF111923" && Color(dialog.Foreground)=="#FFDCE5F0");
                Check("Settings inputs and checkbox labels keep dark-theme contrast",Descendants(dialog).OfType<TextBox>().All(input=>Color(input.Background)=="#FF111923" && Color(input.Foreground)=="#FFDCE5F0") && Descendants(dialog).OfType<CheckBox>().All(input=>Color(input.Foreground)=="#FFDCE5F0"));
                Capture(dialog,"settings-dark.png");
                dialog.DialogResult=false;
            });
            Check("Cancelling actual settings dialog returns no changes",settings==null);

            const string syntheticPassword="Fixture-Only! 2026";
            var original=new Equipment{Name="검증용 설비 · 실제 연결 없음",Group="샘플",Host="192.0.2.42",UseX11=true};
            var saved=RunDialog(()=>Dialogs.EquipmentEditor(owner,original),dialog=>
            {
                var username=Find<TextBox>(dialog,"EquipmentUsername");
                var password=Find<PasswordBox>(dialog,"EquipmentPassword");
                var remember=Find<CheckBox>(dialog,"RememberPassword");
                Check("New equipment accepts password input without a hidden enable step",password.Password.Length==0 && password.IsEnabled && remember.IsChecked==false);
                username.Text="operator.demo";password.Password=syntheticPassword;
                Check("Typing a password enables saving and automatic login",remember.IsChecked==true && Find<CheckBox>(dialog,"AutomaticLogin").IsChecked==true);
                dialog.UpdateLayout();
                Check("Password input uses masking and dark colors",password.IsEnabled && password.PasswordChar=='●' && Color(password.Background)=="#FF111923" && Color(password.Foreground)=="#FFDCE5F0" && password.Template.FindName("PART_ContentHost",password)!=null);
                Check("Equipment editor and dropdowns match workspace background",Color(dialog.Background)=="#FF111923" && Descendants(dialog).OfType<ComboBox>().All(input=>Color(input.Background)=="#FF111923"));
                Capture(dialog,"equipment-credentials-dark.png");
                Save(dialog);
            });
            Check("Actual Save button returns saved ID and protected password",saved!=null && saved.Username=="operator.demo" && saved.HasSavedPassword && saved.EncryptedPassword!=syntheticPassword && CredentialProtector.Unprotect(saved,saved.EncryptedPassword!)==syntheticPassword);
            Check("Saving the editor does not mutate the original equipment object",original.Username=="" && !original.HasSavedPassword);
            if(saved==null)throw new InvalidOperationException("Saved equipment fixture is unavailable.");
            var cipher=saved.EncryptedPassword;
            var cancelled=RunDialog(()=>Dialogs.EquipmentEditor(owner,saved),dialog=>
            {
                Find<TextBox>(dialog,"EquipmentUsername").Text="cancelled.demo";
                Find<PasswordBox>(dialog,"EquipmentPassword").Password="Discarded fixture input";
                dialog.DialogResult=false;
            });
            Check("Cancelling replacement credentials keeps original ID and protected password",cancelled==null && saved.Username=="operator.demo" && saved.EncryptedPassword==cipher);
            var preserved=RunDialog(()=>Dialogs.EquipmentEditor(owner,saved),dialog=>
            {
                Check("Saved password is never populated back into the password field",Find<PasswordBox>(dialog,"EquipmentPassword").Password.Length==0 && Find<CheckBox>(dialog,"RememberPassword").IsChecked==true);
                Check("Saved credential state is visible without decrypting into the input",Find<TextBlock>(dialog,"PasswordSavedPlaceholder").Visibility==Visibility.Visible && Find<TextBlock>(dialog,"EquipmentPasswordStatus").Text.StartsWith("저장됨"));
                Capture(dialog,"equipment-password-saved.png");
                Save(dialog);
            });
            Check("Saving existing credentials with an empty field preserves protected password",preserved!=null && preserved.EncryptedPassword==cipher && CredentialProtector.Unprotect(preserved,preserved.EncryptedPassword!)==syntheticPassword);
            var persisted=new AppStore{Equipment=[preserved!]};persisted.Save();var reloaded=AppStore.Load().Equipment.Single();
            var manual=RunDialog(()=>Dialogs.EquipmentEditor(owner,reloaded),dialog=>
            {
                Check("Reopening saved settings still shows the stored-password marker",Find<TextBlock>(dialog,"PasswordSavedPlaceholder").Visibility==Visibility.Visible);
                Find<CheckBox>(dialog,"AutomaticLogin").IsChecked=false;Save(dialog);
            });
            persisted.Equipment[0]=manual!;persisted.Save();
            Check("Automatic-login opt-out persists without removing the password",!AppStore.Load().Equipment.Single().AutoLogin&&CredentialProtector.Unprotect(manual!,manual!.EncryptedPassword!)==syntheticPassword);
            var cleared=RunDialog(()=>Dialogs.EquipmentEditor(owner,saved),dialog=>
            {
                var password=Find<PasswordBox>(dialog,"EquipmentPassword");
                password.Password="Discarded fixture input";
                Find<CheckBox>(dialog,"RememberPassword").IsChecked=false;
                Check("Disabling storage clears the field and disables automatic login selection",password.Password.Length==0 && !Find<CheckBox>(dialog,"AutomaticLogin").IsEnabled && Find<TextBlock>(dialog,"PasswordSavedPlaceholder").Visibility==Visibility.Collapsed);
                Save(dialog);
            });
            Check("Saving with password storage disabled removes the saved password",cleared!=null && !cleared.HasSavedPassword && cleared.Username==saved.Username);
            report.AppendLine("All actual modal dialog checks passed. Synthetic credentials only; no real equipment connections were made.");
            File.WriteAllText(Path.Combine(AppPaths.Root,"dialog-test-report.txt"),report.ToString());
        }
        finally
        {
            FormDialog.TestLoaded=null;
            owner.Close();
        }
    }

    private static T RunDialog<T>(Func<T> open,Action<FormDialog> action)
    {
        Exception? failure=null;bool loaded=false;
        var previous=FormDialog.TestLoaded;
        FormDialog.TestLoaded=dialog=>
        {
            try{loaded=true;dialog.UpdateLayout();action(dialog);}
            catch(Exception error){failure=error;}
            finally{if(dialog.IsVisible)dialog.DialogResult=false;}
        };
        try
        {
            T result=open();
            if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
            if(!loaded)throw new InvalidOperationException("The actual dialog did not reach its Loaded callback.");
            return result;
        }
        finally{FormDialog.TestLoaded=previous;}
    }
    private static void Save(FormDialog dialog)=>Find<Button>(dialog,"SaveEquipment").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Color(Brush brush)=>brush.ToString();
    private static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>Descendants(root).OfType<T>().Single(control=>control.Name==name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static void Capture(FormDialog dialog,string filename)
    {
        dialog.UpdateLayout();
        var content=(FrameworkElement)dialog.Content;
        // Window.ActualWidth/Height include the native frame. The content's margin
        // plus its layout size give the client dimensions, without blank frame pixels.
        int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right);
        int height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        if(width<=0 || height<=0)throw new InvalidOperationException("Dialog has no rendered client area.");
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(AppPaths.Root,filename));encoder.Save(output);
    }
}
