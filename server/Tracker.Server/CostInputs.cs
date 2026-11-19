using System.Globalization;
using System.Text.Json;
using Tracker.Protocol;

namespace Tracker.Server;

internal sealed record Bill(string Plan, DateOnly Start, DateOnly End, string Zone, string Currency,
    decimal Paid, decimal PaidCny, DateOnly PaymentDate, string ApiCurrency, string WeightSet, long? CoveredThrough)
{
    public long Boundary(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidDataException("所选账期的本地午夜存在时区歧义。");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUnixTimeMilliseconds();
    }
}
internal sealed record Rate(string Type, string Unit, string Model, string Speed, long From, long? Until,
    decimal? Input, decimal? Read, decimal? Write, decimal? Output, string WriteBasis, string Source);
internal sealed record CreditSheet(Guid BillId, long Start, long Through, string Evidence, string Source,
    decimal Total, Dictionary<string,decimal> Parts);
internal sealed record CostReset(Guid BillId, long At, string Type, string Reference, decimal? Before, decimal? After,
    decimal? Cash, string Currency);
internal sealed record Revision<T>(Guid Id, long Version, T Value);
internal sealed record CostBooks(List<Revision<Bill>> Bills, List<Revision<Rate>> Rules,
    List<Revision<CreditSheet>> Credits, List<Revision<CostReset>> Resets);
internal sealed class CostConflict(string message) : Exception(message);

internal static class CostInputs
{
    internal const string Pool = "@pool";
    internal static int Digits(string unit) => unit switch {
        "JPY" or "KRW" => 0, "KWD" or "BHD" => 3,
        "USD" or "CNY" or "EUR" or "GBP" or "CAD" or "AUD" or "NZD" or "HKD" or "SGD" or "TWD" or "INR" or "CHF" => 2,
        _ => throw new InvalidDataException("币种未配置小数精度。")
    };
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    static bool Quantity(decimal x) => x >= 0 && x <= 1_000_000_000_000m && decimal.Round(x,6)==x;
    static bool Money(decimal x, string currency) => Quantity(x) && decimal.Round(x,Digits(currency))==x;
    static bool Ref(string? s) => s is {Length: >0 and <=240} && !string.IsNullOrWhiteSpace(s) && !s.Any(char.IsControl);
    static bool Moment(long x) => x is >=0 and <=253402300799999L;
    internal static List<Revision<T>> Read<T>(Sql db, string account, string kind) =>
        db.List("SELECT DISTINCT ON(id) id,version,body::text FROM cost_revision WHERE acct=$1 AND kind=$2 ORDER BY id,version DESC",
            r=>new Revision<T>(r.GetGuid(0),r.GetInt64(1),Wire.Read<T>(r.GetString(2))),account,kind);
    internal static CostBooks Load(Sql db,string a) => new(Read<Bill>(db,a,"bill"), Read<Rate>(db,a,"rule"),
        Read<CreditSheet>(db,a,"credits"), Read<CostReset>(db,a,"reset"));

