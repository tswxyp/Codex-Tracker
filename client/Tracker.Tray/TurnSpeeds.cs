using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal readonly record struct SpeedKey(string Thread,string Turn,string Model);

// Each chat or report calculation reads one immutable view of turn speeds.
internal sealed class TurnSpeeds(Dictionary<SpeedKey,string> tiers)
{
    internal static readonly TurnSpeeds Empty=new([]);
    internal static string Canonical(string speed)=>speed.Trim().ToLowerInvariant() switch{
        "default" or "standard"=>"standard","priority" or "fast"=>"fast","ultrafast"=>"ultrafast",_=>"unsupported"
    };
    internal string? Resolve(string thread,string? turn,string model,string? ledgerSpeed)
    {
        string? logged=turn==null?null:tiers.GetValueOrDefault(new(thread,turn,model));
        if(string.IsNullOrWhiteSpace(ledgerSpeed))return logged;
        string ledger=Canonical(ledgerSpeed);
        return logged==null || logged==ledger?ledger:"ambiguous";
    }

}

internal sealed partial class TurnSpeedHistory(string directory,string root)
{
    readonly object gate=new();
    TurnSpeeds current=TurnSpeeds.Empty;
    bool refreshing,loaded;
    long nextRefresh;
    string CacheFile=>Path.Combine(directory,"turn-speeds.sqlite");
    internal TurnSpeeds Current {get {lock(gate)return current;}}

    internal TurnSpeeds Snapshot()
    {
        lock(gate){
            if(!refreshing && Environment.TickCount64>=nextRefresh){
                refreshing=true;
                _=Task.Run(Refresh);
            }
            return current;
        }
    }

    void Refresh()
    {
        int delay=300_000;
        try{
            if(!loaded && File.Exists(CacheFile))LoadStored();
            loaded=true;
            string source=Path.Combine(root,"logs_2.sqlite");
            var observed=ReadDiagnostics(source);
            if(observed.Count>0 && Save(observed)){
                loaded=false;LoadStored();loaded=true;
            }
        }catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException){
            delay=60_000;
        }finally{
            lock(gate){nextRefresh=Environment.TickCount64+delay;refreshing=false;}
        }
    }

    // Require the sampling request's turn and model as well as the log row's thread.
    // Feature switches such as feature.fast_mode only describe availability.
    [GeneratedRegex(@":try_run_sampling_request\{turn_id=([a-fA-F0-9-]{36}) model=([a-zA-Z0-9_.-]{1,80})\}: model=""\2"" ",RegexOptions.CultureInvariant)]
    private static partial Regex SamplingRequest();

    Dictionary<SpeedKey,string> ReadDiagnostics(string source)
    {
        var observed=new Dictionary<SpeedKey,string>();
        var folder=Directory.CreateDirectory(Path.Combine(directory,"speed-snapshot"));
        using var guard=new FileStream(Path.Combine(folder.FullName,"snapshot.lock"),FileMode.OpenOrCreate,
            FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
        string target=Path.Combine(folder.FullName,"log.db");
        void ClearSnapshot(){
            foreach(var suffix in new[]{"-shm","-wal","-journal",""})File.Delete(target+suffix);
        }
        // A forced exit releases the lock. Remove any abandoned private snapshot before reuse.
        ClearSnapshot();
        if(!File.Exists(source))return observed;
        try{
            TitleCatalog.CopyVerified(source,target);
            using var db=Data.Open(target,SqliteOpenMode.ReadWrite);
            using var q=db.CreateCommand();
            q.CommandText="""
                SELECT thread_id,feedback_log_body FROM logs WHERE target='feedback_tags' AND thread_id IS NOT NULL
                AND length(feedback_log_body)<=65536 AND instr(feedback_log_body,'"service_tier"')>0
                """;
            using var r=q.ExecuteReader();
            while(r.Read()){
                string thread=r.GetString(0),body=r.GetString(1);
                if(!Guid.TryParseExact(thread,"D",out _) || !body.StartsWith("session_loop{thread_id="+thread+"}:",StringComparison.Ordinal))continue;
                const string separator=" tags_json=";
                int tags=body.LastIndexOf(separator,StringComparison.Ordinal);
                if(tags<0)continue;
                var match=SamplingRequest().Match(body,0,tags);
                if(!match.Success || !Guid.TryParseExact(match.Groups[1].Value,"D",out _))continue;
                try{
                    using var doc=JsonDocument.Parse(body.AsMemory(tags+separator.Length));
                    if(doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("service_tier",out var tier)
                        || tier.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(tier.GetString()))continue;
                    string speed=TurnSpeeds.Canonical(tier.GetString()!);
                    var key=new SpeedKey(thread,match.Groups[1].Value,match.Groups[2].Value);
                    observed[key]=observed.TryGetValue(key,out var other) && other!=speed?"ambiguous":speed;
                }catch(JsonException){ }
            }
        }finally{
            ClearSnapshot();
        }
        return observed;
    }

    bool Save(Dictionary<SpeedKey,string> observed)
    {
        using var db=Data.Open(CacheFile,SqliteOpenMode.ReadWriteCreate);
        using var tx=db.BeginTransaction();
        using var q=db.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            CREATE TABLE IF NOT EXISTS turn_speed(root TEXT COLLATE NOCASE NOT NULL,thread TEXT NOT NULL,
                turn TEXT NOT NULL,model TEXT NOT NULL,speed TEXT NOT NULL,PRIMARY KEY(root,thread,turn,model)) WITHOUT ROWID
            """;
        q.ExecuteNonQuery();
        q.CommandText="""
            INSERT INTO turn_speed VALUES($root,$thread,$turn,$model,$speed)
            ON CONFLICT(root,thread,turn,model) DO UPDATE SET speed='ambiguous'
            WHERE speed<>excluded.speed AND speed<>'ambiguous'
            """;
        q.Parameters.AddWithValue("$root",root);
        var thread=q.Parameters.Add("$thread",SqliteType.Text);var turn=q.Parameters.Add("$turn",SqliteType.Text);
        var model=q.Parameters.Add("$model",SqliteType.Text);var speed=q.Parameters.Add("$speed",SqliteType.Text);
        bool changed=false;
        foreach(var (key,value) in observed){
            thread.Value=key.Thread;turn.Value=key.Turn;model.Value=key.Model;speed.Value=value;
            changed|=q.ExecuteNonQuery()>0;
        }
        tx.Commit();return changed;
    }

    void LoadStored()
    {
        using var db=Data.Open(CacheFile,SqliteOpenMode.ReadOnly);
        using var q=db.CreateCommand();
        q.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='turn_speed'";
        if(q.ExecuteScalar()==null)return;
        q.CommandText="SELECT thread,turn,model,speed FROM turn_speed WHERE root=$root";
        q.Parameters.AddWithValue("$root",root);
        using var r=q.ExecuteReader();
        var tiers=new Dictionary<SpeedKey,string>();
        while(r.Read())tiers.Add(new(r.GetString(0),r.GetString(1),r.GetString(2)),r.GetString(3));
        lock(gate)current=new(tiers);
    }
}
