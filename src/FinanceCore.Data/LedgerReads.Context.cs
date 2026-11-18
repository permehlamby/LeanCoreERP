using FinanceCore.Core.Commands;
using FinanceCore.Core.Hashing;
using FinanceCore.Core.Reads;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

public sealed partial class LedgerReads
{
    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateOnly? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);

    /// <summary>The last anchor of a chain at or after a position (09, 4.7 integrity): the first one that covers it. Null when none was written yet.</summary>
    private static async Task<AnchorRead?> AnchorAtOrAfterAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, string chainType, string scopeKey, long seq, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select anchor_id, chain_seq, anchored_at, anchor_state, tsa_id from core.chain_anchor where tenant_id = @t and company_id = @c and chain_type = @ct " +
            "and scope_key = @sk and chain_seq >= @seq order by chain_seq, anchored_at limit 1");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("ct", chainType);
        cmd.Parameters.AddWithValue("sk", scopeKey); cmd.Parameters.AddWithValue("seq", seq);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AnchorRead(r.GetGuid(0), r.GetInt64(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetString(3), Text(r, 4))
            : null;
    }

    /// <summary>
    /// documents:get-context (09, 4.7): one document with its context, the includes `lines`, `open_items`, `reversal`, `clearings`, `changes`, `origin`, `integrity` and
    /// `legal_snapshot` (no include names all of them). The legal snapshot needs read.legal_snapshot and is logged (LEGAL_SNAPSHOT, one row); without the permission it
    /// is left out and named in `omitted`, not rejected. Not built: the includes `tax`, `dimensions`, `intercompany`, `revaluation` and `vat`.
    /// </summary>
    public async Task<CommandResult<DocumentContextResult>> GetDocumentContextAsync(Guid tenantId, CommandContext ctx, DocumentContextRequest req, CancellationToken ct = default)
    {
        if (Denied(ctx) is { } denied) return Reject<DocumentContextResult>(denied);
        if (req.DocumentId == Guid.Empty) return Reject<DocumentContextResult>(Error("SCHEMA.INVALID_VALUE", "P1", "document_id", ("reason", "required")));
        IReadOnlyList<string> include = req.Include is { Count: > 0 } ? req.Include : DocumentContextRules.Includes;
        foreach (string i in include)
            if (!DocumentContextRules.Includes.Contains(i))
                return Reject<DocumentContextResult>(Error("READ.INVALID_FILTER", "P1", "include", ("field", i), ("allowed", DocumentContextRules.Includes.ToArray())));
        if (!ReadAccessLog.ValidPurpose(req.PurposeCode)) return Reject<DocumentContextResult>(Error("SCHEMA.INVALID_VALUE", "P1", "purpose_code", ("reason", "1 to 50 characters")));

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct, snapshot: true);
        JournalDocumentRead? doc = null;
        byte[]? prevHash = null;
        string? idempotencyKey = null;
        await using (var cmd = tx.Command($"select {DocumentSelect}, d.prev_hash, d.idempotency_key from core.journal_document d where d.tenant_id = @t and d.document_id = @d"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("d", req.DocumentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { doc = ReadDocument(r); prevHash = (byte[])r[23]; idempotencyKey = r.GetString(24); }
        }
        if (doc is null) return Reject<DocumentContextResult>(Error("READ.NOT_FOUND", "P2", "document_id"));
        var opened = await OpenAsync(tx, tenantId, doc.CompanyId, doc.PostingDate, req.MinLedgerSeq, ct);
        if (opened.Error is not null) return Reject<DocumentContextResult>(opened.Error);
        bool hasRc = opened.Company!.Rc is not null;
        Guid companyId = doc.CompanyId, documentId = doc.DocumentId;

        if (include.Contains("lines"))
            doc = doc with { Lines = (await ReadLinesAsync(tx, tenantId, [documentId], hasRc, ct)).GetValueOrDefault(documentId) ?? [] };

        List<DocumentOpenItemRead>? openItems = null;
        if (include.Contains("open_items"))
        {
            openItems = [];
            await using var cmd = tx.Command(
                "select line_no, partner_id, baseline_date, due_date, payment_block, clearing_status, cleared_amount_tc, last_clearing_id from core.journal_line_open_item " +
                "where tenant_id = @t and company_id = @c and document_id = @d order by line_no");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                openItems.Add(new DocumentOpenItemRead(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetGuid(1), Date(r, 2), Date(r, 3), Text(r, 4), r.GetString(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetGuid(7)));
        }

        DocumentReversalRead? reversal = null;
        if (include.Contains("reversal") && (doc.ReversalOfDocumentId is not null || doc.ReversedByDocumentId is not null))
        {
            async Task<string?> NumberOf(Guid? id)
            {
                if (id is null) return null;
                await using var cmd = tx.Command("select document_number from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d");
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", id.Value);
                return await cmd.ExecuteScalarAsync(ct) as string;
            }
            reversal = new DocumentReversalRead(doc.ReversalOfDocumentId, await NumberOf(doc.ReversalOfDocumentId), doc.ReversedByDocumentId, await NumberOf(doc.ReversedByDocumentId), doc.ReversalReasonCode);
        }

        List<DocumentClearingRead>? clearings = null;
        if (include.Contains("clearings"))
        {
            clearings = [];
            await using var cmd = tx.Command(
                "select c.clearing_id, c.clearing_type, i.clearing_date, i.open_item_line_no, i.currency_tc, i.cleared_amount_tc, i.cleared_amount_lc, c.document_id, c.matching_ref " +
                "from core.clearing_item i join core.clearing c on c.tenant_id = i.tenant_id and c.company_id = i.company_id and c.clearing_id = i.clearing_id " +
                "where i.tenant_id = @t and i.company_id = @c and i.open_item_document_id = @d order by c.chain_seq, i.open_item_line_no");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                clearings.Add(new DocumentClearingRead(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetInt32(3), r.GetString(4).Trim(), r.GetDecimal(5), r.GetDecimal(6),
                    r.IsDBNull(7) ? null : r.GetGuid(7), Text(r, 8)));
        }

        List<DocumentChangeRead>? changes = null;
        if (include.Contains("changes"))
        {
            changes = [];
            await using var cmd = tx.Command(
                "select changed_at, changed_by, entity_type, line_no, field_name, old_value, new_value, reason_code, reason_text, correlation_id from core.change_log " +
                "where tenant_id = @t and company_id = @c and document_id = @d order by changed_at, change_id");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                changes.Add(new DocumentChangeRead(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4),
                    Text(r, 5), Text(r, 6), r.GetString(7), Text(r, 8), r.GetString(9)));
        }

        DocumentOriginRead? origin = null;
        if (include.Contains("origin"))
        {
            await using var cmd = tx.Command("select command_name, actor, correlation_id, executed_at from core.command_log where tenant_id = @t and idempotency_key = @k");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("k", idempotencyKey!);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) origin = new DocumentOriginRead(r.GetString(0), r.GetString(1), r.GetString(2), DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc));
        }

        DocumentIntegrityRead? integrity = null;
        if (include.Contains("integrity"))
            integrity = new DocumentIntegrityRead(doc.ChainSeq, Canonical.FormatHex32(prevHash!), doc.RowHash,
                await AnchorAtOrAfterAsync(tx, tenantId, companyId, "LEDGER", doc.LedgerId.ToString("D"), doc.ChainSeq, ct));

        var omitted = new List<string>();
        LegalSnapshotRead? legal = null;
        if (include.Contains("legal_snapshot"))
        {
            if (!ctx.Permissions.Contains(PartnerReadRules.LegalSnapshotPermission)) omitted.Add("legal_snapshot");
            else
            {
                await using (var cmd = tx.Command(
                    "select partner_id, legal_name, address_line_1, address_line_2, postal_code, city, country_code, partner_vat_id, electronic_address, legal_registration_id, anonymised_at, retention_until " +
                    "from core.document_legal_snapshot where tenant_id = @t and company_id = @c and document_id = @d"))
                {
                    cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                        legal = new LegalSnapshotRead(r.IsDBNull(0) ? null : r.GetGuid(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6)?.Trim(), Text(r, 7), Text(r, 8), Text(r, 9),
                            r.IsDBNull(10) ? null : DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc), r.GetFieldValue<DateOnly>(11));
                }
                // the snapshot of a document without a partner carries no personal data and is not logged (5.2: a read that returns no personal data writes no row)
                if (legal?.PartnerId is not null)
                    await ReadAccessLog.WriteAsync(tx, tenantId, ctx, "documents:get-context", ReadAccessLog.LegalSnapshot, companyId, legal.PartnerId, documentId, req.PurposeCode, null, ct);
            }
        }

        var result = new DocumentContextResult(doc, openItems, reversal, clearings, changes, origin, integrity, legal, omitted, opened.Snapshot!);
        await tx.CommitAsync(ct);
        return CommandResult<DocumentContextResult>.Ok(result, Guid.Empty);
    }
}