    // Caller holds the same per-account transaction lock as ingestion and all other cost writers.
    internal static Guid Save(Sql db,string account,string kind,Guid id,long expected,JsonElement body)
    {
        Require(id!=Guid.Empty && expected>=0,"无效记录标识或修订号。");
        var books=Load(db,account);
        string canonical;
        switch(kind)
        {
            case "bill":
                var bill=Wire.Read<Bill>(body.GetRawText());
                Require(Ref(bill.Plan) && bill.End>bill.Start && Ref(bill.Zone) && Wire.Id(bill.WeightSet),"请核对订阅、账期、时区及权重版本。");
                var start=bill.Boundary(bill.Start); var end=bill.Boundary(bill.End);
                Require(Moment(start) && Moment(end) && Money(bill.Paid,bill.Currency) && Money(bill.PaidCny,"CNY") &&
                    (bill.Currency!="CNY" || bill.PaidCny==bill.Paid) && bill.PaymentDate!=default,"实付金额、人民币金额或支付日无效。");
                Digits(bill.ApiCurrency);
                Require(bill.CoveredThrough==null || (bill.CoveredThrough>=start && bill.CoveredThrough<=end &&
                    bill.CoveredThrough<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),"覆盖确认时间须在账期内且不能晚于现在。");
                var duplicate=books.Bills.FirstOrDefault(b=>b.Id!=id && string.Equals(b.Value.Plan.Trim(),bill.Plan.Trim(),StringComparison.OrdinalIgnoreCase)
                    && b.Value.Boundary(b.Value.Start)==start && b.Value.Boundary(b.Value.End)==end);
                if(duplicate!=null){
                    if(expected==0 && duplicate.Value==bill)return duplicate.Id;
                    throw new CostConflict("同一订阅和账期已有账单，请修订原账单。");
                }
                var linkedResets=books.Resets.Where(r=>r.Value.BillId==id);
                Require(linkedResets.All(r=>r.Value.At>=start && r.Value.At<end),"新账期未覆盖已有重置记录。");
                canonical=Wire.Pack(bill); break;
            case "rule":
                var rule=Wire.Read<Rate>(body.GetRawText());
                if(rule.Type=="WEIGHT" && rule.Unit==CodexTracker.Billing.CodexCreditRates.Version &&
                    !books.Rules.Any(x=>x.Value.Type=="WEIGHT" && x.Value.Unit==rule.Unit))
                    throw new CostConflict("这个版本使用内置的个人订阅权重。请给自定义权重使用独立的版本名。");
                Require(rule.Type is "PRICE" or "WEIGHT" && Wire.Id(rule.Model) && rule.Model!="UNKNOWN" &&
                    rule.Speed is "any" or "standard" or "fast" or "ultrafast" or "priority" or "batch" &&
                    rule.WriteBasis is "unknown" or "included" or "separate" &&
                    Moment(rule.From) && (rule.Until==null || Moment(rule.Until.Value) && rule.Until>rule.From) &&
                    Ref(rule.Source) && Wire.Id(rule.Unit),"请核对规则类型、来源、适用期间、模型和速度。");
                if(rule.Type=="PRICE"){
                    Digits(rule.Unit);
                    Require(Uri.TryCreate(rule.Source,UriKind.Absolute,out var url) && url.Scheme=="https" && url.UserInfo.Length==0,"价表须保存 HTTPS 价格来源。");
                }
                var rates=new[]{rule.Input,rule.Read,rule.Write,rule.Output};
                Require(rates.Any(x=>x!=null) && rates.All(x=>x==null || Quantity(x.Value) && x<=1_000_000),"费率须在 0 到 1000000 之间，最多六位小数。");
                if(books.Rules.Any(x=>x.Id!=id && x.Value.Type==rule.Type && x.Value.Model==rule.Model &&
                    x.Value.Unit==rule.Unit && x.Value.Speed==rule.Speed && rule.From<(x.Value.Until??long.MaxValue) && x.Value.From<(rule.Until??long.MaxValue)))
                    throw new CostConflict("同模型、单位和档位的规则生效时间重叠。");
                canonical=Wire.Pack(rule); break;
            case "credits":
                var sheet=Wire.Read<CreditSheet>(body.GetRawText());
                Require(!books.Credits.Any(x=>x.Id==id && x.Value.BillId!=sheet.BillId),"Credits 修订须保留所属账单。");
                var owner=books.Bills.SingleOrDefault(x=>x.Id==sheet.BillId)?.Value;
                Require(owner!=null && sheet.Start==owner.Boundary(owner.Start) && sheet.Through>sheet.Start &&
                    sheet.Through<=owner.Boundary(owner.End) && sheet.Through<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"Credits 凭据账期或截至时间无效。");
                Require(sheet.Evidence is "SETTLED" or "BALANCE" or "LIFETIME_ESTIMATE" && Ref(sheet.Source) && Quantity(sheet.Total)
                    && sheet.Parts is {Count:<=5000} && sheet.Parts.All(x=>(x.Key==Pool || Wire.Id(x.Key)) && Quantity(x.Value))
                    && sheet.Parts.Values.Sum()<=sheet.Total,"请核对 Credits 类型、数量和分项。");
                // One active sheet of each evidence type per invoice. A correction creates a new revision.
                if(books.Credits.Any(x=>x.Id!=id && x.Value.BillId==sheet.BillId && x.Value.Evidence==sheet.Evidence))
                    throw new CostConflict("该账单已存在同类型凭据，请修订原记录。");
                canonical=Wire.Pack(sheet); break;
            case "reset":
                var reset=Wire.Read<CostReset>(body.GetRawText());
                Require(!books.Resets.Any(x=>x.Id==id && x.Value.BillId!=reset.BillId),"重置修订须保留所属账单。");
                var period=books.Bills.SingleOrDefault(x=>x.Id==reset.BillId)?.Value;
                Require(period!=null && reset.At>=period.Boundary(period.Start) && reset.At<period.Boundary(period.End) &&
                    reset.At<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"重置必须发生在所选账期内。");
                Require(reset.Type is "SCHEDULED" or "GLOBAL" or "BANKED" or "PAID" or "USAGE_CREDITS" or "UNKNOWN" &&
                    Ref(reset.Reference) && (reset.Before==null || Quantity(reset.Before.Value)) && (reset.After==null || Quantity(reset.After.Value)),"重置类型或证据无效。");
                Digits(reset.Currency);
                Require(reset.Cash==null || Money(reset.Cash.Value,reset.Currency),"重置金额无效。");
                Require(reset.Type switch{"GLOBAL" or "SCHEDULED" or "BANKED"=>reset.Cash==0,"PAID"=>reset.Cash>0,
                    "UNKNOWN"=>reset.Cash==null,_=>true},"免费重置应为零，付费重置需有实付金额，未知重置金额应留空。");
                var prior=books.Resets.FirstOrDefault(x=>x.Id!=id && x.Value.Reference==reset.Reference);
                if(prior!=null){
                    if(expected==0 && prior.Value==reset)return prior.Id;
                    throw new CostConflict("重置凭据已录入，请修订原记录。");
                }
                canonical=Wire.Pack(reset); break;
            default: throw new InvalidDataException("未知成本记录类型。");
        }
        var previous=db.List("SELECT version,body=$4::jsonb FROM cost_revision WHERE acct=$1 AND kind=$2 AND id=$3 ORDER BY version DESC LIMIT 1",
            r=>(Version:r.GetInt64(0),Same:r.GetBoolean(1)),account,kind,id,canonical).SingleOrDefault();
        if(previous.Same)return id;
        if(previous.Version!=expected)throw new CostConflict("记录已被修改，请重新载入当前版本。");
        db.Run("INSERT INTO cost_revision(acct,kind,id,version,body) VALUES($1,$2,$3,$4,$5::jsonb)",account,kind,id,expected+1,canonical);
        return id;
    }
}
