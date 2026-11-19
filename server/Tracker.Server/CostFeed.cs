using Tracker.Protocol;

namespace Tracker.Server;

internal static class CostFeed
{
    internal sealed record SpeedEntry(Guid Id,long Rev,string? Speed);
    internal static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/costs/speeds",async(HttpContext h)=>{
            var entries=await h.Request.ReadFromJsonAsync<SpeedEntry[]>(Wire.Json);
            if(entries==null || entries.Length>200 || entries.Any(e=>e==null || e.Id==Guid.Empty || e.Rev<=0 ||
                e.Speed is not (null or "standard" or "fast" or "ultrafast" or "priority" or "batch")))return Results.BadRequest();
            return await DeviceAuth.Authorized(h,(db,a)=>{
                foreach(var e in entries){
                    if(e.Speed==null){
                        db.Run("DELETE FROM cost_speed WHERE acct=$1 AND device_id=$2 AND id=$3 AND rev<=$4",a.Account,a.Device,e.Id,e.Rev);
                    }else db.Run("""
                        INSERT INTO cost_speed SELECT acct,device_id,id,rev,$5::text FROM observation
                        WHERE acct=$1 AND device_id=$2 AND id=$3 AND rev=$4 AND fact->>'type' IN ('response','snapshot') AND NOT disputed
                        ON CONFLICT(acct,device_id,id) DO UPDATE SET rev=excluded.rev,speed=excluded.speed
                        """,a.Account,a.Device,e.Id,e.Rev,e.Speed);
                }
                return Results.NoContent();
            });
        });
        app.MapPost("/api/v1/costs/current",(HttpContext h,bool complete)=>DeviceAuth.Authorized(h,(db,a)=>{
            db.Run("INSERT INTO cost_coverage VALUES($1,$2,$3) ON CONFLICT(acct,device_id) DO UPDATE SET pending=excluded.pending",a.Account,a.Device,!complete);
            var now=CostPage.Now;
            var books=CostInputs.Load(db,a.Account);
            var active=books.Bills.Where(b=>b.Value.Boundary(b.Value.Start)<=now && now<b.Value.Boundary(b.Value.End)).Take(2).ToList();
            if(active.Count!=1)return Results.NoContent();
            var current=active[0];
            var epoch=h.RequestServices.GetRequiredService<Settings>().Epoch;
            var r=CostEngine.Calculate(db,a.Account,current,books,now,epoch);
            var own=db.List("SELECT DISTINCT thread_id FROM observation WHERE acct=$1 AND device_id=$2 AND thread_id IS NOT NULL AND fact IS NOT NULL",
                row=>row.GetString(0),a.Account,a.Device).ToHashSet(StringComparer.Ordinal);
            return Results.Json(new {account=a.Account,epoch,bill_id=current.Id,from_ms=r.Start,until_ms=r.End,through_ms=r.Through,
                generated_ms=now,currency=r.Bill.Currency,api_currency=r.Bill.ApiCurrency,basis=r.Basis,reasons=r.Reasons,
                lines=r.Lines.Where(l=>own.Contains(l.Thread)).Select(l=>new{l.Thread,l.ApiKnown,l.ApiStatus,l.Credits,l.Share,l.Allocation,l.AllocationCny})},Wire.Json);
        }));
    }
}
