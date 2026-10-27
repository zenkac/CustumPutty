using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ComDataObject=System.Runtime.InteropServices.ComTypes.IDataObject;

namespace EquipLink;

/// <summary>Windows Shell virtual-file source. Explorer owns the destination and
/// its collision dialog; only COPY is offered, so remote originals stay intact.</summary>
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class FtpVirtualFiles : ComDataObject,IDisposable
{
    public static readonly short DescriptorFormat=unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    public static readonly short ContentsFormat=unchecked((short)RegisterClipboardFormat("FileContents"));
    static readonly short PreferredEffect=unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));
    const int FormatError=unchecked((int)0x80040064),IndexError=unchecked((int)0x80040068);
    readonly FtpSite site;readonly FtpEntry[] files;readonly string[] names;
    readonly CancellationTokenSource cancellation=new();readonly object sync=new();
    readonly Dictionary<int,Task<string>> downloads=[];readonly List<FileComStream> streams=[];
    readonly Dictionary<int,long> delivered=[];string protectedPassword;bool disposed;
    public IReadOnlyList<(FtpEntry Entry,long Bytes)> Delivered{get{lock(sync)return delivered.Select(x=>(files[x.Key],x.Value)).ToArray();}}
    public FtpVirtualFiles(FtpSite site,string password,IEnumerable<FtpEntry> entries)
    {
        this.site=site.Copy();files=entries.ToArray();
        if(files.Length is 0 or >1000||files.Any(f=>f.IsDirectory||f.IsLink))throw new ArgumentException("일반 파일을 1~1,000개 선택하세요.");
        names=files.Select(f=>FtpPaths.SafeLocalName(f.Name)).ToArray();
        if(names.Any(x=>x.Length>=260)||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)throw new ArgumentException("Windows에서 중복되거나 너무 긴 파일 이름이 있습니다.");
        foreach(var file in files)FtpPaths.Validate(file.FullPath);
        protectedPassword=CredentialProtector.Protect(this.site.CredentialTarget(),password);this.site.EncryptedPassword=null;
    }
    public uint DoDragDrop()
    {
        int initialized=OleInitialize(IntPtr.Zero);if(initialized<0)Marshal.ThrowExceptionForHR(initialized);
        try{int hr=NativeDragDrop(this,new CopyDropSource(),1,out uint effect);if(hr<0)Marshal.ThrowExceptionForHR(hr);return hr==0x40101?0:effect&1;}
        finally{OleUninitialize();}
    }
    public void GetData(ref FORMATETC format,out STGMEDIUM medium)
    {
        ObjectDisposedException.ThrowIf(disposed,this);int result=QueryGetData(ref format);if(result!=0)Marshal.ThrowExceptionForHR(result);
        medium=new();
        if(format.cfFormat==DescriptorFormat){medium.tymed=TYMED.TYMED_HGLOBAL;medium.unionmember=GlobalBytes(Descriptors());return;}
        if(format.cfFormat==PreferredEffect){medium.tymed=TYMED.TYMED_HGLOBAL;medium.unionmember=GlobalBytes(BitConverter.GetBytes(1));return;}
        int index=format.lindex;if(index<0||index>=files.Length)Marshal.ThrowExceptionForHR(IndexError);
        Task<string> download;
        lock(sync)
        {
            if(!downloads.TryGetValue(index,out download!))
            {
                string cipher=protectedPassword;download=Task.Run(()=>DownloadAsync(index,cipher,cancellation.Token));downloads[index]=download;
            }
        }
        try
        {
            string path=Wait(download);ObjectDisposedException.ThrowIf(disposed,this);
            var stream=new FileComStream(path);lock(sync){streams.Add(stream);delivered[index]=new FileInfo(path).Length;}
            medium.tymed=TYMED.TYMED_ISTREAM;medium.unionmember=Marshal.GetComInterfaceForObject(stream,typeof(IStream));
        }
        catch(Exception){throw new COMException("FTP 파일을 Windows에 전달하지 못했습니다.",unchecked((int)0x8003001E));}
    }
    async Task<string> DownloadAsync(int index,string cipher,CancellationToken ct)
    {
        string folder=Path.Combine(AppPaths.Root,"drag-cache");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,Guid.NewGuid().ToString("N")+".part");string password="";
        try
        {
            password=CredentialProtector.Unprotect(site.CredentialTarget(),cipher);
            await using var client=await FtpClient.ConnectAsync(site,password,ct).ConfigureAwait(false);
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                await client.DownloadAsync(files[index].FullPath,output,null,ct).ConfigureAwait(false);await output.FlushAsync(ct).ConfigureAwait(false);
                if(files[index].Size.HasValue&&files[index].Size!=output.Length)throw new IOException("서버 파일 크기와 수신 크기가 다릅니다.");
            }
            ct.ThrowIfCancellationRequested();return path;
        }
        catch{try{File.Delete(path);}catch{}throw;}
        finally{password="";}
    }
    static T Wait<T>(Task<T> task)
    {
        var dispatcher=Application.Current?.Dispatcher;
        if(!task.IsCompleted&&dispatcher?.CheckAccess()==true)
        {
            var frame=new DispatcherFrame();_=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
    byte[] Descriptors()
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.Unicode,true);writer.Write(files.Length);
        for(int i=0;i<files.Length;i++)
        {
            uint flags=0x80000000u|0x4u|0x4000u;if(files[i].Size.HasValue)flags|=0x40;if(files[i].Modified.HasValue)flags|=0x20;
            writer.Write(flags);writer.Write(new byte[32]);writer.Write(0x80u);writer.Write(0L);writer.Write(0L);writer.Write(files[i].Modified?.UtcDateTime.ToFileTimeUtc()??0L);
            ulong size=(ulong)Math.Max(0,files[i].Size??0);writer.Write((uint)(size>>32));writer.Write((uint)size);
            byte[] name=new byte[520];Encoding.Unicode.GetBytes(names[i]).CopyTo(name,0);writer.Write(name);
        }
        return output.ToArray();
    }
    public int QueryGetData(ref FORMATETC format)
    {
        if(format.dwAspect!=DVASPECT.DVASPECT_CONTENT)return FormatError;
        if(format.cfFormat==DescriptorFormat||format.cfFormat==PreferredEffect)return (format.tymed&TYMED.TYMED_HGLOBAL)!=0?0:FormatError;
        if(format.cfFormat==ContentsFormat)return (format.tymed&TYMED.TYMED_ISTREAM)!=0&&format.lindex>=-1&&format.lindex<files.Length?0:IndexError;
        return FormatError;
    }
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if(direction!=DATADIR.DATADIR_GET)throw new COMException("Not supported",unchecked((int)0x80004001));
        return new FormatEnumerator([Format(DescriptorFormat,TYMED.TYMED_HGLOBAL),Format(ContentsFormat,TYMED.TYMED_ISTREAM),Format(PreferredEffect,TYMED.TYMED_HGLOBAL)]);
    }
    public static FORMATETC Format(short id,TYMED medium,int index=-1)=>new(){cfFormat=id,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=index,tymed=medium};
    public void GetDataHere(ref FORMATETC format,ref STGMEDIUM medium)=>throw new COMException("Unsupported medium",FormatError);
    public int GetCanonicalFormatEtc(ref FORMATETC format,out FORMATETC result){result=format;result.ptd=IntPtr.Zero;return 0x40130;}
    public void SetData(ref FORMATETC format,ref STGMEDIUM medium,bool release){if(release)ReleaseStgMedium(ref medium);}
    public int DAdvise(ref FORMATETC format,ADVF flags,IAdviseSink sink,out int connection){connection=0;return unchecked((int)0x80040003);}
    public void DUnadvise(int connection)=>throw new COMException("Not supported",unchecked((int)0x80040003));
    public int EnumDAdvise(out IEnumSTATDATA? result){result=null;return unchecked((int)0x80040003);}
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;disposed=true;cancellation.Cancel();protectedPassword="";
            foreach(var stream in streams)stream.Dispose();streams.Clear();
            foreach(var task in downloads.Values)_=task.ContinueWith(done=>{if(done.Status==TaskStatus.RanToCompletion){try{File.Delete(done.Result);}catch{}}},TaskScheduler.Default);
        }
    }
    static IntPtr GlobalBytes(byte[] bytes)
    {
        IntPtr memory=GlobalAlloc(0x42,(UIntPtr)bytes.Length);if(memory==IntPtr.Zero)throw new OutOfMemoryException();
        IntPtr pointer=GlobalLock(memory);if(pointer==IntPtr.Zero){GlobalFree(memory);throw new OutOfMemoryException();}
        try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}return memory;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")]static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
    [DllImport("kernel32.dll")]internal static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]internal static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("ole32.dll")]internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")]static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]static extern void OleUninitialize();
    [DllImport("ole32.dll",EntryPoint="DoDragDrop")]static extern int NativeDragDrop([MarshalAs(UnmanagedType.Interface)]ComDataObject data,[MarshalAs(UnmanagedType.Interface)]ICopyDropSource source,uint allowed,out uint effect);
}

