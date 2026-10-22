using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// companies:check-readiness and companies:activate (07, 11.3). A company in DRAFT accepts configuration but no postings.
/// The checks that the schema can answer today are done. The checks of the other rows of 11.3 (roles, account determination,
/// VAT registration, migration roles, retention, e-invoicing, consolidation) need tables that do not exist yet. They are
/// returned as the warning LIMIT.READINESS_CHECKS_NOT_IMPLEMENTED, never as passed.
/// </summary>
public sealed class CompanyActivator(NpgsqlDataSource source)
{
    public const string ActivateCommandName = "companies:activate";
    public const string ActivatePermission = "company.activate";
    public const string ReadPermission = "read.config";

    /// <summary>The rows of 11.3 that are not checked yet.</summary>
    public static readonly string[] NotChecked =
    [
        "ORDER_SETTING_VERSION", "ROLES", "ROLE_ACCOUNT_RULES", "ACCOUNT_DETERMINATION", "VAT_REGISTRATION", "MIGRATION_ROLES",
        "GR_IR_WRITE_OFF", "RETENTION_RULES", "TAX_DETERMINATION", "DEDUCTION_KEY_RATES", "EINVOICE_RULE_SET", "CONSOLIDATION"
    ];

    public async Task<CommandResult<ReadinessReport>> CheckReadinessAsync(
        Guid tenantId, CommandContext ctx, Guid companyId, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ReadPermission))
            return CommandResult<ReadinessReport>.Rejected([Permission(ReadPermission)]);

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);
        ReadinessReport? report = await BuildReportAsync(tx, tenantId, companyId, ct);
        if (report is null) return CommandResult<ReadinessReport>.Rejected([new CommandError("COMPANY.NOT_FOUND", "P2", "company_id")]);
        return ToResult(report, Guid.Empty);
    }

    public async Task<CommandResult<ActivatedCompany>> ActivateAsync(
        Guid tenantId, CommandContext ctx, Guid companyId, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(ActivatePermission))
            return CommandResult<ActivatedCompany>.Rejected([Permission(ActivatePermission)]);
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100 ||
            string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100 ||
            string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100 || companyId == Guid.Empty)
            return CommandResult<ActivatedCompany>.Rejected([new CommandError("SCHEMA.INVALID_VALUE", "P1", "",
                new Dictionary<string, object?> { ["reason"] = "idempotency_key, correlation_id, actor and company_id are required, at most 100 characters" })]);

        byte[] fingerprint = CommandFingerprint.ForActivateCompany(companyId);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ActivateOnceAsync(tenantId, ctx, companyId, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<ActivatedCompany>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<ActivatedCompany>> ActivateOnceAsync(
        Guid tenantId, CommandContext ctx, Guid companyId, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Config, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return CommandResult<ActivatedCompany>.Rejected([new CommandError("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key")]);
            return CommandResult<ActivatedCompany>.Ok(new ActivatedCompany(companyId, "ACTIVE", []), earlier.CommandLogId, replay: true);
        }

        // The company row is locked first, so two activations of the same company are serialised.
        string? status;
        await using (var cmd = tx.Command("select status from core.company where tenant_id = @t and company_id = @c for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            status = await cmd.ExecuteScalarAsync(ct) as string;
        }
        if (status is null) return CommandResult<ActivatedCompany>.Rejected([new CommandError("COMPANY.NOT_FOUND", "P2", "company_id")]);
        if (status != "DRAFT")
            return CommandResult<ActivatedCompany>.Rejected([new CommandError("COMPANY.NOT_DRAFT", "P2", "company_id",
                new Dictionary<string, object?> { ["status"] = status })]);

        ReadinessReport report = (await BuildReportAsync(tx, tenantId, companyId, ct))!;
        if (!report.Ready)
            return CommandResult<ActivatedCompany>.Rejected(FailedChecks(report));

        // The chains of the company start here if they do not exist yet (genesis, sequence 0)
        var created = new List<string>();
        var ledgers = new List<Guid>();
        await using (var cmd = tx.Command("select ledger_id from core.ledger where tenant_id = @t and company_id = @c and active order by ledger_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ledgers.Add(r.GetGuid(0));
        }
        foreach (Guid ledger in ledgers)
            if (await CommandSupport.EnsureHeadAsync(tx, tenantId, companyId, "LEDGER", ledger.ToString("D"),
                    LedgerHash2.Genesis(tenantId, companyId, "LEDGER", ledger.ToString("D")), ct))
                created.Add($"LEDGER/{ledger:D}");
        if (await CommandSupport.EnsureHeadAsync(tx, tenantId, companyId, PeriodEventHash2.ChainType, "",
                LedgerHash2.Genesis(tenantId, companyId, PeriodEventHash2.ChainType, ""), ct))
            created.Add(PeriodEventHash2.ChainType);

        await using (var cmd = tx.Command(
            "update core.company set status = 'ACTIVE', row_version = row_version + 1 where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, ActivateCommandName, fingerprint, "COMPANY", companyId, ct);
        DateTime now = DateTime.UtcNow;
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "company.activated", "COMPANY", companyId.ToString("D"),
            "STATUS", new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: false, null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["entity_type"] = "COMPANY", ["valid_from"] = DateOnly.FromDateTime(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }), ct);

        await tx.CommitAsync(ct);
        return CommandResult<ActivatedCompany>.Ok(new ActivatedCompany(companyId, "ACTIVE", created), logId, WarningsOf(report));
    }

    // ---------------------------------------------------------------------------------------------------------
    private static async Task<ReadinessReport?> BuildReportAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        string status, local; string? reporting;
        await using (var cmd = tx.Command(
            "select status, local_currency, reporting_currency from core.company where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            status = r.GetString(0); local = r.GetString(1); reporting = r.IsDBNull(2) ? null : r.GetString(2);
        }

        async Task<T?> Scalar<T>(string sql, params (string, object)[] extra)
        {
            await using var cmd = tx.Command(sql);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            foreach (var (n, v) in extra) cmd.Parameters.AddWithValue(n, v);
            object? value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)value;
        }

        string? group = await Scalar<string>(
            "select g.group_currency from core.group_membership m join core.company_group g on g.tenant_id = m.tenant_id and g.group_id = m.group_id " +
            "where m.tenant_id = @t and m.company_id = @c and m.valid_from <= current_date and (m.valid_to is null or m.valid_to > current_date)");

        var checks = new List<ReadinessCheck>();

        var needed = new[] { local, reporting, group }.Where(c => c is not null).Distinct().ToArray();
        var missing = new List<string>();
        foreach (string? c in needed)
            if (await Scalar<int?>("select 1 from core.tenant_currency where tenant_id = @t and currency_code = @cur and active", ("cur", c!)) is null)
                missing.Add(c!);
        checks.Add(new("CURRENCIES_ENABLED", missing.Count == 0, missing.Count == 0 ? null : "not enabled: " + string.Join(", ", missing)));

        checks.Add(new("GROUP_MEMBERSHIP", group is not null, group is null ? "no group membership valid today" : null));
        checks.Add(new("LEDGER", await Scalar<int?>(
            "select 1 from core.ledger where tenant_id = @t and company_id = @c and is_default and active") is not null, null));
        checks.Add(new("FISCAL_YEAR_OPEN", await Scalar<int?>(
            "select 1 from core.fiscal_year where tenant_id = @t and company_id = @c and status = 'OPEN' limit 1") is not null, null));
        checks.Add(new("COMPANY_SETTING_VERSION", await Scalar<int?>(
            "select 1 from core.company_setting_version where tenant_id = @t and company_id = @c and effective_from <= now() limit 1") is not null, null));
        // 11.3 says "for each document type in use". Which types are in use is not known yet, so the slice requires
        // the general journal (finding F-IMPL-12).
        checks.Add(new("SERIES_ASSIGNED_JOURNAL", await Scalar<int?>(
            "select 1 from core.number_series_assignment where tenant_id = @t and company_id = @c and document_type = 'JOURNAL' limit 1") is not null, null));

        return new ReadinessReport(companyId, status, checks.All(c => c.Passed), checks);
    }

    private static List<CommandError> FailedChecks(ReadinessReport report) =>
        report.Checks.Where(c => !c.Passed).Select(c => new CommandError("CONFIG.COMPANY_NOT_READY", "P4", "company_id",
            new Dictionary<string, object?> { ["check"] = c.Check, ["detail"] = c.Detail })).ToList();

    private static List<CommandError> WarningsOf(ReadinessReport report) =>
    [
        new CommandError("LIMIT.READINESS_CHECKS_NOT_IMPLEMENTED", "P4", "company_id",
            new Dictionary<string, object?> { ["checks"] = NotChecked }, false, "WARNING")
    ];

    private static CommandResult<ReadinessReport> ToResult(ReadinessReport report, Guid logId) =>
        report.Ready
            ? CommandResult<ReadinessReport>.Ok(report, logId, WarningsOf(report))
            : CommandResult<ReadinessReport>.Rejected(FailedChecks(report)) with { Value = report };

    private static CommandError Permission(string permission) =>
        new("AUTH.PERMISSION_DENIED", "P0", "", new Dictionary<string, object?> { ["permission"] = permission });
}
