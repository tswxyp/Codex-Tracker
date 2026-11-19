using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record DailyCost(DateOnly Date,MoneyAmount? Cost,bool AssumedStandard=false);
internal sealed class DailyFigures(DateOnly date)
{
    internal DateOnly Date {get;}=date;
    internal decimal? Received=0m,Cost=0m;
    internal decimal? Net=>Received-Cost;
}
internal sealed record ReportFigures(DailyFigures[] Days,int Undated,bool AssumedStandard);

internal sealed record ProfitReportData(List<IncomeReceipt> Receipts,List<DailyCost> Costs)
{
    internal ReportFigures Calculate(DateOnly from,DateOnly until,DisplayMoney money){
        var days=new SortedDictionary<DateOnly,DailyFigures>();
        DailyFigures Day(DateOnly date){
            if(!days.TryGetValue(date,out var figures))days.Add(date,figures=new(date));
            return figures;
        }
        int undated=0;
        foreach(var receipt in Receipts){
            if(receipt.Date==null){if(receipt.Amount.Minor>0)undated++;continue;}
            if(receipt.Date<from || receipt.Date>until)continue;
            Day(receipt.Date.Value).Received+=money.Convert(receipt.Amount.MoneyValue);
        }
        bool assumed=false;
        foreach(var cost in Costs){
            if(cost.Date<from || cost.Date>until)continue;
            assumed|=cost.AssumedStandard;
            Day(cost.Date).Cost+=cost.Cost is {} charge?money.Convert(charge):null;
        }
        return new(days.Values.ToArray(),undated,assumed);
    }

    internal static ProfitReportData Read(string database,string root,SidebarSettings settings,DateOnly from,DateOnly until,
        QuotaCalibrationCache calibration,CancellationToken cancel,string? threadId)
    {
        var receipts=settings.ReadReceipts(threadId==null?null:("chat",threadId));
        if(threadId==null){
            var manual=receipts.Where(r=>r.Scope=="project").Select(r=>r.Entry).ToHashSet(StringComparer.Ordinal);
            if(manual.Count>0){
                var projects=LocalProjects.Read(root);
                receipts.RemoveAll(r=>r.Scope=="chat" && projects.ByThread.TryGetValue(r.Entry,out var id) && manual.Contains(id));
            }
        }
        var costs=new List<DailyCost>();
        if(!File.Exists(database))return new(receipts,costs);
        var plan=settings.ReadPlan();
        var speeds=settings.Speeds.Snapshot();
        var rate=plan!=null?calibration.Read(database,root,speeds):null;
        cancel.ThrowIfCancellationRequested();
        using var db=Data.Open(database,SqliteOpenMode.ReadOnly);
        // WinSQLite can return NULL for the 'localtime' modifier. Use the same
        // Windows time zone for grouping ledger events and choosing the range.
        db.CreateFunction("tracker_local_day", (long ms)=>
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime).DayNumber,
            isDeterministic:true);
        using var q=db.CreateCommand();
        q.CommandText="SELECT id FROM Device WHERE root=$root";
        q.Parameters.AddWithValue("$root",root);
        if(q.ExecuteScalar() is not string device)return new(receipts,costs);
        // Both endpoints are local midnights, including days with a daylight-saving transition.
        long Midnight(DateOnly day)=>new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local)).ToUnixTimeMilliseconds();
        q.Parameters.Clear();
        q.Parameters.AddWithValue("$dev",device);q.Parameters.AddWithValue("$from",Midnight(from));q.Parameters.AddWithValue("$to",Midnight(until.AddDays(1)));
        string threads=threadId==null?"""
            SELECT DISTINCT thread_id FROM Event WHERE device_id=$dev AND kind IN ('response','snapshot')
            AND time_ms >= $from AND time_ms < $to
            """:"SELECT $thread AS thread_id";
        if(threadId!=null)q.Parameters.AddWithValue("$thread",threadId);
        q.CommandText=$"""
            WITH recent AS ({threads})
            SELECT tracker_local_day(e.time_ms) AS day,l.model,l.confidence,l.speed,
                sum(l.total),sum(l.input),sum(l.output),CASE WHEN count(l.cache_read)=count(*) THEN sum(l.cache_read) END,
                l.thread_id,l.turn_id
            FROM recent CROSS JOIN TokenLedger l ON l.device_id=$dev AND l.thread_id=recent.thread_id
            CROSS JOIN Event e ON e.id=l.event_id WHERE e.time_ms >= $from AND e.time_ms < $to
            GROUP BY l.thread_id,day,l.model,l.confidence,l.speed,l.turn_id
            """;
        using var reader=q.ExecuteReader();
        while(reader.Read()){
            cancel.ThrowIfCancellationRequested();
            string model=reader.GetString(1);
            string? speed=speeds.Resolve(reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),model,reader.IsDBNull(3)?null:reader.GetString(3));
            var usage=new Usage(model,reader.GetString(2),reader.GetInt64(4),reader.GetInt64(5),
                reader.GetInt64(6),reader.IsDBNull(7)?null:reader.GetInt64(7),null,speed);
            var estimate=plan==null?null:rate?.Estimate([usage],plan);
            costs.Add(new(DateOnly.FromDayNumber(reader.GetInt32(0)),
                estimate is {UnpricedTokens:0}?estimate.Cost:null,estimate is {UnpricedTokens:0,AssumedStandard:true}));
        }
        return new(receipts,costs);
    }
}
