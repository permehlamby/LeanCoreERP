using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Periods;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// fiscal-years:open (04, 7.3): creates the year, all periods (OPEN), the number counters of every series of the company
/// and the YEAR_OPEN event of the period chain in one transaction, and emits year.opened. Not in this slice:
/// calendar_id other than the company's, history years, the cutover year of a migration.
/// Locks (04, 6.3): the PERIOD_EVENT chain head first, so two openings of the same company are serialised and the
/// contiguity check below reads a stable list of years, then outbox_head last.
/// </summary>
public sealed class FiscalYearOpener(NpgsqlDataSource source)
{
    public const string CommandName = "fiscal-years:open";
    public const string Permission = "period.admin";

    public async Task<CommandResult<OpenedFiscalYear>> OpenAsync(
        Guid tenantId, CommandContext ctx, OpenFiscalYearRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(new CommandError("AUTH.PERMISSION_DENIED", "P0", "", new Dictionary<string, object?> { ["permission"] = Permission }));

        var shape = new List<CommandError>();
        void Invalid(string path, string reason) =>
            shape.Add(new CommandError("SCHEMA.INVALID_VALUE", "P1", path, new Dictionary<string, object?> { ["reason"] = reason }));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (req.CompanyId == Guid.Empty) Invalid("company_id", "required");
        if (req.FiscalYear is < 1900 or > 2999) Invalid("fiscal_year", "between 1900 and 2999");
        if (shape.Count > 0) return CommandResult<OpenedFiscalYear>.Rejected(shape);

        byte[] fingerprint = CommandFingerprint.ForOpenFiscalYear(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await OpenOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<OpenedFiscalYear>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<OpenedFiscalYear>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<OpenedFiscalYear>> OpenOnceAsync(
        Guid tenantId, CommandContext ctx, OpenFiscalYearRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Period, ct: ct);

        // Idempotency
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(new CommandError("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            OpenedFiscalYear? replay = earlier.EntityId is null ? null : await LoadByEventAsync(tx, tenantId, earlier.EntityId.Value, ct);
            return replay is null
                ? CommandResult<OpenedFiscalYear>.Failed(new CommandError("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<OpenedFiscalYear>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // P2: company and calendar
        int firstMonth; int specialCount; string labelRule; Guid calendarId;
        await using (var cmd = tx.Command(
            "select c.fiscal_calendar_id, k.first_month, k.special_period_count, k.year_label_rule " +
            "from core.company c join core.fiscal_calendar k on k.tenant_id = c.tenant_id and k.calendar_id = c.fiscal_calendar_id " +
            "where c.tenant_id = @t and c.company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject(new CommandError("COMPANY.NOT_FOUND", "P2", "company_id"));
            calendarId = r.GetGuid(0); firstMonth = r.GetInt16(1); specialCount = r.GetInt16(2); labelRule = r.GetString(3);
        }
        if (req.CalendarId is not null && req.CalendarId != calendarId)
            return Reject(new CommandError("LIMIT.CALENDAR_ID_NOT_SUPPORTED", "P2", "calendar_id"));

        // P7: the period chain head first. Everything that depends on the list of years is read after this lock.
        byte[] genesis = LedgerHash2.Genesis(tenantId, req.CompanyId, PeriodEventHash2.ChainType, "");
        (long headSeq, byte[] headHash, _) = await CommandSupport.LockOrCreateHeadAsync(
            tx, tenantId, req.CompanyId, PeriodEventHash2.ChainType, "", genesis, ct);

        var years = new List<(int Year, DateOnly Start, DateOnly End)>();
        await using (var cmd = tx.Command(
            "select fiscal_year, start_date, end_date from core.fiscal_year where tenant_id = @t and company_id = @c order by start_date"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) years.Add((r.GetInt32(0), r.GetFieldValue<DateOnly>(1), r.GetFieldValue<DateOnly>(2)));
        }

        if (years.Any(y => y.Year == req.FiscalYear))
            return Reject(new CommandError("FISCAL_YEAR.ALREADY_OPEN", "P3", "fiscal_year", new Dictionary<string, object?> { ["fiscal_year"] = req.FiscalYear }));

        DateOnly nominalStart = FiscalCalendarMath.NominalStart(req.FiscalYear, firstMonth, labelRule);
        DateOnly nominalEnd = FiscalCalendarMath.NominalEnd(nominalStart);
        DateOnly start = nominalStart;
        int firstPeriodNo = 1;

        if (years.Count == 0)
        {
            // First year of the company: may be short (Q6). It starts on the first day of a month, inside the nominal year.
            if (req.StartDate is { } given)
            {
                if (given.Day != 1 || given < nominalStart || given > nominalStart.AddMonths(11))
                    return Reject(new CommandError("FISCAL_YEAR.START_DATE_INVALID", "P3", "start_date", new Dictionary<string, object?>
                    {
                        ["earliest"] = nominalStart, ["latest"] = nominalStart.AddMonths(11), ["rule"] = "first day of a month"
                    }));
                start = given;
                firstPeriodNo = (given.Year - nominalStart.Year) * 12 + given.Month - nominalStart.Month + 1;
            }
        }
        else
        {
            if (req.StartDate is not null)
                return Reject(new CommandError("FISCAL_YEAR.START_DATE_NOT_ALLOWED", "P3", "start_date"));
            DateOnly expected = years[^1].End.AddDays(1);
            if (nominalStart != expected)
                return Reject(new CommandError("FISCAL_YEAR.NOT_CONTIGUOUS", "P3", "fiscal_year", new Dictionary<string, object?>
                {
                    ["expected_start"] = expected, ["start_of_requested_year"] = nominalStart, ["previous_year"] = years[^1].Year
                }));
        }

        List<OpenedPeriod> periods = FiscalCalendarMath.Periods(nominalStart, firstPeriodNo, specialCount);

        var series = new List<(Guid Id, long StartNumber)>();
        await using (var cmd = tx.Command(
            "select series_id, start_number from core.number_series where tenant_id = @t and company_id = @c order by series_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) series.Add((r.GetGuid(0), r.GetInt64(1)));
        }

        // The event of the period chain
        Guid eventId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        DateTime eventAt = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        long eventSeq = headSeq + 1;
        var body = new Dictionary<string, object?>
        {
            ["event_id"] = eventId, ["fiscal_year"] = (long)req.FiscalYear, ["period_no"] = null, ["event_type"] = "YEAR_OPEN",
            ["from_status"] = null, ["to_status"] = "OPEN", ["reason_code"] = null, ["reason_text"] = null,
            ["authorisation"] = Permission, ["document_id"] = null, ["event_at"] = eventAt, ["event_by"] = ctx.Actor,
            ["correlation_id"] = ctx.CorrelationId, ["idempotency_key"] = ctx.IdempotencyKey
        };
        byte[] rowHash = PeriodEventHash2.RowHash(tenantId, req.CompanyId, eventSeq, headHash, body);

        await using (var cmd = tx.Command(
            "insert into core.period_event (tenant_id, event_id, company_id, event_seq, fiscal_year, period_no, event_type, from_status, to_status, " +
            "authorisation, event_at, event_by, correlation_id, idempotency_key, prev_hash, row_hash, hash_version) " +
            "values (@t, @e, @c, @seq, @y, null, 'YEAR_OPEN', null, 'OPEN', @auth, @at, @by, @corr, @key, @prev, @row, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("e", eventId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("seq", eventSeq);
            cmd.Parameters.AddWithValue("y", req.FiscalYear);
            cmd.Parameters.AddWithValue("auth", Permission);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = eventAt });
            cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
            cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("prev", headHash);
            cmd.Parameters.AddWithValue("row", rowHash);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = tx.Command(
            "insert into core.fiscal_year (tenant_id, company_id, fiscal_year, calendar_id, start_date, end_date, first_period_no, " +
            "special_period_count, is_history, status, opened_by, last_event_id) " +
            "values (@t, @c, @y, @cal, @s, @end, @first, @special, false, 'OPEN', @by, @e)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("y", req.FiscalYear);
            cmd.Parameters.AddWithValue("cal", calendarId);
            cmd.Parameters.AddWithValue("s", start);
            cmd.Parameters.AddWithValue("end", nominalEnd);
            cmd.Parameters.AddWithValue("first", firstPeriodNo);
            cmd.Parameters.AddWithValue("special", (short)specialCount);
            cmd.Parameters.AddWithValue("by", ctx.Actor);
            cmd.Parameters.AddWithValue("e", eventId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // The first period of a short first year starts on the start date of the year
        periods[0] = periods[0] with { StartDate = start };
        foreach (OpenedPeriod p in periods)
        {
            await using var cmd = tx.Command(
                "insert into core.fiscal_period (tenant_id, company_id, fiscal_year, period_no, period_type, start_date, end_date, status) " +
                "values (@t, @c, @y, @p, @type, @s, @e, 'OPEN')");
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("y", req.FiscalYear);
            cmd.Parameters.AddWithValue("p", p.PeriodNo);
            cmd.Parameters.AddWithValue("type", p.PeriodType);
            cmd.Parameters.AddWithValue("s", p.StartDate);
            cmd.Parameters.AddWithValue("e", p.EndDate);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // One counter per series, starting at start_number - 1 (04, 6). Rows are new, so no lock is needed.
        foreach (var s in series)
        {
            await using var cmd = tx.Command(
                "insert into core.number_range_counter (tenant_id, company_id, series_id, fiscal_year, last_number) values (@t, @c, @s, @y, @n)");
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            cmd.Parameters.AddWithValue("s", s.Id);
            cmd.Parameters.AddWithValue("y", req.FiscalYear);
            cmd.Parameters.AddWithValue("n", s.StartNumber - 1);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = tx.Command(
            "update core.chain_head set last_seq = @s, last_hash = @h, hash_version = 2, updated_at = now() " +
            "where tenant_id = @t and company_id = @c and chain_type = 'PERIOD_EVENT' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("s", eventSeq);
            cmd.Parameters.AddWithValue("h", rowHash);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", req.CompanyId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, CommandName, fingerprint, "PERIOD_EVENT", eventId, ct);

        string rowHashHex = Canonical.FormatHex32(rowHash);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "year.opened", "FISCAL_YEAR",
            $"{req.CompanyId:D}/{req.FiscalYear}", "CREATED", eventAt, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["chain_type"] = PeriodEventHash2.ChainType, ["scope"] = "", ["chain_seq"] = eventSeq, ["row_hash"] = rowHashHex
            }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["fiscal_year"] = req.FiscalYear, ["period_no"] = null, ["status"] = "OPEN" }), ct);

        await tx.CommitAsync(ct);

        var opened = new OpenedFiscalYear(req.CompanyId, req.FiscalYear, calendarId, start, nominalEnd, firstPeriodNo, specialCount,
            periods, series.Select(s => s.Id).ToList(), eventId, eventSeq, rowHashHex, Canonical.FormatHex32(headHash));
        return CommandResult<OpenedFiscalYear>.Ok(opened, logId);
    }

    private static async Task<OpenedFiscalYear?> LoadByEventAsync(TenantTransaction tx, Guid tenantId, Guid eventId, CancellationToken ct)
    {
        Guid companyId; int year; long seq; byte[] row, prev;
        await using (var cmd = tx.Command(
            "select company_id, fiscal_year, event_seq, row_hash, prev_hash from core.period_event where tenant_id = @t and event_id = @e"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("e", eventId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            companyId = r.GetGuid(0); year = r.GetInt32(1); seq = r.GetInt64(2); row = (byte[])r[3]; prev = (byte[])r[4];
        }
        Guid calendarId; DateOnly start, end; int first, special;
        await using (var cmd = tx.Command(
            "select calendar_id, start_date, end_date, first_period_no, special_period_count from core.fiscal_year " +
            "where tenant_id = @t and company_id = @c and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("y", year);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            calendarId = r.GetGuid(0); start = r.GetFieldValue<DateOnly>(1); end = r.GetFieldValue<DateOnly>(2);
            first = r.GetInt32(3); special = r.GetInt16(4);
        }
        var periods = new List<OpenedPeriod>();
        await using (var cmd = tx.Command(
            "select period_no, period_type, start_date, end_date from core.fiscal_period " +
            "where tenant_id = @t and company_id = @c and fiscal_year = @y order by period_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("y", year);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                periods.Add(new OpenedPeriod(r.GetInt32(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateOnly>(3)));
        }
        var counters = new List<Guid>();
        await using (var cmd = tx.Command(
            "select series_id from core.number_range_counter where tenant_id = @t and company_id = @c and fiscal_year = @y order by series_id"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("y", year);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) counters.Add(r.GetGuid(0));
        }
        return new OpenedFiscalYear(companyId, year, calendarId, start, end, first, special, periods, counters,
            eventId, seq, Canonical.FormatHex32(row), Canonical.FormatHex32(prev));
    }

    private static CommandResult<OpenedFiscalYear> Reject(CommandError error) => CommandResult<OpenedFiscalYear>.Rejected([error]);
}
