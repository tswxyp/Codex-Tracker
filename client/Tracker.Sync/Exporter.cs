using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tracker.Protocol;
namespace Tracker.Sync;

internal static class Exporter
{
    static SqliteConnection Open(string path)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        db.Open();
        if(!db.ApplicationIdOk()){db.Dispose();throw new InvalidDataException("需要 Tracker SQLite v2 统计库。");}
        return db;
    }
    static bool ApplicationIdOk(this SqliteConnection db)
    {
        using var q=db.CreateCommand();q.CommandText="PRAGMA application_id";var id=(long)q.ExecuteScalar()!;
        q.CommandText="PRAGMA user_version";return id==0x43545831 && (long)q.ExecuteScalar()! ==2;
    }
    internal static void List(string path){
        using var db=Open(path);using var q=db.CreateCommand();
        q.CommandText="SELECT device_id,thread_id,session_id FROM Session ORDER BY device_id,thread_id";
        using var r=q.ExecuteReader();while(r.Read())Console.WriteLine($"{r[0]}\t{r[1]}\t{r[2]}");
    }
    internal static void Allow(Sidecar local,string path,string dev,string thread)
    {
        if(!Wire.Id(thread)||thread.StartsWith("unresolved-",StringComparison.Ordinal))throw new ArgumentException("线程身份未核实。");
        using var db=Open(path);using var q=db.CreateCommand();
        q.CommandText="SELECT count(*) FROM Session WHERE device_id=$d AND thread_id=$t";
        q.Parameters.AddWithValue("$d",dev);q.Parameters.AddWithValue("$t",thread);
        if((long)q.ExecuteScalar()! != 1)throw new ArgumentException("未知线程。");
        local.Run("INSERT INTO scope(p,db,device,thread) VALUES($p0,$p1,$p2,$p3) ON CONFLICT DO NOTHING",local.Active,Path.GetFullPath(path),dev,thread);
        if(local.Scalar("SELECT p FROM scope WHERE db=$p0 AND device=$p1 AND thread=$p2",Path.GetFullPath(path),dev,thread) as string!=local.Active)
            throw new InvalidOperationException("该来源已绑定另一账号。");
    }
    internal static bool Collect(Sidecar local)
    {
        var profile=local.Active;
        if(local.Get("refresh/"+profile)==null)throw new InvalidOperationException("此来源分区缺少有效设备授权，待传记录已保存。");
        var scopes=local.Rows("SELECT id,db,device,thread FROM scope WHERE p=$p0",r=>(Id:r.GetInt64(0),Path:r.GetString(1),Device:r.GetString(2),Thread:r.GetString(3)),profile);
        bool complete=true;
        foreach(var sc in scopes){
            if(local.Scalar("SELECT reason FROM source_review WHERE scope=$p0",sc.Id)!=null){
                complete=false;
                continue;
            }
            try{CollectScope(local,profile,sc);}
            catch(InvalidDataException ex){
                local.Run("INSERT OR IGNORE INTO source_review VALUES($p0,$p1)",sc.Id,ex.Message);
                Console.Error.WriteLine($"来源 {sc.Id} 待核实：{ex.Message}");
                complete=false;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SqliteException){
                Console.Error.WriteLine($"来源 {sc.Id} 暂不可读，已保留清单。");
                complete=false;
            }
        }
        return complete;
    }
    static void CollectScope(Sidecar local,string profile,(long Id,string Path,string Device,string Thread) sc)
    {
            var fileKey=SourceFileId.Read(sc.Path);
            var known=local.Scalar("SELECT file_key FROM source_identity WHERE scope=$p0",sc.Id) as string;
            if(known!=null && known!=fileKey)throw new InvalidDataException("统计库文件已替换，已暂停此来源。");
            using var db=Open(sc.Path);using var tx=db.BeginTransaction(deferred:true);
            var facts=new Dictionary<string,Fact>();
            var eventIds=new HashSet<string>();
            SqliteCommand Query(string sql){
                var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;
                c.Parameters.AddWithValue("$d",sc.Device);c.Parameters.AddWithValue("$t",sc.Thread);return c;
            }
            static string? Str(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
            static long? Num(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
            using(var q=Query("SELECT session_id,parent_id,fork_id,is_subagent FROM Session WHERE device_id=$d AND thread_id=$t"))
            using(var r=q.ExecuteReader()){
                if(!r.Read())throw new InvalidDataException("来源线程消失，已保留清单。");
                facts["lineage"]=new("lineage",sc.Thread,Str(r,0),ParentId:Str(r,1),ForkId:Str(r,2),Subagent:r.GetInt32(3)!=0);
            }
            using(var q=Query("""
                SELECT e.id,e.kind,e.time_ms,e.turn_id,json_extract(e.data,'$.Response'),json_extract(e.data,'$.Ordinal'),
                       l.entry_id,l.method,l.model,l.confidence,l.input,l.output,l.cache_read,l.cache_write,l.reasoning,l.total,
                       json_extract(e.data,'$.Cumulative.Input'),json_extract(e.data,'$.Cumulative.Output'),
                       json_extract(e.data,'$.Cumulative.Read'),json_extract(e.data,'$.Cumulative.Write'),
                       json_extract(e.data,'$.Cumulative.Reason'),json_extract(e.data,'$.Cumulative.Total'),
                       (SELECT min(code) FROM DataQuality WHERE event_id=e.id AND code IN ('RESPONSE_CONFLICT','INVALID_RESPONSE'))
                FROM Event e LEFT JOIN TokenLedger l ON l.event_id=e.id AND l.device_id=e.device_id AND l.thread_id=e.thread_id
                WHERE e.device_id=$d AND e.thread_id=$t
                """))
            using(var r=q.ExecuteReader())while(r.Read()){
                var eventId=r.GetString(0);eventIds.Add(eventId);
                string? response=Str(r,4);var lineage=facts["lineage"];
                if(Str(r,22) is string reason && Wire.Id(response)){
                    facts["quality/"+eventId]=new("quality",sc.Thread,lineage.SessionId,Str(r,3),response,Num(r,2),Kind:reason);
                }
                if(r.IsDBNull(6))continue;
                var usage=new Usage(r.GetInt64(10),r.GetInt64(11),Num(r,12),Num(r,13),Num(r,14),r.GetInt64(15));
                Usage? cumulative=Num(r,16) is long input && Num(r,17) is long output && Num(r,21) is long total
                    ?new(input,output,Num(r,18),Num(r,19),Num(r,20),total):null;
                var f=new Fact(r.GetString(7)=="response"?"response":"snapshot",sc.Thread,lineage.SessionId,
                    Str(r,3),response,Num(r,2),Wire.Model(Str(r,8)),Str(r,9),usage,cumulative,Num(r,5));
                facts["ledger/"+r.GetString(6)]=f;
            }
            using(var q=Query("""
                SELECT 'BOUNDARY',first_ms,last_ms FROM TaskTiming WHERE device_id=$d AND thread_id=$t
                UNION ALL SELECT kind,start_ms,end_ms FROM TimingInterval WHERE device_id=$d AND thread_id=$t
                """))
            using(var r=q.ExecuteReader())while(r.Read()){
                if(Num(r,1) is not long start || Num(r,2) is not long end)continue;
                var kind=r.GetString(0);
                facts[kind=="BOUNDARY"?kind:$"{kind}/{start}/{end}"]=new("timing",sc.Thread,StartMs:start,EndMs:end,Kind:kind);
            }
            tx.Commit();
            if(SourceFileId.Read(sc.Path)!=fileKey)throw new InvalidDataException("读取期间统计库已替换，保留上一次清单。");
            if(facts.Values.Any(f=>!Wire.Valid(f)))throw new InvalidDataException("来源包含不支持的统计字段，未提交此线程变更。");
            local.Tx(()=>{
                local.Run("INSERT INTO source_identity VALUES($p0,$p1) ON CONFLICT DO NOTHING",sc.Id,fileKey);
                var prev=local.Rows("SELECT event FROM seen WHERE scope=$p0",r=>r.GetString(0),sc.Id).ToHashSet();
                if(!prev.IsSubsetOf(eventIds))throw new InvalidDataException("来源历史回退，请核对统计库；已保留待传记录。");
                foreach(var e in eventIds.Except(prev))local.Run("INSERT INTO seen VALUES($p0,$p1)",sc.Id,e);
                var old=local.Rows("SELECT slot,id,rev,fact FROM manifest WHERE scope=$p0",r=>(Slot:r.GetString(0),Id:Guid.Parse(r.GetString(1)),
                    Rev:r.GetInt64(2),Body:Str(r,3)),sc.Id).ToDictionary(x=>x.Slot);
                foreach(var slot in old.Keys.Union(facts.Keys)){
                    old.TryGetValue(slot,out var previous);facts.TryGetValue(slot,out var f);
                    string? body=f==null?null:Wire.Pack(f);
                    if(body==previous.Body)continue;
                    var id=previous.Id==Guid.Empty?Guid.NewGuid():previous.Id;long rev=previous.Rev+1;
                    local.Run("""
                        INSERT INTO manifest VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(scope,slot)
                        DO UPDATE SET rev=excluded.rev,fact=excluded.fact
                        """,sc.Id,slot,id.ToString(),rev,body);
                    local.Enqueue(profile,new(Guid.NewGuid(),f==null?"observation.retract":"observation.upsert",id,rev,f));
                }
            });
    }
}
