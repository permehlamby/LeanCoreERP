using System.Globalization;
using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// dropship-confirmations:reverse (11, 6.6, 6.8), the owning command of a confirmation and its DROPSHIP_COST journal (permission dropship.confirm). One transaction: a mirror confirmation with
/// negative quantity and value (a node of the FULFILMENT chain, its row hash the detail_digest of the mirror journal), the mirror journal by the reverser (which clears the GR/IR open item of the
/// original credit against its mirror and refuses with REVERSAL.HAS_CLEARED_LINES when something else cleared it), and the status of the original. Rejected with ORDER.DELIVERY_RETURNED while a
/// return that is not reversed refers to the confirmation, and with ORDER.DELIVERY_INVOICED while an invoice line names it.
/// </summary>
public sealed partial class DropshipCommands
{
    public async Task<CommandResult<ReversedDropshipConfirmation>> ReverseAsync(Guid tenantId, CommandContext ctx, ReverseDropshipConfirmationRequest req, CancellationToken ct = default)
    {
        static CommandResult<ReversedDropshipConfirmation> Fail(CommandError e) => CommandResult<ReversedDropshipConfirmation>.Rejected([e]);
        if (!ctx.Permissions.Contains(DropshipRules.Permission)) return Fail(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", DropshipRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.ConfirmationId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/confirmation_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<ReversedDropshipConfirmation>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = DropshipRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Fail(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    if (earlier.EntityId is null) return CommandResult<ReversedDropshipConfirmation>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
                    return CommandResult<ReversedDropshipConfirmation>.Ok(await LoadReversalAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await ReverseCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (Rejected ex)
            {
                return Fail(ex.Error);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<ReversedDropshipConfirmation>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<ReversedDropshipConfirmation>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<ReversedDropshipConfirmation>> ReverseCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, ReverseDropshipConfirmationRequest req, byte[] fingerprint, CancellationToken ct)
    {
        static CommandResult<ReversedDropshipConfirmation> Fail(CommandError e) => CommandResult<ReversedDropshipConfirmation>.Rejected([e]);
        Guid companyId = req.CompanyId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return Fail(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company?.Status)));

        // P2: the original
        string status; bool isMirror; DateOnly originalDate; Guid customer, supplier, salesOrder, purchaseOrder, item, journal;
        int salesLine, purchaseLine; decimal quantity, baseQuantity, value; string unit; string? shipment, carrier, sourceRef;
        await using (var cmd = tx.Command(
            "select status, reversal_of_confirmation_id is not null, confirmation_date, customer_partner_id, supplier_partner_id, sales_order_id, sales_order_line_no, purchase_order_id, purchase_order_line_no, item_id, " +
            "quantity, unit_code, base_quantity, value_lc, journal_document_id, supplier_shipment_reference, carrier_reference, source_document_ref from core.dropship_confirmation where tenant_id = @t and company_id = @c and dropship_confirmation_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.ConfirmationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail(Error("REVERSAL.NOT_FOUND", "P2", "/confirmation_id"));
            status = r.GetString(0); isMirror = r.GetBoolean(1); originalDate = DateOnly.FromDateTime(r.GetDateTime(2)); customer = r.GetGuid(3); supplier = r.GetGuid(4);
            salesOrder = r.GetGuid(5); salesLine = r.GetInt32(6); purchaseOrder = r.GetGuid(7); purchaseLine = r.GetInt32(8); item = r.GetGuid(9); quantity = r.GetDecimal(10); unit = r.GetString(11).Trim();
            baseQuantity = r.GetDecimal(12); value = r.GetDecimal(13); journal = r.GetGuid(14); shipment = r.IsDBNull(15) ? null : r.GetString(15); carrier = r.IsDBNull(16) ? null : r.GetString(16);
            sourceRef = r.IsDBNull(17) ? null : r.GetString(17);
        }
        if (isMirror) return Fail(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/confirmation_id", ("reason", "a reversal document is not reversed")));
        if (status != "posted") return Fail(Error("REVERSAL.ALREADY_REVERSED", "P2", "/confirmation_id"));
        DateOnly date = req.ReversalDate ?? originalDate;

        // position 1 of the lock order: the order rows, sorted (sales, then purchase), then the confirmation itself
        foreach (var (table, column, id) in new[] { ("sales_order", "sales_order_id", salesOrder), ("purchase_order", "purchase_order_id", purchaseOrder) })
        {
            await using var cmd = tx.Command($"select 1 from core.{table} where tenant_id = @t and company_id = @c and {column} = @i for update");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
            await cmd.ExecuteScalarAsync(ct);
        }
        await using (var cmd = tx.Command("select status from core.dropship_confirmation where tenant_id = @t and company_id = @c and dropship_confirmation_id = @i for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.ConfirmationId);
            if ((string?)await cmd.ExecuteScalarAsync(ct) != "posted") return Fail(Error("REVERSAL.ALREADY_REVERSED", "P7", "/confirmation_id"));
        }

        // a return that is not reversed refers to the confirmation (5.4, B6): the returns are reversed first
        foreach (var (line, header) in new[] { ("sales_return_line", "sales_return"), ("purchase_return_line", "purchase_return") })
        {
            await using var cmd = tx.Command(
                $"select exists (select 1 from core.{line} l join core.{header} h on h.tenant_id = l.tenant_id and h.company_id = l.company_id and h.return_id = l.return_id " +
                "where l.tenant_id = @t and l.company_id = @c and l.dropship_confirmation_id = @i and h.status = 'posted' and h.reversal_of_return_id is null)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.ConfirmationId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Fail(Error("ORDER.DELIVERY_RETURNED", "P6", "/confirmation_id"));
        }
        // an invoice line that names the confirmation as its delivery reference protects it (D2)
        await using (var cmd = tx.Command("select exists (select 1 from core.sales_invoice_line where tenant_id = @t and company_id = @c and dropship_confirmation_id = @i)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.ConfirmationId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return Fail(Error("ORDER.DELIVERY_INVOICED", "P6", "/confirmation_id"));
        }

        // the number of the mirror confirmation
        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Fail(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/reversal_date", ("date", date)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, DropshipRules.SeriesType, date, ledger, ct);
        if (series is null) return Fail(Error("SERIES.NO_ASSIGNMENT", "P2", "/reversal_date", ("document_type", DropshipRules.SeriesType), ("posting_date", date)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Fail(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/reversal_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        long sequenceNo = last.Value + 1;
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid mirrorId = Guid.NewGuid(), mirrorJournal = Guid.Empty;
        DateTime at = Now();
        string baseKey = ctx.IdempotencyKey.Length > 90 ? ctx.IdempotencyKey[..90] : ctx.IdempotencyKey;
        CommandContext rowCtx = ctx with { IdempotencyKey = $"{baseKey}#D1" };
        var plan = new Plan(0, new DropshipConfirmationLineInput("", salesOrder, salesLine, -quantity, unit), item, -baseQuantity, salesOrder, salesLine, purchaseOrder, purchaseLine, customer, supplier, -value, 0m, Guid.Empty, Guid.Empty);
        // the unit price of the line is that of the original node: read from the purchase line as at posting
        decimal unitPrice = 0m;
        await using (var cmd = tx.Command("select unit_price from core.purchase_order_line where tenant_id = @t and company_id = @c and purchase_order_id = @o and line_no = @n"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("o", purchaseOrder); cmd.Parameters.AddWithValue("n", purchaseLine);
            unitPrice = Math.Round((decimal)(await cmd.ExecuteScalarAsync(ct))!, 4);
        }
        plan = plan with { UnitPrice = unitPrice };
        long chainSeq = 0; byte[] prev = [], rowHash = [], lineDigest = [];

        async Task<byte[]?> NodeAsync(TenantTransaction t, Guid journalId)
        {
            mirrorJournal = journalId;
            (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(t, tenantId, companyId, FulfilmentHash2.ChainType, "", LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
            chainSeq = seq + 1; prev = hash;
            lineDigest = FulfilmentHash2.LineDigest([LineRow(tenantId, companyId, mirrorId, plan, -quantity, -baseQuantity, -value)]);
            var body = Body(mirrorId, series.SeriesId, fiscalYear, sequenceNo, number, date, customer, shipment, carrier, journalId, req.ConfirmationId, req.ReasonCode, ctx, rowCtx, at, sourceRef, ReverseName, lineDigest);
            rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
            return rowHash;
        }
        async Task AfterAsync(TenantTransaction t, Guid journalId, CancellationToken token)
        {
            await InsertAsync(t, tenantId, companyId, mirrorId, number, series.SeriesId, fiscalYear, sequenceNo, date, plan, -quantity, -baseQuantity, -value, shipment, carrier, journalId, req.ConfirmationId, req.ReasonCode, sourceRef,
                at, rowCtx, ctx, chainSeq, prev, rowHash, lineDigest, token);
            await using (var cmd = t.Command("update core.dropship_confirmation set status = 'reversed' where tenant_id = @t and company_id = @c and dropship_confirmation_id = @i"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", req.ConfirmationId);
                await cmd.ExecuteNonQueryAsync(token);
            }
            await ClearingPoster.MoveHeadAsync(t, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, token);
            await CommandSupport.WriteOutboxAsync(t, tenantId, companyId, ctx, "dropship.reversed", "DROPSHIP_CONFIRMATION", $"{companyId:D}/{mirrorId:D}", "REVERSED", at, isAudit: true,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["reversal_of"] = req.ConfirmationId, ["reason_code"] = req.ReasonCode, ["journal_document_id"] = journalId }), token);
        }

        var mode = new DocumentReverser.ReverseMode(ReverseName, "DROPSHIP_COST", "ORDER", (_, _, _) => Task.FromResult<CommandError?>(null), AfterAsync, null, null, NodeAsync);
        var reverser = new DocumentReverser(source);
        CommandContext journalCtx = ctx with { IdempotencyKey = $"{baseKey}#J1" };
        var reversed = await reverser.ReverseCoreAsync(tx, tenantId, journalCtx, new ReverseDocumentRequest(journal, date, req.ReasonCode, null), fingerprint, ct, mode, owned: false);
        if (reversed.Status != CommandStatus.Ok) return CommandResult<ReversedDropshipConfirmation>.Rejected(reversed.Errors.ToList()) with { Status = reversed.Status };

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, ReverseName, fingerprint, "DROPSHIP_CONFIRMATION", mirrorId, ct);
        var result = await LoadReversalAsync(tx, tenantId, companyId, mirrorId, ct);
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the journal documents, the clearing, the outbox position
        return CommandResult<ReversedDropshipConfirmation>.Ok(result, logId);
    }

    private static async Task<ReversedDropshipConfirmation> LoadReversalAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid mirrorId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select reversal_of_confirmation_id, journal_document_id, row_hash from core.dropship_confirmation where tenant_id = @t and company_id = @c and dropship_confirmation_id = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", mirrorId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ReversedDropshipConfirmation(r.GetGuid(0), mirrorId, r.GetGuid(1), Canonical.FormatHex32((byte[])r[2]));
    }
}
