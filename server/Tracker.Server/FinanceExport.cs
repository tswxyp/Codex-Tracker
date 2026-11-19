using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Tracker.Server;

internal sealed record FinanceSheet(string Name,List<object?[]> Rows);
internal static class FinanceExport
{
    internal static List<FinanceSheet> Tables(FinanceReport report,IEnumerable<Revision<CashEntry>> cash)
    {
        var projects=new FinanceSheet("项目利润",[["项目编号","项目","客户","合同总额 CNY","本期到账 CNY","累计到账 CNY","合同余额 CNY","订阅 CNY","额外 AI CNY","重置 CNY","人工 CNY","服务器 CNY","其他 CNY","已知成本 CNY","利润 CNY","利润率 %","AI 成本率 %","总历时 h","实测活跃 h","每活跃小时到账 CNY","API 等效已知小计","待核算","订阅分摊依据"]]);
        foreach(var p in report.Projects.Append(report.Pool))
            projects.Rows.Add([p.Id,p.Name,p.Profile?.Customer,p.Profile?.Contract,p.Received,p.CumulativeReceived,p.Outstanding,
                p.Subscription,p.ExtraAi,p.Reset,p.Labor,p.Server,p.Other,p.Cost,p.Profit,p.Margin,p.AiRatio,
                p.Time?.TotalMs/3600000m,p.Time?.ActiveMs/3600000m,p.RevenuePerHour,
                string.Join(" / ",p.ApiKnown.Select(x=>$"{x.Key} {x.Value.ToString(CultureInfo.InvariantCulture)}")),string.Join(" / ",p.Pending),
                string.Join(" / ",p.Basis)]);
        var periods=new FinanceSheet("订阅账期",[["账单编号","订阅","开始","结束（不含）","实付 CNY","已分摊 CNY","Global Reset 次数","已核实 Token","Credits","依据","权重版本","API 币种","API 已知小计","API 可信度","每千万 Token 折算 CNY","每亿 Token 折算 CNY","待核算","进行中"]]);
        foreach(var b in report.Bills)
            periods.Rows.Add([b.Id.ToString(),b.Plan,b.Start.ToString("O"),b.End.ToString("O"),b.PaidCny,b.AllocatedCny,b.GlobalResets,
                b.Tokens,b.Credits,b.Basis,b.WeightSet,b.ApiCurrency,b.ApiKnown,b.ApiStatus,b.PerTenMillion,b.PerTenMillion*10,
                string.Join(" / ",b.Reasons.Select(CostPage.Reason)),b.Provisional?"是":"否"]);
        var entries=new FinanceSheet("项目流水",[["流水编号","项目编号","项目","类别","日期","金额 CNY","凭据","状态","版本"]]);
        var names=report.Projects.ToDictionary(p=>p.Id,p=>p.Name);
        foreach(var r in cash.OrderBy(x=>x.Value.Date).ThenBy(x=>x.Id)){
            var v=r.Value;
            entries.Rows.Add([r.Id.ToString(),v.Project.ToString(),names.GetValueOrDefault(v.Project.ToString()),FinancePage.Label(v.Category),
                v.Date.ToString("O"),v.Cny,v.Reference,v.Voided?"已作废":"有效",r.Version]);
        }
        var summary=new FinanceSheet("账号总览",[["项目","金额 CNY"],["订阅实付",report.SubscriptionPaid],
            ["实际到账",report.Received],["已知现金成本",report.KnownCost],["现金净额",report.Net],
            ["口径",string.Join(" / ",report.Warnings)]]);
        return [projects,periods,entries,summary];
    }

    static string Value(object v)=>Convert.ToString(v,CultureInfo.InvariantCulture)!;
    internal static byte[] Csv(FinanceSheet sheet)
    {
        string Cell(object? value){
            if(value==null)return "";
            if(value is not string s)return Value(value);
            if(s.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || s.StartsWith('\t') || s.StartsWith('\r'))s="'"+s;
            return "\""+s.Replace("\"","\"\"")+"\"";
        }
        var utf8=new UTF8Encoding(true);
        return [..utf8.GetPreamble(),..utf8.GetBytes(string.Join("\r\n",sheet.Rows.Select(row=>string.Join(",",row.Select(Cell))))+"\r\n")];
    }

    internal static byte[] Workbook(List<FinanceSheet> sheets)
    {
        XNamespace main="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel="http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace doc="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace types="http://schemas.openxmlformats.org/package/2006/content-types";
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)){
            void Save(string path,XElement xml){
                using var stream=zip.CreateEntry(path).Open();
                xml.Save(stream,SaveOptions.DisableFormatting);
            }
            XElement Link(int id,string type,string target)=>new(rel+"Relationship",new XAttribute("Id","rId"+id),
                new XAttribute("Type",doc.NamespaceName+"/"+type),new XAttribute("Target",target));
            XElement Override(string part,string type)=>new(types+"Override",new XAttribute("PartName","/"+part),
                new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml."+type+"+xml"));
            Save("_rels/.rels",new(rel+"Relationships",Link(1,"officeDocument","xl/workbook.xml")));
            var content=new XElement(types+"Types",new XElement(types+"Default",new XAttribute("Extension","rels"),
                new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),Override("xl/workbook.xml","sheet.main"));
            var list=new XElement(main+"sheets");
            var links=new XElement(rel+"Relationships");
            foreach(var (sheet,index) in sheets.Select((s,i)=>(s,i+1))){
                string path=$"worksheets/sheet{index}.xml";
                content.Add(Override("xl/"+path,"worksheet"));
                links.Add(Link(index,"worksheet",path));
                list.Add(new XElement(main+"sheet",new XAttribute("name",sheet.Name),new XAttribute("sheetId",index),new XAttribute(doc+"id","rId"+index)));
                XElement Cell(object? value){
                    if(value==null)return new(main+"c");
                    if(value is string s)return new(main+"c",new XAttribute("t","inlineStr"),new XElement(main+"is",new XElement(main+"t",
                        new XAttribute(XNamespace.Xml+"space","preserve"),s)));
                    return new(main+"c",new XElement(main+"v",Value(value)));
                }
                Save("xl/"+path,new(main+"worksheet",new XElement(main+"sheetData",sheet.Rows.Select(row=>new XElement(main+"row",row.Select(Cell))))));
            }
            Save("xl/workbook.xml",new(main+"workbook",new XAttribute(XNamespace.Xmlns+"r",doc.NamespaceName),list));
            Save("xl/_rels/workbook.xml.rels",links);
            Save("[Content_Types].xml",content);
        }
        return output.ToArray();
    }
}
