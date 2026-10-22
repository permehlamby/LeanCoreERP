using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>Reads posted documents back, for replays of idempotent commands and for verification.</summary>
public static class LedgerReader
{
    private const string DocumentColumns =
        "company_id, document_id, ledger_id, document_type, is_system_generated, number_series_id, fiscal_year, sequence_no, " +
        "document_number, fiscal_period, soft_close_override, migration_batch_id, detail_digest, document_date, posting_date, " +
        "posted_at, transaction_currency, reversal_of_document_id, reversal_reason_code, auto_reversal_date, ic_transaction_id, " +
        "ic_counterpart_company_id, origin, source_system, source_document_ref, idempotency_key, command_name, posted_by, " +
        "correlation_id, status, chain_seq, prev_hash, row_hash, hash_version";

    private const string LineColumns =
        "company_id, document_id, line_no, ledger_id, line_type, account_id, posting_date, fiscal_year, fiscal_period, " +
        "reversed_line_no, tax_code_id, amount_tc, amount_lc, currency_lc, rate_lc, rate_lc_table, amount_gc, currency_gc, rate_gc, " +
        "rate_gc_table, amount_rc, currency_rc, rate_rc, rate_rc_table, rate_type, rate_date, ic_counterpart_company_id, line_text";

    /// <summary>A stored document: the hash input of the header (L1 columns) and the status, with its lines.</summary>
    public sealed record StoredDocument(
        Dictionary<string, object?> Header, string Status, long ChainSeq, byte[] PrevHash, byte[] RowHash,
        List<Dictionary<string, object?>> Lines);

    public static async Task<PostedDocument?> LoadDocumentAsync(TenantTransaction tx, Guid tenantId, Guid documentId, CancellationToken ct)
    {
        StoredDocument? doc = (await ReadDocumentsAsync(tx, tenantId, documentId, null, null, ct)).FirstOrDefault();
        if (doc is null) return null;
        Dictionary<string, object?> h = doc.Header;
        var lines = doc.Lines.Select(l => new PostedLine(
            (int)l["line_no"]!, null, (Guid)l["account_id"]!, (string)l["line_type"]!,
            (decimal)l["amount_tc"]!, (decimal)l["amount_lc"]!, (string)l["currency_lc"]!, (decimal)l["rate_lc"]!,
            (decimal)l["amount_gc"]!, (string)l["currency_gc"]!, (decimal)l["rate_gc"]!,
            (decimal?)l["amount_rc"], (string?)l["currency_rc"], (decimal?)l["rate_rc"],
            (string)l["rate_type"]!, (DateOnly)l["rate_date"]!)).ToList();
        return new PostedDocument(
            documentId, (Guid)h["company_id"]!, (Guid)h["ledger_id"]!, (string)h["document_type"]!, (string)h["document_number"]!,
            (long)h["sequence_no"]!, (int)h["fiscal_year"]!, (int)h["fiscal_period"]!, (DateOnly)h["document_date"]!,
            (DateOnly)h["posting_date"]!, (string)h["transaction_currency"]!, doc.Status, (bool)h["soft_close_override"]!,
            doc.ChainSeq, Canonical.FormatHex32(doc.RowHash), Canonical.FormatHex32(doc.PrevHash), (int)(long)h["hash_version"]!, lines);
    }

