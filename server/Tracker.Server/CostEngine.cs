using System.Numerics;
using System.Text.Json;
using CodexTracker.Billing;
using Tracker.Protocol;

namespace Tracker.Server;

internal sealed record Meter(string Key,long Version,string Thread,string Model,string Speed,long? At,string Confidence,Usage? Usage);
internal sealed record Charge(decimal Known,int Terms,string[] Missing,Guid? Rule,long? Revision,Dictionary<string,decimal?> Components);
internal sealed record CostLine(string Thread,string? Project,string? ProjectName,string? TaskGroup,string? GroupName,
    decimal ApiKnown,string ApiStatus,decimal? Credits,decimal? Share,decimal? Allocation,decimal? AllocationCny);
internal sealed record PricedMeter(string Key,long Version,string Thread,long? At,string Model,string Speed,string Confidence,Usage? Usage,Charge Api,Charge Weight);
internal sealed record DeviceCoverage(Guid Device,long? LastSync,bool Revoked,bool Pending);
internal sealed record CostReport(Guid BillingId,long BillVersion,Bill Bill,long Start,long End,long Through,
    string Basis,string[] Reasons,decimal KnownApi,string ApiStatus,List<CostLine> Lines,
    List<PricedMeter> Evidence,List<DeviceCoverage> Devices,List<Revision<CostReset>> Resets,
    List<Revision<CreditSheet>> CreditEvidence,List<Revision<Rate>> Rules,long UsageVersion,Guid Epoch,string CalculationVersion="cost-v1");

internal static class CostEngine
{
    internal static Charge Price(Meter e,IEnumerable<Revision<Rate>> rules)
    {
        static Charge Missing(string reason)=>new(0,0,[reason],null,null,[]);
        if(e.At==null)return Missing("MISSING_TIME");
        if(e.Confidence is not ("OBSERVED" or "DERIVED") || e.Usage==null)return Missing("UNVERIFIED_USAGE");
        var candidates=rules.Where(x=>x.Value.Model==e.Model && x.Value.From<=e.At &&
            (x.Value.Until==null || e.At<x.Value.Until)).ToList();
        if(e.Speed=="UNKNOWN" && candidates.Any(x=>x.Value.Speed!="any"))return Missing("MISSING_SPEED");
        var rule=candidates.SingleOrDefault(x=>x.Value.Speed==e.Speed)??candidates.SingleOrDefault(x=>x.Value.Speed=="any");
        if(rule==null)return Missing("MISSING_RULE");
        var r=rule.Value;
        var u=e.Usage;
        decimal amount=0;
        int terms=0;
        var missing=new List<string>();
        var components=new Dictionary<string,decimal?>();
        void Add(string field,long? count,decimal? rate)
        {
            if(count==0){components[field]=0;return;}
            if(count==null || rate==null){components[field]=null;missing.Add(field);return;}
            var subtotal=count.Value*rate.Value/1_000_000m;
            components[field]=subtotal;
            amount+=subtotal;
            terms++;
        }
        long? ordinary=u.Input-u.CacheRead;
        if(u.CacheWrite!=0){
            if(r.WriteBasis=="included")ordinary-=u.CacheWrite;
            if(r.WriteBasis=="unknown")ordinary=null;
        }
        if(ordinary<0)missing.Add("OVERLAPPING_INPUT");
        else{
            Add("INPUT",ordinary,r.Input);
            Add("CACHE_READ",u.CacheRead,r.Read);
            Add("CACHE_WRITE",u.CacheWrite,r.WriteBasis=="unknown"?null:r.Write);
        }
        Add("OUTPUT",u.Output,r.Output);
        return new(amount,terms,missing.ToArray(),rule.Id,rule.Version,components);
    }

