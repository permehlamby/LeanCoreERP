using System.Web;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// Connection strings of the application and of platform administration (tenant provisioning, tests).
/// They come from the environment and never from the repository (db/README.md):
/// FINANCECORE_DB (login role core_app) and FINANCECORE_ADMIN_DB (the migration role).
/// Without them the local PGlite server of db/tools/serve-pglite.mjs on 127.0.0.1:5433 is assumed.
/// </summary>
public sealed record DbSettings(string AppConnection, string AdminConnection, bool IsLocalTestDouble)
{
    public const string AppVariable = "FINANCECORE_DB";
    public const string AdminVariable = "FINANCECORE_ADMIN_DB";
    private const string LocalDefault = "Host=127.0.0.1;Port=5433;Username=postgres;Database=postgres;Pooling=false;Timeout=10";

    public static DbSettings FromEnvironment()
    {
        string? app = Environment.GetEnvironmentVariable(AppVariable);
        string? admin = Environment.GetEnvironmentVariable(AdminVariable);
        if (string.IsNullOrWhiteSpace(app) && string.IsNullOrWhiteSpace(admin))
            return new DbSettings(LocalDefault, LocalDefault, true);
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(admin))
            throw new InvalidOperationException($"Set both {AppVariable} and {AdminVariable}, or neither");
        return new DbSettings(Normalise(app), Normalise(admin), false);
    }

    /// <summary>Accepts a postgresql:// URI (as Supabase shows it) or an Npgsql key-value string.</summary>
    public static string Normalise(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        string[] userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = HttpUtility.UrlDecode(userInfo[0]),
            Password = userInfo.Length > 1 ? HttpUtility.UrlDecode(userInfo[1]) : null,
            Database = uri.AbsolutePath.Trim('/') is { Length: > 0 } d ? d : "postgres",
            SslMode = SslMode.Require,
            Timeout = 15
        };
        return b.ConnectionString;
    }
}
