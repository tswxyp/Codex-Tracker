using System.Text.Json;

namespace CodexTracker.Tray;

internal sealed record NativeProject(string Id,string Name,string Location);
internal sealed class LocalProjects
{
    internal Dictionary<string,NativeProject> Items {get;}=new(StringComparer.Ordinal);
    internal Dictionary<string,string> ByThread {get;}=new(StringComparer.Ordinal);
    internal static LocalProjects Read(string root)
    {
        var result=new LocalProjects();
        using var file=TitleCatalog.OpenSide(Path.Combine(root,".codex-global-state.json"),true);
        if(file==null)return result;
        using var document=JsonDocument.Parse(file);
        var state=document.RootElement;
        if(!state.TryGetProperty("local-projects",out var projects) || projects.ValueKind!=JsonValueKind.Object)return result;
        static string? Field(JsonElement item,string key)=>item.ValueKind==JsonValueKind.Object &&
            item.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String?v.GetString():null;
        foreach(var p in projects.EnumerateObject()){
            var name=Field(p.Value,"name");
            if(Field(p.Value,"id")!=p.Name || string.IsNullOrWhiteSpace(name))continue;
            var location=p.Value.TryGetProperty("rootPaths",out var paths) && paths.ValueKind==JsonValueKind.Array
                ?paths.EnumerateArray().FirstOrDefault().ToString():"";
            result.Items[p.Name]=new(p.Name,name,location);
        }
        if(!state.TryGetProperty("thread-project-assignments",out var assignments) || assignments.ValueKind!=JsonValueKind.Object)return result;
        state.TryGetProperty("thread-project-membership-host-ids",out var hosts);
        var unassigned=new HashSet<string>(StringComparer.Ordinal);
        if(state.TryGetProperty("projectless-thread-ids",out var list) && list.ValueKind==JsonValueKind.Array)
            foreach(var id in list.EnumerateArray())if(id.ValueKind==JsonValueKind.String)unassigned.Add(id.GetString()!);
        foreach(var a in assignments.EnumerateObject()){
            if(unassigned.Contains(a.Name) || (hosts.ValueKind==JsonValueKind.Object && hosts.TryGetProperty(a.Name,out var host)
                && (host.ValueKind!=JsonValueKind.String || host.GetString()!="local")))continue;
            if(Field(a.Value,"projectKind")=="local" && Field(a.Value,"projectId") is {} id && result.Items.ContainsKey(id))
                result.ByThread[a.Name]=id;
        }
        return result;
    }
}

internal sealed record ReceiptTotals(MoneyAmount[] ByCurrency,bool FromChats);
internal sealed record ProjectFigures(string? Project=null,ReceiptTotals? Income=null,MoneyAmount? Cost=null,
    bool HasZeroIncome=false,MoneyAmount? ZeroCost=null,bool Unavailable=false,bool AssumedStandard=false);

internal static class SidebarData
{
    internal static DisplayData Read(string root,string database,TitleCatalog catalog,string[] visible,SidebarSettings settings)
    {
        LocalProjects projects;
        Dictionary<string,MoneyAmount[]> income,chatIncome;
        bool unavailable=false;
        try{
            projects=LocalProjects.Read(root);
            var receipts=settings.ReadReceipts();
            Dictionary<string,MoneyAmount[]> ByEntry(string scope)=>receipts.Where(r=>r.Scope==scope)
                .GroupBy(r=>r.Entry).ToDictionary(g=>g.Key,g=>g.Select(r=>r.Amount.MoneyValue).ToArray(),StringComparer.Ordinal);
            income=ByEntry("project");chatIncome=ByEntry("chat");
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException){
            projects=new();income=new();chatIncome=new();unavailable=true;
        }
        var funded=new HashSet<string>(income.Keys,StringComparer.Ordinal);
        foreach(var thread in chatIncome.Keys)
            if(projects.ByThread.TryGetValue(thread,out var project))funded.Add(project);
        var wanted=visible.Select(id=>projects.ByThread.GetValueOrDefault(id)).OfType<string>().Where(funded.Contains).ToHashSet();
        var members=projects.ByThread.Where(kv=>wanted.Contains(kv.Value)).ToLookup(kv=>kv.Value,kv=>kv.Key,StringComparer.Ordinal);
        var ids=visible.Concat(members.SelectMany(g=>g)).Distinct().ToArray();
        var speeds=settings.Speeds.Snapshot();
        var usage=Data.Read(database,root,ids.Length>500?null:ids,speeds);
        SubscriptionPlan? plan=null;
        try{plan=settings.ReadPlan();}catch(Microsoft.Data.Sqlite.SqliteException){ }
        foreach(var stats in usage.Values){
            stats.HasPlan=plan!=null;
            stats.Value=plan!=null?SubscriptionCost.Estimate(stats.Usage,plan):null;
        }
        var figures=new Dictionary<string,ProjectFigures>(StringComparer.Ordinal);
        foreach(var project in wanted){
            decimal? totalCost=0m,zeroCost=0m;
            bool hasZero=false,assumed=false;
            foreach(var thread in members[project]){
                var estimate=usage.GetValueOrDefault(thread)?.Value;
                decimal? memberCost=estimate is {UnpricedTokens:0}?estimate.Cost.Amount:null;
                totalCost+=memberCost;
                assumed|=estimate is {UnpricedTokens:0,AssumedStandard:true};
                if(chatIncome.GetValueOrDefault(thread) is {} amounts && amounts.All(a=>a.Amount==0)){
                    hasZero=true;zeroCost+=memberCost;
                }
            }
            var receipt=income.TryGetValue(project,out var manual)?new ReceiptTotals(manual,false):
                new ReceiptTotals(members[project].Where(chatIncome.ContainsKey).SelectMany(thread=>chatIncome[thread]).ToArray(),true);
            MoneyAmount? InPlanCurrency(decimal? value)=>plan!=null && value.HasValue?new(value.Value,plan.Paid.Currency):null;
            figures.Add(project,new(project,receipt,InPlanCurrency(totalCost),hasZero,InPlanCurrency(zeroCost),AssumedStandard:assumed));
        }
        var rows=new Dictionary<string,Stats>(StringComparer.Ordinal);
        foreach(var thread in visible){
            if(!usage.TryGetValue(thread,out var stats))stats=new(){HasPlan=plan!=null};
            stats.ChatIncome=chatIncome.GetValueOrDefault(thread);
            if(unavailable)stats.Project=new(Unavailable:true);
            if(projects.ByThread.TryGetValue(thread,out var project))stats.Project=figures.GetValueOrDefault(project)??new(project);
            rows[thread]=stats;
        }
        return new(catalog,rows,Environment.TickCount64);
    }
}
