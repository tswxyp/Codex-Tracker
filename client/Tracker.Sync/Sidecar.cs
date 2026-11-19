using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Tracker.Protocol;

namespace Tracker.Sync;

internal sealed class Sidecar : IDisposable
{
    internal readonly SqliteConnection Db;
    readonly FileStream processLock;
    internal Sidecar(string folder)
    {
        Directory.CreateDirectory(folder);
        processLock=new FileStream(Path.Combine(folder,"sync.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        Db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(folder,"sync.sqlite"),Pooling=false}.ToString());
        Db.Open();
        var version=Convert.ToInt64(Scalar("PRAGMA user_version"));
        long app=Convert.ToInt64(Scalar("PRAGMA application_id"));
        if(version>3 || (app!=0 && app!=0x43545333) || (version==0 && Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_master"))!=0))
            throw new InvalidDataException("Unsupported sync database.");
        Run("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON");
        if(version==0)Tx(()=>Run("""
            CREATE TABLE settings(k TEXT PRIMARY KEY,v TEXT NOT NULL);
            CREATE TABLE profile(id TEXT PRIMARY KEY,issuer TEXT NOT NULL,account TEXT NOT NULL,device TEXT NOT NULL,
                epoch TEXT NOT NULL,cursor INTEGER NOT NULL DEFAULT 0,UNIQUE(issuer,account));
            CREATE TABLE scope(id INTEGER PRIMARY KEY,p TEXT NOT NULL REFERENCES profile,db TEXT NOT NULL COLLATE NOCASE,
                device TEXT NOT NULL,thread TEXT NOT NULL,UNIQUE(db,device,thread));
            CREATE TABLE seen(scope INTEGER NOT NULL REFERENCES scope,event TEXT NOT NULL,PRIMARY KEY(scope,event));
            CREATE TABLE manifest(scope INTEGER NOT NULL REFERENCES scope,slot TEXT NOT NULL,id TEXT NOT NULL UNIQUE,
                rev INTEGER NOT NULL,fact TEXT,PRIMARY KEY(scope,slot));
            CREATE TABLE outbox(n INTEGER PRIMARY KEY,p TEXT NOT NULL REFERENCES profile,id TEXT NOT NULL UNIQUE,body TEXT NOT NULL);
            CREATE TABLE replica(p TEXT NOT NULL REFERENCES profile,kind TEXT NOT NULL,id TEXT NOT NULL,version INTEGER NOT NULL,
                body TEXT,PRIMARY KEY(p,kind,id));
            CREATE TABLE journal(p TEXT NOT NULL REFERENCES profile,op TEXT NOT NULL PRIMARY KEY,body TEXT NOT NULL,status TEXT NOT NULL);
            CREATE INDEX pending ON outbox(p,n);
            PRAGMA user_version=1;
            """));
        if(version<=1)Tx(()=>Run("""
            CREATE TABLE source_identity(scope INTEGER PRIMARY KEY REFERENCES scope, file_key TEXT NOT NULL);
            PRAGMA application_id=0x43545333;
            PRAGMA user_version=2;
            """));
        if(version<=2)Tx(()=>Run("""
            CREATE TABLE source_review(scope INTEGER PRIMARY KEY REFERENCES scope,reason TEXT NOT NULL);
            PRAGMA user_version=3;
            """));
        Run("""
            CREATE TABLE IF NOT EXISTS cost_speed(scope INTEGER NOT NULL REFERENCES scope,slot TEXT NOT NULL,
                mode TEXT,ack TEXT,PRIMARY KEY(scope,slot));
            CREATE TABLE IF NOT EXISTS cost_cache(p TEXT NOT NULL REFERENCES profile,thread TEXT NOT NULL,epoch TEXT NOT NULL,
                start_ms INTEGER NOT NULL,end_ms INTEGER NOT NULL,through_ms INTEGER NOT NULL,fetched_ms INTEGER NOT NULL,
                currency TEXT NOT NULL,api_currency TEXT NOT NULL,basis TEXT NOT NULL,line TEXT NOT NULL,PRIMARY KEY(p,thread));
            """);
    }
    internal object? Scalar(string sql,params object?[] v){using var c=Cmd(sql,v);var x=c.ExecuteScalar();return x is DBNull?null:x;}
    internal void Run(string sql,params object?[] v){using var c=Cmd(sql,v);c.ExecuteNonQuery();}
    internal List<T> Rows<T>(string sql,Func<SqliteDataReader,T> read,params object?[] v){
        using var c=Cmd(sql,v);using var r=c.ExecuteReader();var rows=new List<T>();while(r.Read())rows.Add(read(r));return rows;
    }
    SqliteCommand Cmd(string sql,object?[] v){
        var c=Db.CreateCommand();c.CommandText=sql;
        for(int i=0;i<v.Length;i++)c.Parameters.AddWithValue("$p"+i,v[i]??DBNull.Value);
        return c;
    }
    internal void Tx(Action fn){
        Run("BEGIN IMMEDIATE");
        try{fn();Run("COMMIT");}catch{Run("ROLLBACK");throw;}
    }
    internal string? Get(string k)=>Scalar("SELECT v FROM settings WHERE k=$p0",k) as string;
    internal void Set(string k,string? v){
        if(v==null)Run("DELETE FROM settings WHERE k=$p0",k);
        else Run("INSERT INTO settings VALUES($p0,$p1) ON CONFLICT(k) DO UPDATE SET v=excluded.v",k,v);
    }
    internal string? Secret(string k)=>Get(k) is string v?Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v),
        Encoding.UTF8.GetBytes(k),DataProtectionScope.CurrentUser)):null;
    internal void SetSecret(string k,string? value)=>Set(k,value==null?null:Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value),Encoding.UTF8.GetBytes(k),DataProtectionScope.CurrentUser)));
    internal string Active=>Get("active")??throw new InvalidOperationException("请先登录或选择已授权账号。");
    internal void Enqueue(string p,Operation o)=>Run("INSERT INTO outbox(p,id,body) VALUES($p0,$p1,$p2)",p,o.OpId.ToString(),Wire.Pack(o));
    internal void Apply(string p,Resource r)=>Run("""
        INSERT INTO replica VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(p,kind,id) DO UPDATE SET
        version=excluded.version,body=excluded.body WHERE excluded.version>replica.version
        """,p,r.Kind,r.Id,r.Version,r.Deleted?null:r.Value?.GetRawText());
    public void Dispose(){Db.Dispose();processLock.Dispose();}
}
