using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker;

public sealed record TaskRow(string Device,string Thread,string? Session,string? Parent,string? Fork,bool Subagent,
    long? Total,long? Input,long? Output,long? CacheRead,long? CacheWrite,long? Reasoning,
    string State,long TotalMs,long ActiveMs,long PausedMs,long UnknownMs,long Issues,long PartialTokens);

public static class LocalReport
{
    public static string DefaultDatabase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTracker","tracker.sqlite");

    private static SqliteConnection Open(string path)
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        try
        {
            db.Open();
            using var q=db.CreateCommand();q.CommandText="PRAGMA application_id";
            if(Convert.ToInt64(q.ExecuteScalar())!=Store.ApplicationId)throw new InvalidDataException("请选择 Tracker 数据库。");
            return db;
        }
        catch {db.Dispose();throw;}
    }
    internal static List<Dictionary<string,object?>> Details(string path,bool models)
    {
        using var db=Open(path);using var q=db.CreateCommand();
        q.CommandText=models?"SELECT * FROM ModelUsage ORDER BY device_id,thread_id,total DESC":
            "SELECT device_id,thread_id,code,confidence,count(*) AS observations FROM DataQuality GROUP BY device_id,thread_id,code,confidence ORDER BY observations DESC";
        using var r=q.ExecuteReader();var rows=new List<Dictionary<string,object?>>();
        while(r.Read())
        {
            var row=new Dictionary<string,object?>();
            for(int i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    public static List<TaskRow> Read(string path)
    {
        using var db=Open(path);using var q=db.CreateCommand();
        q.CommandText="""
            WITH sums AS (
              SELECT device_id,thread_id,sum(total) total,sum(input) input,sum(output) output,
                CASE WHEN count(cache_read)=count(*) THEN sum(cache_read) END cr,
                CASE WHEN count(cache_write)=count(*) THEN sum(cache_write) END cw,
                CASE WHEN count(reasoning)=count(*) THEN sum(reasoning) END reasoning,
                sum(CASE WHEN confidence='PARTIAL' THEN total ELSE 0 END) partial FROM TokenLedger GROUP BY device_id,thread_id
            )
            SELECT s.device_id,s.thread_id,s.session_id,s.parent_id,s.fork_id,s.is_subagent,u.total,u.input,u.output,u.cr,u.cw,u.reasoning,
              coalesce(t.state,'UNKNOWN'),coalesce(t.total_ms,0),coalesce(t.active_ms,0),coalesce(t.paused_ms,0),coalesce(t.unknown_ms,0),
              (SELECT count(*) FROM DataQuality d WHERE d.device_id=s.device_id AND d.thread_id=s.thread_id),coalesce(u.partial,0)
            FROM Session s LEFT JOIN TaskTiming t USING(device_id,thread_id) LEFT JOIN sums u USING(device_id,thread_id)
            ORDER BY t.last_ms DESC
            """;
        using var r=q.ExecuteReader();
        var result=new List<TaskRow>();
        string? Str(int i)=>r.IsDBNull(i)?null:r.GetString(i);
        long? Num(int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
        while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),Str(2),Str(3),Str(4),r.GetInt64(5)!=0,
            Num(6),Num(7),Num(8),Num(9),Num(10),Num(11),r.GetString(12),r.GetInt64(13),r.GetInt64(14),r.GetInt64(15),r.GetInt64(16),r.GetInt64(17),r.GetInt64(18)));
        return result;
    }
}