    // Scale all decimal weights to a shared integer unit before multiplying by paid minor units.
    static BigInteger Units(decimal v)
    {
        int[] bits=decimal.GetBits(v);
        BigInteger n=(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
        return n*BigInteger.Pow(10,28-((bits[3]>>16)&255));
    }
    static Dictionary<string,decimal> Split(Dictionary<string,BigInteger> units,BigInteger total,decimal amount,int digits)
    {
        int factor=(int)Math.Pow(10,digits);
        var cents=new BigInteger(amount*factor);
        var parts=units.Select(k=>{
            var whole=BigInteger.DivRem(cents*k.Value,total,out var rem);
            return (k.Key,Whole:whole,Remain:rem);
        }).OrderByDescending(r=>r.Remain).ThenBy(r=>r.Key,StringComparer.Ordinal).ToList();
        int left=(int)(cents-parts.Aggregate(BigInteger.Zero,(n,p)=>n+p.Whole));
        return parts.Select((x,i)=>(x.Key,Amount:(decimal)(x.Whole+(i<left?1:0))/factor)).ToDictionary(x=>x.Key,x=>x.Amount);
    }

    internal readonly record struct Assignment(string Thread,string? Project,string? Name,string? Group,string? GroupName);
    // A bill can opt into this fixed reference set for its entire period. Its rates
    // and identity are also included in archived statements, like manually saved rules.
    static List<Revision<Rate>> PersonalWeights(long start,long end)
    {
        var rules=new List<Revision<Rate>>();
        foreach(var (model,rate) in CodexCreditRates.Rates){
            foreach(var speed in new[]{"standard","fast","priority","ultrafast"}){
                if(rate.Multiplier(speed) is not decimal factor)continue;
                var key=Wire.Key(CodexCreditRates.Version,model,speed);
                var id=new Guid(Convert.FromHexString(key[..32]));
                var weight=new Rate("WEIGHT",CodexCreditRates.Version,model,speed,start,end,
                    rate.Input*factor,rate.Read*factor,0,rate.Output*factor,"separate",CodexCreditRates.Source);
                rules.Add(new(id,1,weight));
            }
        }
        return rules;
    }
    internal static Dictionary<string,Assignment> Assignments(Sql db,string account)
    {
        return db.List("""
            SELECT t.value->>'thread_id',
                CASE WHEN count(DISTINCT coalesce(g.value->>'project_id',''))=1 THEN min(p.id) END,
                CASE WHEN count(DISTINCT coalesce(g.value->>'project_id',''))=1 THEN min(p.value->>'name') END,
                CASE WHEN count(DISTINCT g.id)=1 THEN min(g.id) END,
                CASE WHEN count(DISTINCT g.id)=1 THEN min(g.value->>'name') END
            FROM resource t
            JOIN resource m ON m.acct=t.acct AND m.kind='member' AND
                (m.value->>'thread_id'=t.value->>'thread_id' OR (t.value->'sessions') ? (m.value->>'session_id'))
            JOIN resource g ON g.acct=t.acct AND g.kind='group' AND g.id=m.value->>'group_id' AND g.value IS NOT NULL
            LEFT JOIN resource p ON p.acct=t.acct AND p.kind='project' AND p.id=g.value->>'project_id' AND p.value IS NOT NULL
            WHERE t.acct=$1 AND t.kind='thread' GROUP BY t.value->>'thread_id'
            """,r=>new Assignment(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),
                r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4)),account).ToDictionary(x=>x.Thread);
    }

    internal static CostReport Calculate(Sql db,string account,Revision<Bill> invoice,CostBooks books,long now,Guid epoch)
    {
        var bill=invoice.Value;
        long start=bill.Boundary(bill.Start),end=bill.Boundary(bill.End);
        var through=end<=now?end:Math.Clamp(bill.CoveredThrough??now,start,end);
        var devices=db.List("""
            SELECT d.id,floor(extract(epoch from d.last_sync)*1000)::bigint,d.revoked,coalesce(c.pending,false)
            FROM device d LEFT JOIN cost_coverage c ON (c.acct,c.device_id)=(d.acct,d.id) WHERE d.acct=$1 ORDER BY d.id
            """,r=>new DeviceCoverage(r.GetGuid(0),r.IsDBNull(1)?null:r.GetInt64(1),r.GetBoolean(2),r.GetBoolean(3)),account);
        var speeds=db.List("""
            SELECT o.thread_id,o.fact->>'response_id',o.fact->>'ordinal',s.speed FROM cost_speed s
            JOIN observation o USING(acct,device_id,id) WHERE s.acct=$1 AND s.rev=o.rev
            AND NOT o.disputed AND o.fact->>'type' IN ('response','snapshot')
            """,r=>{
                var thread=r.GetString(0);
                var key=!r.IsDBNull(1)?Wire.Key("ct.response.v1",account,thread,r.GetString(1)):
                    !r.IsDBNull(2)?Wire.Key("ct.snapshot.v1",account,thread,thread,r.GetString(2)):"";
                return (Key:key,Speed:r.GetString(3));
            },account).ToLookup(x=>x.Key,x=>x.Speed);
        // Older canonical snapshots omit event time. Accept a time only when all live sources agree.
        var snapshotTimes=db.List("""
            SELECT thread_id,fact->>'ordinal',min((fact->>'occurred_ms')::bigint) FROM observation
            WHERE acct=$1 AND fact->>'type'='snapshot' AND fact->>'ordinal' IS NOT NULL
            GROUP BY thread_id,fact->>'ordinal'
            HAVING bool_and(NOT disputed AND fact->>'occurred_ms' IS NOT NULL) AND count(DISTINCT fact->>'occurred_ms')=1
            """,r=>(Key:Wire.Key("ct.snapshot.v1",account,r.GetString(0),r.GetString(0),r.GetString(1)),Time:r.GetInt64(2)),account)
            .ToDictionary(x=>x.Key,x=>x.Time);
        var meters=db.List("""
            SELECT id,version,value::text FROM resource WHERE acct=$1 AND kind IN ('event','pending') AND value IS NOT NULL
            AND ((value->>'occurred_ms') IS NULL OR (value->>'occurred_ms')::bigint >= $2 AND (value->>'occurred_ms')::bigint < $3)
            ORDER BY id
            """,r=>{
                var v=Wire.Read<JsonElement>(r.GetString(2));
                var key=r.GetString(0);
                var modes=speeds[key].Distinct().ToArray();
                return new Meter(key,r.GetInt64(1),v.GetProperty("thread_id").GetString()!,
                    v.TryGetProperty("model",out var m)?m.GetString()!:"UNKNOWN",
                    modes.Length==1?modes[0]:"UNKNOWN",
                    v.TryGetProperty("occurred_ms",out var t)?t.GetInt64():snapshotTimes.TryGetValue(key,out var known)?known:null,v.GetProperty("confidence").GetString()!,
                    v.TryGetProperty("usage",out var u)?u.Deserialize<Usage>(Wire.Json):null);
            },account,start,through).Where(m=>m.At==null || m.At>=start && m.At<through).ToList();
        var assignments=Assignments(db,account);
        var prices=books.Rules.Where(x=>x.Value.Type=="PRICE" && x.Value.Unit==bill.ApiCurrency).ToList();
        var weights=books.Rules.Where(x=>x.Value.Type=="WEIGHT" && x.Value.Unit==bill.WeightSet).ToList();
        if(bill.WeightSet==CodexCreditRates.Version && weights.Count==0)weights=PersonalWeights(start,end);
        var evidence=meters.Select(e=>new PricedMeter(e.Key,e.Version,e.Thread,e.At,e.Model,e.Speed,e.Confidence,e.Usage,Price(e,prices),Price(e,weights))).ToList();
        var reasons=new List<string>();
        if(books.Bills.Any(b=>b.Id!=invoice.Id && b.Value.Boundary(b.Value.Start)<through && start<b.Value.Boundary(b.Value.End)))
            reasons.Add("OVERLAPPING_BILLS");
        if(now<start || through<=start || bill.CoveredThrough==null || bill.CoveredThrough<through)reasons.Add("COVERAGE_UNCONFIRMED");
        if(devices.Any(d=>d.Pending || d.LastSync==null || d.LastSync<through))reasons.Add("DEVICE_SYNC_PENDING");
        var sheets=books.Credits.Where(x=>x.Value.BillId==invoice.Id).ToList();
        var official=sheets.FirstOrDefault(x=>x.Value.Evidence=="SETTLED" && x.Value.Start==start && x.Value.Through==through &&
            x.Value.Parts.Values.Sum()==x.Value.Total);
        var basis=official==null?"ESTIMATED":"OFFICIAL";
        Dictionary<string,decimal> credits;
        if(official!=null)credits=new(official.Value.Parts,StringComparer.Ordinal);
        else{
            if(evidence.Any(e=>e.Weight.Missing.Length>0))reasons.Add("INCOMPLETE_WEIGHT");
            credits=evidence.GroupBy(e=>e.Thread).ToDictionary(g=>g.Key,g=>g.Sum(e=>e.Weight.Known));
        }
        var units=credits.ToDictionary(x=>x.Key,x=>Units(x.Value));
        var total=units.Values.Aggregate(BigInteger.Zero,(n,v)=>n+v);
        if(total==0)reasons.Add("ZERO_DENOMINATOR");
        var usable=reasons.Count==0;
        var money=usable?Split(units,total,bill.Paid,CostInputs.Digits(bill.Currency)):[];
        var cny=usable?Split(units,total,bill.PaidCny,2):[];
        static string Status(IEnumerable<Charge> items){
            var rows=items.ToList();
            if(rows.Count==0)return "UNKNOWN";
            if(rows.All(r=>r.Missing.Length==0))return "ESTIMATED";
            return rows.Any(r=>r.Terms>0 || r.Missing.Length==0)?"PARTIAL":"UNKNOWN";
        }
        var grouped=evidence.ToLookup(e=>e.Thread);
        var lines=new List<CostLine>();
        foreach(var key in credits.Keys.Union(grouped.Select(g=>g.Key)).Order(StringComparer.Ordinal)){
            var api=grouped[key].Select(e=>e.Api).ToList();
            bool included=credits.ContainsKey(key);
            // A metered task absent from the official statement has no proved task allocation.
            var assignment=assignments.GetValueOrDefault(key);
            lines.Add(new(key,assignment.Project,assignment.Name,assignment.Group,assignment.GroupName,api.Sum(x=>x.Known),Status(api),
                included && (official!=null || grouped[key].All(e=>e.Weight.Missing.Length==0))?credits[key]:null,
                usable && included?(decimal)(units[key]*100_000_000/total)/1_000_000m:null,
                usable && included?money[key]:null,usable && included?cny[key]:null));
        }
        return new(invoice.Id,invoice.Version,bill,start,end,through,basis,reasons.ToArray(),lines.Sum(l=>l.ApiKnown),
            Status(evidence.Select(e=>e.Api)),lines,evidence,devices,books.Resets.Where(r=>r.Value.BillId==invoice.Id).ToList(),sheets,
            prices.Concat(weights).ToList(),db.One<long>("SELECT head FROM account_stream WHERE acct=$1",account),epoch);
    }
}
