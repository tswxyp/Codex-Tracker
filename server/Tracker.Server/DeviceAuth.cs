using Microsoft.AspNetCore;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Tracker.Protocol;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tracker.Server;

internal sealed record Actor(string Account,Guid Device,Guid Grant);
internal sealed record LogoutTicket(string Account,Guid Grant,string Proof);
internal static class DeviceAuth
{
    const string OAuth=OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    const string Bearer=OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    static string E(string? s)=>WebUtility.HtmlEncode(s??"");
    internal static ValueTask Begin(OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext ctx)
    {
        var q=ctx.Request;
        string? Get(string name)=>(string?)q.GetParameter(name);
        var key=Get("public_key");
        try
        {
            if(!Guid.TryParse(Get("device_id"),out var id) || id==Guid.Empty || !Wire.Name(Get("device_name"))
                || key==null || key.Length>256 || !q.GetScopes().Contains("tracker")) throw new FormatException();
            using var ec=ECDsa.Create();
            var encoded=Convert.FromBase64String(key);
            ec.ImportSubjectPublicKeyInfo(encoded,out var read);
            if(read!=encoded.Length || ec.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new FormatException();
            var identity=new ClaimsIdentity();
            identity.SetClaim("ct_device",id.ToString()).SetClaim("ct_key",key).SetClaim("ct_name",Get("device_name"));
            var principal=new ClaimsPrincipal(identity);
            principal.SetScopes(q.GetScopes());
            ctx.SignIn(principal);
        }
        catch(Exception ex) when(ex is FormatException or CryptographicException or ArgumentException){ctx.Reject(Errors.InvalidRequest);}
        return ValueTask.CompletedTask;
    }

    internal static string? Account(ClaimsPrincipal principal)=>principal.FindFirstValue(ClaimTypes.NameIdentifier);
    internal static bool Active(Sql db,Actor a,Settings s)=>db.One<bool>("""
        SELECT EXISTS(SELECT 1 FROM grant_state g JOIN device d ON (d.acct,d.id)=(g.acct,g.device_id)
            JOIN "AspNetUsers" u ON u."Id"=g.acct WHERE g.acct=$1 AND g.id=$2 AND g.device_id=$3 AND g.epoch=$4
            AND NOT g.revoked AND NOT d.revoked AND u."SecurityStamp"=g.stamp AND u."EmailConfirmed"
            AND g.issued_at>now()-interval '90 days')
        """,a.Account,a.Grant,a.Device,s.Epoch);
    static Actor? ActorFrom(ClaimsPrincipal? p)=>p?.GetClaim(Claims.Subject) is string a
        && Guid.TryParse(p.GetClaim("ct_device"),out var d) && Guid.TryParse(p.GetClaim("ct_grant"),out var g)?new(a,d,g):null;
    internal static async Task<IResult> Authorized(HttpContext ctx,Func<Sql,Actor,IResult> work)
    {
        var token=await ctx.AuthenticateAsync(Bearer);
        var a=ActorFrom(token.Principal);
        if(!token.Succeeded || a==null || !token.Principal!.HasScope("tracker"))return Results.Unauthorized();
        var sp=ctx.RequestServices;
        return Storage.Tenant(sp.GetRequiredService<NpgsqlDataSource>(),a.Account,db=>{
            if(!Active(db,a,sp.GetRequiredService<Settings>()) || sp.GetRequiredService<Recovery>().Revoked(a.Account,a.Device))return Results.StatusCode(403);
            db.Run("UPDATE device SET last_active=now() WHERE acct=$1 AND id=$2",a.Account,a.Device);
            return work(db,a);
        });
    }
    static string Csrf(HttpContext h,IAntiforgery af)
    {
        var t=af.GetAndStoreTokens(h);
        return $"<input type='hidden' name='{E(t.FormFieldName)}' value='{E(t.RequestToken)}'>";
    }
    static async Task<bool> RecentLogin(HttpContext h)
    {
        var result=await h.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return result.Succeeded && result.Properties?.Items.TryGetValue("ct.login_at",out var at)==true
            && long.TryParse(at,out var seconds) && seconds>=DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeSeconds();
    }
    static IResult Html(string body)=>Results.Content("<!doctype html><html lang='zh'><meta charset='utf-8'><title>Codex Tracker</title><main>"+body+"</main></html>","text/html; charset=utf-8");
    internal static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/device-logout-ticket",(LogoutTicket t,NpgsqlDataSource ds,Settings settings)=>{
            if(!Guid.TryParse(t.Account,out _) || t.Proof==null || t.Proof.Length>128)return Results.BadRequest();
            using(var conn=ds.OpenConnection()){
                var lookup=new Sql(conn);
                if(!lookup.One<bool>("SELECT EXISTS(SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\"=$1)",t.Account))return Results.NoContent();
            }
            return Storage.Tenant(ds,t.Account,db=>{
                var key=db.One<string>("SELECT d.public_key FROM grant_state g JOIN device d ON (d.acct,d.id)=(g.acct,g.device_id) WHERE g.acct=$1 AND g.id=$2",t.Account,t.Grant);
                if(key!=null){
                    using var ec=ECDsa.Create();ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key),out _);
                    var message="tracker-logout/"+settings.Issuer.GetLeftPart(UriPartial.Authority)+"/"+t.Grant;
                    if(ec.VerifyData(Encoding.UTF8.GetBytes(message),Convert.FromBase64String(t.Proof),HashAlgorithmName.SHA256))
                        db.Run("UPDATE grant_state SET revoked=true WHERE acct=$1 AND id=$2",t.Account,t.Grant);
                }
                return Results.NoContent();
            });
        });
        app.MapMethods("/connect/verify",["GET","POST"],Verify);
        app.MapPost("/connect/token",Token);
        app.MapGet("/devices",(HttpContext h,IAntiforgery af,NpgsqlDataSource ds)=>{
            var account=Account(h.User)!;
            return Storage.Tenant(ds,account,db=>{
                string hidden=Csrf(h,af);
                var list=db.List("""
                    SELECT id,name,revoked,version,os,client_version,last_active,last_sync,
                           coalesce(last_active>now()-interval '3 minutes',false)
                    FROM device WHERE acct=$1 ORDER BY registered_at
                    """,r=>{
                        string Time(int i)=>r.IsDBNull(i)?"暂无记录":r.GetDateTime(i).ToUniversalTime().ToString("u");
                        string status=r.GetBoolean(2)?"已解绑":r.GetBoolean(8)?"在线":"离线";
                        return $"""
                            <li><strong>{E(r.GetString(1))}</strong> · {status} · {E(r.GetString(4))} · 客户端 {E(r.GetString(5))}
                            <p>设备 ID <code>{r.GetGuid(0)}</code><br>最近活动 {Time(6)} · 最近完整同步 {Time(7)}</p>
                            <form method='post'>{hidden}<input type='hidden' name='id' value='{r.GetGuid(0)}'><input type='hidden' name='version' value='{r.GetInt64(3)}'>
                            <input name='name' maxlength='100' aria-label='设备名称'><button name='action' value='rename'>重命名</button>
                            <button name='action' value='revoke'>解绑</button></form></li>
                            """;
                    },account);
                return Html("<nav><a href='/dashboard'>用量统计</a> · <a href='/Identity/Account/Manage'>账号管理</a></nav><h1>我的设备</h1><ul>"
                    +string.Join("",list)+"</ul><form method='post' action='/account/logout-all'>"+hidden+"<button>退出所有设备和网页会话</button></form>");
            });
        }).RequireAuthorization();
        app.MapPost("/account/logout-all",async (HttpContext h,IAntiforgery af,UserManager<IdentityUser> users,SignInManager<IdentityUser> signin)=>{
            if(!await af.IsRequestValidAsync(h))return Results.BadRequest();
            if(!await RecentLogin(h))return Results.Redirect("/Identity/Account/Login?returnUrl=%2Fdevices");
            var user=await users.GetUserAsync(h.User);
            if(user==null)return Results.Unauthorized();
            if(!(await users.UpdateSecurityStampAsync(user)).Succeeded)return Results.Conflict();
            await signin.SignOutAsync();
            return Results.Redirect("/");
        }).RequireAuthorization();
        app.MapPost("/devices",async (HttpContext h,IAntiforgery af,NpgsqlDataSource ds)=>{
            if(!await af.IsRequestValidAsync(h))return Results.BadRequest();
            if(!await RecentLogin(h))return Results.Redirect("/Identity/Account/Login?returnUrl=%2Fdevices");
            var f=await h.Request.ReadFormAsync();
            if(!Guid.TryParse(f["id"],out var id)||!long.TryParse(f["version"],out var version))return Results.BadRequest();
            var a=Account(h.User)!;
            return Storage.Tenant(ds,a,db=>{
                string? name=f["name"];
                long current=db.One<long>("SELECT version FROM device WHERE acct=$1 AND id=$2",a,id);
                if(current==0)return Results.NotFound();
                if(current!=version)return Results.Conflict();
                if(f["action"]=="rename" && Wire.Name(name))
                    db.Run("UPDATE device SET name=$1,version=version+1 WHERE acct=$2 AND id=$3 AND version=$4",name,a,id,version);
                else if(f["action"]=="revoke"){
                    h.RequestServices.GetRequiredService<Recovery>().Revoke(a,id);
                    db.Run("UPDATE device SET revoked=true,version=version+1 WHERE acct=$1 AND id=$2",a,id);
                    db.Run("UPDATE grant_state SET revoked=true WHERE acct=$1 AND device_id=$2",a,id);
                }else return Results.BadRequest();
                return Results.Redirect("/devices");
            });
        }).RequireAuthorization();
        app.MapPost("/api/v1/device-logout",(Func<HttpContext,Task<IResult>>)((HttpContext h)=>Authorized(h,(db,a)=>{
            db.Run("UPDATE grant_state SET revoked=true WHERE acct=$1 AND id=$2",a.Account,a.Grant);return Results.NoContent();
        })));
        app.MapGet("/api/v1/devices",(Func<HttpContext,Task<IResult>>)((HttpContext h)=>Authorized(h,(db,a)=>Results.Json(db.List("""
            SELECT id,name,os,client_version,registered_at,last_active,last_sync,
                CASE WHEN revoked THEN 'revoked' WHEN last_active>now()-interval '3 minutes' THEN 'online' ELSE 'offline' END
            FROM device WHERE acct=$1 ORDER BY registered_at
            """,r=>new {device_id=r.GetGuid(0), name=r.GetString(1),os=r.GetString(2),client_version=r.GetString(3),
                registered_at=r.GetDateTime(4),last_active=r.IsDBNull(5)?(DateTime?)null:r.GetDateTime(5),
                last_sync=r.IsDBNull(6)?(DateTime?)null:r.GetDateTime(6),status=r.GetString(7)},a.Account)))));
        app.MapPost("/api/v1/devices/self/heartbeat",(Func<HttpContext,Task<IResult>>)((HttpContext h)=>Authorized(h,(db,a)=>{
            static string? VersionHeader(HttpContext ctx,string key)=>Version.TryParse(ctx.Request.Headers[key].ToString(),out var version)?version.ToString():null;
            var os=VersionHeader(h,"Tracker-Windows-Version");
            var version=VersionHeader(h,"Tracker-Client-Version");
            db.Run("UPDATE device SET client_version=coalesce($3::text,client_version),os=coalesce($4::text,os) WHERE acct=$1 AND id=$2",
                a.Account,a.Device,version,os==null?null:"Windows "+os);
            return Results.NoContent();
        })));
    }

    static async Task<IResult> Verify(HttpContext h,IAntiforgery af,UserManager<IdentityUser> users,NpgsqlDataSource ds,Settings settings)
    {
        if(!await RecentLogin(h))return Results.Challenge(new AuthenticationProperties{
            RedirectUri=h.Request.Path+h.Request.QueryString},[IdentityConstants.ApplicationScheme]);
        var principal=(await h.AuthenticateAsync(OAuth)).Principal;
        var request=h.GetOpenIddictServerRequest()!;
        if(principal==null || !Guid.TryParse(principal.GetClaim("ct_device"),out var device))
            return Html("<h1>授权设备</h1><form><label>设备上显示的短码 <input name='user_code' required></label><button>继续</button></form>");
        var code=E(request.UserCode);var name=principal.GetClaim("ct_name")!;
        if(HttpMethods.IsGet(h.Request.Method))return Html($"<h1>授权 Windows 设备</h1><p>{E(name)} · {code}</p><p>允许读取本账号统计并上传此设备选定的统计记录。</p><form method='post'>{Csrf(h,af)}<input type='hidden' name='user_code' value='{code}'><button name='decision' value='approve'>批准</button><button name='decision' value='deny'>拒绝</button></form>");
        if(!await af.IsRequestValidAsync(h))return Results.BadRequest();
        if((await h.Request.ReadFormAsync())["decision"]!="approve")return Results.Forbid(authenticationSchemes:[OAuth]);
        var user=await users.GetUserAsync(h.User);
        if(user==null || !user.EmailConfirmed)return Results.StatusCode(403);
        var key=principal.GetClaim("ct_key")!;var grant=Guid.NewGuid();
        var allowed=Storage.Tenant(ds,user.Id,db=>{
            if(h.RequestServices.GetRequiredService<Recovery>().Revoked(user.Id,device))return false;
            db.Run("INSERT INTO device(acct,id,name,public_key) VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING",user.Id,device,name,key);
            if(!db.One<bool>("SELECT EXISTS(SELECT 1 FROM device WHERE acct=$1 AND id=$2 AND public_key=$3 AND NOT revoked)",user.Id,device,key))return false;
            db.Run("INSERT INTO grant_state(acct,id,device_id,epoch,stamp) VALUES($1,$2,$3,$4,$5)",user.Id,grant,device,settings.Epoch,user.SecurityStamp);
            return true;
        });
        if(!allowed)return Results.Forbid(authenticationSchemes:[OAuth]);
        var identity=new ClaimsIdentity("device");
        identity.SetClaim(Claims.Subject,user.Id).SetClaim("ct_device",device.ToString()).SetClaim("ct_grant",grant.ToString()).SetClaim("ct_key",key);
        identity.SetDestinations(c=>c.Type is "ct_device" or "ct_grant" or Claims.Subject?[Destinations.AccessToken]:[]);
        var result=new ClaimsPrincipal(identity);
        result.SetScopes(principal.GetScopes());result.SetResources("tracker-api");
        return Results.SignIn(result,authenticationScheme:OAuth);
    }

    static async Task<IResult> Token(HttpContext h,NpgsqlDataSource ds,Settings settings)
    {
        var request=h.GetOpenIddictServerRequest()!;
        var p=(await h.AuthenticateAsync(OAuth)).Principal;
        var actor=ActorFrom(p);
        if(actor==null || !Storage.Tenant(ds,actor.Account,db=>Active(db,actor,settings) && !h.RequestServices.GetRequiredService<Recovery>().Revoked(actor.Account,actor.Device)))return Results.Forbid(authenticationSchemes:[OAuth]);
        try{
            var challenge=request.DeviceCode??request.RefreshToken??"";
            using var ec=ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(p!.GetClaim("ct_key")!),out _);
            if(!ec.VerifyData(Encoding.UTF8.GetBytes(challenge),Convert.FromBase64String((string?)request.GetParameter("proof")??""),HashAlgorithmName.SHA256))
                return Results.Forbid(authenticationSchemes:[OAuth]);
        }catch(Exception ex) when(ex is FormatException or CryptographicException){return Results.Forbid(authenticationSchemes:[OAuth]);}
        return Results.SignIn(p!,authenticationScheme:OAuth);
    }
}
