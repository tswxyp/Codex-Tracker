using System.Globalization;
using CodexTracker.Billing;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record Usage(string Model,string Confidence,long Total,long Input,long Output,long? Read,long? Write,string? Speed=null,long? Reasoning=null);
internal sealed record CardField(string Label,string Value,int ProfitSign=0,bool ModelTags=false,string? Note=null);
internal sealed class Stats
{
    internal List<Usage> Usage {get;}=[];
    internal WeeklyEstimate? Value;
    internal bool HasPlan;
    internal ProjectFigures Project=new();
    internal MoneyAmount[]? ChatIncome;
    IEnumerable<Usage> Known=>Usage.Where(u=>u.Confidence is "OBSERVED" or "DERIVED");
    internal long? Total=>Known.Any()?Known.Sum(u=>u.Total):null;
    internal long Partial=>Usage.Where(u=>u.Confidence is not ("OBSERVED" or "DERIVED")).Sum(u=>u.Total);
    static string Compact(long n)
    {
        var inv=CultureInfo.InvariantCulture;
        if(n>=1_000_000_000)return "≈"+(n/1_000_000_000m).ToString("0.00",inv)+"B Tok";
        if(n>=1_000_000)return "≈"+(n/1_000_000m).ToString("0.00",inv)+"M Tok";
        if(n>=1000)return "≈"+(n/1000m).ToString("0.00",inv)+"K Tok";
        return n.ToString(inv)+" Tok";
    }
    internal IEnumerable<string> Labels(DisplayOptions? options=null)
    {
        options??=new();
        if(Total is not long total){yield return options.T("用量待确认","Usage pending");yield break;}
        string token=Compact(total)+(Partial>0?"*":"");
        if(Value is {UnpricedTokens:0} value && value.Cost.Currency==options.DisplayCurrency)yield return token+", "+options.Approx(value.Cost);
        yield return token;
    }
    static string ModelName(string id,DisplayOptions options)
    {
        if(id.Equals("UNKNOWN",StringComparison.OrdinalIgnoreCase))return options.T("未知模型","Unknown model");
        if(!id.StartsWith("gpt-",StringComparison.OrdinalIgnoreCase))return id;
        var parts=id[4..].Split('-');
        return "GPT-"+string.Join(" ",parts.Select(p=>CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p)));
    }
    internal CardField[] Fields(DisplayOptions options,MarketRates? market=null)
    {
        string T(string zh,string en)=>options.T(zh,en);
        string blank=T("暂无数据","No data");
        var rows=Known.ToArray();
        long input=rows.Sum(u=>u.Input);
        string Tokens(long number)=>number.ToString("N0",options.Culture);
        string total=Total is long n?Tokens(n):blank;
        if(Partial>0 && Total.HasValue)total+="\n"+T("（已确认）"," (confirmed)").Trim();
        string hit=blank;
        if(input>0 && rows.All(u=>u.Read.HasValue))
            hit=(100m*rows.Sum(u=>u.Read!.Value)/input).ToString("0.##",options.Culture)+"%";
        string ModelTag(Usage row){
            string name=ModelName(row.Model,options);
            if(row.Speed is not ("fast" or "ultrafast") || !CodexCreditRates.Rates.TryGetValue(row.Model,out var rate)
                || rate.Multiplier(row.Speed) is not decimal factor)return name;
            return name+" / "+(row.Speed=="fast"?"Fast":"Ultrafast")+" ×"+factor.ToString("0.##",options.Culture);
        }
        string models=string.Join(" · ",rows.GroupBy(ModelTag).OrderByDescending(g=>g.Sum(u=>u.Total))
            .ThenBy(g=>g.Key,StringComparer.Ordinal).Select(g=>g.Key));
        var money=new DisplayMoney(options,market);
        string speedNote=options.SpeedEstimateNote;
        string value=Usage.Count==0?T("暂无用量记录","No usage recorded yet"):
            !HasPlan?T("请在托盘设置订阅费用","Set the fee in the tray"):
            T("这条聊天的用量记录不全","Chat usage details incomplete");
        if(Value is {} estimate){
            string percent=estimate.Percent is >0m and <0.1m?"<0.1":estimate.Percent.ToString("0.#",CultureInfo.InvariantCulture);
            value=money.Format(money.Convert(estimate.Cost),approx:true)+"\n"+(estimate.UnpricedTokens>0
                ?options.Format("已估部分 · {0:N0} Token 待估","Known portion · {0:N0} tokens unpriced",estimate.UnpricedTokens)
                :options.Format("折合一周额度的 {0}%","{0}% of one week's allowance",percent));
            if(estimate.AssumedStandard)value+="\n"+speedNote;
        }
        string missing=Project.Unavailable?T("暂时无法读取","Unavailable"):T("未填写","Not entered");
        var received=ChatIncome==null?null:money.Sum(ChatIncome);
        CardField Profit(string label,decimal? net,string? note=null)=>new(label,money.Format(net,signed:true),
            Math.Sign(decimal.Round((net??0)*Money.Scale(options.DisplayCurrency),0)),Note:net.HasValue?note:null);
        var result=new List<CardField>{
            new(T("累计消耗 Token","Total tokens"),total),
            new(T("对应成本","Attributed cost"),value),
            new(T("输入","Input"),rows.Length>0?Tokens(input):blank),
            new(T("输出","Output"),rows.Length>0?Tokens(rows.Sum(u=>u.Output)):blank,
                Note:rows.Length>0 && rows.All(u=>u.Reasoning.HasValue)?
                    options.Format("其中推理 {0:N0}","{0:N0} reasoning",rows.Sum(u=>u.Reasoning!.Value)):null),
            new(T("缓存命中率","Cache hit rate"),hit),
            new(T("模型区分","Models"),models.Length>0?models:blank,ModelTags:models.Length>0),
            new(T("本会话收入","Chat income"),ChatIncome==null?missing:money.Format(received)),
            Profit(T("会话预估盈亏","Estimated chat profit / loss"),Value is {UnpricedTokens:0} cost?received-money.Convert(cost.Cost):null)
        };
        if(Project.Income is {} project){
            result.Add(new(project.FromChats?T("项目已录收入","Recorded project income"):T("本项目收入","Project income"),
                money.Format(money.Sum(project.ByCurrency))));
            result.Add(Profit(T("项目预估盈亏","Estimated project profit / loss"),Project.Cost is {} projectCost?
                money.Sum(project.ByCurrency)-money.Convert(projectCost):null,
                Project.AssumedStandard && Value?.AssumedStandard!=true?speedNote:null));
            if(Project.HasZeroIncome)result.Add(new(T("其中零收入会话成本","Cost of zero-revenue chats"),
                Project.ZeroCost is {} overhead?money.Format(money.Convert(overhead),approx:true):T("待计算","Pending")));
        }
        else if(Project.Project!=null){
            result.Add(new(T("本项目收入","Project income"),missing));
            result.Add(Profit(T("项目预估盈亏","Estimated project profit / loss"),null));
        }
        return result.ToArray();
    }

}

