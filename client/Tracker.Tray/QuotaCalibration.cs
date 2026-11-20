using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record WeeklyEstimate(MoneyAmount Cost,decimal Percent,long UnpricedTokens=0,bool AssumedStandard=false);
internal sealed record QuotaCalibration(decimal CreditsPerPoint,int Samples,decimal Min,decimal Max,long SampleEnd,long ValidUntil,bool AssumedStandard=false)
{
    internal WeeklyEstimate? Estimate(IEnumerable<Usage> usage,SubscriptionPlan plan)
    {
        decimal? total=null;
        long unpriced=0;
        bool assumed=false;
        foreach(var row in usage){
            if(CreditWeights.Credits(row,out bool missingSpeed) is not decimal credits){unpriced+=row.Total;continue;}
            assumed|=missingSpeed;
            total=(total??0m)+credits;
        }
        if(total is not decimal units)return null;
        decimal percent=units/CreditsPerPoint;
        var budget=plan.WeeklyBudget;
        return new(new(budget.Amount*percent/100m,budget.Currency),percent,unpriced,units>0 && (assumed || AssumedStandard));
    }
}

internal sealed class QuotaCalibrationCache
{
    QuotaCalibration? cached;
    string? lastDatabase;
    TurnSpeeds? lastSpeeds;
    long nextRead;

    // Called on the sidebar's single background reader. A busy database is retried soon.
    internal QuotaCalibration? Read(string path,string root,TurnSpeeds speeds)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(lastDatabase==path && lastSpeeds==speeds && now<nextRead && (cached==null || now<cached.ValidUntil))return cached;
        if(lastDatabase!=path || lastSpeeds!=speeds || cached?.ValidUntil<=now)cached=null;
        lastDatabase=path;lastSpeeds=speeds;
        try{
            cached=WeeklyCalibration.Read(path,root,now,speeds);
            nextRead=now+(cached==null?30_000:120_000);
        }catch(SqliteException){
            nextRead=now+5_000;
        }
        return cached;
    }
}

internal static class WeeklyCalibration
{
    const long Minute=60_000, ResetTolerance=15*Minute;
    internal static QuotaCalibration? Read(string path,string root,long now,TurnSpeeds speeds)
    {
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using var tx=db.BeginTransaction(deferred:true);
        using var q=db.CreateCommand();
        q.Transaction=tx;
        q.CommandText="SELECT id FROM Device WHERE root=$root";
        q.Parameters.AddWithValue("$root",root);
        if(q.ExecuteScalar() is not string device)return null;
        q.Parameters.Clear();
        q.Parameters.AddWithValue("$dev",device);
        var observations=ReadWindows(q,now-QuotaWindow.Week,now);
        var current=observations.LastOrDefault();
        if(current==null || current.End<=now)return null;
        var rate=Fit(q,observations,current,current.End,speeds);
        // Bridge a natural reset only when both logs explicitly name the same plan.
        if(rate!=null || current.Plan==null)return rate;
        var previousEnd=current.End-QuotaWindow.Week;
        var history=ReadWindows(q,previousEnd-QuotaWindow.Week-ResetTolerance,previousEnd+ResetTolerance);
        var previous=history.LastOrDefault(w=>w.Seen<current.Seen && Math.Abs(w.End-previousEnd)<=ResetTolerance);
        return previous!=null && previous.Plan==current.Plan?Fit(q,history,previous,current.End,speeds):null;
    }