internal static class Program
{
    private const string Help="""
        Codex Tracker · 本地采集
        CodexTracker scan|watch [--root DATA_ROOT] [--device NAME] [--db tracker.sqlite] [--file PATH ...] [--replay]
        CodexTracker watch [--interval SECONDS]
        CodexTracker list|models|quality [--db tracker.sqlite] [--json]
        CodexTracker rebuild [--db tracker.sqlite]
        默认发现 CODEX_HOME（否则用户目录下的 .codex）中的 sessions 与 archived_sessions。
        watch 在前台持续运行。Ctrl+C 安全退出；rebuild 从数据库中的事件重新核算。
        """;
    public static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        if(args.Length==0 || args[0] is "--help" or "-h"){Console.WriteLine(Help);return 0;}
        try{return Execute(args);}
        catch(OperationCanceledException){return 0;}
        catch(Exception ex){Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");return 1;}
    }
    private static int Execute(string[] args)
    {
        string command=args[0];
        if(command is not ("scan" or "watch" or "list" or "models" or "quality" or "rebuild"))throw new ArgumentException(Help);
        var options=new Dictionary<string,string>(); var selected=new List<string>();bool replay=false,json=false;
        for(int i=1;i<args.Length;i++)
        {
            string k=args[i];
            if(k=="--replay")replay=true;
            else if(k=="--json")json=true;
            else if(k is "--db" or "--root" or "--device" or "--file" or "--interval" && i+1<args.Length)
            {var val=args[++i];if(k=="--file")selected.Add(Path.GetFullPath(val));else options.Add(k,val);}
            else throw new ArgumentException(Help);
        }
        string standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        string home=Environment.GetEnvironmentVariable("CODEX_HOME") is {Length:>0} h?h:standard;
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.GetValueOrDefault("--root",home)));
        string path=Path.GetFullPath(options.GetValueOrDefault("--db",LocalReport.DefaultDatabase));
        bool Inside(string file,string dir)
        {
            string relative=Path.GetRelativePath(Path.GetFullPath(dir),file);
            return relative!=".." && !relative.StartsWith(".."+Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
        }
        if(Inside(path,root)||Inside(path,standard)||Inside(path,home)||!path.EndsWith(".sqlite",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Tracker 数据库必须是 Codex 数据根以外的独立 .sqlite 文件。");
        for(FileSystemInfo? n=new FileInfo(path);n!=null;n=n is FileInfo fi?fi.Directory:((DirectoryInfo)n).Parent)
            if(n.Exists && n.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("数据库路径不可经过重解析点。");
        if(command is "models" or "quality")
        {
            var rows=LocalReport.Details(path,command=="models");
            if(json)Console.WriteLine(JsonSerializer.Serialize(rows));
            else if(rows.Count>0)
            {
                Console.WriteLine(string.Join('\t',rows[0].Keys));
                foreach(var row in rows)Console.WriteLine(string.Join('\t',row.Values.Select(v=>v??"UNKNOWN")));
            }
            return 0;
        }
        if(command=="list")
        {
            var rows=LocalReport.Read(path);
            if(json)Console.WriteLine(JsonSerializer.Serialize(rows));else
            {
                Console.WriteLine("设备 / 线程\t已入账 Token\t其中待核实\t总 / 活跃 / 暂停 / 未知 (秒)\t状态\t质量提示");
                foreach(var r in rows)Console.WriteLine($"{r.Device} / {r.Thread}\t{r.Total?.ToString()??"UNKNOWN"}\t{r.PartialTokens}\t{r.TotalMs/1000} / {r.ActiveMs/1000} / {r.PausedMs/1000} / {r.UnknownMs/1000}\t{r.State}\t{r.Issues}");
            }
            return 0;
        }
        if(command!="rebuild" && !Directory.Exists(root))throw new DirectoryNotFoundException("找不到 Codex 数据根。");
        if(selected.Any(f=>!Inside(f,root)||!Scanner.IsRollout(f)))throw new ArgumentException("指定的 rollout 必须位于数据根内。");
        int interval=int.Parse(options.GetValueOrDefault("--interval","15"));
        if(interval is <1 or >86400)throw new ArgumentException("扫描间隔应为 1 至 86400 秒。");
        string device=options.GetValueOrDefault("--device","local-"+Store.Key(root.ToUpperInvariant())[..12]);
        using var cancel=new CancellationTokenSource();
        Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;cancel.Cancel();};
        using var mutex=new Mutex(false,"Local\\CodexTracker-"+Store.Key(path.ToUpperInvariant()));
        do
        {
            bool locked;
            try{locked=mutex.WaitOne(TimeSpan.FromSeconds(10));}catch(AbandonedMutexException){locked=true;}
            if(!locked)throw new IOException("另一个采集器正在写此数据库。");
            int errors=0;
            try
            {
                using var db=new Store(path);
                if(command=="rebuild")
                    db.Atomic(()=>{foreach(var r in db.Rows("SELECT device_id,thread_id FROM Session"))Projection.Build(db,(string)r[0]!, (string)r[1]!);});
                else
                {
                    var scanner=new Scanner(db,device,root);
                    scanner.Scan(selected,replay,cancel.Token);
                    Console.WriteLine($"扫描完成　文件更新 {scanner.Changed}　新增事件 {scanner.NewEvents}　读取失败 {scanner.Errors}");
                    errors=scanner.Errors;
                }
            }
            finally{mutex.ReleaseMutex();}
            if(command!="watch")return errors==0?0:2;
            replay=false;
        }while(!cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval)));
        return 0;
    }
}
