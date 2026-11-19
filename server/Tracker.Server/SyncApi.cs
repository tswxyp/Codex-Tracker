using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tracker.Protocol;
namespace Tracker.Server;

internal static class SyncApi
{
    static long Head(Sql db,Actor a)=>db.One<long>("SELECT head FROM account_stream WHERE acct=$1",a.Account);
    internal static void Map(WebApplication app)
    {
        var settings=app.Services.GetRequiredService<Settings>();
        app.MapGet("/api/v1/sync/state",(Func<HttpContext,Task<IResult>>)((HttpContext h)=>DeviceAuth.Authorized(h,(db,a)=>
            Results.Json(new State(settings.Epoch,Head(db,a),a.Account,a.Device,a.Grant),Wire.Json))));
        app.MapPost("/api/v1/sync/push",async (HttpContext h)=>{
            var batch=await h.Request.ReadFromJsonAsync<Push>(Wire.Json);
            if(batch==null || batch.Protocol!=1 || batch.Operations==null || batch.Operations.Length is 0 or >200
                || batch.Operations.Any(o=>o==null || !Wire.Valid(o)))return Results.UnprocessableEntity(new{code="INVALID_OPERATION"});
            if(batch.Epoch!=settings.Epoch)return Results.Conflict(new{code="EPOCH_CHANGED"});
            var replies=new List<Receipt>();
            // A committed prefix can be replayed after a dropped connection. Each operation revalidates its grant.
            foreach(var op in batch.Operations){
                var response=await DeviceAuth.Authorized(h,(db,a)=>{
                    replies.Add(Apply(db,a,settings.Epoch,op));return Results.NoContent();
                });
                if(response is not Microsoft.AspNetCore.Http.HttpResults.NoContent)return response;
            }
            return Results.Json(new{epoch=settings.Epoch,results=replies},Wire.Json);
        });
        app.MapGet("/api/v1/sync/changes",(HttpContext h,Guid epoch,long after,long? until,int? limit)=>DeviceAuth.Authorized(h,(db,a)=>{
            if(epoch!=settings.Epoch)return Results.Conflict(new{code="EPOCH_CHANGED"});
            var head=Head(db,a);var upper=until??head;int count=limit??100;
            if(after<0 || upper>head || after>upper || count is <1 or >200)return Results.BadRequest();
            var changes=db.List("SELECT seq,changes::text FROM change_log WHERE acct=$1 AND seq>$2 AND seq<=$3 ORDER BY seq LIMIT $4",
                r=>new Change(r.GetInt64(0),r.GetString(1)=="null"?[]:Wire.Read<Resource[]>(r.GetString(1))),a.Account,after,upper,count);
            if(changes.Any(c=>c.Resources.Length==0))return Results.StatusCode(410);
            if((after<upper && changes.Count==0) || changes.Where((c,i)=>c.Seq!=after+i+1).Any())return Results.StatusCode(410);
            long next=changes.Count==0?after:changes[^1].Seq;
            return Results.Json(new Page(epoch,upper,next,next<upper,changes.ToArray()),Wire.Json);
        }));
        app.MapPost("/api/v1/sync/complete",(HttpContext h,Guid epoch,long cursor,long until)=>DeviceAuth.Authorized(h,(db,a)=>{
            if(epoch!=settings.Epoch || until!=cursor || cursor<0 || cursor>Head(db,a))return Results.Conflict();
            db.Run("UPDATE device SET last_sync=now() WHERE acct=$1 AND id=$2",a.Account,a.Device);return Results.NoContent();
        }));
        app.MapPost("/api/v1/sync/snapshots",(Func<HttpContext,Task<IResult>>)((HttpContext h)=>DeviceAuth.Authorized(h,(db,a)=>{
            var id=Guid.NewGuid();var watermark=Head(db,a);
            db.Run("DELETE FROM snapshot WHERE acct=$1 AND device_id=$2 AND expires<now()",a.Account,a.Device);
            db.Run("INSERT INTO snapshot VALUES($1,$2,$3,$4,$5,now()+interval '30 minutes',$6)",a.Account,a.Device,id,settings.Epoch,watermark,a.Grant);
            var items=db.List("SELECT kind,id,version,value::text FROM resource WHERE acct=$1 ORDER BY kind,id",ReadResource,a.Account)
                .Select(r=>Wire.Element(new{resource=r})).ToList();
            items.AddRange(db.List("SELECT id,rev,fact::text FROM observation WHERE acct=$1 AND device_id=$2 ORDER BY id",
                r=>Wire.Element(new{observation_id=r.GetGuid(0),source_rev=r.GetInt64(1),fact=r.IsDBNull(2)?null:Wire.Read<Fact>(r.GetString(2))}),a.Account,a.Device));
            var hashes=new List<string>();
            foreach(var chunk in items.Chunk(200)){
                string body=Wire.Pack(chunk);
                db.Run("INSERT INTO snapshot_page VALUES($1,$2,$3,$4,$5)",a.Account,a.Device,id,hashes.Count,body);
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
            }
            return Results.Json(new{id,epoch=settings.Epoch,watermark,hashes});
        })));
        app.MapGet("/api/v1/sync/snapshots/{id:guid}/pages/{page:int}",(HttpContext h,Guid id,int page)=>DeviceAuth.Authorized(h,(db,a)=>{
            var body=db.One<string>("SELECT p.content FROM snapshot_page p JOIN snapshot s USING(acct,device_id,id) WHERE p.acct=$1 AND p.device_id=$2 AND p.id=$3 AND p.page=$4 AND s.epoch=$5 AND s.grant_id=$6 AND s.expires>now()",a.Account,a.Device,id,page,settings.Epoch,a.Grant);
            return body==null?Results.NotFound():Results.Text(body,"application/json");
        }));
    }
    static Resource ReadResource(Npgsql.NpgsqlDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.IsDBNull(3),
        r.IsDBNull(3)?null:Wire.Read<JsonElement>(r.GetString(3)));
    static Receipt Apply(Sql db,Actor a,Guid epoch,Operation op)
    {
        var digest=Wire.Digest(op);
        var old=db.List("SELECT digest,result::text FROM receipt WHERE acct=$1 AND epoch=$2 AND device_id=$3 AND op_id=$4",
            r=>(Digest:r.GetString(0),Result:r.GetString(1)),a.Account,epoch,a.Device,op.OpId).SingleOrDefault();
        if(old.Result!=null)return old.Digest==digest?Wire.Read<Receipt>(old.Result):new(op.OpId,"OP_ID_REUSE",Head(db,a));
        var changes=new List<Resource>();
        var status="applied";string? conflict=null;
        if(op.ObservationId is Guid oid){
            var prior=db.List("SELECT rev,fact::text,thread_id,disputed FROM observation WHERE acct=$1 AND device_id=$2 AND id=$3",
                r=>(Rev:r.GetInt64(0),Fact:r.IsDBNull(1)?null:Wire.Read<Fact>(r.GetString(1)),Thread:r.IsDBNull(2)?null:r.GetString(2),Disputed:r.GetBoolean(3)),
                a.Account,a.Device,oid).SingleOrDefault();
            bool disputed=false;
            if(prior.Rev>op.SourceRev)status="superseded";
            else if(prior.Rev==op.SourceRev && prior.Fact==op.Fact && !prior.Disputed)status="duplicate";
            else if(prior.Rev==op.SourceRev || prior.Thread!=null && op.Fact!=null && prior.Thread!=op.Fact.ThreadId){
                status="recorded_conflict";disputed=true;
                conflict=Wire.Key("ct.source-conflict",a.Account,a.Device.ToString(),oid.ToString());
                Put(db,a.Account,"conflict",conflict,Wire.Element(new{reason="SOURCE_CONFLICT",observation_id=oid}),changes);
                db.Run("UPDATE observation SET disputed=true WHERE acct=$1 AND device_id=$2 AND id=$3",a.Account,a.Device,oid);
            }else db.Run("""
                INSERT INTO observation(acct,device_id,id,rev,thread_id,fact) VALUES($1,$2,$3,$4,$5,$6::jsonb)
                ON CONFLICT(acct,device_id,id) DO UPDATE SET rev=excluded.rev,fact=excluded.fact,thread_id=coalesce(excluded.thread_id,observation.thread_id),disputed=false
                """,a.Account,a.Device,oid,op.SourceRev,op.Fact?.ThreadId,op.Fact==null?null:Wire.Pack(op.Fact));
            if(prior.Disputed && status=="applied")
                Put(db,a.Account,"conflict",Wire.Key("ct.source-conflict",a.Account,a.Device.ToString(),oid.ToString()),null,changes);
            if((disputed || status=="applied") && (prior.Thread??op.Fact?.ThreadId) is string thread){
                var touched=new HashSet<string>{thread};
                foreach(var f in new[]{prior.Fact,op.Fact}.OfType<Fact>().Where(f=>f.Type is "response" or "quality"))
                    touched.UnionWith(db.List("""
                        SELECT DISTINCT thread_id FROM observation WHERE acct=$1 AND fact->>'type'='snapshot'
                          AND (fact->>'turn_id'=$2 OR (fact->>'occurred_ms')::bigint BETWEEN $3::bigint-2000 AND $3::bigint+2000)
                        """,r=>r.GetString(0),a.Account,f.TurnId,f.OccurredMs));
                foreach(var target in touched)ProjectThread(db,a,target,changes);
            }
        }else{
            conflict=EditResource(db,a.Account,op,changes);
            if(conflict!=null)status="recorded_conflict";
        }
        var seq=Publish(db,a.Account,changes);
        var receipt=new Receipt(op.OpId,status,seq,op.SourceRev,conflict);
        db.Run("INSERT INTO receipt VALUES($1,$2,$3,$4,$5,$6::jsonb)",a.Account,epoch,a.Device,op.OpId,digest,Wire.Pack(receipt));
        return receipt;
    }
    internal static string? EditResource(Sql db,string account,Operation op,List<Resource> changes)
    {
        string? conflict=null;
        var kind=op.Kind.Split('.')[0];var id=op.ResourceId!.Value.ToString();
        var current=db.List("SELECT kind,id,version,value::text FROM resource WHERE acct=$1 AND kind=$2 AND id=$3",ReadResource,account,kind,id).SingleOrDefault();
        bool deleting=op.Kind.EndsWith(".delete") || op.Kind is "member.remove" or "conflict.resolve";
        bool creating=op.Kind.EndsWith(".create");
        bool exists=current is {Deleted:false};
        bool valid=(current?.Version??0)==op.BaseVersion && (creating?current==null:op.Kind=="member.set"?current?.Deleted!=true:exists);
        var resolving=op.Value?.ConflictId;
        var edit=op.Value==null?null:op.Value with { ConflictId=null };
        if(resolving is Guid conflictId)
            valid&=db.One<bool>("SELECT EXISTS(SELECT 1 FROM resource WHERE acct=$1 AND kind='conflict' AND id=$2 AND value->>'reason'='EDIT_CONFLICT' AND value->>'kind'=$3 AND value->>'id'=$4)",
                account,conflictId.ToString(),kind,id);
        bool Ref(string k,string ident)=>db.One<bool>("SELECT EXISTS(SELECT 1 FROM resource WHERE acct=$1 AND kind=$2 AND id=$3 AND value IS NOT NULL)",account,k,ident);
        if(edit?.ProjectId is Guid project)valid&=Ref("project",project.ToString());
        if(edit?.GroupId is Guid group){
            valid&=Ref("group",group.ToString());
            valid&=edit.ThreadId!=null?Ref("thread",Wire.Key("ct.thread.v1",account,edit.ThreadId))
                :db.One<bool>("SELECT EXISTS(SELECT 1 FROM resource WHERE acct=$1 AND kind='thread' AND (value->'sessions') ? $2)",account,edit.SessionId);
            valid&=!db.One<bool>("SELECT EXISTS(SELECT 1 FROM resource WHERE acct=$1 AND kind='member' AND id<>$2 AND ((value->>'thread_id')=$3 OR (value->>'session_id')=$4))",account,id,edit.ThreadId,edit.SessionId);
        }
        if(!valid){
            conflict=op.OpId.ToString();
            Put(db,account,"conflict",conflict,Wire.Element(new {reason="EDIT_CONFLICT",kind,id,base_version=op.BaseVersion,
                current_version=current?.Version??0,current=current?.Value,proposed=edit,operation=op.Kind}),changes);
        }else{
            Put(db,account,kind,id,deleting?null:Wire.Element(edit),changes);
            if(resolving is Guid done)Put(db,account,"conflict",done.ToString(),null,changes);
            if(deleting && kind is "project" or "group"){
                var dependents=db.List("SELECT kind,id,version,value::text FROM resource WHERE acct=$1 AND ((kind='group' AND value->>'project_id'=$2) OR (kind='member' AND value->>'group_id'=$3))",
                    ReadResource,account,kind=="project"?id:"",kind=="group"?id:"");
                foreach(var item in dependents){
                    var revised=kind=="project"?Wire.Element(Wire.Read<Edit>(item.Value!.Value.GetRawText()) with {ProjectId=null}):(JsonElement?)null;
                    Put(db,account,item.Kind,item.Id,revised,changes);
                }
            }
        }
        return conflict;
    }
    internal static long Publish(Sql db,string account,List<Resource> changes)
    {
        var seq=db.One<long>("SELECT head FROM account_stream WHERE acct=$1",account);
        if(changes.Count>0){
            seq=db.One<long>("UPDATE account_stream SET head=head+1 WHERE acct=$1 RETURNING head",account);
            var payload=Wire.Pack(changes);
            db.Run("INSERT INTO change_log(acct,seq,changes) VALUES($1,$2,$3::jsonb)",account,seq,Encoding.UTF8.GetByteCount(payload)>524288?"null":payload);
            if(seq%128==0)db.Run("DELETE FROM change_log WHERE acct=$1 AND created_at < now()-interval '90 days'",account);
        }
        return seq;
    }
    static void Put(Sql db,string account,string kind,string id,JsonElement? value,List<Resource> changes)
    {
        long ver=db.One<long>("""
            INSERT INTO resource(acct,kind,id,version,value) VALUES($1,$2,$3,1,$4::jsonb)
            ON CONFLICT(acct,kind,id) DO UPDATE SET version=resource.version+1,value=excluded.value
            WHERE resource.value IS DISTINCT FROM excluded.value RETURNING version
            """,account,kind,id,value?.GetRawText());
        if(ver>0)changes.Add(new(kind,id,ver,value==null,value));
    }
    static void ProjectThread(Sql db,Actor a,string thread,List<Resource> changes){
        var evidence=db.List("SELECT device_id,id,fact::text,disputed FROM observation WHERE acct=$1 AND thread_id=$2 AND fact IS NOT NULL ORDER BY device_id,id",
            r=>new Evidence(r.GetGuid(0),r.GetGuid(1),Wire.Read<Fact>(r.GetString(2)),r.GetBoolean(3)),a.Account,thread);
        var ambiguous=db.List("""
            SELECT s.device_id,s.id FROM observation s WHERE s.acct=$1 AND s.thread_id=$2 AND s.fact->>'type'='snapshot'
              AND EXISTS(SELECT 1 FROM observation r WHERE r.acct=s.acct AND r.thread_id<>s.thread_id AND r.fact->>'type' IN ('response','quality')
                AND (r.fact->>'turn_id'=s.fact->>'turn_id' OR
                ((r.fact->>'occurred_ms')::bigint BETWEEN (s.fact->>'occurred_ms')::bigint-2000 AND (s.fact->>'occurred_ms')::bigint+2000
                  AND r.fact->'usage'=s.fact->'usage' AND r.fact->'thread_cumulative'=s.fact->'thread_cumulative')))
            """,r=>(r.GetGuid(0),r.GetGuid(1)),a.Account,thread).ToHashSet();
        var next=Projection.Thread(a.Account,thread,evidence,ambiguous);
        var existing=db.List("SELECT kind,id FROM resource WHERE acct=$1 AND (kind IN ('thread','event','timing','pending') OR (kind='conflict' AND value->>'reason'='METERING_CONFLICT')) AND value->>'thread_id'=$2",
            r=>(r.GetString(0),r.GetString(1)),a.Account,thread);
        foreach(var k in existing.Where(k=>!next.ContainsKey(k)))Put(db,a.Account,k.Item1,k.Item2,null,changes);
        foreach(var (k,v) in next)Put(db,a.Account,k.Item1,k.Item2,v,changes);
    }
}