internal static class Data
{
    static Data()=>SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    internal static SqliteConnection Open(string path,SqliteOpenMode mode)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false,DefaultTimeout=1}.ToString());
        try{db.Open();return db;}catch{db.Dispose();throw;}
    }
    internal static Dictionary<string,Stats> Read(string path,string root,IReadOnlyCollection<string>? threads=null,TurnSpeeds? speeds=null)
    {
        using var db=Open(path,SqliteOpenMode.ReadOnly);
        speeds??=TurnSpeeds.Empty;
        using var tx=db.BeginTransaction(deferred:true);
        using var q=db.CreateCommand();
        q.Transaction=tx;
        q.CommandText="SELECT (SELECT application_id FROM pragma_application_id)=0x43545831 AND (SELECT user_version FROM pragma_user_version)=2";
        if(Convert.ToInt32(q.ExecuteScalar())!=1)throw new InvalidDataException("需要 Tracker v2 数据库。");
        q.CommandText="SELECT id FROM Device WHERE root=$root";
        q.Parameters.AddWithValue("$root",root);
        var rows=new Dictionary<string,Stats>(StringComparer.Ordinal);
        if(q.ExecuteScalar() is not string id)return rows;
        q.Parameters.Clear();q.Parameters.AddWithValue("$dev",id);
        string restrict="";
        if(threads!=null)
        {
            if(threads.Count==0)return rows;
            var keys=new List<string>();
            foreach(var thread in threads.Distinct())
            {
                string parameter="$t"+keys.Count;keys.Add(parameter);q.Parameters.AddWithValue(parameter,thread);
            }
            restrict=" AND thread_id IN ("+string.Join(",",keys)+")";
        }
        q.CommandText=$"""
          SELECT thread_id,model,confidence,sum(total),sum(input),sum(output),
            CASE WHEN count(cache_read)=count(*) THEN sum(cache_read) END,
            CASE WHEN count(cache_write)=count(*) THEN sum(cache_write) END,
            speed,CASE WHEN count(reasoning)=count(*) THEN sum(reasoning) END,turn_id
          FROM TokenLedger WHERE device_id=$dev {restrict} GROUP BY thread_id,model,confidence,speed,turn_id
          """;
        Stats Row(string key){if(!rows.TryGetValue(key,out var value))rows[key]=value=new();return value;}
        using(var r=q.ExecuteReader())while(r.Read()){
            string thread=r.GetString(0),model=r.GetString(1);
            string? speed=speeds.Resolve(thread,r.IsDBNull(10)?null:r.GetString(10),model,r.IsDBNull(8)?null:r.GetString(8));
            Row(thread).Usage.Add(new(model,r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),
                r.IsDBNull(6)?null:r.GetInt64(6),r.IsDBNull(7)?null:r.GetInt64(7),speed,
                r.IsDBNull(9)?null:r.GetInt64(9)));
        }
        // Retain rows with timing but no attributable usage so a freshly created chat
        // still gets the same eight fields, with missing usage distinct from zero.
        q.CommandText="SELECT thread_id FROM TaskTiming WHERE device_id=$dev AND first_ms IS NOT NULL"+restrict;
        using(var r=q.ExecuteReader())while(r.Read())Row(r.GetString(0));
        tx.Commit();
        return rows;
    }
}
