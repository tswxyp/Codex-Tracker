using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.Server;
using Tracker.Server;

var builder=WebApplication.CreateBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets",optional:true);
var cfg=builder.Configuration;
string Required(string key) => cfg[key] ?? throw new InvalidOperationException("Missing configuration: "+key);
var issuer=new Uri(Required("Tracker:Issuer"));
if(issuer.Scheme!="https" || issuer.PathAndQuery!="/" || !string.IsNullOrEmpty(issuer.UserInfo))
    throw new InvalidOperationException("Use a dedicated HTTPS origin as issuer.");
var connString=cfg.GetConnectionString("Tracker") ?? new NpgsqlConnectionStringBuilder {
    Host=Required("Pg:Host"), Database=Required("Pg:Database"), Username=Required("Pg:User"),Password=Required("Pg:Password").TrimEnd('\r','\n')
}.ConnectionString;
var settings=new Settings(issuer,Guid.Parse(Required("Tracker:Epoch")),connString);
var cert=X509CertificateLoader.LoadPkcs12FromFile(Required("Tracker:Certificate"),Required("Tracker:CertificatePassword").TrimEnd('\r','\n'));
builder.WebHost.ConfigureKestrel(o=>o.ConfigureHttpsDefaults(https=>https.ServerCertificate=cert));
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new Recovery(Required("Tracker:SecurityDirectory")));
builder.Services.AddSingleton(_=>NpgsqlDataSource.Create(settings.Connection));
builder.Services.AddDbContext<Accounts>(o=>{o.UseNpgsql(settings.Connection);o.UseOpenIddict();});
builder.Services.AddDefaultIdentity<IdentityUser>(o=>{
    o.SignIn.RequireConfirmedAccount=true;
    o.User.RequireUniqueEmail=true;
    o.Password.RequiredLength=15;
    o.Password.RequireDigit=o.Password.RequireUppercase=o.Password.RequireLowercase=o.Password.RequireNonAlphanumeric=false;
    o.Lockout.MaxFailedAccessAttempts=5;
}).AddEntityFrameworkStores<Accounts>();
builder.Services.Configure<SecurityStampValidatorOptions>(o=>o.ValidationInterval=TimeSpan.Zero);
builder.Services.Configure<DataProtectionTokenProviderOptions>(o=>o.TokenLifespan=TimeSpan.FromMinutes(15));
builder.Services.ConfigureApplicationCookie(o=>{
    o.Cookie.SecurePolicy=CookieSecurePolicy.Always;
    o.SlidingExpiration=false;o.ExpireTimeSpan=TimeSpan.FromHours(8);
    o.Events.OnSigningIn=ctx=>{
        ctx.Properties.Items.TryAdd("ct.login_at",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        return Task.CompletedTask;
    };
});
builder.Services.AddDataProtection().SetApplicationName("CodexTracker:"+issuer+":"+settings.Epoch)
    .PersistKeysToFileSystem(new DirectoryInfo(Required("Tracker:KeyDirectory"))).ProtectKeysWithCertificate(cert);
builder.Services.AddTransient<IEmailSender,AccountMail>();
builder.Services.AddRazorPages();
builder.Services.AddOpenIddict().AddCore(o=>o.UseEntityFrameworkCore().UseDbContext<Accounts>())
    .AddServer(o=>{
        o.SetIssuer(issuer).SetDeviceAuthorizationEndpointUris("/connect/deviceauthorization")
            .SetEndUserVerificationEndpointUris("/connect/verify").SetTokenEndpointUris("/connect/token")
            .SetRevocationEndpointUris("/connect/revocation");
        o.AllowDeviceAuthorizationFlow().AllowRefreshTokenFlow().RegisterScopes("tracker");
        o.AddSigningCertificate(cert).AddEncryptionCertificate(cert).UseReferenceAccessTokens().UseReferenceRefreshTokens();
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(10)).SetRefreshTokenLifetime(TimeSpan.FromDays(30))
            .SetRefreshTokenReuseLeeway(TimeSpan.Zero);
        o.AddEventHandler<OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext>(d=>
            d.UseInlineHandler(DeviceAuth.Begin).SetOrder(int.MinValue+100_000));
        o.UseAspNetCore().EnableEndUserVerificationEndpointPassthrough().EnableTokenEndpointPassthrough();
    }).AddValidation(o=>{o.UseLocalServer();o.AddAudiences("tracker-api");o.EnableTokenEntryValidation();o.UseAspNetCore();});
