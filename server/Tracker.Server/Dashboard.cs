using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Tracker.Protocol;

namespace Tracker.Server;
internal static class Dashboard
{
    internal static void Map(WebApplication app)
    {
        foreach(var (path,kind) in new[]{("projects","project"),("task-groups","group"),("conflicts","conflict")}){
            app.MapGet("/api/v1/"+path,(HttpContext h,IDataProtectionProvider dp,Settings settings,string? cursor,int? limit)=>
                DeviceAuth.Authorized(h,(db,a)=>{
                    int size=limit??100;
                    if(size is <1 or >200)return Results.BadRequest();
                    var protector=dp.CreateProtector("resource-page",settings.Epoch.ToString(),a.Account,a.Grant.ToString(),kind).ToTimeLimitedDataProtector();
                    string after="";
                    if(cursor!=null){
                        try{after=protector.Unprotect(cursor);}catch(CryptographicException){return Results.NotFound();}
                    }
                    var list=db.List("SELECT id,version,value::text FROM resource WHERE acct=$1 AND kind=$2 AND id>$3 AND value IS NOT NULL ORDER BY id LIMIT $4",
                        r=>new Resource(kind,r.GetString(0),r.GetInt64(1),false,Wire.Read<System.Text.Json.JsonElement>(r.GetString(2))),a.Account,kind,after,size+1);
                    var next=list.Count>size?protector.Protect(list[size-1].Id,TimeSpan.FromMinutes(30)):null;
                    return Results.Json(new{items=list.Take(size),next},Wire.Json);
                }));
        }
        app.MapGet("/dashboard",(HttpContext h,NpgsqlDataSource ds,Guid? group,Guid? project)=>{
            var account=DeviceAuth.Account(h.User)!;
            return Storage.Tenant(ds,account,db=>{
                var rows=db.List("""
                    SELECT coalesce(e.value->>'model','UNKNOWN'),
                        coalesce(sum((e.value->'usage'->>'total')::numeric) FILTER(WHERE e.value->>'confidence' IN ('OBSERVED','DERIVED')),0),
                        count(*) FILTER(WHERE e.value->>'confidence' NOT IN ('OBSERVED','DERIVED'))
                    FROM resource e WHERE e.acct=$1 AND e.kind IN ('event','pending') AND e.value IS NOT NULL
                        AND (($2::text IS NULL AND $3::text IS NULL) OR EXISTS(
                            SELECT 1 FROM resource t JOIN resource m ON m.acct=t.acct AND m.kind='member' AND
                                (m.value->>'thread_id'=t.value->>'thread_id' OR (t.value->'sessions') ? (m.value->>'session_id'))
                            JOIN resource g ON g.acct=m.acct AND g.kind='group' AND g.id=m.value->>'group_id' AND g.value IS NOT NULL
                            WHERE t.acct=e.acct AND t.kind='thread' AND t.value->>'thread_id'=e.value->>'thread_id'
                                AND ($2 IS NULL OR g.id=$2) AND ($3 IS NULL OR g.value->>'project_id'=$3)))
                    GROUP BY 1 ORDER BY 2 DESC,1
                    """,r=>(Model:r.GetString(0),Total:r.GetDecimal(1),Pending:r.GetInt64(2)),account,group?.ToString(),project?.ToString());
                var links=db.List("SELECT kind,id,value->>'name' FROM resource WHERE acct=$1 AND kind IN ('group','project') AND value IS NOT NULL ORDER BY kind,id LIMIT 200",
                    r=>$"<a href='/dashboard?{r.GetString(0)}={r.GetString(1)}'>{WebUtility.HtmlEncode(r.GetString(2))}</a>",account);
                string body=string.Concat(rows.Select(r=>$"<tr><td>{WebUtility.HtmlEncode(r.Model)}</td><td>{r.Total.ToString("N0",CultureInfo.InvariantCulture)}</td><td>{r.Pending}</td></tr>"));
                return Results.Content($$"""
                    <!doctype html><html lang="zh"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                    <title>用量统计 · Codex Tracker</title><style>
                    :root{color-scheme:light dark}body{font:16px/1.55 "Source Sans 3","Segoe UI",sans-serif;max-width:58rem;margin:3rem auto;padding:0 1rem}
                    nav a{margin-right:1rem}table{border-collapse:collapse;width:100%}td,th{text-align:right;border-bottom:1px solid #8887;padding:.65rem}td:first-child,th:first-child{text-align:left}
                    </style><nav><a href="/dashboard">全部用量</a><a href="/devices">设备</a><a href="/Identity/Account/Manage">账号</a></nav>
                    <h1>用量统计</h1><nav>{{string.Join(" ",links)}}</nav><table><thead><tr><th>模型</th><th>可归属 Token</th><th>待核实记录</th></tr></thead><tbody>{{body}}</tbody></table>
                    </html>
                    ""","text/html; charset=utf-8");
            });
        }).RequireAuthorization();
    }
}
