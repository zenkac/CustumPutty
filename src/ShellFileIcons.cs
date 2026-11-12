using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EquipLink;

// Query only a synthetic extension with USEFILEATTRIBUTES. Never open, execute,
// or ask a thumbnail handler to inspect a local or remote user file.
internal static class ShellFileIcons
{
    private static readonly ConcurrentDictionary<string,Lazy<Task<ImageSource?>>> cache=new();
    private static string Extension(string name)
    {
        string extension=Path.GetExtension(name).ToLowerInvariant();
        return extension.Length<=16&&extension.All(c=>char.IsLetterOrDigit(c)||c=='.')?extension:"";
    }
    public static string TypeName(string name,bool folder)=>folder?"파일 폴더":Extension(name) is {Length:>1} ext?ext[1..].ToUpperInvariant()+" 파일":"파일";
    public static FrameworkElement Create(string name,bool folder,int size)
    {
        var host=new Grid{Width=size,Height=size};
        var fallback=Ui.Glyph(folder?"\uE8B7":"\uE8A5",size-2);fallback.Foreground=Ui.Brush(folder?"#E9C786":"#88B8F2");host.Children.Add(fallback);
        var icon=new Image{Width=size,Height=size,Stretch=Stretch.Uniform};RenderOptions.SetBitmapScalingMode(icon,BitmapScalingMode.HighQuality);host.Children.Add(icon);
        string extension=Extension(name),key=(folder?"folder":extension)+":"+size;
        if(cache.Count>=256&&!cache.ContainsKey(key)){extension="";key="generic:"+size;}
        string lookup=folder?"folder":"file"+extension;
        var task=cache.GetOrAdd(key,_=>new(()=>Task.Run(()=>Load(lookup,folder,size)))).Value;
        _=SetImageAsync(icon,fallback,task);return host;
    }
    private static async Task SetImageAsync(Image icon,FrameworkElement fallback,Task<ImageSource?> load)
    {
        try{var source=await load;if(source!=null&&!icon.Dispatcher.HasShutdownStarted){icon.Source=source;fallback.Visibility=Visibility.Collapsed;}}catch{ /* Keep the lightweight fallback icon. */ }
    }
    private static ImageSource? Load(string lookup,bool folder,int size)
    {
        int initialized=CoInitializeEx(IntPtr.Zero,0);var info=new ShellInfo();
        try
        {
            uint flags=0x100|0x10|(size<=16?1u:0u);
            if(SHGetFileInfo(lookup,folder?0x10u:0x80u,ref info,(uint)Marshal.SizeOf<ShellInfo>(),flags)==IntPtr.Zero||info.Icon==IntPtr.Zero)return null;
            var image=Imaging.CreateBitmapSourceFromHIcon(info.Icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();return image;
        }
        finally{if(info.Icon!=IntPtr.Zero)DestroyIcon(info.Icon);if(initialized>=0)CoUninitialize();}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ShellInfo
    {
        public IntPtr Icon;public int Index;public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string TypeName;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHGetFileInfoW")]private static extern IntPtr SHGetFileInfo(string path,uint attributes,ref ShellInfo info,uint size,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(IntPtr reserved,uint mode);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
}
