using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// sales-returns:reverse and purchase-returns:reverse (11, 5.6): the owning commands of a return, its STOCK_RETURN document and its derived journal. The mirror return has negative base
/// quantities (so the returned quantity of the delivery or receipt line is restored), the reason code and the id of the original, and is the next node of the FULFILMENT chain; the
/// stock command reverses its stock document and journal in the same transaction and the row hash of the mirror is the detail_digest of the mirror stock document. Returned goods that are
/// consumed again reject the reversal with INVENTORY.RECEIPT_CONSUMED. Not built: ORDER.RETURN_CREDITED (no credit notes yet), the dropship pair.
/// </summary>
public sealed partial class ReturnCommands
{
    private sealed record OriginalLine(
        int LineNo, Guid SourceId, int SourceLineNo, Guid OrderId, int OrderLineNo, Guid ItemId, decimal Quantity, string UnitCode, decimal BaseQuantity, string ReasonCode, string Disposition,
        Guid? Warehouse, Guid? Location, int? StockLine, decimal? Value);

    public Task<CommandResult<FulfilmentResult>> ReverseSalesReturnAsync(Guid tenantId, CommandContext ctx, ReverseReturnRequest req, CancellationToken ct = default) =>
        ReverseAsync(SalesKind, "sales-returns:reverse", tenantId, ctx, req, ct);

    public Task<CommandResult<FulfilmentResult>> ReversePurchaseReturnAsync(Guid tenantId, CommandContext ctx, ReverseReturnRequest req, CancellationToken ct = default) =>
        ReverseAsync(PurchaseKind, "purchase-returns:reverse", tenantId, ctx, req, ct);

