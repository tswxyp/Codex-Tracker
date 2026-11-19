using System.Text.Json;

namespace CodexTracker;

internal sealed record Evidence(string Id,Fact Fact);

internal static class Projection
{
    public static void Build(Store db,string device,string thread)
    {
        var events=db.Rows("SELECT id,data FROM Event WHERE device_id=$p0 AND thread_id=$p1 ORDER BY time_ms,id",device,thread)
            .Select(r=>new Evidence((string)r[0]!,JsonSerializer.Deserialize<Fact>((string)r[1]!)!))
            .OrderBy(e=>e.Fact.Time).ThenBy(e=>e.Fact.Ordinal).ToList();
        db.Run("DELETE FROM TokenLedger WHERE device_id=$p0 AND thread_id=$p1",device,thread);
        db.Run("DELETE FROM Reconciliation WHERE device_id=$p0 AND thread_id=$p1",device,thread);
        db.Run("DELETE FROM DataQuality WHERE device_id=$p0 AND thread_id=$p1 AND source_id IS NULL",device,thread);
        void Issue(Evidence e,string code,string confidence="PARTIAL") =>
            db.Run("INSERT OR IGNORE INTO DataQuality VALUES($p0,$p1,$p2,NULL,$p3,$p4,$p5,'')",
                Store.Key(device,e.Id,code),device,thread,e.Id,code,confidence);
        var contexts=events.Where(e=>e.Fact.Kind=="context" && e.Fact.Turn!=null)
            .ToLookup(e=>e.Fact.Turn,e=>e.Fact);
        void Post(Evidence e,Usage u,string key,string method,bool uncertainOrigin=false)
        {
            var f=e.Fact;
            var earlier=contexts[f.Turn].Where(c=>c.Time<f.Time || (c.Time==f.Time && c.Time.HasValue &&
                (c.Ordinal==null || f.Ordinal==null || c.Ordinal<=f.Ordinal)))
                .OrderByDescending(c=>c.Time).ThenByDescending(c=>c.Ordinal).ToList();
            var options=earlier.Where(c=>c.Time==earlier[0].Time && c.Ordinal==earlier[0].Ordinal)
                .Select(c=>(c.Model,c.Effort,c.Speed)).Distinct().ToList();
            var model=f.Model; var effort=f.Effort; var speed=f.Speed;
            if(model==null && options.Count==1) (model,effort,speed)=options[0];
            if(model==null) Issue(e,"MODEL_UNKNOWN","UNKNOWN");
            if(!u.Complete) Issue(e,"TOKEN_FIELD_MISSING");
            if(uncertainOrigin) Issue(e,"FORK_PROVENANCE_PARTIAL");
            db.Run("INSERT INTO TokenLedger VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)",
                device,thread,key,e.Id,f.Turn,model??"UNKNOWN",effort,speed,method,
                !u.Complete || uncertainOrigin ? "PARTIAL" : method=="response" ? "OBSERVED" : "DERIVED",
                u.Input,u.Output,u.Read,u.Write,u.Reason,u.Total);
        }

        var responses=events.Where(e=>e.Fact.Kind=="response").ToList();
        var candidates=new List<Evidence>();
        foreach(var gr in responses.GroupBy(e=>e.Fact.Response))
        {
            var first=gr.First();
            if(gr.Key==null || gr.Any(e=>e.Fact.MissingOwner || e.Fact.Delta?.Valid!=true))
            {
                foreach(var e in gr) Issue(e,"INVALID_RESPONSE","UNKNOWN");
                continue;
            }
            if(gr.Select(e=>(e.Fact.Delta,e.Fact.Turn)).Distinct().Count()!=1)
            {
                foreach(var e in gr) Issue(e,"RESPONSE_CONFLICT","UNKNOWN");
                continue;
            }
            Post(first,first.Fact.Delta!,Store.Key(thread,gr.Key),"response");
            candidates.AddRange(gr);
        }
        var byVectors=candidates.ToLookup(e=>(e.Fact.Delta,e.Fact.Cumulative));
        var modernTurns=responses.Select(e=>e.Fact.Turn).ToHashSet();
        var crossTurns=db.Rows("""
            SELECT DISTINCT turn_id FROM Event WHERE device_id=$p0 AND kind='response' AND thread_id<>$p1 AND turn_id IN
                (SELECT turn_id FROM Event WHERE device_id=$p0 AND thread_id=$p1 AND kind='snapshot')
            """,device,thread).Select(r=>(string?)r[0]).ToHashSet();
        bool honestBaselines=responses.Count>0 && candidates.Count==responses.Count &&
            candidates.All(e=>e.Fact.Time.HasValue && e.Fact.Cumulative?.Valid==true && e.Fact.Delta?.Complete==true &&
                e.Fact.Cumulative.Complete && e.Fact.Cumulative.Minus(e.Fact.Delta).Valid);
        var firstResponse=responses.Select(e=>e.Fact.Time).Min();
        // A fork can repeat its parent's old-format transcript under the child's metadata.
        var ancestors=new HashSet<string>();
        var pending=new Queue<string>();pending.Enqueue(thread);
        while(pending.TryDequeue(out var child))
        {
            foreach(var row in db.Rows("SELECT parent_id,fork_id,history_thread_id FROM Session WHERE device_id=$p0 AND thread_id=$p1",device,child))
                foreach(var source in row.OfType<string>())
                    if(source!=thread && ancestors.Add(source))pending.Enqueue(source);
        }
        Usage? previous=null;
        bool uncertainBaseline=false;
        var matched=new HashSet<string>();
        foreach(var e in events.Where(e=>e.Fact.Kind=="snapshot"))
        {
            var f=e.Fact;
            var u=f.Cumulative;
            string status; string? paired=null;
            if (f.MissingOwner || !f.Time.HasValue || u?.Valid!=true)
            {
                status="INVALID_SNAPSHOT"; Issue(e,status,"UNKNOWN");
                uncertainBaseline=true;
            }
            else if(previous==u) status="REPEATED";
            else if(previous!=null && u.Regresses(previous))
            {
                status="COUNTER_REGRESSION"; Issue(e,status); uncertainBaseline=true;
                // Keep the high-water mark. Replayed lower values cannot create another debit.
            }
            else
            {
                var delta=previous==null ? u : u.Minus(previous);
                bool safePrefix=honestBaselines && f.Turn!=null && !modernTurns.Contains(f.Turn) && f.Time<firstResponse &&
                    candidates.All(x=>!x.Fact.Cumulative!.Minus(x.Fact.Delta!).Regresses(u));
                var other=db.Rows("SELECT id,data FROM Event WHERE device_id=$p0 AND kind='response' AND thread_id<>$p1 AND time_ms BETWEEN $p2 AND $p3",
                    device,thread,f.Time-2000,f.Time+2000).Where(r=>
                    {
                        var alt=JsonSerializer.Deserialize<Fact>((string)r[1]!)!;
                        return alt.Delta==f.Delta && alt.Cumulative==u && alt.Response!=null;
                    }).ToList();
                bool inherited=ancestors.Count>0 && db.Rows("SELECT thread_id,data FROM Event WHERE device_id=$p0 AND kind='snapshot' AND time_ms=$p1",
                    device,f.Time).Any(row=>
                    {
                        if(!ancestors.Contains((string)row[0]!))return false;
                        var original=JsonSerializer.Deserialize<Fact>((string)row[1]!)!;
                        return original.Delta==f.Delta && original.Cumulative==u;
                    });
                if(inherited){status="INHERITED_SNAPSHOT";Issue(e,status);}
                else if(other.Count>0 || crossTurns.Contains(f.Turn))
                {
                    status=other.Count==1?"CROSS_THREAD_MATCH":"AMBIGUOUS_OWNER";
                    if(other.Count==1)paired=(string)other[0][0]!;
                    Issue(e,status);
                }
                else if(responses.Count>0 && !safePrefix)
                {
                    var hits=byVectors[(f.Delta,u)].Where(x=>x.Fact.Time.HasValue &&
                        Math.Abs(x.Fact.Time.Value-f.Time.Value)<=2000 &&
                        (f.Turn==null || x.Fact.Turn==f.Turn)).DistinctBy(x=>x.Fact.Response).Take(2).ToList();
                    if(hits.Count==1)
                    {
                        status="MATCHED"; paired=hits[0].Id; matched.Add(hits[0].Fact.Response!);
                    }
                    else {status="UNMATCHED_SNAPSHOT"; Issue(e,status);}
                }
                else if(uncertainBaseline || !delta.Valid || delta!=f.Delta)
                {
                    status=previous==null?"OPENING_BALANCE_UNKNOWN":"DELTA_UNCONFIRMED"; Issue(e,status);
                }
                else
                {
                    status="VERIFIED_DELTA";
                    if(delta.Total>0) Post(e,delta,Store.Key(thread,e.Id),"snapshot_delta",ancestors.Count>0);
                }
                previous=u; uncertainBaseline=false;
            }
            db.Run("INSERT INTO Reconciliation VALUES($p0,$p1,$p2,$p3,$p4)",e.Id,device,thread,status,paired);
        }
        foreach(var e in candidates.DistinctBy(e=>e.Fact.Response))
            if(!matched.Contains(e.Fact.Response!)) Issue(e,"RESPONSE_WITHOUT_SNAPSHOT");
        foreach(var e in events.Where(x=>x.Fact.Time==null)) Issue(e,"TIMESTAMP_UNKNOWN","UNKNOWN");
        Timing.Build(db,device,thread,events);
    }
}

