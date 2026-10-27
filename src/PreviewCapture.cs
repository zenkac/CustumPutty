using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

/// <summary>
/// Renders this application's loopback-test workspace, including the native PuTTY
/// children that WPF's RenderTargetBitmap cannot render. This never captures the
/// desktop, enumerates other windows, or reads pixels from a screen DC.
/// </summary>
internal static class PreviewCapture
{
    public static void Save(MainWindow window,string path)
    {
        if(!App.IsTesting)throw new InvalidOperationException("Workspace preview is available only in an explicit test run.");
        window.Dispatcher.VerifyAccess();
        if(window.Content is not FrameworkElement content)throw new InvalidOperationException("The workspace has no content to render.");
        window.UpdateLayout();content.UpdateLayout();
        var dpi=VisualTreeHelper.GetDpi(content);
        int width=(int)Math.Ceiling(content.ActualWidth*dpi.DpiScaleX),height=(int)Math.Ceiling(content.ActualHeight*dpi.DpiScaleY);
        if(width<=0||height<=0||width>16384||height>16384)throw new InvalidOperationException("The workspace must have a valid arranged size before capture.");
        var panes=window.VisiblePanesForTest.Where(p=>p.IsVisible&&p.Host.IsVisible&&p.Host.Embedded).ToArray();
        var owner=new WindowInteropHelper(window).Handle;
        foreach(var pane in panes)
        {
            if(!IPAddress.TryParse(pane.Equipment.Host,out var address)||!IPAddress.IsLoopback(address))
                throw new InvalidOperationException("Native preview capture accepts only loopback test sessions.");
            Native.GetWindowThreadProcessId(pane.Host.TerminalHandle,out uint process);
            if(owner==IntPtr.Zero||!Native.IsChild(owner,pane.Host.TerminalHandle)||pane.Host.ProcessId is not int expected||process!=(uint)expected)
                throw new InvalidOperationException("The terminal is not an owned child of this test workspace.");
        }

        var shell=new RenderTargetBitmap(width,height,dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        shell.Render(content);
        var scene=new DrawingVisual();
        using(var drawing=scene.RenderOpen())
        {
            drawing.DrawImage(shell,new Rect(0,0,content.ActualWidth,content.ActualHeight));
            drawing.PushClip(new RectangleGeometry(new Rect(0,0,content.ActualWidth,content.ActualHeight)));
            foreach(var pane in panes)
            {
                var host=pane.Host;
                var image=RenderOwnedClient(host.TerminalHandle);
                var location=host.TranslatePoint(new Point(0,0),content);
                location=new Point(Math.Round(location.X*dpi.DpiScaleX)/dpi.DpiScaleX,Math.Round(location.Y*dpi.DpiScaleY)/dpi.DpiScaleY);
                drawing.PushClip(new RectangleGeometry(new Rect(location,new Size(host.ActualWidth,host.ActualHeight))));
                drawing.DrawImage(image,new Rect(location,new Size(image.PixelWidth/dpi.DpiScaleX,image.PixelHeight/dpi.DpiScaleY)));
                drawing.Pop();
            }
            drawing.Pop();
        }
        var result=new RenderTargetBitmap(width,height,dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        result.Render(scene);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(result));
        string fullPath=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var output=File.Create(fullPath);encoder.Save(output);
    }

    private static BitmapSource RenderOwnedClient(IntPtr window)
    {
        if(!Native.GetClientRect(window,out var bounds))throw new Win32Exception(Marshal.GetLastWin32Error());
        int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
        if(width<=0||height<=0)throw new InvalidOperationException("The native terminal has an empty client area.");
        IntPtr source=IntPtr.Zero,context=IntPtr.Zero,bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try
        {
            // Compatibility information comes from this owned window; no pixels
            // are copied from its DC. PuTTY draws directly into our DIB instead.
            source=CaptureNative.GetDC(window);
            if(source==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            context=CaptureNative.CreateCompatibleDC(source);
            if(context==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            var info=new CaptureNative.BITMAPINFO
            {
                Header=new CaptureNative.BITMAPINFOHEADER
                {
                    Size=(uint)Marshal.SizeOf<CaptureNative.BITMAPINFOHEADER>(),Width=width,Height=-height,
                    Planes=1,BitCount=32,Compression=0,SizeImage=checked((uint)(width*height*4))
                }
            };
            bitmap=CaptureNative.CreateDIBSection(source,ref info,0,out var bits,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero||bits==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            previous=CaptureNative.SelectObject(context,bitmap);
            if(previous==IntPtr.Zero||previous==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!CaptureNative.PrintWindow(window,context,1))throw new InvalidOperationException("PuTTY did not render its client area for the test preview.");
            CaptureNative.GdiFlush();
            int stride=checked(width*4);var pixels=new byte[checked(stride*height)];Marshal.Copy(bits,pixels,0,pixels.Length);
            // GDI does not promise alpha-channel contents, so use opaque Bgr32.
            var result=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,stride);
            result.Freeze();return result;
        }
        finally
        {
            if(context!=IntPtr.Zero&&previous!=IntPtr.Zero&&previous!=new IntPtr(-1))CaptureNative.SelectObject(context,previous);
            if(bitmap!=IntPtr.Zero)Native.DeleteObject(bitmap);
            if(context!=IntPtr.Zero)CaptureNative.DeleteDC(context);
            if(source!=IntPtr.Zero)CaptureNative.ReleaseDC(window,source);
        }
    }

    private static class CaptureNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFOHEADER
        {
            public uint Size;
            public int Width,Height;
            public ushort Planes,BitCount;
            public uint Compression,SizeImage;
            public int XPelsPerMeter,YPelsPerMeter;
            public uint ClrUsed,ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Colors; }
        [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window,IntPtr context);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateCompatibleDC(IntPtr context);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr context);
        [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateDIBSection(IntPtr context,ref BITMAPINFO info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr SelectObject(IntPtr context,IntPtr value);
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool PrintWindow(IntPtr window,IntPtr context,uint flags);
    }
}
