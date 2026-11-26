using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Reads;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// The two jobs of the read access log (09, 5.2, Q6), run by the retention role. SummariseAsync writes one access.summary event per day, company and field group for the full UTC days that have
/// not been summarised (counts only: reads, distinct actors, distinct partners, an export job counts its partner_count), takes the outbox positions like a command and moves the state forward in
/// the same transaction, so a day is summarised once. PurgeAsync deletes the rows older than read_log_retention_days (Q6), or, when the setting is null, older than the longest retention rule of
/// the tenant (the rules of the class READ_LOG, else of any class). It deletes only days that have been summarised, and nothing without a rule. A read still writes no event (A6).
/// </summary>
public sealed class ReadLogJobs(NpgsqlDataSource source)
{
    private static CommandContext Context(string correlation) => new(correlation, correlation, ReadLogJobRules.Actor, new HashSet<string>(), "JOB");

    private static DateTime Utc(DateOnly d) => new(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<DateOnly?> ReadStateAsync(TenantTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select summarised_through from core.read_log_summary_state where tenant_id = @t");
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct) is DateTime d ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>Summarises the full UTC days before `today` that are not summarised yet.</summary>
    public async Task<ReadLogSummaryResult> SummariseAsync(Guid tenantId, DateOnly today, CancellationToken ct = default)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Retention, ct: ct);
        DateOnly? through = await ReadStateAsync(tx, tenantId, ct);
        DateOnly last = today.AddDays(-1);
        DateOnly? first = through?.AddDays(1);
        if (first is null)
        {
            await using var cmd = tx.Command("select (min(read_at) at time zone 'UTC')::date from core.read_access_log where tenant_id = @t");
            cmd.Parameters.AddWithValue("t", tenantId);
            if (await cmd.ExecuteScalarAsync(ct) is DateTime d) first = DateOnly.FromDateTime(d);
        }
        if (first is null || first > last) return new ReadLogSummaryResult(0, 0, through);

        var groups = new List<(DateOnly Day, Guid? Company, string FieldGroup, long Reads, long Actors, long Partners)>();
        await using (var cmd = tx.Command(
            "select (read_at at time zone 'UTC')::date, company_id, field_group, count(*), count(distinct actor), count(distinct partner_id) + coalesce(sum(partner_count), 0) " +
            "from core.read_access_log where tenant_id = @t and read_at >= @from and read_at < @to group by 1, 2, 3 order by 1, 3, 2 nulls first"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = Utc(first.Value) });
            cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = Utc(last.AddDays(1)) });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                groups.Add((DateOnly.FromDateTime(r.GetDateTime(0)), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        }
        CommandContext ctx = Context("read-log-summary-" + last.ToString("yyyy-MM-dd"));
        DateTime now = DateTime.UtcNow;
        DateTime at = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        foreach (var g in groups)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, g.Company, ctx, ReadLogJobRules.SummaryEvent, "ACCESS", $"{g.Day:yyyy-MM-dd}|{g.FieldGroup}|{g.Company?.ToString("D") ?? "-"}", "SUMMARY", at, isAudit: true, null,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["day"] = g.Day.ToString("yyyy-MM-dd"), ["company_id"] = g.Company, ["field_group"] = g.FieldGroup, ["read_count"] = g.Reads, ["actor_count"] = g.Actors, ["partner_count"] = g.Partners
                }), ct);
        await using (var cmd = tx.Command(
            "insert into core.read_log_summary_state (tenant_id, summarised_through) values (@t, @d) on conflict (tenant_id) do update set summarised_through = excluded.summarised_through, updated_at = now()"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", last);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new ReadLogSummaryResult(groups.Select(g => g.Day).Distinct().Count(), groups.Count, last);
    }

    /// <summary>Deletes the summarised rows older than the retention of the log.</summary>
    public async Task<ReadLogPurgeResult> PurgeAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Retention, ct: ct);
        int? days = null;
        await using (var cmd = tx.Command("select read_log_retention_days from core.tenant_setting where tenant_id = @t"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            if (await cmd.ExecuteScalarAsync(ct) is int d) days = d;
        }
        DateTime cutoff;
        if (days is { } setting) cutoff = nowUtc.AddDays(-setting);
        else
        {
            // the longest retention of the tenant: of every rule the latest version in force, for READ_LOG, else for any class
            short? years = null;
            await using (var cmd = tx.Command(
                "with latest as (select distinct on (company_id, rule_id) record_class, retention_years from core.retention_rule where tenant_id = @t and effective_from <= @now " +
                "order by company_id, rule_id, effective_from desc) " +
                "select coalesce((select max(retention_years) from latest where record_class = 'READ_LOG'), (select max(retention_years) from latest))"))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = nowUtc });
                if (await cmd.ExecuteScalarAsync(ct) is short y) years = y;
            }
            if (years is null) return new ReadLogPurgeResult(0, null, ReadLogJobRules.NoRule);
            cutoff = nowUtc.AddYears(-years.Value);
        }
        DateOnly? through = await ReadStateAsync(tx, tenantId, ct);
        if (through is null) return new ReadLogPurgeResult(0, cutoff, ReadLogJobRules.NotSummarised);
        // only whole days that have been summarised: the rows before the end of the last summarised day and before the cutoff
        DateTime bound = Utc(through.Value.AddDays(1)) < cutoff ? Utc(through.Value.AddDays(1)) : cutoff;
        long deleted;
        await using (var cmd = tx.Command("with d as (delete from core.read_access_log where tenant_id = @t and read_at < @bound returning 1) select count(*) from d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.Add(new NpgsqlParameter("bound", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = bound });
            deleted = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return new ReadLogPurgeResult(deleted, cutoff, null);
    }
}
