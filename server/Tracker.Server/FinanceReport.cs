namespace Tracker.Server;

internal sealed class ProfitRow(string id,string name)
{
    public string Id {get;}=id;
    public string Name {get;}=name;
    public ProjectProfile? Profile {get;set;}
    public decimal Received {get;set;}
    public decimal CumulativeReceived {get;set;}
    public decimal? Outstanding=>Profile?.Contract-CumulativeReceived;
    public decimal Subscription {get;set;}
    public decimal ExtraAi {get;set;}
    public decimal Reset {get;set;}
    public decimal Labor {get;set;}
    public decimal Server {get;set;}
    public decimal Other {get;set;}
    public decimal Cost=>Subscription+ExtraAi+Reset+Labor+Server+Other;
    public HashSet<string> Pending {get;}=[];
    public HashSet<string> Basis {get;}=[];
    public decimal? Profit=>Pending.Count==0?Received-Cost:null;
    public decimal? Margin=>Received>0?Profit/Received*100:null;
    public decimal? AiRatio=>Received>0 && Pending.Count==0?(ExtraAi+Reset+Subscription)/Received*100:null;
    public ProjectHours? Time {get;set;}
    public decimal? RevenuePerHour=>Time?.ActiveMs>0?Received*3600000/Time.ActiveMs:null;
    public Dictionary<string,decimal> ApiKnown {get;}=[];
}
internal sealed record BillSummary(Guid Id,string Plan,DateOnly Start,DateOnly End,decimal PaidCny,decimal AllocatedCny,
    long GlobalResets,decimal Tokens,decimal? Credits,string Basis,string WeightSet,string ApiCurrency,decimal ApiKnown,string ApiStatus,
    decimal? PerTenMillion,string[] Reasons,bool Provisional);
