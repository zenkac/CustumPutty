using System.IO;

namespace EquipLink;

// The displayed directory is the approved boundary. Parent directories may be
// inaccessible even when its children are writable. Require complete listings
// inside a selected subtree, without requiring unrelated siblings to parse.
internal static class FtpFileDeletion
{
    private const int Limit=10000;
    public static async Task DeleteRemoteAsync(FtpClient client,string folder,FtpEntry[] chosen,Action deleted,CancellationToken ct)
    {
        folder=FtpPaths.Normalize(folder);if(chosen.Length==0)return;
        var files=new List<FtpEntry>();var directories=new List<FtpEntry>();var pending=new Stack<FtpEntry>();var seen=new HashSet<string>(StringComparer.Ordinal);
        string stage="삭제 대상 확인",target=folder;
        try
        {
            foreach(var selected in chosen)
            {
                target=selected.FullPath;
                if(selected.IsLink||selected.FullPath!=FtpPaths.Join(folder,selected.Name)||selected.Name.Contains('\\')||selected.FullPath=="/")throw new IOException("삭제 대상이 링크이거나 현재 폴더 밖에 있거나 이름이 올바르지 않습니다.");
            }
            ct.ThrowIfCancellationRequested();stage="현재 폴더 확인";target=folder;await client.VerifyDeletionDirectoryAsync(folder,ct);
            var current=await client.ListAsync(folder,ct);
            foreach(var selected in chosen)
            {
                target=selected.FullPath;
                var actual=current.SingleOrDefault(x=>x.Name==selected.Name)??throw new IOException("삭제할 항목을 정확하게 확인할 수 없습니다. 새로고침한 뒤 다시 선택해 주세요.");
                if(actual.IsDirectory!=selected.IsDirectory||actual.IsLink)throw new IOException("링크 또는 변경된 항목은 삭제하지 않습니다.");pending.Push(actual);
            }
            while(pending.TryPop(out var entry))
            {
                target=entry.FullPath;ct.ThrowIfCancellationRequested();if(!seen.Add(entry.FullPath))continue;
                if(seen.Count>Limit)throw new IOException("한 번에 삭제할 항목은 하위 폴더를 포함해 10,000개 이내여야 합니다.");
                if(entry.IsLink||entry.Name.Contains('\\'))throw new IOException("폴더 안에 링크 또는 처리할 수 없는 이름이 있어 삭제를 중단했습니다.");
                if(!entry.IsDirectory){files.Add(entry);continue;}
                directories.Add(entry);stage="하위 폴더 목록 확인";
                foreach(var child in Complete(await client.ListAsync(entry.FullPath,ct)))
                {
                    if(child.FullPath!=FtpPaths.Join(entry.FullPath,child.Name))throw new IOException("폴더 목록 경로가 올바르지 않습니다.");pending.Push(child);
                }
            }
            foreach(var entry in files.Concat(directories.OrderByDescending(x=>x.FullPath.Count(c=>c=='/'))))
            {
                ct.ThrowIfCancellationRequested();stage="삭제 직전 대상 확인";target=entry.FullPath;string parent=FtpPaths.Parent(entry.FullPath);
                await CheckRemoteDescendants(client,folder,parent,ct);
                var listing=await client.ListAsync(parent,ct);if(parent!=folder)Complete(listing);
                var actual=listing.SingleOrDefault(x=>x.Name==entry.Name);
                if(actual==null||actual.IsLink||actual.IsDirectory!=entry.IsDirectory)throw new IOException("삭제 도중 항목이 변경되어 중단했습니다.");
                stage=entry.IsDirectory?"폴더 삭제":"파일 삭제";await client.DeleteListedEntryAsync(entry,deleted,ct);
            }
        }
        catch(Exception error) when(error is not OperationCanceledException){throw new FtpDeletionException(stage,target,error);}
    }
    private static async Task CheckRemoteDescendants(FtpClient client,string anchor,string path,CancellationToken ct)
    {
        if(path==anchor)return;string prefix=anchor.TrimEnd('/')+"/";
        if(!path.StartsWith(prefix,StringComparison.Ordinal))throw new IOException("삭제 경로가 선택한 폴더를 벗어났습니다.");
        string parent=anchor;
        foreach(string name in path[prefix.Length..].Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            var listing=await client.ListAsync(parent,ct);if(parent!=anchor)Complete(listing);
            var entry=listing.SingleOrDefault(x=>x.Name==name);
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

internal sealed class FtpDeletionException(string stage,string path,Exception inner):IOException($"{stage}\n대상: {path}\n{inner.Message}",inner)
{
    internal string Stage {get;}=stage;
    internal string TargetPath {get;}=path;
}
