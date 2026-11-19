using System.Net.Http.Json;
using System.Text.Json;

namespace CodexTracker.Tray;

internal sealed record RateTable(string Base,DateOnly Date,Dictionary<string,decimal> Rates)
{
    internal bool Valid=>Base=="USD" && Date>new DateOnly(1999,1,1) && Date<=DateOnly.FromDateTime(DateTime.UtcNow)
        && Rates!=null && Money.Codes.Where(code=>code!="USD").All(code=>Rates.TryGetValue(code,out var value) && value is >0.000001m and <1_000_000m);
    public decimal RateFor(string code)=>code=="USD"?1m:Rates[code];
    internal decimal Between(string from,string to)=>RateFor(to)/RateFor(from);
}

internal sealed class MarketRates
{
    const string Url="https://api.frankfurter.dev/v1/latest?base=USD";
    static readonly HttpClient Client=new(){Timeout=TimeSpan.FromSeconds(8)};
    sealed record Cached(DateTimeOffset CheckedAt,RateTable Table);
    readonly string path;
    readonly object gate=new();
    RateTable? table;
    DateTimeOffset nextCheck;
    bool refreshing;

    internal MarketRates(string directory){
        path=Path.Combine(directory,"exchange-rates.json");
        try{
            if(!File.Exists(path) || new FileInfo(path).Length>64_000)return;
            var cached=JsonSerializer.Deserialize<Cached>(File.ReadAllBytes(path));
            if(cached?.Table is {Valid:true} saved){
                table=saved;
                if(cached.CheckedAt<=DateTimeOffset.UtcNow)nextCheck=cached.CheckedAt.AddHours(6);
            }
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){ }
    }

    internal RateTable? ForConversion(){
        lock(gate){
            if(!refreshing && DateTimeOffset.UtcNow>=nextCheck){
                refreshing=true;
                _=Task.Run(RefreshAsync);
            }
            return table;
        }
    }

    async Task RefreshAsync(){
        var retry=TimeSpan.FromMinutes(10);
        try{
            var latest=await Client.GetFromJsonAsync<RateTable>(Url);
            if(latest is not {Valid:true})return;
            lock(gate){
                if(table!=null && latest.Date<table.Date)return;
                table=latest;
            }
            retry=TimeSpan.FromHours(6);
            var temporary=path+".tmp";
            await File.WriteAllBytesAsync(temporary,JsonSerializer.SerializeToUtf8Bytes(new Cached(DateTimeOffset.UtcNow,latest)));
            File.Move(temporary,path,true);
        }catch(Exception e) when(e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException){
            // Keep the last published reference date visible when offline.
        }finally{
            lock(gate){refreshing=false;nextCheck=DateTimeOffset.UtcNow+retry;}
        }
    }
}