internal static class Timing
{
    internal readonly record struct Span(long Start,long End);
    private static List<Span> Union(IEnumerable<Span> spans)
    {
        var result=new List<Span>();
        foreach(var s in spans.Where(s=>s.End>s.Start).OrderBy(s=>s.Start))
        {
            if(result.Count==0 || result[^1].End<s.Start) result.Add(s);
            else result[^1]=new(result[^1].Start,Math.Max(result[^1].End,s.End));
        }
        return result;
    }
    public static void Build(Store db,string device,string thread,List<Evidence> events)
    {
        var facts=events.Select(e=>e.Fact).ToList();
        var boundaries=facts.SelectMany(f=>new long?[]{f.Time,f.Start,f.End}).Where(n=>n is >=0 and <=253402300799999).Select(n=>n!.Value).ToList();
        long? first=boundaries.Count==0 ? null:boundaries.Min(), last=boundaries.Count==0 ? null:boundaries.Max();
        var paused=new List<Span>(); var working=new List<Span>();
        var runs=new Dictionary<string,string>(); long? pauseStart=null; long? completed=null;
        string state="UNKNOWN";
        var transitions=facts.Where(f=>f.Kind is "start" or "resume" or "finish" or "interrupt" or "pause")
            .Select(f=>(f,at:f.Kind is "finish" or "interrupt" ? f.End??f.Time : f.Kind=="start" ? f.Start??f.Time : f.Time))
            .Where(x=>x.at.HasValue).OrderBy(x=>x.at).ThenBy(x=>x.f.Time);
        foreach(var (f,at) in transitions)
        {
            var key=f.Turn??"unknown";
            if(f.Kind is "start" or "resume") {runs[key]="RUNNING"; completed=null;}
            else runs[key]=f.Kind=="finish"?"COMPLETED":"PAUSED";
            state=runs.ContainsValue("RUNNING")?"RUNNING":runs.ContainsValue("PAUSED")?"PAUSED":"COMPLETED";
            // A new turn after an interrupted one resumes this conversation.
            if(f.Kind is "start" or "resume")
                foreach(var old in runs.Where(k=>k.Key!=key && k.Value=="PAUSED").Select(k=>k.Key).ToList()) runs.Remove(old);
            if(state=="PAUSED") pauseStart??=at;
            else if(pauseStart is long t){paused.Add(new(t,at!.Value));pauseStart=null;}
            completed=state=="COMPLETED"?at:null;
        }
        var starts=facts.Where(f=>f.Kind=="start").Select(f=>f.Start??f.Time).Where(t=>t.HasValue).ToList();
        if(starts.Count>0) first=starts.Min();
        if(state=="COMPLETED" && completed>=first) last=completed;
        if(last<first) last=first;
        if(pauseStart is long p && last.HasValue) paused.Add(new(p,last.Value));
        foreach(var gap in db.Rows("SELECT start_ms,end_ms FROM PowerGap WHERE device_id=$p0 AND start_ms<$p1 AND end_ms>$p2",device,last,first))
            paused.Add(new((long)gap[0]!, (long)gap[1]!));
        var pauses=Union(paused.Select(s=>new Span(Math.Max(s.Start,first??0),Math.Min(s.End,last??0))));
        // Only short explicitly timed model items count. Tool waits and inter-event gaps have no activity evidence.
        foreach(var f in facts.Where(f=>f.Kind=="work" && f.Start is >=0 && f.End is >=0 && f.End>f.Start && f.End-f.Start<=60_000))
        {
            if(first==null||last==null) continue;
            var a=Math.Max(first.Value,f.Start!.Value);var b=Math.Min(last.Value,f.End!.Value);
            foreach(var skip in pauses)
            {
                if(skip.End<=a)continue;
                if(skip.Start>=b)break;
                if(skip.Start>a)working.Add(new(a,skip.Start));
                a=Math.Max(a,skip.End);
            }
            if(a<b)working.Add(new(a,b));
        }
        var active=Union(working);
        db.Run("DELETE FROM TimingInterval WHERE device_id=$p0 AND thread_id=$p1",device,thread);
        foreach(var s in pauses) Save(s,"PAUSED","pause/resume or recorded system sleep");
        foreach(var s in active) Save(s,"ACTIVE","timed model item");
        void Save(Span s,string kind,string evidence) =>
            db.Run("INSERT INTO TimingInterval VALUES($p0,$p1,$p2,$p3,$p4,$p5)",device,thread,s.Start,s.End,kind,evidence);
        var total=(last-first)??0;var act=active.Sum(s=>s.End-s.Start);var pause=pauses.Sum(s=>s.End-s.Start);
        db.Run("INSERT OR REPLACE INTO TaskTiming VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10)",
            device,thread,first,last,completed,total,act,pause,total-act-pause,state,
            first==null?"UNKNOWN":total>act+pause?"PARTIAL":"OBSERVED");
    }
}
