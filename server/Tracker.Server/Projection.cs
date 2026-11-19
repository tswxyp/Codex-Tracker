using System.Text.Json;
using Tracker.Protocol;
namespace Tracker.Server;

internal sealed record Evidence(Guid Device,Guid Id,Fact Fact,bool Disputed);
internal static class Projection
{
    // Values in this map become independent versioned resources in the account change feed.
    internal static Dictionary<(string,string),JsonElement> Thread(string account,string thread,List<Evidence> observations,HashSet<(Guid,Guid)> ambiguous)
    {
        var result=new Dictionary<(string,string),JsonElement>();
        if(observations.Count==0)return result;
        var threadKey=Wire.Key("ct.thread.v1",account,thread);
        var facts=observations.Select(o=>o.Fact).ToList();
        var lineage=observations.Where(x=>!x.Disputed && x.Fact.Type=="lineage").Select(x=>x.Fact).ToList();
        result[("thread",threadKey)]=Wire.Element(new {thread_id=thread,
            sessions=facts.Select(x=>x.SessionId).OfType<string>().Distinct().Order(StringComparer.Ordinal),
            parents=lineage.SelectMany(x=>new[]{x.ParentId,x.ForkId}).OfType<string>().Distinct().Order(StringComparer.Ordinal),
            subagent=lineage.Any(x=>x.Subagent==true)});
        var responses=observations.Where(x=>x.Fact.Type=="response").ToList();
        var negatives=facts.Where(f=>f.Type=="quality").Select(f=>f.ResponseId).ToHashSet();
        foreach(var group in responses.GroupBy(x=>x.Fact.ResponseId!))
        {
            var f=group.First().Fact;
            var key=Wire.Key("ct.response.v1",account,thread,group.Key);
            bool conflict=negatives.Contains(group.Key) || group.Any(x=>x.Disputed || x.Fact.Usage!=f.Usage || x.Fact.TurnId!=f.TurnId);
            var status=conflict?"CONFLICT":group.Any(x=>x.Fact.Confidence=="OBSERVED") && f.Usage!.Complete?"OBSERVED":"PARTIAL";
            var models=group.Select(x=>x.Fact.Model).OfType<string>().Where(x=>x!="UNKNOWN").Distinct().ToArray();
            result[("event",key)]=Wire.Element(new{thread_id=thread,response_id=group.Key,confidence=status,
                usage=conflict?null:f.Usage,model=models.Length==1?models[0]:"UNKNOWN",sources=group.Count(),
                occurred_ms=group.Select(x=>x.Fact.OccurredMs).Distinct().Count()==1?f.OccurredMs:null});
            if(conflict)result[("conflict",key)]=Wire.Element(new{reason="METERING_CONFLICT",thread_id=thread,response_id=group.Key,
                observations=group.Select(x=>new{device_id=x.Device,observation_id=x.Id})});
        }
        foreach(var response in negatives.Where(id=>!responses.Any(r=>r.Fact.ResponseId==id)))
            result[("event",Wire.Key("ct.response.v1",account,thread,response!))]=Wire.Element(new{thread_id=thread,response_id=response,confidence="CONFLICT"});
        var snaps=observations.Where(x=>x.Fact.Type=="snapshot").ToList();
        Usage? previous=null;
        foreach(var group in snaps.Where(x=>x.Fact.Ordinal.HasValue).GroupBy(x=>x.Fact.Ordinal!.Value).OrderBy(g=>g.Key))
        {
            var f=group.First().Fact;
            var key=Wire.Key("ct.snapshot.v1",account,thread,thread,group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var cumulative=f.ThreadCumulative!;
            bool same=group.All(x=>!x.Disputed && x.Fact.Usage==f.Usage && x.Fact.ThreadCumulative==cumulative
                && x.Fact.TurnId==f.TurnId && x.Fact.OccurredMs==f.OccurredMs);
            bool prefix=responses.All(x=>x.Fact.TurnId!=f.TurnId && f.OccurredMs<x.Fact.OccurredMs
                && Before(cumulative,x.Fact.ThreadCumulative,x.Fact.Usage));
            bool derived=same && prefix && negatives.Count==0 && group.All(x=>!ambiguous.Contains((x.Device,x.Id))) && snaps.All(x=>x.Fact.Ordinal.HasValue)
                && lineage.Count>0 && !observations.Any(x=>x.Disputed && x.Fact.Type=="lineage")
                && lineage.All(x=>x.ParentId==null && x.ForkId==null && x.Subagent!=true)
                && f.TurnId!=null && f.OccurredMs.HasValue && group.All(x=>x.Fact.Confidence=="DERIVED")
                && f.Usage!.Complete && Delta(cumulative,previous)==f.Usage;
            result[("event",key)]=Wire.Element(new{thread_id=thread,confidence=derived?"DERIVED":"PARTIAL",
                usage=derived?f.Usage:null,model=group.Select(x=>x.Fact.Model).Distinct().Count()==1?f.Model:"UNKNOWN",sources=group.Count()});
            if(same && (previous==null || Before(previous,cumulative,new Usage(0,0,0,0,0,0))))previous=cumulative;
        }
        foreach(var item in snaps.Where(x=>!x.Fact.Ordinal.HasValue))
            result[("pending",Wire.Key("ct.pending.v1",account,item.Device.ToString(),item.Id.ToString()))]=Wire.Element(new{thread_id=thread,confidence="UNKNOWN"});
        var timing=observations.Where(x=>!x.Disputed && x.Fact.Type=="timing").Select(x=>x.Fact).ToList();
        var bounds=timing.Where(x=>x.Kind=="BOUNDARY").ToList();
        if(bounds.Count>0){
            long start=bounds.Min(x=>x.StartMs!.Value),end=bounds.Max(x=>x.EndMs!.Value);
            var edges=new SortedDictionary<long,(int Work,int Pause)> { [start]=(0,0),[end]=(0,0) };
            foreach(var interval in timing.Where(x=>x.Kind!="BOUNDARY")){
                long a=Math.Max(start,interval.StartMs!.Value),b=Math.Min(end,interval.EndMs!.Value);
                if(b<=a)continue;
                void Mark(long at,int sign){var p=edges.GetValueOrDefault(at);edges[at]=interval.Kind=="ACTIVE"?(p.Work+sign,p.Pause):(p.Work,p.Pause+sign);}
                Mark(a,1);Mark(b,-1);
            }
            long active=0,paused=0,last=start;int w=0,p=0;
            foreach(var (at,delta) in edges){if(w>0 && p==0)active+=at-last;if(p>0 && w==0)paused+=at-last;
                w+=delta.Work;p+=delta.Pause;last=at;}
            result[("timing",threadKey)]=Wire.Element(new{thread_id=thread,start_ms=start,end_ms=end,total_ms=end-start,
                active_ms=active,paused_ms=paused,unknown_ms=end-start-active-paused});
        }
        return result;
    }
    static Usage Delta(Usage u,Usage? p)=>p==null?u:new(u.Input-p.Input,u.Output-p.Output,u.CacheRead-p.CacheRead,
        u.CacheWrite-p.CacheWrite,u.Reasoning-p.Reasoning,u.Total-p.Total);
    static bool Before(Usage prior,Usage? total,Usage? increment)
    {
        if(total==null || increment==null || !prior.Complete || !total.Complete || !increment.Complete)return false;
        var baseValue=Delta(total,increment);
        return prior.Input<=baseValue.Input && prior.Output<=baseValue.Output && prior.CacheRead<=baseValue.CacheRead
            && prior.CacheWrite<=baseValue.CacheWrite && prior.Reasoning<=baseValue.Reasoning && prior.Total<=baseValue.Total;
    }
}
