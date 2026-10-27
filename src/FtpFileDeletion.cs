using System.IO;

namespace EquipLink;

// Build a complete, bounded plan before removing anything. Refuse links and
// incomplete listings; never use recursive delete or follow another tree.
internal static class FtpFileDeletion
{
    private const int Limit=10000;
    public static async Task DeleteRemoteAsync(FtpClient client,string folder,FtpEntry[] chosen,Action deleted,CancellationToken ct)
    {
        folder=FtpPaths.Normalize(folder);var files=new List<FtpEntry>();var directories=new List<FtpEntry>();var pending=new Stack<FtpEntry>();var seen=new HashSet<string>(StringComparer.Ordinal);
        await CheckRemoteAncestors(client,folder,ct);
        var current=Complete(await client.ListAsync(folder,ct));
        foreach(var selected in chosen)
        {
            string path=FtpPaths.Join(folder,selected.Name);
            if(selected.FullPath!=path||path=="/")throw new IOException("삭제 대상이 현재 폴더 밖에 있습니다.");
            var actual=current.SingleOrDefault(x=>x.Name==selected.Name)??throw new IOException("삭제할 항목이 변경되었습니다. 새로고침해 주세요.");
            if(actual.IsDirectory!=selected.IsDirectory||actual.IsLink)throw new IOException("링크 또는 변경된 항목은 삭제하지 않습니다.");pending.Push(actual);
        }
        while(pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();if(!seen.Add(entry.FullPath))continue;
            if(seen.Count>Limit)throw new IOException("한 번에 삭제할 항목은 하위 폴더를 포함해 10,000개 이내여야 합니다.");
            if(entry.IsLink)throw new IOException("폴더 안에 심볼릭 링크가 있어 삭제를 중단했습니다. 링크는 직접 확인해 주세요.");
            if(!entry.IsDirectory){files.Add(entry);continue;}
            directories.Add(entry);
            foreach(var child in Complete(await client.ListAsync(entry.FullPath,ct)))
            {
                if(child.FullPath!=FtpPaths.Join(entry.FullPath,child.Name))throw new IOException("폴더 목록 경로가 올바르지 않습니다.");pending.Push(child);
            }
        }
        foreach(var entry in files.Concat(directories.OrderByDescending(x=>x.FullPath.Count(c=>c=='/'))))
        {
            ct.ThrowIfCancellationRequested();await CheckRemoteAncestors(client,FtpPaths.Parent(entry.FullPath),ct);
            var actual=Complete(await client.ListAsync(FtpPaths.Parent(entry.FullPath),ct)).SingleOrDefault(x=>x.Name==entry.Name);
            if(actual==null||actual.IsLink||actual.IsDirectory!=entry.IsDirectory)throw new IOException("삭제 도중 항목이 변경되어 중단했습니다.");
            if(entry.IsDirectory)await client.RemoveDirectoryAsync(entry.FullPath,ct);else await client.DeleteFileAsync(entry.FullPath,ct);deleted();
        }
    }
    private static async Task CheckRemoteAncestors(FtpClient client,string path,CancellationToken ct)
    {
        string parent="/";
        foreach(string name in FtpPaths.Normalize(path).Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            var entry=Complete(await client.ListAsync(parent,ct)).SingleOrDefault(x=>x.Name==name);
            if(entry==null||entry.IsLink||!entry.IsDirectory)throw new IOException("삭제 경로에 링크 또는 접근할 수 없는 폴더가 있습니다.");
            parent=FtpPaths.Join(parent,name);
        }
    }
    public static void DeleteLocal(string folder,string[] chosen,Action deleted,CancellationToken ct)
    {
        folder=Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));CheckLocalAncestors(folder);
        var pending=new Stack<string>();var files=new List<string>();var directories=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string selected in chosen)
        {
            string path=Path.GetFullPath(selected);
            if(!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!),folder,StringComparison.OrdinalIgnoreCase))throw new IOException("삭제 대상이 현재 폴더 밖에 있습니다.");pending.Push(path);
        }
        string prefix=Path.EndsInDirectorySeparator(folder)?folder:folder+Path.DirectorySeparatorChar;
        while(pending.TryPop(out string? path))
        {
            ct.ThrowIfCancellationRequested();if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("삭제 경로가 현재 폴더를 벗어났습니다.");
            if(!seen.Add(path))continue;if(seen.Count>Limit)throw new IOException("한 번에 삭제할 항목은 하위 폴더를 포함해 10,000개 이내여야 합니다.");
            var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("링크 또는 연결점이 있어 삭제를 중단했습니다.");
            if((attributes&FileAttributes.Directory)==0)files.Add(path);
            else{directories.Add(path);foreach(string child in Directory.EnumerateFileSystemEntries(path))pending.Push(child);}
        }
        foreach(string path in files.Concat(directories.OrderByDescending(x=>x.Length)))
        {
            ct.ThrowIfCancellationRequested();CheckLocalAncestors(path);
            bool directory=directories.Contains(path);
            if(((File.GetAttributes(path)&FileAttributes.Directory)!=0)!=directory)throw new IOException("삭제 도중 항목이 변경되어 중단했습니다.");
            if(directory)Directory.Delete(path,false);else File.Delete(path);deleted();
        }
    }
    private static void CheckLocalAncestors(string path)
    {
        for(string? cursor=Path.GetFullPath(path);cursor!=null;cursor=Path.GetDirectoryName(cursor))
            if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new IOException("링크 또는 연결점 경로는 삭제하지 않습니다.");
    }
    private static IReadOnlyList<FtpEntry> Complete(IReadOnlyList<FtpEntry> listing)
    {
        if(listing is FtpListing{OmittedCount:>0})throw new IOException("폴더 목록 일부를 읽지 못해 삭제를 중단했습니다. 문자셋을 확인하고 새로고침한 뒤 다시 선택해 주세요.");
        return listing;
    }
}
