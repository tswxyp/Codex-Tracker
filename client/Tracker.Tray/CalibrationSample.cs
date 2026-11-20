using Microsoft.Data.Sqlite;
using CodexTracker.Billing;

namespace CodexTracker.Tray;

// The interval stays in memory for a final local check. It is never included in the wire payload.
internal sealed record CalibrationSample(string Model,string Effort,string Speed,long Input,long Output,
    long CacheRead,long CacheWrite,long Reasoning,long? SnapshotTokens,int ActiveSessions,bool TaskOverlap,
    bool UnattributedUsage,QuotaIntervals.Interval Window)
{
    internal const int PerSize=2, MaxPoints=3;
    internal static List<CalibrationSample> Read(string path,string root,long consent,long cursor,int[] slots,
        TurnSpeeds speeds,CancellationToken stop)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long from=Math.Max(consent,now-2*60*60_000L);
        var samples=new List<CalibrationSample>();
        if(now-from<60_000 || !File.Exists(path))return samples;
        stop.ThrowIfCancellationRequested();
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using(var temp=db.CreateCommand()){temp.CommandText="PRAGMA temp_store=MEMORY";temp.ExecuteNonQuery();}
        using var tx=db.BeginTransaction(deferred:true);
        using var q=db.CreateCommand();
        q.Transaction=tx;
        q.Parameters.AddWithValue("$root",root);
        q.Parameters.AddWithValue("$from",from);
        q.Parameters.AddWithValue("$to",now);
        var spans=ReadSpans(q,stop);
        PrepareIntervalQuery(db,q,speeds);
        foreach(var span in Candidates(spans)){
            stop.ThrowIfCancellationRequested();
            if(span.End>now-120_000)break;
            if(span.Start<cursor || slots[span.Points]==0)continue;
            if(ReadInterval(q,span) is not {} sample)continue;
            samples.Add(sample);slots[span.Points]--;cursor=span.End;
            if(slots.All(n=>n==0))break;
        }
        return samples;
    }

    // Adjoining rises can form one larger observation. Never bridge a broken meter series.
    static IEnumerable<QuotaIntervals.Interval> Candidates(List<QuotaIntervals.Interval> spans)
    {
        for(int i=0;i<spans.Count;i++){
            var span=spans[i];
            yield return span;
            for(int j=i-1;j>=0 && spans[j].End==span.Start;j--){
                int points=span.Points+spans[j].Points;
                if(points>MaxPoints)break;
                span=new(spans[j].Start,span.End,points);
                yield return span;
            }
        }
    }

    static List<QuotaIntervals.Interval> ReadSpans(SqliteCommand q,CancellationToken stop)
    {
        // Project the shared weekly meter inside SQLite; the sampler never reads raw log bodies.
        q.CommandText="""
            WITH meters AS (
                SELECT e.time_ms AS seen,(SELECT raw_metering FROM SourceRecord s WHERE s.event_id=e.id
                    AND raw_metering IS NOT NULL LIMIT 1) AS raw
                FROM Event e WHERE device_id=(SELECT id FROM Device WHERE root=$root) AND kind='snapshot'
                    AND time_ms>$from AND time_ms<=$to
            )
            SELECT seen,json_extract(w.value,'$.used_percent'),json_extract(w.value,'$.resets_at'),
                json_extract(raw,'$.payload.rate_limits.plan_type')
            FROM meters,json_each(CASE WHEN json_valid(raw) THEN json_extract(raw,'$.payload.rate_limits') END) w
            WHERE w.key IN ('primary','secondary') AND w.type='object'
                AND json_extract(w.value,'$.window_minutes')=10080
                AND json_extract(raw,'$.payload.rate_limits.limit_id')='codex'
                AND json_type(w.value,'$.used_percent') IN ('integer','real')
                AND json_type(w.value,'$.resets_at')='integer'
            ORDER BY seen
            """;
        var windows=new List<QuotaWindow>();
        using(var r=q.ExecuteReader())while(r.Read()){
            stop.ThrowIfCancellationRequested();
            long seen=r.GetInt64(0),seconds=r.GetInt64(2);
            decimal percent=r.GetDecimal(1);
            if(seconds is <=0 or >253402300799 || percent is <0 or >100)continue;
            long reset=seconds*1000;
            if(seen>=reset-QuotaWindow.Week && seen<reset)windows.Add(new(seen,reset,percent,r.IsDBNull(3)?null:r.GetString(3)));
        }
        return windows.Count==0?[]:QuotaIntervals.Intervals(windows,windows[^1],MaxPoints);
    }

    internal bool StillMatches(string path,string root,long consent,TurnSpeeds speeds,CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using var q=db.CreateCommand();
        q.CommandText="PRAGMA temp_store=MEMORY";q.ExecuteNonQuery();
        using var tx=db.BeginTransaction(deferred:true);q.Transaction=tx;
        q.Parameters.AddWithValue("$root",root);
        q.Parameters.AddWithValue("$from",Math.Max(consent,Window.Start-20*60_000));
        q.Parameters.AddWithValue("$to",Window.End);
        if(!Candidates(ReadSpans(q,stop)).Contains(Window))return false;
        PrepareIntervalQuery(db,q,speeds);
        return ReadInterval(q,Window)==this;
    }

    static void PrepareIntervalQuery(SqliteConnection db,SqliteCommand q,TurnSpeeds speeds)
    {
        db.CreateFunction<string,string?,string,string?,string?>("sample_speed",speeds.Resolve,isDeterministic:true);
        q.CommandText="""
            WITH interval_events AS (
                SELECT id,thread_id,turn_id,kind,time_ms FROM Event WHERE device_id=(SELECT id FROM Device WHERE root=$root)
                    AND time_ms>$from AND time_ms<=$to AND kind IN ('response','snapshot','start','work','finish','resume','pause','interrupt')
            ), activity AS (
                -- A repeated meter reading alone does not show that an idle chat ran a task.
                SELECT x.* FROM interval_events x WHERE x.kind<>'snapshot' OR EXISTS (
                    SELECT 1 FROM Reconciliation r WHERE r.event_id=x.id AND r.status IN ('MATCHED','VERIFIED_DELTA'))
            ), active_threads AS (
                SELECT thread_id FROM activity
                UNION SELECT thread_id FROM TimingInterval WHERE device_id=(SELECT id FROM Device WHERE root=$root)
                    AND kind='ACTIVE' AND start_ms<$to AND end_ms>$from
            ), turns AS (
                SELECT turn_id,min(time_ms) AS first_seen,max(time_ms) AS last_seen,
                    min(CASE WHEN kind='start' THEN time_ms END) AS started,
                    max(CASE WHEN kind='finish' THEN time_ms END) AS finished
                FROM activity WHERE turn_id IS NOT NULL GROUP BY thread_id,turn_id
            ), ordered_turns AS (
                SELECT *,lag(last_seen) OVER w AS prior_seen,lag(finished) OVER w AS prior_finish
                FROM turns WINDOW w AS (ORDER BY first_seen,turn_id)
            ), boundaries AS (
                SELECT time_ms, json_extract(data,'$.Cumulative.Total') AS total
                FROM Event WHERE device_id=(SELECT id FROM Device WHERE root=$root)
                    AND thread_id IN (SELECT thread_id FROM active_threads) AND kind='snapshot'
                    AND time_ms IN ($from,$to)
            ), ledger AS (
                SELECT l.thread_id,l.turn_id,l.event_id,l.model,l.effort,l.speed,l.confidence,l.input,l.output,
                    l.cache_read,l.cache_write,l.reasoning FROM (SELECT DISTINCT thread_id FROM interval_events) t
                CROSS JOIN TokenLedger l ON l.device_id=(SELECT id FROM Device WHERE root=$root) AND l.thread_id=t.thread_id
                CROSS JOIN Event e ON e.id=l.event_id WHERE e.time_ms>$from AND e.time_ms<=$to
            ), evidence AS (
                SELECT (SELECT count(*) FROM active_threads) AS active_sessions,
                    EXISTS (SELECT 1 FROM ordered_turns WHERE started<prior_finish OR first_seen<prior_seen) AS task_overlap,
                    (EXISTS (SELECT 1 FROM ledger WHERE confidence NOT IN ('OBSERVED','DERIVED') OR turn_id IS NULL)
                    OR EXISTS (SELECT 1 FROM activity WHERE turn_id IS NULL)
                    OR EXISTS (SELECT 1 FROM interval_events x WHERE x.kind='response' AND NOT EXISTS (
                        SELECT 1 FROM ledger t WHERE t.event_id=x.id))
                    OR EXISTS (SELECT 1 FROM interval_events x WHERE x.kind='snapshot' AND NOT EXISTS (
                        SELECT 1 FROM Reconciliation r WHERE r.event_id=x.id AND r.status IN ('MATCHED','VERIFIED_DELTA','REPEATED')))
                    OR EXISTS (SELECT 1 FROM active_threads a WHERE NOT EXISTS (
                        SELECT 1 FROM Session s WHERE s.device_id=(SELECT id FROM Device WHERE root=$root)
                        AND s.thread_id=a.thread_id AND s.session_id IS NOT NULL))) AS unattributed_usage
            )
            SELECT l.model,l.effort,sample_speed(l.thread_id,l.turn_id,l.model,l.speed) AS tier,
                sum(l.input),sum(l.output),sum(l.cache_read),sum(l.cache_write),sum(l.reasoning),
                (SELECT CASE WHEN count(*)=2 AND count(total)=2 AND count(DISTINCT time_ms)=2 THEN
                    max(CASE WHEN time_ms=$to THEN total END)-max(CASE WHEN time_ms=$from THEN total END) END
                    FROM boundaries),a.active_sessions,a.task_overlap,a.unattributed_usage
            FROM ledger l CROSS JOIN evidence a
            WHERE NOT EXISTS (SELECT 1 FROM active_threads t JOIN Session s ON s.thread_id=t.thread_id
                    WHERE s.device_id=(SELECT id FROM Device WHERE root=$root) AND s.is_subagent=1)
                AND NOT EXISTS (SELECT 1 FROM ordered_turns WHERE prior_seen IS NOT NULL
                    AND (started IS NULL OR prior_finish IS NULL OR prior_seen=first_seen))
                AND NOT EXISTS (SELECT 1 FROM ledger WHERE cache_read IS NULL OR cache_write IS NULL OR reasoning IS NULL)
            GROUP BY l.model,l.effort,tier
            """;
    }

    static CalibrationSample? ReadInterval(SqliteCommand q,QuotaIntervals.Interval span)
    {
        q.Parameters["$from"].Value=span.Start;q.Parameters["$to"].Value=span.End;
        using var r=q.ExecuteReader();
        if(!r.Read())return null;
        int activeSessions=r.GetInt32(9);
        bool taskOverlap=r.GetBoolean(10),unattributedUsage=r.GetBoolean(11);
        if(activeSessions!=1 || taskOverlap || unattributedUsage)return null;
        string model=r.GetString(0),effort=r.IsDBNull(1)?"unknown":r.GetString(1),speed=r.IsDBNull(2)?"unknown":r.GetString(2);
        if(!CodexCreditRates.Rates.ContainsKey(model) ||
            effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" or "unknown") ||
            speed is not ("standard" or "fast" or "ultrafast" or "unknown"))return null;
        var sample=new CalibrationSample(model.ToLowerInvariant(),effort,speed,r.GetInt64(3),r.GetInt64(4),
            r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.IsDBNull(8)?null:r.GetInt64(8),
            activeSessions,taskOverlap,unattributedUsage,span);
        return !r.Read() && sample.Input+sample.Output>0?sample:null;
    }
}
