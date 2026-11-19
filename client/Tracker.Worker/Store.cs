using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker;

internal sealed class Store : IDisposable
{
    public const int ApplicationId = 0x43545831;
    private readonly SqliteConnection connection;
    private readonly Dictionary<string, SqliteCommand> commands = new();
    private SqliteTransaction? transaction;
    static Store() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        long app = Number("PRAGMA application_id"), version = Number("PRAGMA user_version");
        if (app != ApplicationId && (app != 0 || version != 0 || Number("SELECT count(*) FROM sqlite_master") != 0))
            throw new InvalidDataException("目标文件属于其他数据库。");
        if (version > 2) throw new InvalidDataException("数据库版本高于此客户端。");
        Run("PRAGMA foreign_keys=ON");
        Run("PRAGMA journal_mode=WAL");
        Run("PRAGMA synchronous=FULL");
        foreach (var migration in new[] {(1,"001-local.sql"),(2,"002-lineage.sql")})
        {
            if(version>=migration.Item1) continue;
            Atomic(()=>
            {
                using var reader=new StreamReader(typeof(Store).Assembly.GetManifestResourceStream(migration.Item2)!);
                Run(reader.ReadToEnd());
                Run($"PRAGMA application_id={ApplicationId}");
                Run($"PRAGMA user_version={migration.Item1}");
            });
        }
    }

    internal static string Key(params object?[] fields) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    public void Atomic(Action action)
    {
        using var tx = connection.BeginTransaction();
        transaction = tx;
        try { action(); tx.Commit(); }
        finally { transaction = null; }
    }

    private SqliteCommand Command(string sql, object?[] values)
    {
        if (!commands.TryGetValue(sql, out var cmd))
        {
            cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, DBNull.Value);
            commands.Add(sql, cmd);
        }
        cmd.Transaction = transaction;
        for (int i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        return cmd;
    }
    public int Run(string sql, params object?[] args) => Command(sql,args).ExecuteNonQuery();
    public long Number(string sql, params object?[] args) => Convert.ToInt64(Command(sql,args).ExecuteScalar(), CultureInfo.InvariantCulture);
    public List<object?[]> Rows(string sql, params object?[] args)
    {
        using var reader = Command(sql,args).ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i=0; i<row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    public void Dispose() { foreach (var cmd in commands.Values) cmd.Dispose(); connection.Dispose(); }
}
