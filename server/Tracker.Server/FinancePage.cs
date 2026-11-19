using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;
using Tracker.Protocol;

namespace Tracker.Server;

[Authorize]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class FinancePage(NpgsqlDataSource source,Settings settings):PageModel
{
    string Account=>DeviceAuth.Account(User)!;
    internal FinanceLedger Ledger {get;private set;}=null!;
    internal CostBooks Costs {get;private set;}=null!;
    internal FinanceReport Report {get;private set;}=null!;
    internal Revision<Bill>? Period {get;private set;}
    internal string Section {get;private set;}="overview";
    internal string Order {get;private set;}="profit";
    internal Guid? EditId {get;private set;}
    internal string? Error {get;private set;}
    [BindProperty(SupportsGet=true)] public Guid? Device {get;set;}
    [BindProperty(SupportsGet=true)] public string? ModelId {get;set;}
    [BindProperty(SupportsGet=true)] public string? ProjectId {get;set;}
    [BindProperty(SupportsGet=true)] public DateOnly? From {get;set;}
    [BindProperty(SupportsGet=true)] public DateOnly? To {get;set;}
    [BindProperty(SupportsGet=true)] public decimal? MinTokens {get;set;}
    [BindProperty(SupportsGet=true)] public decimal? MinCost {get;set;}
    [BindProperty(SupportsGet=true)] public decimal? MinHours {get;set;}
    [BindProperty(SupportsGet=true)] public int PageIndex {get;set;}=1;
    internal List<FinanceTask> Filtered {get;private set;}=[];
    internal Dictionary<string,string> TaskPage(int index){
        var query=Request.Query.ToDictionary(x=>x.Key,x=>x.Value.ToString(),StringComparer.OrdinalIgnoreCase);
        query["pageIndex"]=index.ToString(CultureInfo.InvariantCulture);
        return query;
    }
    void FilterTasks(string? sort)
    {
        static long? Midnight(DateOnly? date)=>date.HasValue?new DateTimeOffset(date.Value.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)).ToUnixTimeMilliseconds():null;
        var from=Midnight(From);
        var until=Midnight(To)+86400000;
        var rows=Report.Tasks!.Where(t=>(Device==null || t.Devices.ContainsKey(Device.Value)) &&
            (ModelId==null || t.Models.Contains(ModelId)) && (ProjectId==null || (t.Project??CostInputs.Pool)==ProjectId) &&
            (from==null || t.Last>=from) && (until==null || t.First<until) &&
            (MinTokens==null || t.Tokens>=MinTokens) && (MinCost==null || t.Subscription>=MinCost) &&
            (MinHours==null || t.Time?.ActiveMs/3600000m>=MinHours));
        Order=sort is "tokens" or "cost" or "hours"?sort:"date";
        Filtered=(Order switch{"tokens"=>rows.OrderByDescending(t=>t.Tokens),"cost"=>rows.OrderByDescending(t=>t.Subscription),
            "hours"=>rows.OrderByDescending(t=>t.Time?.ActiveMs),_=>rows.OrderByDescending(t=>t.Last)})
            .ThenBy(t=>t.Thread,StringComparer.Ordinal).ToList();
        PageIndex=Math.Clamp(PageIndex,1,Math.Max(1,(Filtered.Count+49)/50));
    }
    internal static string Label(string code)=>code switch{
        "RECEIPT"=>"回款","LABOR"=>"人工","SERVER"=>"服务器","OTHER"=>"其他支出","AI"=>"额外 AI",
        "PLANNED"=>"计划中","ACTIVE"=>"进行中","ON_HOLD"=>"暂停","COMPLETED"=>"已完成",_=>code};
    internal IEnumerable<Revision<CashEntry>> Cash=>Ledger.Cash.Where(x=>Period==null ||
        x.Value.Date>=Period.Value.Start && x.Value.Date<Period.Value.End);
    internal IEnumerable<ProfitRow> Ranked=>Order=="income"?Report.Projects.OrderByDescending(p=>p.Received).ThenBy(p=>p.Id):
        Report.Projects.OrderByDescending(p=>p.Profit).ThenBy(p=>p.Id);
    internal string ProjectName(Guid id)=>Report.Projects.FirstOrDefault(p=>p.Id==id.ToString())?.Name??id.ToString();
    internal List<Resource> Resources(string kind)=>Ledger.Resources.Where(r=>r.Kind==kind).ToList();
    bool Load(Guid? billId,string? section,string? sort,Guid? edit)
    {
        Section=section is "projects" or "cash" or "links" or "resets" or "tasks"?section:"overview";
        Order=sort=="income"?"income":"profit";
        EditId=edit;
        return Storage.Tenant(source,Account,db=>{
            Ledger=FinanceLedger.Load(db,Account);
            Costs=CostInputs.Load(db,Account);
            Period=Costs.Bills.FirstOrDefault(b=>b.Id==billId);
            if(billId.HasValue && Period==null)return false;
            Report=FinanceReport.Calculate(db,Account,Ledger,Costs,Period,CostPage.Now,settings.Epoch,Section=="tasks");
            if(Section=="tasks")FilterTasks(sort);
            return true;
        });
    }

    public IActionResult OnGet(Guid? billId,string? section,string? sort,Guid? edit)=>
        !ModelState.IsValid || From>To || MinHours<0 || MinCost<0 || MinTokens<0?BadRequest():Load(billId,section,sort,edit)?Page():NotFound();
    public IActionResult OnGetReport(Guid? billId)=>
        !ModelState.IsValid?BadRequest():Load(billId,null,null,null)?new JsonResult(Report,Wire.Json):NotFound();

    public IActionResult OnGetExport(Guid? billId,string format,string table="projects",string sort="profit")
    {
        if(!ModelState.IsValid)return BadRequest();
        if(!Load(billId,null,sort,null))return NotFound();
        var sheets=FinanceExport.Tables(Report with {Projects=Ranked.ToList()},Cash);
        var name="tracker-finance-"+(Period?.Value.Start.ToString("O")??"all");
        if(format=="xlsx")return File(FinanceExport.Workbook(sheets),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",name+".xlsx");
        var index=table switch{"projects"=>0,"periods"=>1,"cash"=>2,"summary"=>3,_=>-1};
        return format=="csv" && index>=0?File(FinanceExport.Csv(sheets[index]),"text/csv; charset=utf-8",name+"-"+table+".csv"):BadRequest();
    }

    public async Task<IActionResult> OnPostAsync(Guid? billId)
    {
        if(!ModelState.IsValid)return BadRequest();
        var form=await Request.ReadFormAsync();
        string S(string key)=>form[key].ToString().Trim();
        Guid Id(string key)=>Guid.Parse(S(key));
        long Version(string k)=>long.Parse(S(k),CultureInfo.InvariantCulture);
        decimal Money(string key)=>decimal.Parse(S(key),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        DateOnly Day(string key)=>DateOnly.ParseExact(S(key),"yyyy-MM-dd",CultureInfo.InvariantCulture);
        Guid? project=S("project")==""?null:Guid.TryParse(S("project"),out var pid)?pid:Guid.Empty;
        string action=S("action");
        string section=action switch{"profile"=>"projects","cash"=>"cash","reset"=>"resets",_=>"links"};
        try{
            var id=Id("id");
            var version=Version("version");
            Storage.Tenant(source,Account,db=>{
                var ledger=FinanceLedger.Load(db,Account);
                var costs=CostInputs.Load(db,Account);
                if(billId.HasValue && costs.Bills.All(b=>b.Id!=billId))throw new InvalidDataException("所选账期无效。");
                switch(action){
                    case "profile":
                        ledger.SaveProfile(db,Account,id,version,Version("resource_version"),
                            new(S("name"),S("customer"),Money("contract"),S("status"),Day("start"),S("end")==""?null:Day("end")));
                        break;
                    case "cash":
                        var entry=new CashEntry(Id("project"),S("category"),Day("date"),Money("cny"),S("reference"),S("voided")=="true");
                        CostInputs.Require(entry.Date<=DateOnly.FromDateTime(DateTime.UtcNow.AddHours(14)),"日期不能晚于实际收付日。");
                        ledger.SaveCash(db,Account,id,version,entry,costs);
                        break;
                    case "reset":
                        ledger.AssignReset(db,Account,id,version,new(project,Version("source_version"),Money("cny")),costs);
                        break;
                    case "group":
                    case "member":
                    case "unlink":
                        var kind=action=="group"?"group":"member";
                        var current=ledger.Resources.SingleOrDefault(r=>r.Kind==kind && r.Id==id.ToString());
                        var edit=action switch{
                            "group"=>new Edit(Name:S("name"),ProjectId:project),
                            "member"=>new Edit(GroupId:Id("group"),ThreadId:S("target_kind")=="thread"?S("target"):null,
                                SessionId:S("target_kind")=="session"?S("target"):null),
                            _=>(Edit?)null};
                        if(current!=null && edit!=null && Wire.Read<Edit>(current.Value!.Value.GetRawText())==edit)break;
                        FinanceLedger.WriteResource(db,Account,action=="group"?(version==0?"group.create":"group.update"):
                            action=="member"?"member.set":"member.remove",id,version,edit);
                        break;
                    default:throw new InvalidDataException("未知操作。");
                }
                return true;
            });
            return RedirectToPage(new{billId,section,edit=id});
        }catch(Exception ex) when(ex is FormatException or OverflowException or InvalidDataException or CostConflict){
            Response.StatusCode=ex is CostConflict?409:400;
            Error=ex is InvalidDataException or CostConflict?ex.Message:"请核对必填项、金额和日期。";
            if(!Load(billId,section,null,Guid.TryParse(S("id"),out var id)?id:null))return NotFound();
            return Page();
        }
    }
}
