using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace CodexTracker;

internal sealed record Frame(long Start,long End,byte[]? Bytes);

internal sealed class Scanner(Store db,string device,string root)
{
    private const int MaxLine = 16*1024*1024;
    private const long BatchBytes = 32L*1024*1024;
    public long NewEvents { get; private set; }
    public int Changed { get; private set; }
    public int Errors { get; private set; }

    public void Scan(IReadOnlyList<string> selected,bool replay,CancellationToken token)
    {
        db.Run("INSERT OR IGNORE INTO Device VALUES($p0,$p1,$p2)",device,root,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if(db.Number("SELECT count(*) FROM Device WHERE id=$p0 AND root=$p1",device,root)!=1)
            throw new InvalidOperationException("此设备与另一数据根绑定；请为其他目录使用独立设备标识。");
        string localHome=Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        if(string.Equals(root,Path.TrimEndingDirectorySeparator(Path.GetFullPath(localHome)),StringComparison.OrdinalIgnoreCase))
            PowerHistory.Refresh(db,device);
        var paths=selected.Count>0 ? selected : Discover().ToList();
        foreach(var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try { ReadFile(path,replay,token); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ZstdException or InvalidDataException)
            {
                Errors++;
                var source=Source(path);
                FileIssue(source,-1,0,"READ_FAILED",ex.GetType().Name);
            }
        }
    }
    private IEnumerable<string> Discover()
    {
        var options=new EnumerationOptions {RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint};
        foreach(var dir in new[]{"sessions","archived_sessions"})
        {
            var path=Path.Combine(root,dir);
            if(!Directory.Exists(path)) continue;
            var pending=new Stack<string>();pending.Push(path);
            options.RecurseSubdirectories=false;
            while(pending.TryPop(out var folder))
            {
                string[] files;
                try
                {
                    foreach(var child in Directory.GetDirectories(folder,"*",options).Order(StringComparer.OrdinalIgnoreCase))pending.Push(child);
                    files=Directory.GetFiles(folder,"*",options);
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                {
                    Errors++;FileIssue(Source(folder),-1,0,"DISCOVERY_FAILED",ex.GetType().Name);continue;
                }
                db.Run("DELETE FROM DataQuality WHERE source_id=(SELECT id FROM SourceFile WHERE device_id=$p0 AND path=$p1) AND code='DISCOVERY_FAILED'",
                    device,Path.GetRelativePath(root,folder));
                foreach(var f in files.Where(IsRollout).OrderDescending(StringComparer.OrdinalIgnoreCase))yield return f;
            }
        }
    }
    public static bool IsRollout(string p) => p.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)
        || p.EndsWith(".jsonl.zst",StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jsonl.zstd",StringComparison.OrdinalIgnoreCase);
    private long Source(string path)
    {
        var rel=Path.GetRelativePath(root,path);
        db.Run("INSERT OR IGNORE INTO SourceFile(device_id,path) VALUES($p0,$p1)",device,rel);
        return db.Number("SELECT id FROM SourceFile WHERE device_id=$p0 AND path=$p1",device,rel);
    }
    private void FileIssue(long source,long offset,long generation,string code,string detail) =>
        db.Run("INSERT OR REPLACE INTO DataQuality VALUES($p0,$p1,NULL,$p2,NULL,$p3,'PARTIAL',$p4)",
            Store.Key(source,generation,offset,code),device,source,code,$"{offset}: {detail}");

    private static string Fingerprint(FileStream fs,long start,long end)
    {
        if(end<=start)return "";
        var bytes=new byte[checked((int)(end-start))];
        var previous=fs.Position;
        try{fs.Position=start;fs.ReadExactly(bytes);return Convert.ToHexString(SHA256.HashData(bytes));}
        finally{fs.Position=previous;}
    }

    private void ReadFile(string path,bool replay,CancellationToken token)
    {
        var id=Source(path);
        var row=db.Rows("SELECT generation,offset,disk_length,modified,head,anchor,context,complete FROM SourceFile WHERE id=$p0",id).Single();
        long gen=(long)row[0]!, offset=(long)row[1]!, known=(long)row[2]!, oldTicks=(long)row[3]!;
        using var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan);
        long length=fs.Length, ticks=File.GetLastWriteTimeUtc(path).Ticks;
        bool compressed=!path.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase);
        bool same=length==known && ticks==oldTicks;
        bool changedPrefix=!compressed && offset>0 && (length<offset ||
            (string)row[4]! != Fingerprint(fs,0,Math.Min(4096,offset)) ||
            (string)row[5]! != Fingerprint(fs,Math.Max(0,offset-4096),offset));
        bool reset=replay || changedPrefix || (known>=0 && (length<known || (!same && (compressed || length==known))));
        if(!reset && same && (long)row[7]! != 0) return;
        var ctx=reset?new CursorContext():JsonSerializer.Deserialize<CursorContext>((string)row[6]!)!;
        if(reset){gen++;offset=0;}
        if(!compressed)fs.Position=offset;
        using var decompressor=compressed?new DecompressionStream(fs,leaveOpen:true):null;
        Stream input=decompressor is null?fs:decompressor;
        if(compressed && offset>0) Skip(input,offset,token);
        Changed++;
        var buffer=new byte[65536];
        using var line=new MemoryStream();
        bool overflow=false;
        long start=offset,pos=offset,batchBytes=0;
        var batch=new List<Frame>();
        var unresolved="unresolved-"+id;
        while(true)
        {
            token.ThrowIfCancellationRequested();
            int want=compressed?buffer.Length:(int)Math.Min(buffer.Length,Math.Max(0,length-pos));
            var n=want==0?0:input.Read(buffer,0,want);
            if(n==0)break;
            int i=0;
            while(i<n)
            {
                int end=Array.IndexOf(buffer,(byte)10,i,n-i);
                int count=(end<0?n:end)-i;
                if(!overflow && line.Length+count<=MaxLine)line.Write(buffer,i,count);
                else overflow=true;
                pos+=count;i+=count;
                if(end<0)break;
                i++;pos++;
                batch.Add(new(start,pos,overflow?null:line.ToArray()));
                batchBytes+=pos-start;start=pos;line.SetLength(0);overflow=false;
                if(batchBytes>=BatchBytes || batch.Count>=4096) Commit(false);
            }
        }
        Commit(true);

        void Commit(bool final)
        {
            var dirty=new HashSet<string>(StringComparer.Ordinal);
            db.Atomic(()=>
            {
                foreach(var frame in batch)
                {
                    token.ThrowIfCancellationRequested();
                    var bytes=frame.Bytes; Fact? fact=null; string? problem=null;
                    if(bytes==null) problem="OVERSIZE_LINE";
                    else if(bytes.Length>0)
                    {
                        try{fact=Parser.Read(bytes,ref ctx,unresolved);}
                        catch(Exception ex) when(ex is JsonException or FormatException){problem="MALFORMED_LINE";}
                    }
                    if(problem!=null){FileIssue(id,frame.Start,gen,problem,"");ctx=new();}
                    if(fact!=null)
                    {
                        var json=JsonSerializer.Serialize(fact);
                        var logical=fact.Kind=="response" && fact.Response!=null?Store.Key(fact.Thread,fact.Response):Store.Key(json);
                        var eventId=Store.Key(device,json);
                        db.Run("INSERT OR IGNORE INTO Session(device_id,thread_id) VALUES($p0,$p1)",device,fact.Thread);
                        if(fact.History is { } ancestry)
                            db.Run("UPDATE Session SET history_thread_id=coalesce($p0,history_thread_id),history_end_ordinal=coalesce($p1,history_end_ordinal),history_end_offset=coalesce($p2,history_end_offset),subagent_start_ordinal=coalesce($p3,subagent_start_ordinal) WHERE device_id=$p4 AND thread_id=$p5",
                                ancestry.Thread,ancestry.EndOrdinal,ancestry.EndOffset,ancestry.ChildStart,device,fact.Thread);
                        if(fact.Kind=="meta" || fact.Session!=null)
                            db.Run("UPDATE Session SET session_id=coalesce($p0,session_id),parent_id=coalesce($p1,parent_id),fork_id=coalesce($p2,fork_id),is_subagent=max(is_subagent,$p3) WHERE device_id=$p4 AND thread_id=$p5",
                                fact.Session,fact.Parent,fact.Fork,fact.Subagent?1:0,device,fact.Thread);
                        var inserted=db.Run("INSERT OR IGNORE INTO Event VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
                            eventId,device,fact.Thread,logical,fact.Kind,fact.Turn,fact.Time,json);
                        if(inserted>0)
                        {
                            dirty.Add(fact.Thread);NewEvents++;
                            if(fact.Kind=="snapshot")
                                foreach(var peer in db.Rows("SELECT DISTINCT thread_id FROM Event WHERE device_id=$p0 AND kind='snapshot' AND time_ms=$p1 AND thread_id<>$p2",
                                    device,fact.Time,fact.Thread)) dirty.Add((string)peer[0]!);
                            if(fact.Kind=="response")
                                foreach(var other in db.Rows("""
                                    SELECT thread_id FROM Event WHERE device_id=$p0 AND kind='snapshot' AND turn_id=$p1
                                    UNION SELECT thread_id FROM Event WHERE device_id=$p0 AND kind='snapshot' AND time_ms BETWEEN $p2 AND $p3
                                    """,device,fact.Turn,fact.Time-2000,fact.Time+2000))
                                    dirty.Add((string)other[0]!);
                        }
                        db.Run("INSERT OR IGNORE INTO SourceRecord VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                            id,gen,frame.Start,frame.End,eventId,Convert.ToHexString(SHA256.HashData(bytes!)),
                            fact.Kind is "response" or "snapshot"?Encoding.UTF8.GetString(bytes!):null);
                    }
                    offset=frame.End;
                }
                foreach(var thread in dirty)Projection.Build(db,device,thread);
                if(final)
                {
                    db.Run("DELETE FROM DataQuality WHERE source_id=$p0 AND code IN ('INCOMPLETE_TAIL','READ_FAILED')",id);
                    if(pos>offset)FileIssue(id,offset,gen,"INCOMPLETE_TAIL","waiting for newline");
                }
                var head=compressed?"":Fingerprint(fs,0,Math.Min(4096,offset));
                var anchor=compressed?"":Fingerprint(fs,Math.Max(0,offset-4096),offset);
                db.Run("UPDATE SourceFile SET generation=$p0,offset=$p1,disk_length=$p2,modified=$p3,head=$p4,anchor=$p5,context=$p6,complete=$p7 WHERE id=$p8",
                    gen,offset,length,ticks,head,anchor,JsonSerializer.Serialize(ctx),final?1:0,id);
            });
            batch.Clear();batchBytes=0;
        }
    }
    private static void Skip(Stream stream,long count,CancellationToken token)
    {
        var buf=new byte[65536];
        while(count>0){token.ThrowIfCancellationRequested();int n=stream.Read(buf,0,(int)Math.Min(count,buf.Length));if(n==0)throw new EndOfStreamException();count-=n;}
    }
}
