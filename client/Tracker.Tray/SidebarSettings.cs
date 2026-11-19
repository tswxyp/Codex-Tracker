using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed record ProjectIncome(long Minor,string Currency)
{
    internal decimal NativeAmount=>Minor/(decimal)Money.Scale(Currency);
    internal MoneyAmount MoneyValue=>new(NativeAmount,Currency);
}

internal sealed record IncomeReceipt(string Id,string Scope,string Entry,ProjectIncome Amount,DateOnly? Date);

internal sealed class SidebarSettings(string directory,string root)
{
    readonly string path=Path.Combine(directory,"sidebar.sqlite");
    internal QuotaCalibrationCache Calibration {get;}=new();
    internal TurnSpeedHistory Speeds {get;}=new(directory,root);
    SqliteConnection OpenWrite()
    {
        var db=Data.Open(path,SqliteOpenMode.ReadWriteCreate);
        try{
            using var cmd=db.CreateCommand();
            cmd.CommandText="""
                CREATE TABLE IF NOT EXISTS preferences(id INTEGER PRIMARY KEY CHECK(id=1),language TEXT NOT NULL,currency TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS project_income(root TEXT COLLATE NOCASE NOT NULL,project TEXT NOT NULL,
                    minor INTEGER NOT NULL CHECK(minor>=0),currency TEXT NOT NULL CHECK(currency IN ('CNY','USD')),PRIMARY KEY(root,project));
                """;
            cmd.ExecuteNonQuery();return db;
        }catch{db.Dispose();throw;}
    }
    internal SubscriptionPlan? ReadPlan()
    {
        if(!File.Exists(path))return null;
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);using var cmd=db.CreateCommand();
        cmd.Parameters.AddWithValue("$root",root);
        cmd.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='subscription_plan'";
        if(cmd.ExecuteScalar()!=null){
            cmd.CommandText="SELECT currency,minor,estimate_days FROM subscription_plan WHERE root=$root";
            using var plan=cmd.ExecuteReader();
            if(plan.Read())return new(new(plan.GetInt64(1),plan.GetString(0)),plan.GetInt32(2));
        }
        cmd.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='subscription_reference'";
        if(cmd.ExecuteScalar()==null)return null;
        cmd.CommandText="SELECT * FROM subscription_reference WHERE root=$root";
        using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        ProjectIncome paid;
        if(r.FieldCount>=6 && !r.IsDBNull(r.GetOrdinal("original_currency")) && !r.IsDBNull(r.GetOrdinal("original_minor")))
            paid=new(r.GetInt64(r.GetOrdinal("original_minor")),r.GetString(r.GetOrdinal("original_currency")));
        else if(r.FieldCount>3 && !r.IsDBNull(r.GetOrdinal("usd_cents")))paid=new(r.GetInt64(r.GetOrdinal("usd_cents")),"USD");
        else paid=new(r.GetInt64(r.GetOrdinal("yuan_cents")),"CNY");
        return new(paid,r.GetInt32(r.GetOrdinal("days")));
    }
    internal bool FirstFeePrompt(){
        using var db=OpenWrite();using var cmd=db.CreateCommand();
        cmd.CommandText="CREATE TABLE IF NOT EXISTS fee_prompt(root TEXT COLLATE NOCASE PRIMARY KEY);";
        cmd.ExecuteNonQuery();cmd.CommandText="INSERT OR IGNORE INTO fee_prompt VALUES($root)";
        cmd.Parameters.AddWithValue("$root",root);
        return cmd.ExecuteNonQuery()==1;
    }
    internal void SavePlan(SubscriptionPlan plan)
    {
        using var db=OpenWrite();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS subscription_plan(root TEXT COLLATE NOCASE PRIMARY KEY,
                currency TEXT NOT NULL,minor INTEGER NOT NULL CHECK(minor>0),estimate_days INTEGER NOT NULL CHECK(estimate_days BETWEEN 28 AND 31));
            INSERT INTO subscription_plan(root,currency,minor,estimate_days) VALUES($root,$code,$minor,$days)
            ON CONFLICT(root) DO UPDATE SET currency=excluded.currency,minor=excluded.minor,estimate_days=excluded.estimate_days
            """;
        cmd.Parameters.AddWithValue("$root",root);
        cmd.Parameters.AddWithValue("$code",plan.Paid.Currency);
        cmd.Parameters.AddWithValue("$minor",plan.Paid.Minor);
        cmd.Parameters.AddWithValue("$days",plan.EstimateDays);cmd.ExecuteNonQuery();
        tx.Commit();
    }
    internal DisplayOptions ReadOptions()
    {
        if(!File.Exists(path))return new();
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT language,currency FROM preferences WHERE id=1";
        DisplayOptions options;
        using(var r=cmd.ExecuteReader())options=r.Read()?new(r.GetString(0),r.GetString(1)):new();
        if(!HasTable(cmd,"profit_palette"))return options;
        cmd.CommandText="SELECT palette FROM profit_palette WHERE id=1";
        return options with {ProfitColors=cmd.ExecuteScalar() as string??"auto"};
    }
    internal void SaveOptions(DisplayOptions options)
    {
        using var db=OpenWrite();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO preferences VALUES(1,$lang,$cur) ON CONFLICT(id) DO UPDATE SET language=excluded.language,currency=excluded.currency;
            CREATE TABLE IF NOT EXISTS profit_palette(id INTEGER PRIMARY KEY CHECK(id=1),palette TEXT NOT NULL);
            INSERT INTO profit_palette VALUES(1,$palette) ON CONFLICT(id) DO UPDATE SET palette=excluded.palette;
            """;
        cmd.Parameters.AddWithValue("$lang",options.Language);cmd.Parameters.AddWithValue("$cur",options.Currency);
        cmd.Parameters.AddWithValue("$palette",options.ProfitColors);cmd.ExecuteNonQuery();tx.Commit();
    }
    static bool HasTable(SqliteCommand cmd,string name){
        cmd.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        cmd.Parameters.AddWithValue("$table",name);
        bool found=cmd.ExecuteScalar()!=null;
        cmd.Parameters.RemoveAt("$table");
        return found;
    }

    internal List<IncomeReceipt> ReadReceipts((string Scope,string Entry)? target=null)
    {
        var items=new List<IncomeReceipt>();
        if(!File.Exists(path))return items;
        using var db=Data.Open(path,SqliteOpenMode.ReadOnly);
        using var tx=db.BeginTransaction(deferred:true);
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.Parameters.AddWithValue("$root",root);
        if(HasTable(cmd,"receipt"))
            cmd.CommandText="SELECT id,scope,entry,minor,currency,received_on FROM receipt WHERE root=$root";
        else if(HasTable(cmd,"local_income"))
            cmd.CommandText="SELECT 'opening',scope,entry,minor,currency,NULL FROM local_income WHERE root=$root";
        else if(HasTable(cmd,"project_income"))
            cmd.CommandText="SELECT 'opening','project' AS scope,project AS entry,minor,currency,NULL FROM project_income WHERE root=$root";
        else return items;
        if(target is {} key){
            cmd.CommandText="SELECT * FROM ("+cmd.CommandText+") WHERE scope=$scope AND entry=$entry";
            cmd.Parameters.AddWithValue("$scope",key.Scope);cmd.Parameters.AddWithValue("$entry",key.Entry);
        }
        using var r=cmd.ExecuteReader();
        while(r.Read())items.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),new(r.GetInt64(3),r.GetString(4)),
            r.IsDBNull(5)?null:DateOnly.ParseExact(r.GetString(5),"yyyy-MM-dd",CultureInfo.InvariantCulture)));
        return items;
    }

    internal void SaveReceipt(IncomeReceipt? previous,IncomeReceipt? next)
    {
        var item=previous??next??throw new ArgumentException();
        using var db=OpenWrite();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        if(!HasTable(cmd,"receipt")){
            bool hasLocal=HasTable(cmd,"local_income");
            cmd.CommandText="""
                CREATE TABLE receipt(root TEXT COLLATE NOCASE NOT NULL,scope TEXT NOT NULL CHECK(scope IN ('chat','project')),
                    entry TEXT NOT NULL,id TEXT NOT NULL,minor INTEGER NOT NULL CHECK(minor>=0),currency TEXT NOT NULL,
                    received_on TEXT,PRIMARY KEY(root,scope,entry,id));
                """+(hasLocal?
                    "INSERT INTO receipt SELECT root,scope,entry,'opening',minor,currency,NULL FROM local_income":
                    "INSERT INTO receipt SELECT root,'project',project,'opening',minor,currency,NULL FROM project_income");
            cmd.ExecuteNonQuery();
        }
        cmd.Parameters.AddWithValue("$root",root);cmd.Parameters.AddWithValue("$scope",item.Scope);
        cmd.Parameters.AddWithValue("$entry",item.Entry);cmd.Parameters.AddWithValue("$id",item.Id);
        static object Day(DateOnly? d)=>(object?)d?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??DBNull.Value;
        if(previous!=null){
            cmd.CommandText="""
                DELETE FROM receipt WHERE root=$root AND scope=$scope AND entry=$entry AND id=$id
                AND minor=$old AND currency=$oldCode AND received_on IS $oldDay
                """;
            cmd.Parameters.AddWithValue("$old",previous.Amount.Minor);cmd.Parameters.AddWithValue("$oldCode",previous.Amount.Currency);
            cmd.Parameters.AddWithValue("$oldDay",Day(previous.Date));
            if(cmd.ExecuteNonQuery()!=1)throw new IOException("Receipt has changed");
        }
        if(next!=null){
            cmd.CommandText="INSERT INTO receipt(root,scope,entry,id,minor,currency,received_on) VALUES($root,$scope,$entry,$id,$minor,$cur,$day)";
            cmd.Parameters.AddWithValue("$minor",next.Amount.Minor);cmd.Parameters.AddWithValue("$cur",next.Amount.Currency);
            cmd.Parameters.AddWithValue("$day",Day(next.Date));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
