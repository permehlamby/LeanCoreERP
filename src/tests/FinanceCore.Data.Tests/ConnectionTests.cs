using FinanceCore.Data;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>Proves that the application path works against the database: role, tenant and RLS.</summary>
[Collection("db")]
public class ConnectionTests
{
    private static readonly Guid DevTenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task CommandRoleAndTenantAreSetForTheTransaction()
    {
        var settings = DbSettings.FromEnvironment();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(settings.AppConnection);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, DevTenant, CommandRole.Read);

        await using var cmd = tx.Command("select current_user::text, core.current_tenant_id()::text, (select count(*) from core.gl_account)");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("cmd_read", reader.GetString(0));
        Assert.Equal(DevTenant.ToString("D"), reader.GetString(1));
        Assert.Equal(4L, reader.GetInt64(2));   // the four accounts of the dev seed
    }

    [Fact]
    public async Task AnotherTenantSeesNoRows()
    {
        var settings = DbSettings.FromEnvironment();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(settings.AppConnection);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, Guid.NewGuid(), CommandRole.Read);
        await using var cmd = tx.Command("select count(*) from core.gl_account");
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReadRoleCannotWrite()
    {
        var settings = DbSettings.FromEnvironment();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(settings.AppConnection);
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, DevTenant, CommandRole.Read);
        await using var cmd = tx.Command("update core.gl_account set name = 'x'");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);   // insufficient_privilege
    }

    [Fact]
    public void UriConnectionStringsAreNormalised()
    {
        string s = DbSettings.Normalise("postgresql://core_app.proj:p%40ss@aws-0-eu.pooler.supabase.com:5432/postgres");
        var b = new NpgsqlConnectionStringBuilder(s);
        Assert.Equal("aws-0-eu.pooler.supabase.com", b.Host);
        Assert.Equal("core_app.proj", b.Username);
        Assert.Equal("p@ss", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
    }
}
