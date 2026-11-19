using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

// Both bases are computed by the account-wide billing engine, after its coverage checks.
internal sealed record BilledShare(decimal Yuan,long Start,long Through,bool Weighted);
internal sealed record PeriodCredits(decimal Amount,bool Weighted,long Through);
internal sealed record CostDisplay(string Share,string Api,string Allocation,string Credits,string Through,string Fetched)
{
    static string DefaultStore=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexTracker","sync","sync.sqlite");
    internal static Uri? ManagementUrl()
    {
        if(!File.Exists(DefaultStore))return null;
        try{
            using var db=Data.Open(DefaultStore,SqliteOpenMode.ReadOnly);
            using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT issuer FROM profile WHERE id=(SELECT v FROM settings WHERE k='active')";
            return cmd.ExecuteScalar() is string address && Uri.TryCreate(address,UriKind.Absolute,out var uri) &&
                uri.Scheme=="https" && uri.UserInfo.Length==0 && uri.PathAndQuery=="/" ? new Uri(uri,"/costs"):null;
        }catch(SqliteException){return null;}
    }
    static string Amount(JsonElement obj,string key,string format)
        =>obj.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.Number && v.TryGetDecimal(out var n)?n.ToString(format,CultureInfo.InvariantCulture):"待核算";
    internal static void Attach(Dictionary<string,Stats> rows,string database,string device,string? store=null)
    {
        var path=store??DefaultStore;
        if(!File.Exists(path) || rows.Count==0)return;
        try{
            using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
            using var cmd=db.CreateCommand();
            cmd.CommandText="""
                SELECT c.thread,c.currency,c.api_currency,c.basis,c.through_ms,c.fetched_ms,c.line,c.start_ms
                FROM cost_cache c JOIN profile p ON p.id=c.p AND p.epoch=c.epoch
                JOIN scope s ON s.p=c.p AND s.thread=c.thread
                WHERE c.p=(SELECT v FROM settings WHERE k='active') AND s.db=$db AND s.device=$device
                  AND c.start_ms<c.through_ms AND c.through_ms<=c.fetched_ms AND c.fetched_ms<=$now AND c.end_ms>$now
                  AND EXISTS(SELECT 1 FROM settings WHERE k='refresh/'||p.id)
                  AND NOT EXISTS(SELECT 1 FROM outbox WHERE p=c.p)
                  AND NOT EXISTS(SELECT 1 FROM source_review WHERE scope=s.id)
                """;
            cmd.Parameters.AddWithValue("$db",Path.GetFullPath(database));cmd.Parameters.AddWithValue("$device",device);
            cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var reader=cmd.ExecuteReader();
            while(reader.Read()){
                if(!rows.TryGetValue(reader.GetString(0),out var stats))continue;
                using var doc=JsonDocument.Parse(reader.GetString(6));var line=doc.RootElement;
                var share=Amount(line,"share","0.####");
                var api=line.GetProperty("api_status").GetString();
                var split=Amount(line,"allocation","0.###");
                string Time(int i)=>DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(i)).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                stats.Cost=new(share=="待核算"?share:share+"% "+reader.GetString(3),
                    api=="UNKNOWN"?"待核算":reader.GetString(2)+" "+Amount(line,"api_known","0.############")+" "+api,
                    split=="待核算"?split:reader.GetString(1)+" "+split+"（账期内暂计）",
                    Amount(line,"credits","0.############")+" "+reader.GetString(3),Time(4),Time(5));
                var basis=reader.GetString(3);
                if(basis is ("OFFICIAL" or "ESTIMATED") && line.TryGetProperty("thread",out var thread) &&
                    thread.GetString()==reader.GetString(0) &&
                    line.TryGetProperty("credits",out var credits) && credits.ValueKind==JsonValueKind.Number &&
                    credits.TryGetDecimal(out var used) && used>=0){
                    stats.PeriodUsage=new(used,basis=="ESTIMATED",reader.GetInt64(4));
                    if(line.TryGetProperty("share",out var fraction) && fraction.ValueKind==JsonValueKind.Number &&
                        fraction.TryGetDecimal(out var percent) && percent is >=0 and <=100 &&
                        line.TryGetProperty("allocation_cny",out var allocation) && allocation.ValueKind==JsonValueKind.Number &&
                        allocation.TryGetDecimal(out var yuan) && yuan>=0)
                        stats.Bill=new(yuan,reader.GetInt64(7),reader.GetInt64(4),basis=="ESTIMATED");
                }
            }
        }catch(SqliteException){ /* Phase 3 profiles can be used without a cost cache. */ }
        catch(JsonException){ /* A malformed cache must not interrupt the native sidebar. */ }
        catch(Exception ex) when(ex is InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException){ }
    }
}