[ComVisible(true),Guid("00000121-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICopyDropSource
{
    [PreserveSig]int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)]bool escape,uint keys);
    [PreserveSig]int GiveFeedback(uint effect);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class CopyDropSource:ICopyDropSource
{
    public int QueryContinueDrag(bool escape,uint keys)=>escape?0x40101:(keys&1)==0?0x40100:0;
    public int GiveFeedback(uint effect)=>0x40102;
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class FileComStream:IStream,IDisposable
{
    readonly FileStream stream;readonly object sync=new();
    public FileComStream(string path)=>stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
    public void Read(byte[] buffer,int count,IntPtr read){lock(sync){int size=stream.Read(buffer,0,count);if(read!=IntPtr.Zero)Marshal.WriteInt32(read,size);}}
    public void Seek(long offset,int origin,IntPtr position){lock(sync){long value=stream.Seek(offset,(SeekOrigin)origin);if(position!=IntPtr.Zero)Marshal.WriteInt64(position,value);}}
    public void Stat(out STATSTG stat,int flags){stat=new(){type=2,cbSize=stream.Length,grfMode=0};}
    public void CopyTo(IStream target,long bytes,IntPtr read,IntPtr written)
    {
        byte[] buffer=new byte[65536];long total=0;
        lock(sync){while(total<bytes){int size=stream.Read(buffer,0,(int)Math.Min(buffer.Length,bytes-total));if(size==0)break;target.Write(buffer,size,IntPtr.Zero);total+=size;}}
        if(read!=IntPtr.Zero)Marshal.WriteInt64(read,total);if(written!=IntPtr.Zero)Marshal.WriteInt64(written,total);
    }
    public void Clone(out IStream clone){lock(sync){var copy=new FileComStream(stream.Name);copy.stream.Position=stream.Position;clone=copy;}}
    public void Commit(int flags){}
    public void Revert()=>throw new COMException("Read only",unchecked((int)0x80030005));
    public void SetSize(long size)=>throw new COMException("Read only",unchecked((int)0x80030005));
    public void Write(byte[] buffer,int count,IntPtr written)=>throw new COMException("Read only",unchecked((int)0x80030005));
    public void LockRegion(long offset,long bytes,int flags)=>throw new COMException("Not supported",unchecked((int)0x80030001));
    public void UnlockRegion(long offset,long bytes,int flags)=>throw new COMException("Not supported",unchecked((int)0x80030001));
    public void Dispose(){lock(sync)stream.Dispose();}
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class FormatEnumerator(FORMATETC[] values):IEnumFORMATETC
{
    int position;
    public int Next(int count,FORMATETC[] result,int[]? fetched){int read=0;while(read<count&&position<values.Length)result[read++]=values[position++];if(fetched?.Length>0)fetched[0]=read;return read==count?0:1;}
    public int Skip(int count){int old=position;position=Math.Min(values.Length,position+count);return position-old==count?0:1;}
    public int Reset(){position=0;return 0;}
    public void Clone(out IEnumFORMATETC clone)=>clone=new FormatEnumerator(values){position=position};
}
