using Tracker.Protocol;

namespace Tracker.Server;

internal sealed class FinanceTask(string thread,string[] sessions)
{
    public string Thread {get;}=thread;
    public string[] Sessions {get;}=sessions;
    public string? Project {get;set;}
    public string? Group {get;set;}
    public Dictionary<Guid,string> Devices {get;}=[];
    public HashSet<string> Models {get;}=[];
    public long? First {get;private set;}
    public long? Last {get;private set;}
    public decimal Tokens {get;set;}
    public int Unverified {get;set;}
    public decimal? Subscription {get;set;}
    public ProjectHours? Time {get;set;}
    public void Span(long? start,long? end){
        if(start.HasValue && (!First.HasValue || start<First))First=start;
        if(end.HasValue && (!Last.HasValue || end>Last))Last=end;
    }
    internal static List<FinanceTask> Read(Sql db,string account,FinanceLedger ledger,
        Dictionary<string,CostEngine.Assignment> assignments,Dictionary<string,ProjectHours> timing,
        List<CostReport> reports,Revision<Bill>? period)
    {
        var tasks=ledger.Resources.Where(r=>r.Kind=="thread").ToDictionary(r=>FinanceLedger.Field(r,"thread_id")!,r=>{
            var thread=FinanceLedger.Field(r,"thread_id")!;
            var sessions=r.Value!.Value.GetProperty("sessions").EnumerateArray().Select(s=>s.GetString()!).ToArray();
            return new FinanceTask(thread,sessions){Project=assignments.GetValueOrDefault(thread).Project,
                Group=assignments.GetValueOrDefault(thread).GroupName,Time=timing.GetValueOrDefault(thread)};
        });
        foreach(var (thread,device,name) in db.List("""
            SELECT DISTINCT o.thread_id,d.id,d.name FROM observation o JOIN device d ON d.acct=o.acct AND d.id=o.device_id
            WHERE o.acct=$1 AND o.fact IS NOT NULL
            """,r=>(r.GetString(0),r.GetGuid(1),r.GetString(2)),account))
            if(tasks.TryGetValue(thread,out var t))t.Devices[device]=name;

        if(period==null){
            var events=db.List("""
                SELECT value->>'thread_id',value->>'model',value->>'confidence',
                    (value->'usage'->>'total')::numeric,(value->>'occurred_ms')::bigint
                FROM resource WHERE acct=$1 AND kind IN ('event','pending') AND value IS NOT NULL
                """,r=>(Thread:r.GetString(0),Model:r.IsDBNull(1)?"UNKNOWN":r.GetString(1),Quality:r.GetString(2),
                    Tokens:r.IsDBNull(3)?0:r.GetDecimal(3),At:r.IsDBNull(4)?(long?)null:r.GetInt64(4)),account);
            foreach(var e in events)if(tasks.TryGetValue(e.Thread,out var t)){
                t.Models.Add(e.Model);t.Span(e.At,e.At);
                if(e.Quality is "OBSERVED" or "DERIVED")t.Tokens+=e.Tokens;else t.Unverified++;
            }
        }else{
            foreach(var e in reports.Single().Evidence.Where(e=>e.At.HasValue))if(tasks.TryGetValue(e.Thread,out var t)){
                t.Span(e.At,e.At);t.Models.Add(e.Model);
                if(e.Confidence is "OBSERVED" or "DERIVED" && e.Usage!=null)t.Tokens+=e.Usage.Total;else t.Unverified++;
            }
        }
        var first=period?.Value.Boundary(period.Value.Start)??0;
        var last=period?.Value.Boundary(period.Value.End)??long.MaxValue;
        foreach(var (thread,a,b) in db.List("""
            SELECT value->>'thread_id',(value->>'start_ms')::bigint,(value->>'end_ms')::bigint
            FROM resource WHERE acct=$1 AND kind='timing' AND value IS NOT NULL
            """,r=>(r.GetString(0),r.GetInt64(1),r.GetInt64(2)),account))
            if(tasks.TryGetValue(thread,out var t) && a<last && b>=first)t.Span(Math.Max(a,first),Math.Min(b,last-1));
        foreach(var group in reports.SelectMany(r=>r.Lines).GroupBy(l=>l.Thread))
            if(tasks.TryGetValue(group.Key,out var t) && group.All(l=>l.AllocationCny.HasValue))t.Subscription=group.Sum(l=>l.AllocationCny);
        return tasks.Values.Where(t=>period==null || t.First.HasValue || t.Subscription.HasValue).ToList();
    }
}
