using System.Globalization;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>The command roles of 20, section 3. Names are fixed, never built from input.</summary>
public enum CommandRole { Read, Config, Period, Post }

/// <summary>
/// One database transaction of one command (ADR-209, ADR-210): it names its command role and its tenant first,
/// sets the lock timeout, and only then runs the command's statements. Row-level security, the grants of the role
/// and the triggers of db/migrations/0006 do the rest. Commit or dispose: a transaction that is not committed rolls back.
/// </summary>
public sealed class TenantTransaction : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public Guid TenantId { get; }
    public CommandRole Role { get; }

    private TenantTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, CommandRole role)
    {
        _connection = connection;
        _transaction = transaction;
        TenantId = tenantId;
        Role = role;
    }

    public static string RoleName(CommandRole role) => role switch
    {
        CommandRole.Read => "cmd_read",
        CommandRole.Config => "cmd_config",
        CommandRole.Period => "cmd_period",
        CommandRole.Post => "cmd_post",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    /// <summary>Opens a connection and a transaction, names the role and the tenant, sets the lock timeout.</summary>
    public static async Task<TenantTransaction> BeginAsync(
        NpgsqlDataSource source, Guid tenantId, CommandRole role, int lockTimeoutMs = 5000, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required", nameof(tenantId));
        NpgsqlConnection connection = await source.OpenConnectionAsync(ct);
        try
        {
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
            // The role name is from the enum above, so it cannot carry anything else. SET LOCAL ends with the transaction.
            await using (var cmd = new NpgsqlCommand($"set local role {RoleName(role)}", connection, transaction))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                "select set_config('app.tenant_id', @tenant, true), set_config('lock_timeout', @lock, true)", connection, transaction))
            {
                cmd.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
                cmd.Parameters.AddWithValue("lock", lockTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return new TenantTransaction(connection, transaction, tenantId, role);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public NpgsqlCommand Command(string sql)
    {
        var cmd = new NpgsqlCommand(sql, _connection, _transaction);
        return cmd;
    }

    public Task CommitAsync(CancellationToken ct = default) => _transaction.CommitAsync(ct);

    public Task RollbackAsync(CancellationToken ct = default) => _transaction.RollbackAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();   // rolls back when not committed
        await _connection.DisposeAsync();
    }
}
