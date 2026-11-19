namespace Tracker.Server;

internal sealed record ProjectHours(long TotalMs,long ActiveMs);
internal static class ProjectTiming
{
    // Reconstruct the same per-thread bounds and active-minus-paused rule as the timing projection,
    // clip to the report window, then take the union of sibling/thread intervals within each project.
    internal static Dictionary<string,ProjectHours> Read(Sql db,string account,long start,long end,
        Dictionary<string,CostEngine.Assignment> assignments, out Dictionary<string,ProjectHours> threads)
    {
        var intervals=db.List("""
            WITH bounds AS (
                SELECT value->>'thread_id' AS thread,
                    int8range((value->>'start_ms')::bigint,(value->>'end_ms')::bigint,'[)')
                        * int8range($2,$3,'[)') AS span FROM resource
                WHERE acct=$1 AND kind='timing' AND value IS NOT NULL
            ), observations AS (
                SELECT thread_id, fact->>'kind' AS kind,
                    int8range((fact->>'start_ms')::bigint,(fact->>'end_ms')::bigint,'[)') AS span
                FROM observation WHERE acct=$1 AND NOT disputed AND fact->>'type'='timing'
                  AND fact->>'kind' IN ('ACTIVE','PAUSED')
            ), parts AS (
                SELECT b.thread,b.span,coalesce(range_agg(o.span*b.span) FILTER(WHERE o.kind='ACTIVE'),'{}'::int8multirange)
                    - coalesce(range_agg(o.span*b.span) FILTER(WHERE o.kind='PAUSED'),'{}'::int8multirange) AS work
                FROM bounds b LEFT JOIN observations o ON b.thread=o.thread_id AND b.span && o.span
                WHERE NOT isempty(b.span) GROUP BY b.thread,b.span
            )
            SELECT thread,false,lower(span),upper(span) FROM parts
            UNION ALL SELECT thread,true,lower(w),upper(w) FROM parts CROSS JOIN LATERAL unnest(work) w
            """,r=>(Thread:r.GetString(0),Active:r.GetBoolean(1),Start:r.GetInt64(2),End:r.GetInt64(3)),account,start,end);
        long Union(IEnumerable<(long Start,long End)> spans){
            long total=0,through=long.MinValue;
            foreach(var (left,right) in spans.OrderBy(x=>x.Start)){
                total+=Math.Max(0,right-Math.Max(left,through));
                through=Math.Max(through,right);
            }
            return total;
        }
        Dictionary<string,ProjectHours> Summarize(Func<string,string> key)=>intervals.GroupBy(i=>key(i.Thread))
            .ToDictionary(g=>g.Key,g=>new ProjectHours(Union(g.Where(i=>!i.Active).Select(i=>(i.Start,i.End))),
                Union(g.Where(i=>i.Active).Select(i=>(i.Start,i.End)))));
        threads=Summarize(thread=>thread);
        return Summarize(thread=>assignments.GetValueOrDefault(thread).Project??CostInputs.Pool);
    }
}
