using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record SubscriptionPlan(ProjectIncome Paid,int EstimateDays=30)
{
    internal MoneyAmount WeeklyBudget=>new(Paid.NativeAmount*7/EstimateDays,Paid.Currency);
}

internal sealed record QuotaWindow(long Seen,long End,decimal Percent,string? Plan=null)
{
    internal const long Week=7L*24*60*60*1000;
    internal static QuotaWindow? From(string raw,long seen,long now)
    {
        using var document=JsonDocument.Parse(raw);
        if(!document.RootElement.TryGetProperty("payload",out var payload) ||
            !payload.TryGetProperty("rate_limits",out var limits) || limits.ValueKind!=JsonValueKind.Object ||
            !limits.TryGetProperty("limit_id",out var id) || id.GetString()!="codex")return null;
        foreach(var key in new[]{"primary","secondary"}){
            if(!limits.TryGetProperty(key,out var w) || w.ValueKind!=JsonValueKind.Object ||
                !w.TryGetProperty("window_minutes",out var minutes) || !minutes.TryGetInt32(out var duration) || duration!=10080 ||
                !w.TryGetProperty("resets_at",out var reset) || !reset.TryGetInt64(out var seconds) ||
                seconds<=0 || seconds>253402300799 ||
                !w.TryGetProperty("used_percent",out var p) || !p.TryGetDecimal(out var pct) || pct is <0 or >100)continue;
            long end=seconds*1000;
            if(end-Week<=seen && seen<=now && now<end){
                string? plan=limits.TryGetProperty("plan_type",out var type) && type.ValueKind==JsonValueKind.String?type.GetString():null;
                return new(seen,end,pct,plan);
            }
        }
        return null;
    }
}

// A rate-limit snapshot describes every chat and device on the account.
internal static class WeeklyQuota
{
    internal static QuotaWindow? Read(string path,string root)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using var tx=db.BeginTransaction(deferred:true);
        using var q=db.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT id FROM Device WHERE root=$root";
        q.Parameters.AddWithValue("$root",root);
        if(q.ExecuteScalar() is not string device)return null;
        q.Parameters.Clear();q.Parameters.AddWithValue("$dev",device);q.Parameters.AddWithValue("$now",now);
        q.Parameters.AddWithValue("$from",now-QuotaWindow.Week);
        q.CommandText="""
            SELECT e.time_ms,(SELECT s.raw_metering FROM SourceRecord s WHERE s.event_id=e.id AND s.raw_metering IS NOT NULL LIMIT 1)
            FROM Event e WHERE e.device_id=$dev AND e.kind='snapshot' AND e.time_ms BETWEEN $from AND $now
            ORDER BY e.time_ms DESC LIMIT 64
            """;
        QuotaWindow? window=null;
        using(var r=q.ExecuteReader())while(r.Read()){
            if(r.IsDBNull(1))continue;
            try{window=QuotaWindow.From(r.GetString(1),r.GetInt64(0),now);}
            catch(Exception e) when(e is JsonException or InvalidOperationException){continue;}
            if(window!=null)break;
        }
        tx.Commit();
        return window;
    }
}
