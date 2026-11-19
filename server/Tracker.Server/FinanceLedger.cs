using System.Text.Json;
using Tracker.Protocol;

namespace Tracker.Server;

internal sealed record ProjectProfile(string Name,string Customer,decimal Contract,string Status,DateOnly Start,DateOnly? End);
internal sealed record CashEntry(Guid Project,string Category,DateOnly Date,decimal Cny,string Reference,bool Voided);
internal sealed record ResetAssignment(Guid? Project,long SourceVersion,decimal? Cny);
internal sealed record FinanceLedger(List<Revision<ProjectProfile>> Profiles,List<Revision<CashEntry>> Cash,
    List<Revision<ResetAssignment>> Resets,List<Resource> Resources)
{
    internal static FinanceLedger Load(Sql db,string account)
    {
        List<Revision<T>> Read<T>(string kind)=>db.List("""
            SELECT DISTINCT ON(id) id,version,body::text FROM finance_revision
            WHERE acct=$1 AND kind=$2 ORDER BY id,version DESC
            """,r=>new Revision<T>(r.GetGuid(0),r.GetInt64(1),Wire.Read<T>(r.GetString(2))),account,kind);
        var resources=db.List("""
            SELECT kind,id,version,value::text FROM resource
            WHERE acct=$1 AND kind IN ('project','group','member','thread') AND value IS NOT NULL ORDER BY kind,id
            """,r=>new Resource(r.GetString(0),r.GetString(1),r.GetInt64(2),false,Wire.Read<JsonElement>(r.GetString(3))),account);
        return new(Read<ProjectProfile>("profile"),Read<CashEntry>("cash"),Read<ResetAssignment>("reset"),resources);
    }

    internal static string? Field(Resource r,string key)=>r.Value is JsonElement e && e.TryGetProperty(key,out var value)?value.GetString():null;

    internal static void WriteResource(Sql db,string account,string operation,Guid id,long version,Edit? value)
    {
        var op=new Operation(Guid.NewGuid(),operation,ResourceId:id,BaseVersion:version,Value:value);
        CostInputs.Require(Wire.Valid(op),"请核对项目名称、任务组或会话标识。");
        var changes=new List<Resource>();
        if(SyncApi.EditResource(db,account,op,changes)!=null)
            throw new CostConflict("归属或项目版本已经变更，请刷新后核对。");
        SyncApi.Publish(db,account,changes);
    }

    internal void SaveProfile(Sql db,string account,Guid id,long version,long resourceVersion,ProjectProfile profile)
    {
        CostInputs.Require(Wire.Name(profile.Name) && Text(profile.Customer) && Money(profile.Contract) &&
            profile.Status is "PLANNED" or "ACTIVE" or "COMPLETED" or "ON_HOLD" &&
            (profile.End==null || profile.End>=profile.Start) && (profile.Status!="COMPLETED" || profile.End!=null),
            "请核对客户、项目名称、合同金额及起止日期。");
        var resource=Resources.SingleOrDefault(r=>r.Kind=="project" && r.Id==id.ToString());
        var name=resource==null?null:Field(resource,"name");
        if(name!=profile.Name || (resource?.Version??0)!=resourceVersion){
            if(name!=profile.Name && (resource?.Version??0)==resourceVersion)
                WriteResource(db,account,resource==null?"project.create":"project.update",id,resourceVersion,new Edit(Name:profile.Name));
            else if(name!=profile.Name || Profiles.FirstOrDefault(p=>p.Id==id)?.Value!=profile)
                throw new CostConflict("项目已在其他设备修改，请刷新后核对。");
        }
        Append(db,account,"profile",id,version,profile);
    }

    internal void SaveCash(Sql db,string account,Guid id,long version,CashEntry entry,CostBooks costs)
    {
        // A deleted sync project retains its history. Existing entries can still be corrected.
        var old=Cash.SingleOrDefault(x=>x.Id==id);
        RequireProject(entry.Project,old?.Value.Project);
        CostInputs.Require(entry.Category is "RECEIPT" or "AI" or "LABOR" or "SERVER" or "OTHER"
            && Money(entry.Cny) && entry.Cny>0 && Text(entry.Reference),"请核对流水类别、正数人民币金额和唯一凭据编号。");
        if(!entry.Voided){
            if(Cash.Any(x=>x.Id!=id && !x.Value.Voided && x.Value.Reference==entry.Reference))
                throw new CostConflict("该凭据已记账，请修订原流水。");
            if(costs.Resets.Any(x=>x.Value.Reference==entry.Reference))
                throw new CostConflict("该凭据已登记为重置，请在重置归属中维护。");
        }
        Append(db,account,"cash",id,version,entry);
    }
    internal void AssignReset(Sql db,string account,Guid id,long version,ResetAssignment entry,CostBooks costs)
    {
        var source=costs.Resets.SingleOrDefault(x=>x.Id==id);
        CostInputs.Require(source!=null && source.Value.Cash>0,"请选择本账号有实付凭据的重置。");
        if(source!.Version!=entry.SourceVersion)throw new CostConflict("重置已修订，请核对最新支付金额。");
        if(entry.Project.HasValue)RequireProject(entry.Project.Value,Resets.SingleOrDefault(x=>x.Id==id)?.Value.Project);
        CostInputs.Require(entry.Cny is decimal cny && Money(cny) && cny>0 &&
            (source.Value.Currency!="CNY" || source.Value.Cash==cny),"请填写本次重置实付折合人民币金额。");
        Append(db,account,"reset",id,version,entry);
    }

    void RequireProject(Guid project,Guid? original)
    {
        CostInputs.Require(project!=Guid.Empty && (original==project ||
            Resources.Any(r=>r.Kind=="project" && r.Id==project.ToString())),"请选择本账号的项目。");
    }
    static bool Text(string s)=>s.Length is >0 and <=160 && !s.Any(char.IsControl) && !string.IsNullOrWhiteSpace(s);
    static bool Money(decimal amount)=>amount>=0 && amount<=1_000_000_000_000m && decimal.Round(amount,2)==amount;
    static void Append<T>(Sql db,string account,string kind,Guid id,long version,T item)
    {
        CostInputs.Require(id!=Guid.Empty && version>=0,"无效记录编号或版本。");
        string body=Wire.Pack(item);
        var prior=db.List("""
            SELECT version,body=$4::jsonb FROM finance_revision WHERE acct=$1 AND kind=$2 AND id=$3
            ORDER BY version DESC LIMIT 1
            """,r=>(Version:r.GetInt64(0),Same:r.GetBoolean(1)),account,kind,id,body).SingleOrDefault();
        if(prior.Same)return;
        if(prior.Version!=version)throw new CostConflict("流水或档案已修订，请刷新以查看最新版本。");
        db.Run("INSERT INTO finance_revision(acct,kind,id,version,body) VALUES($1,$2,$3,$4,$5::jsonb)",
            account,kind,id,version+1,body);
    }
}
