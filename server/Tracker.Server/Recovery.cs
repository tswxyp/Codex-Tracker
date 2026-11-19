using System.Security.Cryptography;
using System.Text;
using Npgsql;
namespace Tracker.Server;

// This small self-hosted journal must survive a PostgreSQL point-in-time restore.
// Fail closed if an interrupted append or a missing journal is detected.
internal sealed class Recovery(string directory)
{
    readonly object gate=new();
    string Log=>Path.Combine(directory,"revoked.log");
    string Head=>Path.Combine(directory,"revoked.sha256");
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void Durable(string path,byte[] data){
        using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        f.Write(data);f.Flush(true);
    }
    void Mark(byte[] bytes){
        string tmp=Path.Combine(directory,Guid.NewGuid()+".tmp");Durable(tmp,Encoding.ASCII.GetBytes(Hash(bytes)));
        File.Move(tmp,Head,true);
    }
    internal Guid Initialize(Guid? excluded = null)
    {
        Directory.CreateDirectory(directory);
        if(File.Exists(Log)){
            var existing=Encoding.UTF8.GetString(Load());
            if(existing.Length==37 && existing.EndsWith('\n') && Guid.TryParseExact(existing[..36],"D",out var saved) && saved!=excluded)
                return saved;
            throw new InvalidDataException("初始化需要新的安全目录或上次初始化留下的空日志。");
        }
        if(File.Exists(Head))throw new InvalidDataException("撤销日志缺失。");
        var id=Guid.NewGuid();
        var bytes=Encoding.UTF8.GetBytes(id+"\n");
        Durable(Log,bytes);Mark(bytes);
        return id;
    }
    byte[] Load()
    {
        var data=File.ReadAllBytes(Log);
        if(File.ReadAllText(Head)!=Hash(data))throw new InvalidDataException("设备撤销日志校验失败，暂停授权。");
        return data;
    }
    internal void Validate(Guid id){
        lock(gate)if(!Encoding.UTF8.GetString(Load()).StartsWith(id+"\n",StringComparison.Ordinal))
            throw new InvalidDataException("恢复控制记录与数据库不匹配。");
    }
    internal bool Revoked(string account,Guid device){
        lock(gate)return Encoding.UTF8.GetString(Load()).Contains("\n"+Guid.Parse(account)+"/"+device+"\n",StringComparison.Ordinal);
    }
    internal void Revoke(string account,Guid device){
        lock(gate){
            using var writer=new FileStream(Path.Combine(directory,"writer.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);
            var data=Load();var line=Guid.Parse(account)+"/"+device+"\n";
            if(Encoding.UTF8.GetString(data).Contains("\n"+line,StringComparison.Ordinal))return;
            using(var f=new FileStream(Log,FileMode.Append,FileAccess.Write,FileShare.Read)){f.Write(Encoding.UTF8.GetBytes(line));f.Flush(true);}
            Mark(File.ReadAllBytes(Log));
        }
    }
    internal static async Task Admin(IServiceProvider services,string[] args)
    {
        var cfg=services.GetRequiredService<Settings>();
        var recovery=services.GetRequiredService<Recovery>();
        await using var c=await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();var db=new Sql(c);
        db.Run("SELECT pg_advisory_xact_lock(1283462001)");
        var id=db.One<Guid>("SELECT journal_id FROM tracker_instance WHERE singleton");
        bool freeze=args.Contains("--recover-frozen");
        if(!freeze)recovery.Validate(id);
        if(args.Contains("--recover") || freeze){
            if(cfg.Epoch==db.One<Guid>("SELECT epoch FROM tracker_instance WHERE singleton"))
                throw new InvalidOperationException("恢复历史备份时需要新的 Tracker:Epoch。");
            if(freeze){
                id=recovery.Initialize(id);
                foreach(var user in db.List("SELECT \"Id\" FROM \"AspNetUsers\"",r=>r.GetString(0))){
                    db.Run("SELECT set_config('tracker.account',$1,true)",user);
                    db.Run("UPDATE device SET revoked=true WHERE acct=$1",user);
                }
            }
            db.Run("UPDATE tracker_instance SET epoch=$1,journal_id=$2",cfg.Epoch,id);
            db.Run("""
                UPDATE "AspNetUsers" SET "PasswordHash"=NULL,"SecurityStamp"=gen_random_uuid()::text,
                "EmailConfirmed"=false,"LockoutEnabled"=true,"LockoutEnd"=now()+interval '100 years'
                """);
            db.Run("UPDATE \"OpenIddictTokens\" SET \"Status\"='revoked'");
            db.Run("UPDATE \"OpenIddictAuthorizations\" SET \"Status\"='revoked'");
        }else{
            var i=Array.IndexOf(args,"--release-account");
            if(i<0 || i+2>=args.Length || !Guid.TryParse(args[i+1],out var uid))throw new ArgumentException("需要已核实的账号 UUID 与邮箱。");
            if(db.One<string>("""
                UPDATE "AspNetUsers" SET "EmailConfirmed"=true,"LockoutEnd"=NULL WHERE "Id"=$1 AND "NormalizedEmail"=$2
                AND "PasswordHash" IS NULL RETURNING "Id"
                """,uid.ToString(),args[i+2].ToUpperInvariant())==null)throw new InvalidOperationException("账号与核实记录不符。");
        }
        await tx.CommitAsync();
    }
}