    /// <summary>Documents with their lines, in chain order. Either one document, or one ledger of one company.</summary>
    public static async Task<List<StoredDocument>> ReadDocumentsAsync(
        TenantTransaction tx, Guid tenantId, Guid? documentId, Guid? companyId, Guid? ledgerId, CancellationToken ct)
    {
        var docs = new List<StoredDocument>();
        var byKey = new Dictionary<(Guid, Guid), StoredDocument>();

        await using (var cmd = tx.Command(
            $"select {DocumentColumns} from core.journal_document where tenant_id = @t " +
            (documentId is null ? "and company_id = @c and ledger_id = @l " : "and document_id = @d ") +
            "order by chain_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            if (documentId is null)
            {
                cmd.Parameters.AddWithValue("c", companyId!.Value);
                cmd.Parameters.AddWithValue("l", ledgerId!.Value);
            }
            else cmd.Parameters.AddWithValue("d", documentId.Value);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var h = new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId,
                    ["company_id"] = r.GetGuid(0), ["document_id"] = r.GetGuid(1), ["ledger_id"] = r.GetGuid(2),
                    ["document_type"] = r.GetString(3), ["is_system_generated"] = r.GetBoolean(4), ["number_series_id"] = r.GetGuid(5),
                    ["fiscal_year"] = r.GetInt32(6), ["sequence_no"] = r.GetInt64(7), ["document_number"] = r.GetString(8),
                    ["fiscal_period"] = r.GetInt32(9), ["soft_close_override"] = r.GetBoolean(10),
                    ["migration_batch_id"] = r.IsDBNull(11) ? null : r.GetGuid(11),
                    ["detail_digest"] = r.IsDBNull(12) ? null : (byte[])r[12],
                    ["document_date"] = r.GetFieldValue<DateOnly>(13), ["posting_date"] = r.GetFieldValue<DateOnly>(14),
                    ["posted_at"] = DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc),
                    ["transaction_currency"] = r.GetString(16),
                    ["reversal_of_document_id"] = r.IsDBNull(17) ? null : r.GetGuid(17),
                    ["reversal_reason_code"] = r.IsDBNull(18) ? null : r.GetString(18),
                    ["auto_reversal_date"] = r.IsDBNull(19) ? null : r.GetFieldValue<DateOnly>(19),
                    ["ic_transaction_id"] = r.IsDBNull(20) ? null : r.GetGuid(20),
                    ["ic_counterpart_company_id"] = r.IsDBNull(21) ? null : r.GetGuid(21),
                    ["origin"] = r.GetString(22), ["source_system"] = r.GetString(23),
                    ["source_document_ref"] = r.IsDBNull(24) ? null : r.GetString(24),
                    ["idempotency_key"] = r.GetString(25), ["command_name"] = r.GetString(26), ["posted_by"] = r.GetString(27),
                    ["correlation_id"] = r.GetString(28),
                    ["hash_version"] = (long)r.GetInt16(33)
                };
                var doc = new StoredDocument(h, r.GetString(29), r.GetInt64(30), (byte[])r[31], (byte[])r[32], []);
                docs.Add(doc);
                byKey[((Guid)h["company_id"]!, (Guid)h["document_id"]!)] = doc;
            }
        }
        if (docs.Count == 0) return docs;

        await using (var cmd = tx.Command(
            $"select {LineColumns} from core.journal_line where tenant_id = @t " +
            (documentId is null ? "and company_id = @c and ledger_id = @l " : "and document_id = @d ") +
            "order by document_id, line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            if (documentId is null)
            {
                cmd.Parameters.AddWithValue("c", companyId!.Value);
                cmd.Parameters.AddWithValue("l", ledgerId!.Value);
            }
            else cmd.Parameters.AddWithValue("d", documentId.Value);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var l = new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = r.GetGuid(0), ["document_id"] = r.GetGuid(1), ["line_no"] = r.GetInt32(2),
                    ["ledger_id"] = r.GetGuid(3), ["line_type"] = r.GetString(4), ["account_id"] = r.GetGuid(5),
                    ["posting_date"] = r.GetFieldValue<DateOnly>(6), ["fiscal_year"] = r.GetInt32(7), ["fiscal_period"] = r.GetInt32(8),
                    ["reversed_line_no"] = r.IsDBNull(9) ? null : r.GetInt32(9),
                    ["tax_code_id"] = r.IsDBNull(10) ? null : r.GetGuid(10),
                    ["amount_tc"] = r.GetDecimal(11), ["amount_lc"] = r.GetDecimal(12), ["currency_lc"] = r.GetString(13),
                    ["rate_lc"] = r.GetDecimal(14), ["rate_lc_table"] = r.IsDBNull(15) ? null : r.GetDecimal(15),
                    ["amount_gc"] = r.GetDecimal(16), ["currency_gc"] = r.GetString(17), ["rate_gc"] = r.GetDecimal(18),
                    ["rate_gc_table"] = r.IsDBNull(19) ? null : r.GetDecimal(19),
                    ["amount_rc"] = r.IsDBNull(20) ? null : r.GetDecimal(20), ["currency_rc"] = r.IsDBNull(21) ? null : r.GetString(21),
                    ["rate_rc"] = r.IsDBNull(22) ? null : r.GetDecimal(22), ["rate_rc_table"] = r.IsDBNull(23) ? null : r.GetDecimal(23),
                    ["rate_type"] = r.GetString(24), ["rate_date"] = r.GetFieldValue<DateOnly>(25),
                    ["ic_counterpart_company_id"] = r.IsDBNull(26) ? null : r.GetGuid(26)
                };
                // line_text (column 27) is L2 and not part of the hash
                byKey[((Guid)l["company_id"]!, (Guid)l["document_id"]!)].Lines.Add(l);
            }
        }
        return docs;
    }
}
