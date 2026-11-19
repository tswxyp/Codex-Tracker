using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using Tracker.Protocol;

namespace Tracker.Server;

[Authorize]
public sealed class CostPage(NpgsqlDataSource source,Settings settings) : PageModel
{
    internal CostBooks Books {get;private set;}=new([],[],[],[]);
    internal CostReport? Report {get;private set;}
    internal List<string> Archives {get;private set;}=[];
    internal string Kind {get;private set;}="bill";
    internal Guid EditId {get;private set;}
    internal long Version {get;private set;}
    internal JsonElement? Existing {get;private set;}
    internal string? Error {get;private set;}
    string Account => DeviceAuth.Account(User)!;
    internal string Field(string key,string fallback="")
    {
        if(Existing is not JsonElement row || !row.TryGetProperty(key,out var value))return fallback;
        return value.ValueKind==JsonValueKind.String?value.GetString()!:value.ToString();
    }
    internal string TimeField(string key) => long.TryParse(Field(key),out long ms)
        ?DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff",CultureInfo.InvariantCulture):"";
    internal string PartsField() => Existing is JsonElement e && e.TryGetProperty("parts",out var p)
        ?string.Join("\n",p.EnumerateObject().Select(x=>$"{x.Name}={x.Value}")):"";
    private T InAccount<T>(Func<Sql,T> action)=>Storage.Tenant(source,Account,action);
    internal static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Load(Guid? billId,string? kind,Guid? edit)
    {
        InAccount(db=>{
            Books=CostInputs.Load(db,Account);
            Kind=kind is "rule" or "credits" or "reset"?kind:"bill";
            EditId=edit??Guid.NewGuid();
            if(edit.HasValue){
                var record=db.List("SELECT version,body::text FROM cost_revision WHERE acct=$1 AND kind=$2 AND id=$3 ORDER BY version DESC LIMIT 1",
                    r=>(Version:r.GetInt64(0),Body:r.GetString(1)),Account,Kind,EditId).SingleOrDefault();
                if(record.Body!=null){Version=record.Version;Existing=Wire.Read<JsonElement>(record.Body);}
            }
            var now=Now;
            var selected=billId.HasValue?Books.Bills.FirstOrDefault(b=>b.Id==billId):
                Books.Bills.OrderByDescending(b=>b.Value.Boundary(b.Value.Start)<=now && now<b.Value.Boundary(b.Value.End))
                    .ThenByDescending(b=>b.Value.Boundary(b.Value.Start)<=now).ThenByDescending(b=>b.Value.Start).FirstOrDefault();
            if(selected!=null){
                Report=CostEngine.Calculate(db,Account,selected,Books,Now,settings.Epoch);
                Archives=db.List("SELECT digest FROM cost_statement WHERE acct=$1 AND bill=$2 ORDER BY created_at DESC LIMIT 20",
                    r=>r.GetString(0),Account,selected.Id);
            }
            return true;
        });
    }
    public void OnGet(Guid? billId,string? kind,Guid? edit,string? preset)
    {
        Load(billId,kind,edit);
        if(kind=="rule" && edit==null && preset=="gpt-4.1-standard"){
            Existing=Wire.Element(new Rate("PRICE","USD","gpt-4.1","standard",
                new DateTimeOffset(2026,11,19,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds(),null,
                2m,0.5m,null,8m,"unknown","https://developers.openai.com/api/docs/models/gpt-4.1"));
        }
    }
    public IActionResult OnGetReport(Guid billId,string? digest)
    {
        return InAccount<IActionResult>(db=>{
            if(digest!=null){
                var saved=db.One<string>("SELECT result::text FROM cost_statement WHERE acct=$1 AND bill=$2 AND digest=$3",Account,billId,digest);
                return saved==null?NotFound():Content(saved,"application/json");
            }
            var books=CostInputs.Load(db,Account);
            var bill=books.Bills.FirstOrDefault(b=>b.Id==billId);
            return bill==null?NotFound():new JsonResult(CostEngine.Calculate(db,Account,bill,books,Now,settings.Epoch),Wire.Json);
        });
    }
    public IActionResult OnPostArchive(Guid billId)
    {
        return InAccount<IActionResult>(db=>{
            var books=CostInputs.Load(db,Account);
            var selected=books.Bills.FirstOrDefault(b=>b.Id==billId);
            if(selected==null)return NotFound();
            var result=CostEngine.Calculate(db,Account,selected,books,Now,settings.Epoch);
            var json=Wire.Pack(result);
            var digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            db.Run("INSERT INTO cost_statement(acct,bill,digest,result) VALUES($1,$2,$3,$4::jsonb) ON CONFLICT DO NOTHING",Account,billId,digest,json);
            return RedirectToPage(new{billId});
        });
    }
    public async Task<IActionResult> OnPostAsync()
    {
        var f=await Request.ReadFormAsync();
        string S(string k)=>f[k].ToString().Trim();
        Guid? billId=Guid.TryParse(S("bill_id"),out var bid)?bid:null;
        var kind=S("kind");
        try{
            var id=Guid.Parse(S("id"));var version=long.Parse(S("version"),CultureInfo.InvariantCulture);
            decimal? Number(string k)=>S(k)==""?null:decimal.Parse(S(k),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
            decimal Required(string k)=>Number(k)??throw new FormatException();
            DateOnly Day(string k)=>DateOnly.ParseExact(S(k),"yyyy-MM-dd",CultureInfo.InvariantCulture);
            long? Moment(string k){
                if(S(k)=="")return null;
                var date=DateTimeOffset.ParseExact(S(k),["yyyy-MM-dd'T'HH:mm","yyyy-MM-dd'T'HH:mm:ss","yyyy-MM-dd'T'HH:mm:ss.FFF"],
                    CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal);
                return date.ToUnixTimeMilliseconds();
            }
            long At(string k)=>Moment(k)??throw new FormatException();
            var value=kind switch {
                "bill"=>Wire.Element(new Bill(S("plan"),Day("start"),Day("end"),S("zone"),S("currency"),Required("paid"),Required("paid_cny"),
                    Day("payment_date"),S("api_currency"),S("weight_set"),Moment("covered_through"))),
                "rule"=>Wire.Element(new Rate(S("type"),S("unit"),S("model"),S("speed"),At("from"),Moment("until"),
                    Number("input"),Number("read"),Number("write"),Number("output"),S("write_basis"),S("source"))),
                "reset"=>Wire.Element(new CostReset(billId??throw new FormatException(),At("at"),S("type"),S("reference"),
                    Number("before"),Number("after"),Number("cash"),S("currency"))),
                "credits"=>Wire.Element(new CreditSheet(billId??throw new FormatException(),At("start"),At("through"),
                    S("evidence"),S("source"),Required("total"),ParseParts(S("parts")))),
                _=>throw new FormatException()
            };
            var key=InAccount(db=>CostInputs.Save(db,Account,kind,id,version,value));
            return RedirectToPage(new{billId=kind=="bill"?key:billId,kind,edit=key});
        }catch(Exception ex) when(ex is FormatException or OverflowException or InvalidDataException or CostConflict or TimeZoneNotFoundException or InvalidTimeZoneException){
            Response.StatusCode=ex is CostConflict?409:400;
            Error=ex is CostConflict or InvalidDataException?ex.Message:"请核对日期、时区、数量及必填字段。";
            Load(billId,kind,Guid.TryParse(S("id"),out var id)?id:null);
            return Page();
        }
    }
    static Dictionary<string,decimal> ParseParts(string text)
    {
        var parts=new Dictionary<string,decimal>(StringComparer.Ordinal);
        foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){
            var pair=line.Split('=',2,StringSplitOptions.TrimEntries);
            if(pair.Length!=2 || !decimal.TryParse(pair[1],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value) ||
                !parts.TryAdd(pair[0],value))throw new InvalidDataException("分项每行填写唯一线程 ID 或 @pool，再填写等号和数量。");
        }
        return parts;
    }
    internal static string Reason(string code)=>code switch{
        "COVERAGE_UNCONFIRMED"=>"账期覆盖尚未确认","DEVICE_SYNC_PENDING"=>"设备尚欠完整同步",
        "INCOMPLETE_WEIGHT"=>"部分用量缺时间、归属或权重","ZERO_DENOMINATOR"=>"可用分母为零",
        "OVERLAPPING_BILLS"=>"账期重叠，账号用量尚无法区分至各笔订阅",_=>code};
}
