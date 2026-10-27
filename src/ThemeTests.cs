using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EquipLink;

/// <summary>Theme persistence, real controls and owned loopback PuTTY rendering.</summary>
public static class ThemeTests
{
    public static async Task RunAsync()
    {
        if(!App.IsTesting)throw new InvalidOperationException("Theme regression is only available in an explicit test run.");
        Directory.CreateDirectory(AppPaths.Root);
        var report=new StringBuilder();string originalTheme=ThemeService.Current.Id;
        (double Width,double Height)[]? baseline=null;
        void Check(string label,bool ok)
        {
            if(!ok)throw new InvalidOperationException("FAIL: "+label);
            report.AppendLine("PASS: "+label);
        }
        try
        {
            Check("Three unique built-in theme choices are available",ThemeService.All.Count==3 && ThemeService.All.Select(t=>t.Id).Distinct().Count()==3);
            Check("Unknown theme IDs safely use Midnight",ThemeService.NormalizeId("unknown-theme")=="midnight" && ThemeService.NormalizeId(null)=="midnight");
            Check("Legacy preferences without a theme keep Midnight",AppStore.Read("{\"Version\":1,\"Settings\":{}}").Settings.ThemeId=="midnight");
            Check("Invalid persisted theme values normalize to Midnight",AppStore.Read("{\"Version\":1,\"Settings\":{\"ThemeId\":\"removed-theme\"}}").Settings.ThemeId=="midnight");
            Check("Theme ID lookup ignores case",ThemeService.NormalizeId("NORD")=="nord");

            foreach(var theme in ThemeService.All)
            {
                ThemeService.Apply(theme.Id);
                var preferences=new Preferences{ThemeId=theme.Id,AutoStartX=false};
                var roundtrip=AppStore.Read(JsonSerializer.Serialize(new AppStore{Settings=preferences},AppStore.Json));
                Check(theme.Id+" preference survives JSON roundtrip",roundtrip.Settings.ThemeId==theme.Id);
                Check(theme.Id+" has readable body and secondary text",Contrast(theme.Resolve("#DCE5F0"),theme.Resolve("#1D2A38"))>=7 && Contrast(theme.Resolve("#8797AA"),theme.Resolve("#17212D"))>=4.5);
                Check(theme.Id+" accent controls retain readable labels",Contrast(theme.Resolve("#102D29"),theme.Resolve("#5DD6BE"))>=4.5);
                Check(theme.Id+" maps WPF application resources and programmatic brushes consistently",ThemeService.ResourceColors.All(p=>Color(Application.Current.Resources[p.Key] as Brush)==Canonical(theme.Resolve(p.Value))) && Color(Ui.Brush("#111923"))==Canonical(theme.Resolve("#111923")));

                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                int port=((IPEndPoint)listener.LocalEndpoint).Port;
                var first=new Equipment{Name="LOCAL TESTER 01",Group="LOCAL SIMULATION",Host="127.0.0.1",Port=port,UseX11=false,Favorite=true};
                var second=new Equipment{Name="LOCAL TESTER 02",Group="LOCAL SIMULATION",Host="127.0.0.1",Port=port,UseX11=false};
                var data=new AppStore{Settings=preferences,Equipment=[first,second]};
                string? prepared=null;
                try
                {
                    prepared=PuttyService.Prepare(first,preferences,out _);
                    using var key=Registry.CurrentUser.OpenSubKey(PuttyService.SessionsKey+"\\"+prepared);
                    Check(theme.Id+" writes all 22 expected PuTTY registry colors",key!=null && theme.PuttyColors.Count==22 && Enumerable.Range(0,22).All(index=>key.GetValue("Colour"+index) as string==theme.PuttyColors[index]));
                    Check(theme.Id+" PuTTY background matches the workspace canvas",theme.PuttyColors[2]==Rgb(theme.Resolve("#111923")) && theme.PuttyColors[3]==theme.PuttyColors[2]);
                }
                finally{PuttyService.Cleanup(prepared);}

                var window=new MainWindow(data)
                {
                    Width=1440,Height=940,WindowStartupLocation=WindowStartupLocation.Manual,
                    Left=-30000,Top=-30000,ShowActivated=false,ShowInTaskbar=false
                };
                var fixtures=new List<Fixture>();
                try
                {
                    window.Show();
                    foreach(var item in data.Equipment.ToArray())
                    {
                        var accept=listener.AcceptTcpClientAsync();
                        var pane=await window.ConnectForTestAsync(item);
                        var client=await accept.WaitAsync(TimeSpan.FromSeconds(5));
                        var fixture=new Fixture(pane,client);fixtures.Add(fixture);
                        await fixture.InitializeAsync(theme.Name,fixtures.Count);
                    }
                    window.SetLayoutForTest("columns");
                    await Settle(window);
                    Check(theme.Id+" uses two independently embedded native PuTTY processes",fixtures.Count==2 && fixtures.All(f=>f.Pane.Host.Embedded && f.Pane.Host.HasProcess) && fixtures.Select(f=>f.Pane.Host.ProcessId).Distinct().Count()==2);
                    Check(theme.Id+" retains 224px sidebar and 31px pane title rows",Math.Abs(window.SidebarWidthForTest-224)<.1 && fixtures.All(f=>Math.Abs(f.Pane.RowDefinitions[0].ActualHeight-31)<.1));
                    var geometry=window.VisiblePanesForTest.Select(p=>(Width:p.Host.ActualWidth,Height:p.Host.ActualHeight)).ToArray();
                    Check(theme.Id+" native panes keep substantial working area",geometry.Length==2 && geometry.All(p=>p.Width>400 && p.Height>700));
                    if(baseline==null)baseline=geometry;
                    else Check(theme.Id+" terminal dimensions equal Midnight",geometry.Length==baseline.Length && geometry.Select((size,index)=>Math.Abs(size.Width-baseline[index].Width)<1 && Math.Abs(size.Height-baseline[index].Height)<1).All(equal=>equal));
                    Check(theme.Id+" actual window surface matches its terminal background",Color(window.Background)==Canonical(theme.Resolve("#111923")) && fixtures.All(f=>Color(f.Pane.Background)==Canonical(theme.Resolve("#111923"))));
                    for(int index=0;index<fixtures.Count;index++)await fixtures[index].ExpectInputAsync("theme_"+theme.Id+"_"+index);
                    Check(theme.Id+" both local terminal sockets exchange synthetic input",true);
                    PreviewCapture.Save(window,Path.Combine(AppPaths.Root,"theme-"+theme.Id+".png"));
                    Check(theme.Id+" native terminal preview was captured",File.Exists(Path.Combine(AppPaths.Root,"theme-"+theme.Id+".png")));

                    string next=ThemeService.All[(ThemeService.All.ToList().IndexOf(theme)+1)%ThemeService.All.Count].Id;
                    var saved=RunDialog(()=>Dialogs.Settings(window,preferences),dialog=>
                    {
                        Check(theme.Id+" actual settings window uses the selected dark palette",Color(dialog.Background)==Canonical(theme.Resolve("#111923")) && Color(dialog.Foreground)==Canonical(theme.Resolve("#DCE5F0")));
                        var inputs=Descendants(dialog).OfType<TextBox>().ToArray();
                        Check(theme.Id+" actual settings inputs remain readable",inputs.Length>0 && inputs.All(input=>Color(input.Background)==Canonical(theme.Resolve("#111923")) && Color(input.Foreground)==Canonical(theme.Resolve("#DCE5F0"))));
                        var selector=Find<ComboBox>(dialog,"ThemeSelector");
                        Check(theme.Id+" settings selector shows the persisted choice",selector.SelectedValue as string==theme.Id && selector.Items.Count==3);
                        CaptureDialog(dialog,"settings-"+theme.Id+".png");
                        selector.SelectedValue=next;
                        Find<Button>(dialog,"SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    Check(theme.Id+" actual settings Save returns the selected theme",saved?.ThemeId==next && preferences.ThemeId==theme.Id);
                    Check(theme.Id+" settings save keeps the running workspace palette until reopening",ThemeService.Current.Id==theme.Id);
                    var savedData=new AppStore{Settings=saved!};savedData.Save();
                    Check(theme.Id+" selected theme persists in the app settings file",AppStore.Load().Settings.ThemeId==next);
                    await Settle(window);
                    Check(theme.Id+" settings dialog preserves both live terminal processes",fixtures.All(f=>f.Pane.Host.Embedded && f.Pane.Host.HasProcess));
                    report.AppendLine($"GEOMETRY: {theme.Id}: "+string.Join(" / ",geometry.Select(p=>$"{p.Width:F0} x {p.Height:F0} DIP")));
                }
                finally
                {
                    FormDialog.TestLoaded=null;
                    foreach(var fixture in fixtures)fixture.Pane.Host.CloseSession();
                    window.Close();foreach(var fixture in fixtures)fixture.Dispose();listener.Stop();
                }
            }
            report.AppendLine("All theme checks passed. Native previews use owned loopback fixtures with synthetic sample output only. No credentials or real equipment connections were used.");
            File.WriteAllText(Path.Combine(AppPaths.Root,"theme-test-report.txt"),report.ToString());
        }
        catch(Exception exception)
        {
            File.WriteAllText(Path.Combine(AppPaths.Root,"theme-test-report.txt"),report+"\nFAIL: "+exception);throw;
        }
        finally{FormDialog.TestLoaded=null;ThemeService.Apply(originalTheme);}
    }

    private static async Task Settle(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        foreach(var pane in window.VisiblePanesForTest)pane.Host.RefreshViewport();
        await Task.Delay(220);
    }
    private static string Canonical(string value)=>((Color)ColorConverter.ConvertFromString(value)).ToString();
    private static string Color(Brush? brush)=>brush?.ToString()??"";
    private static string Rgb(string value)
    {
        var color=(Color)ColorConverter.ConvertFromString(value);return $"{color.R},{color.G},{color.B}";
    }
    private static double Contrast(string first,string second)
    {
        static double Luma(string value)
        {
            var color=(Color)ColorConverter.ConvertFromString(value);
            double Channel(byte n){double c=n/255.0;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
            return .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);
        }
        double a=Luma(first),b=Luma(second);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private static T RunDialog<T>(Func<T> open,Action<FormDialog> action)
    {
        Exception? failure=null;bool loaded=false;var previous=FormDialog.TestLoaded;
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
            if(!loaded)throw new InvalidOperationException("Settings dialog did not load.");
            return result;
        }
        finally{FormDialog.TestLoaded=previous;}
    }
    private static T Find<T>(DependencyObject root,string name) where T:FrameworkElement=>Descendants(root).OfType<T>().Single(control=>control.Name==name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            foreach(var child in Descendants(VisualTreeHelper.GetChild(root,index)))yield return child;
    }
    private static void CaptureDialog(FormDialog dialog,string filename)
    {
        dialog.UpdateLayout();var content=(FrameworkElement)dialog.Content;
        int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right);
        int height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(AppPaths.Root,filename));encoder.Save(output);
    }
    private sealed class Fixture(SessionPane pane,TcpClient client):IDisposable
    {
        public SessionPane Pane { get; }=pane;
        private readonly NetworkStream stream=client.GetStream();
        public async Task InitializeAsync(string theme,int number)
        {
            await stream.WriteAsync(new byte[]{255,251,1,255,251,3,255,253,31});
            string screen="\u001b[2J\u001b[H\u001b[1;36mEQUIPLINK / LOCAL SIMULATION\u001b[0m\r\n"+
                "Theme preview - loopback fixture only\r\n"+
                "No equipment contacted / sample output\r\n\r\n"+
                "\u001b[32mfixture$\u001b[0m inspect --sample\r\n\r\n"+
                $"  Station       LOCAL TESTER {number:00}\r\n"+
                "  Host          127.0.0.1\r\n"+
                "  Transport     Telnet / embedded PuTTY\r\n"+
                "  Environment   SAMPLE ONLY\r\n\r\n"+
                "\u001b[1;34m  WORKSPACE\u001b[0m\r\n"+
                "  Independent sessions   2\r\n"+
                "  Layout                 Side by side\r\n"+
                "  Status                 Sample ready\r\n\r\n"+
                "\u001b[33m  SAMPLE CHECKS\u001b[0m\r\n"+
                "  [01] sensor             sample ready\r\n"+
                "  [02] display            sample idle\r\n"+
                "  [03] controller         sample ready\r\n\r\n"+
                "\u001b[35m  ANSI palette\u001b[0m\r\n"+
                "  \u001b[31mred\u001b[0m  \u001b[32mgreen\u001b[0m  \u001b[33myellow\u001b[0m  \u001b[34mblue\u001b[0m  \u001b[35mmagenta\u001b[0m  \u001b[36mcyan\u001b[0m\r\n\r\n"+
                "\u001b[32mfixture$\u001b[0m ";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(screen));await Task.Delay(150);
        }
        public async Task ExpectInputAsync(string marker)
        {
            Pane.Host.Send(marker,false);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received=new StringBuilder();var buffer=new byte[2048];
            while(!received.ToString().Contains(marker,StringComparison.Ordinal))
            {
                int count=await stream.ReadAsync(buffer,timeout.Token);if(count==0)throw new IOException("Theme loopback fixture disconnected.");
                received.Append(Encoding.ASCII.GetString(buffer,0,count));
            }
        }
        public void Dispose()=>client.Dispose();
    }
}