builder.Services.AddRateLimiter(o=>{
    o.RejectionStatusCode=429;
    o.OnRejected=(context,_)=>{
        if(context.Lease.TryGetMetadata(MetadataName.RetryAfter,out var delay))
            context.HttpContext.Response.Headers.RetryAfter=Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(h=>{
        var auth=h.Request.Path.StartsWithSegments("/Identity") || h.Request.Path.StartsWithSegments("/connect");
        return RateLimitPartition.GetFixedWindowLimiter((auth?"auth:":"api:")+h.Connection.RemoteIpAddress,
            _=>new FixedWindowRateLimiterOptions{PermitLimit=auth?100:1000,Window=TimeSpan.FromMinutes(1)});
    });
});
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=524288);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command",LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict",LogLevel.Warning);
var app=builder.Build();
if(args.Contains("--migrate")) {await Storage.Migrate(app.Services);return;}
if(args.Contains("--recover") || args.Contains("--recover-frozen") || args.Contains("--release-account")){await Recovery.Admin(app.Services,args);return;}
using(var con=app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnection())
{
    var sql=new Sql(con);
    if(sql.One<bool>("SELECT rolsuper OR rolbypassrls OR pg_has_role(current_user,(SELECT relowner FROM pg_class WHERE oid='resource'::regclass),'USAGE') FROM pg_roles WHERE rolname=current_user")
        || sql.One<int>("SELECT max(version) FROM tracker_schema")!=1 || sql.One<int>("SELECT max(version) FROM cost_schema")!=1
        || sql.One<int>("SELECT max(version) FROM finance_schema")!=1)
        throw new InvalidOperationException("Configure a restricted runtime role and apply migrations.");
    if(sql.One<Guid>("SELECT epoch FROM tracker_instance WHERE singleton")!=settings.Epoch)
        throw new InvalidOperationException("代际不匹配，请检查恢复流程。");
    app.Services.GetRequiredService<Recovery>().Validate(sql.One<Guid>("SELECT journal_id FROM tracker_instance WHERE singleton"));
}
app.Use(async (ctx,next)=>{
    if(!ctx.Request.IsHttps || !string.Equals(ctx.Request.Host.Value,issuer.Authority,StringComparison.OrdinalIgnoreCase)){
        ctx.Response.StatusCode=400;return;
    }
    ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["Referrer-Policy"]="no-referrer";
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";ctx.Response.Headers["X-Frame-Options"]="DENY";
    if(!cfg.GetValue<bool>("Tracker:OpenRegistration") && ctx.Request.Path.StartsWithSegments("/Identity/Account/Register")){
        ctx.Response.StatusCode=403;return;
    }
    try { await next(); }
    catch(Exception ex) when(ex is System.Text.Json.JsonException or InvalidDataException or FormatException or BadHttpRequestException){
        if(ctx.Response.HasStarted) throw;
        ctx.Response.StatusCode=ex is BadHttpRequestException bad?bad.StatusCode:400; await ctx.Response.WriteAsJsonAsync(new{code="INVALID_REQUEST"});
    }
    catch(NpgsqlException ex) when(ex.IsTransient && !ctx.Response.HasStarted){
        ctx.Response.StatusCode=503;ctx.Response.Headers.RetryAfter="5";await ctx.Response.WriteAsJsonAsync(new{code="STORAGE_UNAVAILABLE"});
    }
});
app.UseStaticFiles();
app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/",()=>Results.Content("""<html lang="zh"><meta charset="utf-8"><title>Codex Tracker</title><h1>Codex Tracker</h1><p><a href="/dashboard">用量统计</a> · <a href="/finance">项目财务</a> · <a href="/Identity/Account/Login">登录</a> · <a href="/Identity/Account/Register">注册</a> · <a href="/Identity/Account/Manage">账号资料</a> · <a href="/devices">管理设备</a></p></html>""","text/html; charset=utf-8"));
app.MapGet("/api/v1/capabilities",()=>new {protocol=1,cost_protocol=1,cost_ultrafast=true,issuer=issuer.AbsoluteUri,epoch=settings.Epoch,max_operations=200,max_bytes=524288});
DeviceAuth.Map(app);
SyncApi.Map(app);
Dashboard.Map(app);
CostFeed.Map(app);
app.Run();
