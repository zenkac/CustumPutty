using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

public partial class App : Application
{
    private Mutex? instanceMutex;
    public static bool IsTesting { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsTesting = e.Args.Any(a => a.StartsWith("--"));
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "errors.log"), DateTime.Now + " " + args.Exception + Environment.NewLine); } catch { }
            if (!IsTesting) MessageBox.Show("작업을 완료하지 못했습니다.\n" + args.Exception.Message, "EquipLink", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try
        {
            ThemeService.Apply("midnight");
            if(e.Args.Contains("--login-reload-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args[1]);
                var saved=AppStore.Load().Equipment.Single();
                Shutdown(saved.CanAutoLogin&&CredentialProtector.Unprotect(saved,saved.EncryptedPassword!)==LoginTests.ReloadSecret?0:1);return;
            }
            if(e.Args.Contains("--login-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"login-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await LoginTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-delete-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-delete-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await FtpDeletionTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-tools-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-tools-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await FtpFileToolsTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-explorer-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-explorer-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await FtpExplorerTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-drag-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-drag-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await FtpDragTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--sftp-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"sftp-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await SftpTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-ui-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-ui-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                await FtpUiTests.RunAsync();Shutdown(0);return;
            }
            if(e.Args.Contains("--ftp-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"ftp-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                File.WriteAllText(Path.Combine(AppPaths.Root,"ftp-test-report.txt"),(await FtpTests.RunAsync())+Environment.NewLine+(await FtpSessionTests.RunAsync())+Environment.NewLine+(await FtpCompatibilityTests.RunAsync()));
                Shutdown(0);return;
            }
            if (e.Args.Contains("--theme-test") || e.Args.Contains("--focus-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"regression-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                if(e.Args.Contains("--theme-test"))await ThemeTests.RunAsync();else await FocusTests.RunAsync();
                Shutdown(0);return;
            }
            if (e.Args.Contains("--dialog-test"))
            {
                AppPaths.OverrideRoot=Path.GetFullPath(e.Args.ElementAtOrDefault(1)??"dialog-test-data");
                Directory.CreateDirectory(AppPaths.Root);ShutdownMode=ShutdownMode.OnExplicitShutdown;
                DialogTests.Run();Shutdown(0);return;
            }
            if (e.Args.Contains("--self-test"))
            {
                AppPaths.OverrideRoot = Path.GetFullPath(e.Args.ElementAtOrDefault(1) ?? "self-test-data");
                var report = await SelfTest.RunAsync();
                File.WriteAllText(Path.Combine(AppPaths.Root, "self-test-report.txt"), report);
                Shutdown(0); return;
            }
            if (e.Args.Contains("--smoke-test"))
            {
                AppPaths.OverrideRoot = Path.GetFullPath(e.Args.ElementAtOrDefault(1) ?? "smoke-test-data");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await SelfTest.SmokeAsync();
                Shutdown(0); return;
            }
            if (e.Args.Contains("--preview"))
            {
                AppPaths.OverrideRoot = Path.GetFullPath(e.Args.ElementAtOrDefault(2) ?? "preview-data");
                ThemeService.Apply(e.Args.ElementAtOrDefault(3));
                var window = new MainWindow(AppStore.Demo(), true) { Width = 1440, Height = 940 };
                MainWindow = window;
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1440, 900));
                content.Arrange(new Rect(0, 0, 1440, 900));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.GetFullPath(e.Args.ElementAtOrDefault(1) ?? "preview.png")); encoder.Save(output);
                Shutdown(0); return;
            }
            var mutexId=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToUpperInvariant())))[..16];
            instanceMutex=new Mutex(true,@"Local\EquipLink-"+mutexId,out bool created);
            if(!created)
            {
                foreach(var process in System.Diagnostics.Process.GetProcessesByName("EquipLink"))
                    if(process.Id!=Environment.ProcessId && process.MainWindowHandle!=IntPtr.Zero){Native.ShowWindow(process.MainWindowHandle,9);Native.SetForegroundWindow(process.MainWindowHandle);break;}
                Shutdown(0);return;
            }
            var store = AppStore.Load();
            ThemeService.Apply(store.Settings.ThemeId);
            MainWindow = new MainWindow(store);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(Path.Combine(AppPaths.Root, "startup-error.txt"), exception.ToString()); } catch { }
            if (!IsTesting) MessageBox.Show("EquipLink를 시작하지 못했습니다.\n" + exception.Message, "EquipLink", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e){instanceMutex?.Dispose();base.OnExit(e);}
}



