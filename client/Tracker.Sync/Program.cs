using System.Net;
using System.Text.Json;
using Tracker.Protocol;
using Tracker.Sync;

Console.OutputEncoding=System.Text.Encoding.UTF8;
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
var argv=args.ToList();
string? Option(string key){
    int i=argv.IndexOf(key);
    if(i<0)return null;
    if(i+1>=argv.Count)throw new ArgumentException("缺少参数 "+key);
    string v=argv[i+1];argv.RemoveRange(i,2);return v;
}
try{
    var folder=Option("--state")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexTracker","sync");
    string? customRoot=Option("--ca");
    string alias=Option("--profile")??"default";bool noBrowser=argv.Remove("--no-browser");
    bool rebind=argv.Remove("--rebind");
    if(argv.Count==0 || argv[0] is "--help" or "help"){
        Console.WriteLine("""
            Codex Tracker 多设备同步
              login HTTPS服务地址 设备别名 [--profile 本机配置名称] [--ca 私有CA.pem]
              threads 统计库                         查看可选的设备根、线程和会话
              allow 统计库 本地设备ID 线程ID            授权一个线程（默认范围为空）
              collect | sync | watch                  离线入队、一次同步或持续同步
              devices | status | show [资源类型]       查看设备、积压和云端统计缓存
              costs [--no-browser]                   打开本账号的成本核算页，或打印地址
              edit 操作类型 UUID 当前版本 [JSON]       项目、任务组和成员离线编辑
              logout | flush-logout                  退出当前账号、补传离线撤销凭据
            可用 --state 指定独立同步目录。项目操作示例请见 docs/phase-3-run.md。
            """);return 0;
    }
    if(argv is ["threads",var database]){Exporter.List(database);return 0;}
    if(argv is ["watch"]){
        int failures=0;
        while(true){
            var delay=TimeSpan.FromSeconds(60);
            try{
                using var local=new Sidecar(folder);
                var complete=Exporter.Collect(local);
                using var remote=Active(local);
                await remote.Sync(complete);failures=0;
            }catch(RemoteFailure e) when(e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest){throw;}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or RemoteFailure or IOException){
                failures=Math.Min(failures+1,9);
                delay=TimeSpan.FromSeconds(Random.Shared.NextDouble()*Math.Min(300,Math.Pow(2,failures))+1);
                if(e is RemoteFailure rf && rf.Retry>delay)delay=rf.Retry.Value;
                Console.Error.WriteLine($"离线或服务暂不可用，{delay.TotalSeconds:F0} 秒后重试。");
            }
            await Task.Delay(delay);
        }
    }
    using var l=new Sidecar(folder);
    switch(argv.ToArray()){
        case ["login",var issuer,var name]:
            await Remote.FlushPending(l);
            using(var r=new Remote(l,issuer,alias,customRoot)){
                await r.Login(name,alias,customRoot==null?null:Path.GetFullPath(customRoot),!noBrowser,rebind);
            }break;
        case ["flush-logout"]:await Remote.FlushPending(l);break;
        case ["allow",var path,var device,var thread]:Exporter.Allow(l,path,device,thread);Console.WriteLine("已授权线程。");break;
        case ["collect"]:if(!Exporter.Collect(l))return 2;Console.WriteLine("离线采集差分已提交。");break;
        case ["sync"]:
            bool complete=Exporter.Collect(l);using(var r=Active(l))complete=await r.Sync(complete);
            if(!complete)return 2;break;
        case ["devices"]:using(var r=Active(l))Console.WriteLine((await r.Devices()).GetRawText());break;
        case ["costs"]:
            var costOrigin=new Uri((string)l.Scalar("SELECT issuer FROM profile WHERE id=$p0",l.Active)!);
            if(costOrigin.Scheme!="https" || costOrigin.UserInfo.Length!=0 || costOrigin.PathAndQuery!="/")throw new InvalidDataException("服务地址无效。");
            var url=new Uri(costOrigin,"/costs").AbsoluteUri;
            Console.WriteLine(url);
            if(!noBrowser)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});
            break;
        case ["logout"]:using(var r=Active(l))await r.Logout();Console.WriteLine("已退出。");break;
        case ["status"]:
            Console.WriteLine(Wire.Pack(new{pending=l.Scalar("SELECT count(*) FROM outbox WHERE p=$p0",l.Active),
                cursor=l.Scalar("SELECT cursor FROM profile WHERE id=$p0",l.Active),
                recovery_review=l.Scalar("SELECT count(*) FROM journal WHERE p=$p0 AND status='recovery_review'",l.Active)}));break;
        case ["show",..] when argv.Count<=2:
            var type=argv.Count==2?argv[1]:null;
            foreach(var line in l.Rows("SELECT kind,id,version,body FROM replica WHERE p=$p0 AND body IS NOT NULL AND ($p1 IS NULL OR kind=$p1) ORDER BY kind,id",
                r=>Wire.Pack(new{kind=r.GetString(0),id=r.GetString(1),version=r.GetInt64(2),value=Wire.Read<JsonElement>(r.GetString(3))}),l.Active,type))Console.WriteLine(line);
            break;
        case ["scopes"]:
            foreach(var row in l.Rows("SELECT s.id,s.device,s.thread,r.reason FROM scope s LEFT JOIN source_review r ON r.scope=s.id WHERE s.p=$p0",
                r=>new{id=r.GetInt64(0),device=r.GetString(1),thread=r.GetString(2),review=r.IsDBNull(3)?null:r.GetString(3)},l.Active))Console.WriteLine(Wire.Pack(row));
            break;
        case ["resume-source",var scopeId]:
            l.Run("DELETE FROM source_review WHERE scope IN (SELECT id FROM scope WHERE p=$p0 AND id=$p1)",l.Active,long.Parse(scopeId));
            break;
        case ["review"]:
            foreach(var body in l.Rows("SELECT body FROM journal WHERE p=$p0 AND status='recovery_review'",r=>r.GetString(0),l.Active))Console.WriteLine(body);
            break;
        case ["review-dismiss",var oldId]:
            l.Run("UPDATE journal SET status='dismissed' WHERE p=$p0 AND op=$p1 AND status='recovery_review'",l.Active,Guid.Parse(oldId).ToString());break;
        case ["review-apply",var priorId,var expected]:
            l.Tx(()=>{
                if(l.Scalar("SELECT body FROM journal WHERE p=$p0 AND op=$p1 AND status='recovery_review'",l.Active,Guid.Parse(priorId).ToString()) is not string body)
                    throw new ArgumentException("找不到待复核编辑。");
                var revised=Wire.Read<Operation>(body) with {OpId=Guid.NewGuid(),BaseVersion=long.Parse(expected)};
                if(!Wire.Valid(revised))throw new ArgumentException("修订号无效。");
                l.Enqueue(l.Active,revised);l.Run("UPDATE journal SET status='resubmitted' WHERE p=$p0 AND op=$p1",l.Active,priorId);
                l.Run("INSERT INTO journal VALUES($p0,$p1,$p2,'pending')",l.Active,revised.OpId.ToString(),Wire.Pack(revised));
            });break;
        case ["edit",var kind,var id,var version,..] when argv.Count is 4 or 5:
            var op=new Operation(Guid.NewGuid(),kind,ResourceId:Guid.Parse(id),BaseVersion:long.Parse(version),
                Value:argv.Count==5?Wire.Read<Edit>(argv[4]):null);
            if(!Wire.Valid(op) || op.ObservationId!=null)throw new ArgumentException("项目或任务组操作无效。");
            l.Tx(()=>{l.Enqueue(l.Active,op);l.Run("INSERT INTO journal VALUES($p0,$p1,$p2,'pending')",l.Active,op.OpId.ToString(),Wire.Pack(op));});
            Console.WriteLine(op.OpId);break;
        default:throw new ArgumentException("未知命令，请运行 --help。");
    }
    return 0;
}catch(Exception ex) when(ex is not OutOfMemoryException){
    Console.Error.WriteLine(ex.GetType().Name+" · "+ex.Message);return 1;
}
static Remote Active(Sidecar l){
    var p=l.Active;
    return new Remote(l,(string)l.Scalar("SELECT issuer FROM profile WHERE id=$p0",p)!,l.Get("alias/"+p)!,l.Get("ca/"+p));
}
