using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Tracker.Protocol;

namespace Tracker.Sync;

internal sealed class Remote : IDisposable
{
    readonly Sidecar local;
    readonly HttpClient http;
    readonly string origin;
    readonly string identity;
    string? access;
    internal Remote(Sidecar store,string address,string alias,string? trustRoot)
    {
        local=store;
        var uri=new Uri(address);
        if(uri.Scheme!="https" || uri.PathAndQuery!="/" || uri.UserInfo!="" || uri.Fragment!="")throw new ArgumentException("需要 HTTPS 服务根地址。");
        origin=uri.GetLeftPart(UriPartial.Authority);identity=Wire.Key("ct.install",origin,alias);
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false};
        if(trustRoot!=null){
            var root=X509Certificate2.CreateFromPem(File.ReadAllText(trustRoot));
            handler.ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>{
                if(cert==null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors)!=0)return false;
                using var chain=new X509Chain();
                chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(cert);
            };
        }
        http=new HttpClient(handler){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.Add("Tracker-Windows-Version",Environment.OSVersion.Version.ToString());
        http.DefaultRequestHeaders.Add("Tracker-Client-Version",typeof(Remote).Assembly.GetName().Version!.ToString(3));
    }
    static async Task<JsonElement> Read(HttpResponseMessage r)
    {
        using(r){
            if(!r.IsSuccessStatusCode){
                var retry=r.Headers.RetryAfter;
                var wait=retry?.Delta??(retry?.Date-DateTimeOffset.UtcNow);
                throw new RemoteFailure(r.StatusCode,wait);
            }
            if(r.StatusCode==HttpStatusCode.NoContent)return default;
            return await r.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
    sealed record Tokens(string Access,string Refresh);
    ECDsa BindingKey(){
        var ec=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if(local.Secret("key/"+identity) is string key)ec.ImportPkcs8PrivateKey(Convert.FromBase64String(key),out _);
        else local.SetSecret("key/"+identity,Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
        return ec;
    }
    string Sign(string value){
        using var ec=BindingKey();
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(value),HashAlgorithmName.SHA256));
    }
    async Task<HttpResponseMessage> Form(string path,Dictionary<string,string> args){
        args["client_id"]="tracker-windows";using var body=new FormUrlEncodedContent(args);
        return await http.PostAsync(path,body);
    }
    static Tokens TokensFrom(JsonElement response)=>new(response.GetProperty("access_token").GetString()!,response.GetProperty("refresh_token").GetString()!);
    void Use(Tokens t){access=t.Access;http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",access);}
    internal async Task Login(string name,string alias,string? ca,bool launch,bool rebind)
    {
        if(local.Get("active")!=null)throw new InvalidOperationException("先退出当前账号，再重新授权。");
        if(!Wire.Name(name))throw new ArgumentException("设备名称无效。");
        var caps=await Read(await http.GetAsync("/api/v1/capabilities"));
        if(caps.GetProperty("protocol").GetInt32()!=1 || caps.GetProperty("issuer").GetString()!.TrimEnd('/')!=origin)throw new InvalidDataException("服务标识或协议不匹配。");
        string id=local.Get("device/"+identity)??Guid.NewGuid().ToString();local.Set("device/"+identity,id);
        using var key=BindingKey();
        var challenge=await Read(await Form("/connect/deviceauthorization",new(){
            ["scope"]="tracker offline_access",["device_id"]=id,["device_name"]=name,
            ["public_key"]=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}));
        var verify=new Uri(challenge.GetProperty("verification_uri_complete").GetString()!);
        if(verify.GetLeftPart(UriPartial.Authority)!=origin)throw new InvalidDataException("批准地址与服务不同。");
        Console.WriteLine($"请在系统浏览器确认设备 {name}。短码 {challenge.GetProperty("user_code").GetString()}");
        Console.WriteLine(verify);
        if(launch)Process.Start(new ProcessStartInfo(verify.AbsoluteUri){UseShellExecute=true});
        var dc=challenge.GetProperty("device_code").GetString()!;
        int interval=challenge.TryGetProperty("interval",out var wait)?wait.GetInt32():5;
        var until=DateTimeOffset.UtcNow.AddSeconds(challenge.GetProperty("expires_in").GetInt32());
        while(DateTimeOffset.UtcNow<until){
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1,interval)));
            using var response=await Form("/connect/token",new(){["grant_type"]="urn:ietf:params:oauth:grant-type:device_code",["device_code"]=dc,["proof"]=Sign(dc)});
            if(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable){
                var waitTime=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow);
                if(waitTime>TimeSpan.Zero){
                    if(DateTimeOffset.UtcNow+waitTime.Value>=until)break;
                    await Task.Delay(waitTime.Value);
                }
                continue;
            }
            var body=await response.Content.ReadFromJsonAsync<JsonElement>();
            if(!response.IsSuccessStatusCode){
                var error=body.GetProperty("error").GetString();
                if(error=="slow_down"){interval+=5;continue;}if(error=="authorization_pending")continue;
                throw new InvalidOperationException("设备授权结束："+error);
            }
            var tokens=TokensFrom(body);Use(tokens);
            var state=await State();var profile=Wire.Key("ct.profile",origin,state.Account);
            if(state.Device.ToString()!=id || local.Get("owner/"+identity) is string owner && owner!=profile)
                throw new InvalidOperationException("此本机配置已归属其他账号，请使用新配置名称。");
            bool changed=local.Scalar("SELECT device FROM profile WHERE id=$p0",profile) is string previousDevice && previousDevice!=id;
            if(changed && !rebind)
                throw new InvalidOperationException("此账号已有本机来源身份。请使用原配置或另选独立状态目录。");
            local.Tx(()=>{
                local.Run("INSERT INTO profile(id,issuer,account,device,epoch) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT DO NOTHING",
                    profile,origin,state.Account,id,state.Epoch.ToString());
                local.Set("owner/"+identity,profile);local.Set("alias/"+profile,alias);local.Set("ca/"+profile,ca);
                local.Set("grant/"+profile,state.Grant.ToString());local.SetSecret("refresh/"+profile,tokens.Refresh);
                local.Set("active",profile);
                if(changed)local.Run("UPDATE profile SET device=$p0,epoch=$p1 WHERE id=$p2",id,Guid.Empty.ToString(),profile);
            });
            Console.WriteLine("已授权。使用 allow 明确选择要同步的线程。");return;
        }
        throw new TimeoutException("授权码已过期。");
    }
    async Task Refresh()
    {
        string p=local.Active;
        var refresh=local.Secret("refresh/"+p)??throw new InvalidOperationException("设备需要重新登录。");
        using var response=await Form("/connect/token",new(){["grant_type"]="refresh_token",["refresh_token"]=refresh,["proof"]=Sign(refresh)});
        if(response.StatusCode==HttpStatusCode.BadRequest && (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error",out var error)
            && error.GetString()=="invalid_grant"){
            local.SetSecret("refresh/"+p,null);
            throw new InvalidOperationException("设备授权已失效。待传记录已保存，请退出并重新授权。");
        }
        var t=TokensFrom(await Read(response));
        local.SetSecret("refresh/"+p,t.Refresh);Use(t);
    }
    async Task<State> State()=>(await Read(await http.GetAsync("/api/v1/sync/state"))).Deserialize<State>(Wire.Json)!;
    public async Task<JsonElement> Devices(){await Refresh();return await Read(await http.GetAsync("/api/v1/devices"));}
    internal async Task Logout()
    {
        var p=local.Active;var grant=local.Get("grant/"+p)!;
        var ticket=Wire.Pack(new {account=(string)local.Scalar("SELECT account FROM profile WHERE id=$p0",p)!,
            grant,proof=Sign("tracker-logout/"+origin+"/"+grant)});
        local.Tx(()=>{
            local.Set("revoke/"+identity,ticket);
            local.Set("revoke-target/"+identity,Wire.Pack(new[]{origin,local.Get("alias/"+p),local.Get("ca/"+p)}));
            local.SetSecret("refresh/"+p,null);local.Set("active",null);
        });
        await FlushLogout();
    }
    internal async Task FlushLogout()
    {
        if(local.Get("revoke/"+identity) is not string pending)return;
        using var body=new StringContent(pending,Encoding.UTF8,"application/json");
        await Read(await http.PostAsync("/api/v1/device-logout-ticket",body));
        local.Tx(()=>{local.Set("revoke/"+identity,null);local.Set("revoke-target/"+identity,null);});
    }
    internal static async Task FlushPending(Sidecar store)
    {
        foreach(var target in store.Rows("SELECT v FROM settings WHERE k LIKE 'revoke-target/%'",r=>Wire.Read<string?[]>(r.GetString(0)))){
            try{
                using var r=new Remote(store,target[0]!,target[1]!,target[2]);await r.FlushLogout();
            }catch(Exception e) when(e is HttpRequestException or RemoteFailure or TaskCanceledException){
                Console.Error.WriteLine("离线退出仍待服务端确认。");
            }
        }
    }

    internal async Task<bool> Sync(bool complete)
    {
        await FlushPending(local);
        await Refresh();
        var state=await State();var p=local.Active;
        var stored=local.Rows("SELECT account,device,epoch,cursor FROM profile WHERE id=$p0",
            r=>(Account:r.GetString(0),Device:r.GetString(1),Epoch:r.GetString(2),Cursor:r.GetInt64(3)),p).Single();
        if(state.Account!=stored.Account || state.Device.ToString()!=stored.Device)throw new InvalidDataException("账号或设备绑定不匹配。");
        long cursor=stored.Cursor;
        if(state.Epoch.ToString()!=stored.Epoch || cursor>state.Head)cursor=await Snapshot(p,state,true);
        await Read(await http.PostAsync("/api/v1/devices/self/heartbeat",null));
        while(true){
            var ops=local.Rows("SELECT body FROM outbox WHERE p=$p0 ORDER BY n LIMIT 100",r=>Wire.Read<Operation>(r.GetString(0)),p);
            if(ops.Count==0)break;
            var result=await Read(await http.PostAsJsonAsync("/api/v1/sync/push",new Push(1,state.Epoch,ops.ToArray()),Wire.Json));
            var receipts=result.GetProperty("results").Deserialize<Receipt[]>(Wire.Json)!;
            if(result.GetProperty("epoch").GetGuid()!=state.Epoch || !receipts.Select(r=>r.OpId).SequenceEqual(ops.Select(o=>o.OpId)))
                throw new InvalidDataException("持久回执与上传不匹配。");
            local.Tx(()=>{
                foreach(var r in receipts){
                    if(r.Status is not ("applied" or "duplicate" or "superseded" or "recorded_conflict"))throw new InvalidDataException("操作需核对："+r.Status);
                    local.Run("DELETE FROM outbox WHERE p=$p0 AND id=$p1",p,r.OpId.ToString());
                    local.Run("UPDATE journal SET status=$p0 WHERE p=$p1 AND op=$p2",r.Status,p,r.OpId.ToString());
                }
            });
            if(receipts.Any(r=>r.Status=="superseded"))cursor=await Snapshot(p,state,false,true);
        }
        long? until=null;
        while(true){
            var path=$"/api/v1/sync/changes?epoch={state.Epoch}&after={cursor}"+(until.HasValue?$"&until={until}":"");
            using var response=await http.GetAsync(path);
            if(response.StatusCode==HttpStatusCode.Gone){cursor=await Snapshot(p,state,false);until=null;continue;}
            var page=(await Read(response)).Deserialize<Page>(Wire.Json)!;
            if(page.Epoch!=state.Epoch || page.Next<cursor || (page.HasMore && page.Next==cursor)
                || page.Changes.Where((c,i)=>c.Seq!=cursor+i+1).Any() || page.Next!=cursor+page.Changes.Length)
                throw new InvalidDataException("变更游标不连续。");
            local.Tx(()=>{
                foreach(var c in page.Changes)foreach(var r in c.Resources)local.Apply(p,r);
                local.Run("UPDATE profile SET cursor=$p0 WHERE id=$p1",page.Next,p);
            });
            cursor=page.Next;until=page.Until;if(!page.HasMore)break;
        }
        complete=complete && Convert.ToInt64(local.Scalar("SELECT count(*) FROM source_review r JOIN scope s ON s.id=r.scope WHERE s.p=$p0",p))==0;
        if(complete)await Read(await http.PostAsync($"/api/v1/sync/complete?epoch={state.Epoch}&cursor={cursor}&until={until}",null));
        await RefreshCosts(p,state,complete);
        Console.WriteLine(complete?$"同步完成 · 游标 {cursor}":$"可用记录已同步 · 游标 {cursor}，部分来源待处理，上次完整同步时间已保留。");
        return complete;
    }
    async Task RefreshCosts(string p,State state,bool complete)
    {
        var caps=await Read(await http.GetAsync("/api/v1/capabilities"));
        if(!caps.TryGetProperty("cost_protocol",out var protocol) || protocol.GetInt32()!=1){
            local.Run("DELETE FROM cost_cache WHERE p=$p0",p);
            return;
        }
        string prefix=$"{state.Epoch}/{state.Device}/";
        bool supportsUltrafast=caps.TryGetProperty("cost_ultrafast",out var fastCap) && fastCap.ValueKind==JsonValueKind.True;
        while(true){
            var entries=local.Rows("""
                SELECT c.scope,c.slot,m.id,m.rev,c.mode FROM cost_speed c JOIN scope s ON s.id=c.scope
                JOIN manifest m ON (m.scope,m.slot)=(c.scope,c.slot)
                WHERE s.p=$p0 AND c.ack IS NOT ($p1 || m.rev || CASE WHEN c.mode='ultrafast' THEN $p2 ELSE '' END)
                AND NOT EXISTS(SELECT 1 FROM source_review WHERE scope=c.scope)
                ORDER BY c.scope,c.slot LIMIT 200
                """,r=>(Scope:r.GetInt64(0),Slot:r.GetString(1),Id:r.GetString(2),Rev:r.GetInt64(3),Speed:r.IsDBNull(4)?null:r.GetString(4)),p,prefix,supportsUltrafast?"/ultra":"/unknown");
            if(entries.Count==0)break;
            var payload=entries.Select(e=>new{e.Id,e.Rev,Speed=e.Speed=="ultrafast" && !supportsUltrafast?null:e.Speed});
            await Read(await http.PostAsJsonAsync("/api/v1/costs/speeds",payload,Wire.Json));
            local.Tx(()=>{foreach(var e in entries){
                string suffix=e.Speed=="ultrafast"?(supportsUltrafast?"/ultra":"/unknown"):"";
                local.Run("UPDATE cost_speed SET ack=$p0 WHERE scope=$p1 AND slot=$p2",prefix+e.Rev+suffix,e.Scope,e.Slot);
            }});
        }
        var sheet=await Read(await http.PostAsync("/api/v1/costs/current?complete="+(complete?"true":"false"),null));
        local.Tx(()=>{
            local.Run("DELETE FROM cost_cache WHERE p=$p0",p);
            if(sheet.ValueKind!=JsonValueKind.Object)return;
            if(sheet.GetProperty("account").GetString()!=state.Account || sheet.GetProperty("epoch").GetGuid()!=state.Epoch)
                throw new InvalidDataException("费用核算账号或代际不匹配。");
            foreach(var line in sheet.GetProperty("lines").EnumerateArray())
                local.Run("""
                    INSERT INTO cost_cache SELECT $p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10
                    WHERE EXISTS(SELECT 1 FROM scope WHERE p=$p0 AND thread=$p1)
                    """,p,line.GetProperty("thread").GetString(),state.Epoch.ToString(),sheet.GetProperty("from_ms").GetInt64(),
                    sheet.GetProperty("until_ms").GetInt64(),sheet.GetProperty("through_ms").GetInt64(),sheet.GetProperty("generated_ms").GetInt64(),
                    sheet.GetProperty("currency").GetString(),sheet.GetProperty("api_currency").GetString(),sheet.GetProperty("basis").GetString(),line.GetRawText());
        });
    }
    async Task<long> Snapshot(string p,State state,bool recover,bool repairSources=false)
    {
        var manifest=await Read(await http.PostAsync("/api/v1/sync/snapshots",null));
        if(manifest.GetProperty("epoch").GetGuid()!=state.Epoch)throw new InvalidDataException("快照代际不匹配。");
        var id=manifest.GetProperty("id").GetGuid();long watermark=manifest.GetProperty("watermark").GetInt64();
        local.Run("CREATE TEMP TABLE IF NOT EXISTS incoming(item TEXT NOT NULL); DELETE FROM incoming");
        int page=0;
        foreach(var hash in manifest.GetProperty("hashes").EnumerateArray()){
            var bytes=await http.GetByteArrayAsync($"/api/v1/sync/snapshots/{id}/pages/{page++}");
            if(!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(hash.GetString(),StringComparison.Ordinal))throw new InvalidDataException("快照摘要不符。");
            var items=JsonSerializer.Deserialize<JsonElement[]>(bytes)!;
            local.Tx(()=>{foreach(var item in items)local.Run("INSERT INTO incoming VALUES($p0)",item.GetRawText());});
        }
        local.Tx(()=>{
            local.Run("DELETE FROM replica WHERE p=$p0",p);
            local.Run("""
                INSERT INTO replica(p,kind,id,version,body)
                SELECT $p0,json_extract(item,'$.resource.kind'),json_extract(item,'$.resource.id'),json_extract(item,'$.resource.version'),
                    json_extract(item,'$.resource.value') FROM incoming WHERE json_type(item,'$.resource')='object'
                """,p);
            if(recover || repairSources){
                var remote=local.Rows("SELECT item FROM incoming WHERE json_type(item,'$.observation_id')='text'",
                    r=>Wire.Read<JsonElement>(r.GetString(0))).ToDictionary(i=>i.GetProperty("observation_id").GetGuid());
                local.Run("DELETE FROM outbox WHERE p=$p0 AND ($p1=1 OR json_extract(body,'$.observation_id') IS NOT NULL)",p,recover?1:0);
                if(recover)local.Run("UPDATE journal SET status='recovery_review' WHERE p=$p0 AND status NOT IN ('dismissed','resubmitted')",p);
                var own=local.Rows("SELECT m.id,m.rev,m.fact,m.scope FROM manifest m JOIN scope s ON s.id=m.scope WHERE s.p=$p0",
                    r=>(Id:Guid.Parse(r.GetString(0)),Rev:r.GetInt64(1),Fact:r.IsDBNull(2)?null:Wire.Read<Fact>(r.GetString(2)),Scope:r.GetInt64(3)),p);
                foreach(var o in own){
                    if(remote.TryGetValue(o.Id,out var saved)){
                        var rev=saved.GetProperty("source_rev").GetInt64();
                        var cloud=saved.TryGetProperty("fact",out var value)?value.Deserialize<Fact>(Wire.Json):null;
                        if(rev>o.Rev){
                            local.Run("UPDATE manifest SET rev=$p0,fact=$p1 WHERE id=$p2",rev,cloud==null?null:Wire.Pack(cloud),o.Id.ToString());
                            if(cloud!=o.Fact)local.Run("INSERT OR IGNORE INTO source_review VALUES($p0,'CLOUD_NEWER')",o.Scope);
                            continue;
                        }
                        if(rev==o.Rev && cloud==o.Fact)continue;
                    }
                    local.Enqueue(p,new(Guid.NewGuid(),o.Fact==null?"observation.retract":"observation.upsert",o.Id,o.Rev,o.Fact));
                }
            }
            local.Run("UPDATE profile SET epoch=$p0,cursor=$p1 WHERE id=$p2",state.Epoch.ToString(),watermark,p);
        });
        return watermark;
    }
    public void Dispose()=>http.Dispose();
}
internal sealed class RemoteFailure(HttpStatusCode status,TimeSpan? retry):Exception($"服务返回 HTTP {(int)status}")
{public HttpStatusCode Status=>status;public TimeSpan? Retry=>retry;}