    static List<QuotaWindow> ReadWindows(SqliteCommand q,long from,long to)
    {
        q.CommandText="""
            SELECT e.time_ms,(SELECT raw_metering FROM SourceRecord s WHERE s.event_id=e.id AND raw_metering IS NOT NULL LIMIT 1)
            FROM Event e WHERE device_id=$dev AND kind='snapshot' AND time_ms BETWEEN $from AND $to
            ORDER BY time_ms
            """;
        q.Parameters.AddWithValue("$from",from);
        q.Parameters.AddWithValue("$to",to);
        var result=new List<QuotaWindow>();
        using(var reader=q.ExecuteReader())while(reader.Read()){
            if(reader.IsDBNull(1))continue;
            long seen=reader.GetInt64(0);
            try{
                if(QuotaWindow.From(reader.GetString(1),seen,seen) is {} window)result.Add(window);
            }catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException){ }
        }
        q.Parameters.RemoveAt("$from");
        q.Parameters.RemoveAt("$to");
        return result;
    }

    internal sealed record Interval(long Start,long End,int Points=1);
    internal static List<Interval> Intervals(List<QuotaWindow> windows,QuotaWindow target,int maximumStep=1)
    {
        var result=new List<Interval>();
        QuotaWindow? previous=null,high=null;
        long? crossing=null,firstLowerSeen=null;
        foreach(var w in windows){
            if(w.Seen>target.Seen)break;
            if(w.Plan!=target.Plan || Math.Abs(w.End-target.End)>ResetTolerance){
                previous=high=null;crossing=firstLowerSeen=null;result.Clear();continue;
            }
            if(previous==null || high==null){
                previous=high=w;continue;
            }
            long gap=w.Seen-previous.Seen;
            if(gap>15*Minute)crossing=null;
            if(w.Percent<high.Percent){
                // A chat may report a stale percentage long after other chats first saw
                // the higher reading. Measure the duration of the lower readings themselves.
                firstLowerSeen??=w.Seen;
                if(high.Percent-w.Percent>1 || w.Seen-firstLowerSeen.Value>2*Minute){
                    result.Clear();crossing=null;high=w;
                    firstLowerSeen=null;
                }
            }else{
                firstLowerSeen=null;
                if(w.Percent>high.Percent){
                    decimal step=w.Percent-high.Percent;
                    bool observed=step>=1 && step<=maximumStep && step==decimal.Truncate(step) &&
                        w.Percent<100 && gap<=3*Minute && previous.Percent==high.Percent;
                    if(observed && crossing is long start && w.Seen>start)result.Add(new(start,w.Seen,(int)step));
                    crossing=observed?w.Seen:null;
                    high=w;
                }
            }
            previous=w;
        }
        return result;
    }

    static QuotaCalibration? Fit(SqliteCommand q,List<QuotaWindow> windows,QuotaWindow target,long validUntil,TurnSpeeds speeds)
    {
        var spans=Intervals(windows,target);
        if(spans.Count<3)return null;
        // Discover recent threads via the time index. TaskTiming's last_ms may be a turn
        // completion and can precede a late usage event. TokenLedger owns all deduplication.
        q.CommandText="""
            WITH recent AS (
                SELECT DISTINCT thread_id FROM Event WHERE device_id=$dev AND kind IN ('response','snapshot')
                AND time_ms>$from AND time_ms<=$to
            )
            SELECT e.time_ms,l.model,l.confidence,l.total,l.input,l.output,l.cache_read,l.speed,l.thread_id,l.turn_id
            FROM recent CROSS JOIN TokenLedger l ON l.device_id=$dev AND l.thread_id=recent.thread_id
            CROSS JOIN Event e ON e.id=l.event_id WHERE e.time_ms>$from AND e.time_ms<=$to ORDER BY e.time_ms
            """;
        q.Parameters.AddWithValue("$from",spans[0].Start);
        q.Parameters.AddWithValue("$to",spans[^1].End);
        var sums=new decimal?[spans.Count];
        var assumed=new bool[spans.Count];
        Array.Fill(sums,0m);
        int index=0;
        using(var r=q.ExecuteReader())while(r.Read()){
            long seen=r.GetInt64(0);
            while(index<spans.Count && seen>spans[index].End)index++;
            if(index==spans.Count)break;
            if(seen<=spans[index].Start)continue;
            string model=r.GetString(1);
            string? speed=speeds.Resolve(r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),model,r.IsDBNull(7)?null:r.GetString(7));
            sums[index]+=CreditWeights.Credits(new(model,r.GetString(2),r.GetInt64(3),r.GetInt64(4),
                r.GetInt64(5),r.IsDBNull(6)?null:r.GetInt64(6),null,speed),out bool missingSpeed);
            assumed[index]|=missingSpeed;
        }
        q.Parameters.RemoveAt("$from");
        q.Parameters.RemoveAt("$to");
        var measured=sums.OfType<decimal>().Order().ToArray();
        if(measured.Length<3)return null;
        static decimal Median(decimal[] sorted)=>(sorted[(sorted.Length-1)/2]+sorted[sorted.Length/2])/2;
        decimal center=Median(measured);
        if(center<=0)return null;
        // A second device can move the account meter without any local usage. Require
        // a majority of comparable observations, including zero-usage intervals in the vote.
        var comparable=measured.Where(x=>Math.Abs(x-center)<=center*0.25m).ToArray();
        if(comparable.Length<3 || comparable.Length*2<=measured.Length)return null;
        bool assumedStandard=sums.Where((s,i)=>assumed[i] && s.HasValue && Math.Abs(s.Value-center)<=center*0.25m).Any();
        return new(Median(comparable),comparable.Length,comparable[0],comparable[^1],target.End,validUntil,assumedStandard);
    }
}