    private async Task<CommandResult<FulfilmentResult>> ReverseAsync(Kind kind, string commandName, Guid tenantId, CommandContext ctx, ReverseReturnRequest req, CancellationToken ct)
    {
        if (!ctx.Permissions.Contains(kind.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", kind.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        if (req.CompanyId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/company_id", ("reason", "required")));
        if (req.ReturnId == Guid.Empty) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/return_id", ("reason", "required")));
        if (errors.Count > 0) return CommandResult<FulfilmentResult>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_REQUIRED", "P1", "/reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Reject(Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "/reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = StockReversalRules.Fingerprint(commandName, req.CompanyId, req.ReturnId, req.ReasonCode, req.PostingDate);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Stock, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<FulfilmentResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<FulfilmentResult>.Ok(await LoadAsync(tx, kind, tenantId, req.CompanyId, earlier.EntityId.Value, null, ct), earlier.CommandLogId, replay: true);
                }
                return await ReverseCoreAsync(tx, kind, commandName, tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<FulfilmentResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<FulfilmentResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<FulfilmentResult>> ReverseCoreAsync(
        TenantTransaction tx, Kind kind, string commandName, Guid tenantId, CommandContext ctx, ReverseReturnRequest req, byte[] fingerprint, CancellationToken ct)
    {
        Guid companyId = req.CompanyId, originalId = req.ReturnId;
        var company = await JournalDocumentPoster.ReadCompanyAsync(tx, tenantId, companyId, ct);
        if (company is null || company.Status != "ACTIVE") return Reject(Error("COMPANY.NOT_ACTIVE", "P2", "/company_id", ("status", company?.Status)));

        string status; Guid party; Guid? stockId; DateOnly originalDate; bool mirror;
        await using (var cmd = tx.Command(
            $"select status, {kind.PartyColumn}, stock_document_id, return_date, reversal_of_return_id is not null from core.{kind.Header} where tenant_id = @t and company_id = @c and return_id = @i"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Reject(Error("ORDER.RETURN_NOT_FOUND", "P2", "/return_id"));
            status = r.GetString(0); party = r.GetGuid(1); stockId = r.IsDBNull(2) ? null : r.GetGuid(2); originalDate = DateOnly.FromDateTime(r.GetDateTime(3)); mirror = r.GetBoolean(4);
        }
        if (mirror) return Reject(Error("REVERSAL.TYPE_NOT_REVERSIBLE", "P2", "/return_id", ("reason", "a reversal document is not reversed")));
        if (status != "posted") return Reject(Error("ORDER.RETURN_ALREADY_REVERSED", "P2", "/return_id"));
        DateOnly date = req.PostingDate ?? originalDate;

        var original = new List<OriginalLine>();
        await using (var cmd = tx.Command(
            $"select line_no, {kind.SourceId}, {kind.ReturnSourceLine}, {kind.SourceOrderId}, {kind.SourceOrderLine}, item_id, quantity, unit_code, base_quantity, reason_code, disposition, warehouse_id, location_id, " +
            $"stock_document_line_no, value_lc from core.{kind.Line} where tenant_id = @t and company_id = @c and return_id = @i order by line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                original.Add(new OriginalLine(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetInt32(4), r.GetGuid(5), r.GetDecimal(6), r.GetString(7).Trim(), r.GetDecimal(8), r.GetString(9), r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : r.GetInt32(13), r.IsDBNull(14) ? null : r.GetDecimal(14)));
        }

        // a return that a credit note that is not reversed credits is reversed after the credit note (11, 5.6)
        {
            await using var cmd = tx.Command($"select exists (select 1 from core.{(kind.Sales ? "sales_invoice_line" : "purchase_invoice_line")} where tenant_id = @t and company_id = @c and return_id = @r group by return_line_no having sum(base_quantity) <> 0)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("r", originalId);
            if ((await cmd.ExecuteScalarAsync(ct)) is true) return Reject(Error("ORDER.RETURN_CREDITED", "P6", "/return_id", ("reason", "the credit note is reversed first")));
        }

        // position 1 of the lock order: the order rows, sorted, then the original return
        await using (var cmd = tx.Command(
            $"select {kind.Order.IdColumn} from core.{kind.Order.HeaderTable} where tenant_id = @t and company_id = @c and {kind.Order.IdColumn} = any(@ids) order by {kind.Order.IdColumn} for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ids", original.Select(l => l.OrderId).Distinct().Order().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
        }
        await using (var cmd = tx.Command($"select status from core.{kind.Header} where tenant_id = @t and company_id = @c and return_id = @i for update"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            if ((string?)await cmd.ExecuteScalarAsync(ct) != "posted") return Reject(Error("ORDER.RETURN_ALREADY_REVERSED", "P7", "/return_id"));
        }

        int fiscalYear;
        await using (var cmd = tx.Command("select fiscal_year from core.fiscal_year where tenant_id = @t and company_id = @c and start_date <= @d and end_date >= @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", date);
            if (await cmd.ExecuteScalarAsync(ct) is not int year) return Reject(Error("SYSTEM.FISCAL_YEAR_MISSING", "P2", "/posting_date", ("date", date)));
            fiscalYear = year;
        }
        Guid ledger = await ClearingPoster.ReadDefaultLedgerAsync(tx, tenantId, companyId, ct) ?? Guid.Empty;
        var series = await JournalDocumentPoster.ReadSeriesAsync(tx, tenantId, companyId, kind.SeriesType, date, ledger, ct);
        if (series is null) return Reject(Error("SERIES.NO_ASSIGNMENT", "P2", "/posting_date", ("document_type", kind.SeriesType), ("posting_date", date)));
        long? last = await ClearingPoster.LockCounterAsync(tx, tenantId, companyId, series.SeriesId, fiscalYear, ct);
        if (last is null) return Reject(Error("LIMIT.NUMBER_COUNTER_MISSING", "P7", "/posting_date", ("series_code", series.SeriesCode), ("fiscal_year", fiscalYear)));
        long sequenceNo = last.Value + 1;
        await using (var cmd = tx.Command("update core.number_range_counter set last_number = @n, updated_at = now() where tenant_id = @t and company_id = @c and series_id = @s and fiscal_year = @y"))
        {
            cmd.Parameters.AddWithValue("n", sequenceNo); cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("y", fiscalYear);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string number = PostingRules.FormatNumber(series.Prefix, series.IncludeFiscalYear, series.Separator, series.NumberLength, fiscalYear, sequenceNo);
        Guid documentId = Guid.NewGuid();
        DateTime at = Now();

        long chainSeq = 0; byte[] prev = [], rowHash = [], lineDigest = [];
        List<Dictionary<string, object?>> lineRows = [];
        Guid? mirrorStockId = null, journalId = null;

        async Task<byte[]?> BuildNodeAsync(StockDerivation? derivation)
        {
            (long seq, byte[] hash, _) = await CommandSupport.LockOrCreateHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "",
                LedgerHash2.Genesis(tenantId, companyId, FulfilmentHash2.ChainType, ""), ct);
            chainSeq = seq + 1; prev = hash;
            mirrorStockId = derivation?.StockDocumentId; journalId = derivation?.JournalDocumentId;
            var byStockLine = derivation?.Movements.ToDictionary(m => m.RequestIndex) ?? [];
            lineRows = [];
            foreach (OriginalLine l in original)
            {
                DerivedMovement? m = l.StockLine is null ? null : byStockLine[l.StockLine.Value - 1];
                lineRows.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["document_id"] = documentId, ["line_no"] = l.LineNo, ["source_document_id"] = l.SourceId, ["source_line_no"] = l.SourceLineNo,
                    ["order_id"] = l.OrderId, ["order_line_no"] = l.OrderLineNo, ["item_id"] = l.ItemId, ["quantity"] = -l.Quantity, ["unit_code"] = l.UnitCode, ["base_quantity"] = -l.BaseQuantity,
                    ["reason_code"] = l.ReasonCode, ["disposition"] = l.Disposition, ["warehouse_id"] = l.Warehouse, ["location_id"] = l.Location, ["tracking_id"] = null, ["stock_status"] = "AVAILABLE",
                    ["stock_document_line_no"] = m?.LineNo, ["value_lc"] = l.Value is { } v ? -v : null, ["dropship_confirmation_id"] = null
                });
            }
            lineDigest = FulfilmentHash2.ReturnLineDigest(lineRows);
            var body = new Dictionary<string, object?>
            {
                ["document_id"] = documentId, ["document_type"] = kind.DocumentType, ["number_series_id"] = series.SeriesId, ["fiscal_year"] = fiscalYear, ["sequence_no"] = sequenceNo,
                ["document_number"] = number, ["document_date"] = date, ["partner_id"] = party, ["warehouse_id"] = null, ["reference_1"] = null, ["reference_2"] = null,
                ["stock_document_id"] = mirrorStockId, ["reversal_of_id"] = originalId, ["reason_code"] = req.ReasonCode, ["origin"] = "LIVE", ["migration_batch_id"] = null,
                ["source_system"] = ctx.SourceSystem, ["source_document_ref"] = null, ["posted_at"] = at, ["idempotency_key"] = ctx.IdempotencyKey, ["command_name"] = commandName,
                ["posted_by"] = ctx.Actor, ["correlation_id"] = ctx.CorrelationId, ["line_count"] = lineRows.Count, ["line_digest"] = lineDigest, ["hash_version"] = FulfilmentHash2.Version
            };
            rowHash = FulfilmentHash2.RowHash(tenantId, companyId, chainSeq, prev, body);
            return rowHash;
        }

        if (stockId is { } sid)
        {
            var options = new StockReverseOptions(commandName, WriteLog: false, Commit: false, AllowDerived: true, BuildNodeAsync);
            var reversed = await _stock.ReverseCoreAsync(tx, tenantId, ctx, companyId, sid, req.ReasonCode, date, fingerprint, ct, options);
            if (reversed.Status != CommandStatus.Ok) return CommandResult<FulfilmentResult>.Rejected(reversed.Errors.ToList());
        }
        else await BuildNodeAsync(null);

        await using (var cmd = tx.Command(
            $"insert into core.{kind.Header} (tenant_id, company_id, return_id, return_number, number_series_id, fiscal_year, sequence_no, return_date, {kind.PartyColumn}, stock_document_id, reversal_of_return_id, reason_code, " +
            "source_system, posted_at, posted_by, correlation_id, idempotency_key, line_count, chain_seq, prev_hash, row_hash, line_digest, hash_version) values (@t, @c, @id, @num, @s, @fy, @seq, @d, @party, @stock, @orig, @reason, " +
            "@src, @at, @by, @corr, @key, @n, @cs, @prev, @row, @ld, 2)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("num", number);
            cmd.Parameters.AddWithValue("s", series.SeriesId); cmd.Parameters.AddWithValue("fy", fiscalYear); cmd.Parameters.AddWithValue("seq", sequenceNo); cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("party", party); cmd.Parameters.AddWithValue("orig", originalId); cmd.Parameters.AddWithValue("reason", req.ReasonCode);
            cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
            cmd.Parameters.AddWithValue("n", lineRows.Count); cmd.Parameters.AddWithValue("cs", chainSeq); cmd.Parameters.AddWithValue("prev", prev); cmd.Parameters.AddWithValue("row", rowHash);
            cmd.Parameters.AddWithValue("ld", lineDigest);
            cmd.Parameters.Add(new NpgsqlParameter("stock", NpgsqlDbType.Uuid) { Value = (object?)mirrorStockId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var row in lineRows)
        {
            await using var cmd = tx.Command(
                $"insert into core.{kind.Line} (tenant_id, company_id, return_id, line_no, {kind.SourceId}, {kind.ReturnSourceLine}, {kind.SourceOrderId}, {kind.SourceOrderLine}, item_id, quantity, unit_code, base_quantity, " +
                "reason_code, disposition, warehouse_id, location_id, stock_status, stock_document_line_no, value_lc) values (@t, @c, @id, @n, @sd, @sl, @o, @ol, @i, @q, @u, @bq, @rc, @disp, @w, @l, 'AVAILABLE', @stl, @v)");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("id", documentId); cmd.Parameters.AddWithValue("n", row["line_no"]!);
            cmd.Parameters.AddWithValue("sd", row["source_document_id"]!); cmd.Parameters.AddWithValue("sl", row["source_line_no"]!); cmd.Parameters.AddWithValue("o", row["order_id"]!);
            cmd.Parameters.AddWithValue("ol", row["order_line_no"]!); cmd.Parameters.AddWithValue("i", row["item_id"]!); cmd.Parameters.AddWithValue("q", row["quantity"]!);
            cmd.Parameters.AddWithValue("u", row["unit_code"]!); cmd.Parameters.AddWithValue("bq", row["base_quantity"]!); cmd.Parameters.AddWithValue("rc", row["reason_code"]!);
            cmd.Parameters.AddWithValue("disp", row["disposition"]!);
            cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = row["warehouse_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = row["location_id"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("stl", NpgsqlDbType.Integer) { Value = row["stock_document_line_no"] ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Numeric) { Value = row["value_lc"] ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = tx.Command($"update core.{kind.Header} set status = 'reversed' where tenant_id = @t and company_id = @c and return_id = @i and status = 'posted'"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", originalId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ClearingPoster.MoveHeadAsync(tx, tenantId, companyId, FulfilmentHash2.ChainType, "", chainSeq, rowHash, FulfilmentHash2.Version, ct);
        string entity = kind.Sales ? "SALES_RETURN" : "PURCHASE_RETURN";
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, companyId, ctx, commandName, fingerprint, entity, documentId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, companyId, ctx, "return.reversed", entity, $"{companyId:D}/{originalId:D}", "REVERSED", at, isAudit: true,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["chain_type"] = "FULFILMENT", ["scope"] = "", ["chain_seq"] = chainSeq, ["row_hash"] = Canonical.FormatHex32(rowHash) }),
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["number"] = number, ["partner_id"] = party, ["lines"] = lineRows.Count, ["stock_document_id"] = mirrorStockId, ["reversal_of_id"] = originalId }), ct);

        FulfilmentResult result = await LoadAsync(tx, kind, tenantId, companyId, documentId, journalId, ct);
        await tx.CommitAsync(ct);   // the deferred checks: the chains, the movement and line counts, the journal, the outbox position
        return CommandResult<FulfilmentResult>.Ok(result, logId);
    }
}
