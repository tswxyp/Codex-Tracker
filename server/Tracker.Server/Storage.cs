using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tracker.Server;

public sealed class Accounts(DbContextOptions<Accounts> options) : IdentityDbContext<IdentityUser>(options);
public sealed record Settings(Uri Issuer, Guid Epoch, string Connection);

internal sealed class Sql(NpgsqlConnection connection)
{
    NpgsqlCommand Command(string sql, object?[] values)
    {
        var command = new NpgsqlCommand(sql,connection);
        foreach (var v in values)
            command.Parameters.Add(v == null ? new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Unknown,Value=DBNull.Value }
                : new NpgsqlParameter { Value=v });
        return command;
    }
    public void Run(string sql, params object?[] values)
    {
        using var cmd = Command(sql,values);
        cmd.ExecuteNonQuery();
    }
    public T? One<T>(string sql, params object?[] values)
    {
        using var cmd=Command(sql,values);
        var value=cmd.ExecuteScalar();
        return value is null or DBNull ? default : (T)value;
    }
    public List<T> List<T>(string sql, Func<NpgsqlDataReader,T> read, params object?[] values)
    {
        using var cmd=Command(sql,values);
        using var rows=cmd.ExecuteReader();
        var result=new List<T>();
        while(rows.Read()) result.Add(read(rows));
        return result;
    }
}

internal static class Storage
{
    internal static async Task Migrate(IServiceProvider services)
    {
        using var scope=services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<Accounts>();
        await db.Database.OpenConnectionAsync();
        await using var tx=await db.Database.BeginTransactionAsync();
        var sql=new Sql((NpgsqlConnection)db.Database.GetDbConnection());
        sql.Run("SELECT pg_advisory_xact_lock(1283462001)");
        sql.Run("CREATE TABLE IF NOT EXISTS tracker_schema(version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
        var version=sql.One<int>("SELECT coalesce(max(version),0) FROM tracker_schema");
        if(version>1) throw new InvalidOperationException("Unsupported server schema.");
        if(version==0)
        {
            sql.Run(db.Database.GenerateCreateScript());
            using var stream=typeof(Storage).Assembly.GetManifestResourceStream("sync-v1.sql")!;
            using var reader=new StreamReader(stream);
            sql.Run(await reader.ReadToEndAsync());
            var journal=scope.ServiceProvider.GetRequiredService<Recovery>().Initialize();
            sql.Run("INSERT INTO tracker_instance VALUES(true,$1,$2)",scope.ServiceProvider.GetRequiredService<Settings>().Epoch,journal);
            sql.Run("INSERT INTO tracker_schema(version) VALUES(1)");
        }
        if (!sql.One<bool>("SELECT to_regclass('public.cost_schema') IS NOT NULL")){
            using var costStream=typeof(Storage).Assembly.GetManifestResourceStream("cost-v1.sql")!;
            using var costReader=new StreamReader(costStream);
            sql.Run(await costReader.ReadToEndAsync());
        }
        if(sql.One<int>("SELECT max(version) FROM cost_schema")!=1)throw new InvalidOperationException("Unsupported cost schema.");
        if (!sql.One<bool>("SELECT to_regclass('public.finance_schema') IS NOT NULL")){
            using var financeStream=typeof(Storage).Assembly.GetManifestResourceStream("finance-v1.sql")!;
            using var financeReader=new StreamReader(financeStream);
            sql.Run(await financeReader.ReadToEndAsync());
        }
        if(sql.One<int>("SELECT max(version) FROM finance_schema")!=1)throw new InvalidOperationException("Unsupported finance schema.");
        var apps=scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if(await apps.FindByClientIdAsync("tracker-windows")==null)
            await apps.CreateAsync(new OpenIddictApplicationDescriptor {
                ClientId="tracker-windows",ClientType=ClientTypes.Public,DisplayName="Codex Tracker Windows",
                Permissions={Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                    Permissions.GrantTypes.DeviceCode, Permissions.GrantTypes.RefreshToken, Permissions.Prefixes.Scope+"tracker"}
            });
        await tx.CommitAsync();
    }

    internal static T Tenant<T>(NpgsqlDataSource source,string account,Func<Sql,T> work)
    {
        using var connection=source.OpenConnection();
        using var tx=connection.BeginTransaction();
        var db=new Sql(connection);
        db.Run("SELECT set_config('tracker.account',$1,true),pg_advisory_xact_lock(hashtextextended($1,173))",account);
        db.Run("INSERT INTO account_stream(acct) VALUES($1) ON CONFLICT DO NOTHING",account);
        var answer=work(db);
        tx.Commit();
        return answer;
    }
}
