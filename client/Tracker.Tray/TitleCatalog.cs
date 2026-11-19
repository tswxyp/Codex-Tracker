using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record CatalogEntry(string Host,string Id,string Title,bool Eligible);
internal enum MatchIssue { NotIndexed, OtherDevice, Ambiguous, PendingTitle }
internal sealed class TitleCatalog
{
    readonly Dictionary<string,string> unique=new(StringComparer.Ordinal);
    readonly Dictionary<string,MatchIssue> issues=new(StringComparer.Ordinal);
    internal TitleCatalog(IEnumerable<CatalogEntry> rows,IEnumerable<(string Title,string Id)> index)
    {
        var aliases=index.ToLookup(x=>x.Title,x=>x.Id,StringComparer.Ordinal);
        foreach(var group in rows.GroupBy(r=>r.Title,StringComparer.Ordinal))
        {
            var entries=group.ToArray();
            if(entries.Length!=1){issues[group.Key]=MatchIssue.Ambiguous;continue;}
            var r=entries[0];
            if(aliases[r.Title].Any(id=>id!=r.Id))issues[r.Title]=MatchIssue.Ambiguous;
            else if(r.Host!="local")issues[r.Title]=MatchIssue.OtherDevice;
            else if(!r.Eligible)issues[r.Title]=MatchIssue.PendingTitle;
            else unique.Add(r.Title,r.Id);
        }
    }
    internal string? Resolve(string title)=>unique.GetValueOrDefault(title);
    internal MatchIssue Issue(string title)=>issues.GetValueOrDefault(title,MatchIssue.NotIndexed);

    // Never attach SQLite to the live Codex database. A private WAL index may be built only on our copy.
    internal static TitleCatalog Read(string root)
    {
        string src=Path.Combine(root,"sqlite","codex.db");
        var copy=Directory.CreateTempSubdirectory("CodexTracker-catalog-");
        try
        {
            string target=Path.Combine(copy.FullName,"catalog.db");
            CopyVerified(src,target);
            using var db=Data.Open(target,SqliteOpenMode.ReadWrite);
            using var query=db.CreateCommand();
            query.CommandText="SELECT host_id,thread_id,display_title,missing_candidate,pending_observed_title FROM local_thread_catalog";
            var rows=new List<CatalogEntry>();
            using var reader=query.ExecuteReader();
            while(reader.Read())
                rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3)==0 && reader.GetInt64(4)==0));
            var aliases=new List<(string,string)>();
            using var index=OpenSide(Path.Combine(root,"session_index.jsonl"),true);
            if(index!=null)
            {
                using var lines=new StreamReader(index);
                while(lines.ReadLine() is {} line)
                {
                    using var json=JsonDocument.Parse(line);
                    var item=json.RootElement;
                    if(item.GetProperty("thread_name").GetString() is {} title && item.GetProperty("id").GetString() is {} id)
                        aliases.Add((title,id));
                }
            }
            return new(rows,aliases);
        }
        finally
        {
            // Only delete files created in this call, including SQLite's private sidecars.
            foreach(var file in copy.GetFiles())file.Delete();
            copy.Delete();
        }
    }

    internal static void CopyVerified(string src,string target)
    {
        var sums=new Dictionary<string,byte[]?>();
        foreach(var suffix in new[]{"","-wal"})
        {
            using var input=OpenSide(src+suffix,suffix.Length!=0);
            if(input==null){sums[suffix]=null;continue;}
            if(input.Length>128L*1024*1024)throw new InvalidDataException("数据库文件超出可复核范围。");
            using var output=File.Create(target+suffix);
            using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer=new byte[65536];
            for(int n;(n=input.Read(buffer))>0;)
            {
                output.Write(buffer,0,n);
                sha.AppendData(buffer,0,n);
                if(output.Length>128L*1024*1024)throw new IOException("数据库文件正在增长。");
            }
            sums[suffix]=sha.GetHashAndReset();
        }
        foreach(var (suffix,sum) in sums)
        {
            using var input=OpenSide(src+suffix,suffix.Length!=0);
            if(input==null && sum==null)continue;
            if(input==null || sum==null || !SHA256.HashData(input).AsSpan().SequenceEqual(sum))
                throw new IOException("数据库文件更新中。");
        }
    }
    internal static FileStream? OpenSide(string path,bool optional)
    {
        try{return new(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}
        catch(FileNotFoundException) when(optional){return null;}
    }
}


internal sealed class CatalogCache
{
    string? signature;
    TitleCatalog? previous;
    internal TitleCatalog Read(string root)
    {
        string Fingerprint()
        {
            var hashes=new List<string>();
            foreach(var (name,optional) in new[]{(Path.Combine(root,"sqlite","codex.db"),false),(Path.Combine(root,"sqlite","codex.db-wal"),true),(Path.Combine(root,"session_index.jsonl"),true)})
            {
                using var file=TitleCatalog.OpenSide(name,optional);
                if(file?.Length>128L*1024*1024)throw new InvalidDataException("目录过大。");
                hashes.Add(file==null?"-":Convert.ToHexString(SHA256.HashData(file)));
            }
            return string.Join("/",hashes);
        }
        for(int attempt=0;;attempt++)
        {
            try
            {
                string stamp=Fingerprint();
                if(signature==stamp && previous!=null)return previous;
                var fresh=TitleCatalog.Read(root);
                if(Fingerprint()!=stamp)throw new IOException("目录更新中。");
                signature=stamp;previous=fresh;return fresh;
            }
            catch(IOException) when(attempt<2){Thread.Sleep(75);}
        }
    }
}