internal sealed record FinanceReport(List<ProfitRow> Projects,ProfitRow Pool,List<BillSummary> Bills,
    decimal SubscriptionPaid,decimal Received,decimal KnownCost,decimal? Net,List<string> Warnings,List<FinanceTask>? Tasks)
{
    internal static FinanceReport Calculate(Sql db,string account,FinanceLedger ledger,CostBooks books,
        Revision<Bill>? selected,long now,Guid epoch,bool includeTasks=false)
    {
        var rows=new Dictionary<string,ProfitRow>(StringComparer.Ordinal);
        foreach(var resource in ledger.Resources.Where(r=>r.Kind=="project"))
            rows.Add(resource.Id,new(resource.Id,FinanceLedger.Field(resource,"name")!));
        ProfitRow Row(string? id,string? fallback=null){
            string key=id??CostInputs.Pool;
            if(!rows.TryGetValue(key,out var row))
                rows[key]=row=new(key,fallback??(key==CostInputs.Pool?"未归属池":key));
            return row;
        }
        var pool=Row(null);
        foreach(var profile in ledger.Profiles)Row(profile.Id.ToString(),profile.Value.Name).Profile=profile.Value;
        var start=selected?.Value.Start??DateOnly.MinValue;
        var end=selected?.Value.End??DateOnly.MaxValue;
        var warnings=new HashSet<string>();
        var resetRefs=books.Resets.Select(r=>r.Value.Reference).ToHashSet(StringComparer.Ordinal);
        foreach(var item in ledger.Cash.Select(r=>r.Value).Where(c=>!c.Voided && c.Date<end)){
            var row=Row(item.Project.ToString());
            if(item.Category=="RECEIPT")row.CumulativeReceived+=item.Cny;
            if(item.Date<start)continue;
            if(item.Category!="RECEIPT" && resetRefs.Contains(item.Reference)){
                warnings.Add("同一凭据同时出现于项目支出和重置账本，已采用重置记录。请核对原流水。");
                continue;
            }
            switch(item.Category){
                case "RECEIPT":row.Received+=item.Cny;break;
                case "AI":row.ExtraAi+=item.Cny;break;
                case "LABOR":row.Labor+=item.Cny;break;
                case "SERVER":row.Server+=item.Cny;break;
                case "OTHER":row.Other+=item.Cny;break;
            }
        }

        var allocations=ledger.Resets.ToDictionary(x=>x.Id);
        var summaries=new List<BillSummary>();
        var details=new List<CostReport>();
        bool unknownCash=false;
        var seenApi=new HashSet<(string Currency,string Event)>();
        IEnumerable<Revision<Bill>> bills=selected==null?books.Bills:[selected];
        foreach(var bill in bills.OrderBy(b=>b.Value.Start).ThenBy(b=>b.Id)){
            var report=CostEngine.Calculate(db,account,bill,books,now,epoch);
            if(includeTasks)details.Add(report);
            var lineIndex=report.Lines.ToDictionary(l=>l.Thread);
            foreach(var evidence in report.Evidence){
                // Duplicate periods may contain the same event. Only dated, attributable components enter project totals.
                if(evidence.At.HasValue && seenApi.Add((report.Bill.ApiCurrency,evidence.Key)) && evidence.Api.Terms>0){
                    var api=Row(lineIndex.GetValueOrDefault(evidence.Thread)?.Project).ApiKnown;
                    api[report.Bill.ApiCurrency]=api.GetValueOrDefault(report.Bill.ApiCurrency)+evidence.Api.Known;
                }
            }
            foreach(var line in report.Lines){
                var row=Row(line.Project);
                row.Subscription+=line.AllocationCny??0;
                if(line.AllocationCny==null)row.Pending.Add("订阅分摊待核算");
                else{
                    row.Basis.Add(report.Basis=="OFFICIAL"?"官方实扣分摊":"估算权重分摊");
                    if(report.Through<report.End)row.Basis.Add("账期暂计");
                }
            }
            var allocated=report.Lines.Sum(l=>l.AllocationCny??0);
            // Keep all known paid cash on the account even while project shares cannot be determined.
            pool.Subscription+=bill.Value.PaidCny-allocated;
            if(report.Reasons.Length>0 && bill.Value.PaidCny>0){
                warnings.Add("有账期尚未完成分摊，项目利润待核算。");
                foreach(var row in rows.Values)row.Pending.Add("账期分摊待核算");
            }
            foreach(var source in report.Resets){
                var reset=source.Value;
                if(reset.Cash==0)continue;
                var link=allocations.GetValueOrDefault(source.Id)?.Value;
                if(link?.SourceVersion!=source.Version)link=null;
                var row=Row(link?.Project?.ToString());
                decimal? cny=reset.Currency=="CNY"?reset.Cash:link?.Cny;
                if(cny.HasValue)row.Reset+=cny.Value;
                else{
                    unknownCash=true;
                    row.Pending.Add("重置实付人民币金额待核对");
                    warnings.Add("有重置尚缺实付凭据或折合人民币金额。");
                }
            }
            var verified=report.Evidence.Where(e=>e.At.HasValue && e.Confidence is "OBSERVED" or "DERIVED" && e.Usage!=null).ToList();
            decimal tokens=verified.Sum(e=>(decimal)e.Usage!.Total);
            var meteredThreads=verified.Select(e=>e.Thread).ToHashSet();
            bool otherFunding=report.Resets.Any(r=>r.Value.Cash is null or >0) ||
                ledger.Cash.Any(x=>!x.Value.Voided && x.Value.Category=="AI" &&
                    x.Value.Date>=bill.Value.Start && x.Value.Date<bill.Value.End);
            bool covered=report.Reasons.Length==0 && report.Through==report.End && tokens>0 &&
                !otherFunding && verified.Count==report.Evidence.Count &&
                report.Lines.All(l=>l.AllocationCny!=null && meteredThreads.Contains(l.Thread));
            decimal? credits=report.Lines.Count>0 && report.Lines.All(l=>l.Credits.HasValue)?report.Lines.Sum(l=>l.Credits):null;
            summaries.Add(new(bill.Id,bill.Value.Plan,bill.Value.Start,bill.Value.End,bill.Value.PaidCny,allocated,
                report.Resets.LongCount(r=>r.Value.Type=="GLOBAL"),tokens,credits,report.Basis,bill.Value.WeightSet,
                bill.Value.ApiCurrency,report.KnownApi,report.ApiStatus,covered?bill.Value.PaidCny*10_000_000/tokens:null,
                report.Reasons,report.Through<report.End));
        }
        var boundsStart=selected?.Value.Boundary(start)??0;
        var boundsEnd=Math.Max(boundsStart,Math.Min(now,selected?.Value.Boundary(end)??now));
        var assignments=CostEngine.Assignments(db,account);
        var projectTimes=ProjectTiming.Read(db,account,boundsStart,boundsEnd,assignments,out var threadTimes);
        foreach(var (project,time) in projectTimes)
            Row(project).Time=time;
        var tasks=includeTasks?FinanceTask.Read(db,account,ledger,assignments,threadTimes,details,selected):null;
        if(summaries.Any(b=>b.Provisional))warnings.Add("进行中账期使用当前分摊，后续用量会改变结果。");
        if(summaries.Any(b=>b.Basis=="ESTIMATED" && b.AllocatedCny>0))warnings.Add("部分订阅分摊采用估算 Credits 权重。");
        if(pool.Cost>0)warnings.Add("未归属支出留在账号总成本中，可归属项目后重新核算。");
        var received=rows.Values.Sum(r=>r.Received);
        var cost=rows.Values.Sum(r=>r.Cost);
        return new(rows.Values.Where(r=>r!=pool).ToList(),pool,summaries,summaries.Sum(b=>b.PaidCny),
            received,cost,unknownCash?null:received-cost,warnings.ToList(),tasks);
    }
}
