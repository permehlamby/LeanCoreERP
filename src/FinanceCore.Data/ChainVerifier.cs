using FinanceCore.Core.Hashing;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data;

public sealed record VerificationProblem(string Code, long? ChainSeq, string Detail);

public sealed record VerificationResult(bool Ok, long DocumentsChecked, IReadOnlyList<VerificationProblem> Problems);

/// <summary>
/// chains:verify for the LEDGER chain (02, 9): recomputes every line hash and row hash from the stored columns and
/// checks the links, the gapless chain_seq, the head, the balance of each document and the continuity of the numbers.
/// The recomputation uses the application library only. The database never computes a hash (ADR-211).
/// </summary>
public sealed class ChainVerifier(NpgsqlDataSource source)
{
    /// <summary>chains:verify for the PERIOD_EVENT chain (04, 3.5): recomputes every row hash, checks the links, the gapless
    /// event_seq and the head, and that the status of every year and period is the to_status of its latest event.</summary>
    public async Task<VerificationResult> VerifyPeriodEventsAsync(Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var problems = new List<VerificationProblem>();
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);

        long headSeq = 0; byte[]? headHash = null; bool headFound = false;
        await using (var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'PERIOD_EVENT' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { headFound = true; headSeq = r.GetInt64(0); headHash = (byte[])r[1]; }
        }

        var events = new List<(long Seq, byte[] Prev, byte[] Row, int Version, Dictionary<string, object?> Body)>();
        await using (var cmd = tx.Command(
            "select event_seq, prev_hash, row_hash, hash_version, event_id, fiscal_year, period_no, event_type, from_status, to_status, " +
            "reason_code, reason_text, authorisation, document_id, event_at, event_by, correlation_id, idempotency_key " +
            "from core.period_event where tenant_id = @t and company_id = @c order by event_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var body = new Dictionary<string, object?>
                {
                    ["event_id"] = r.GetGuid(4), ["fiscal_year"] = (long)r.GetInt32(5),
                    ["period_no"] = r.IsDBNull(6) ? null : (long)r.GetInt32(6), ["event_type"] = r.GetString(7),
                    ["from_status"] = Str(8), ["to_status"] = r.GetString(9), ["reason_code"] = Str(10), ["reason_text"] = Str(11),
                    ["authorisation"] = Str(12), ["document_id"] = r.IsDBNull(13) ? null : r.GetGuid(13),
                    ["event_at"] = DateTime.SpecifyKind(r.GetDateTime(14), DateTimeKind.Utc), ["event_by"] = r.GetString(15),
                    ["correlation_id"] = r.GetString(16), ["idempotency_key"] = r.GetString(17)
                };
                events.Add((r.GetInt64(0), (byte[])r[1], (byte[])r[2], r.GetInt16(3), body));
            }
        }

        byte[] prev = LedgerHash2.Genesis(tenantId, companyId, PeriodEventHash2.ChainType, "");
        long expected = 1;
        foreach (var e in events)
        {
            if (e.Seq != expected) { problems.Add(new("CHAIN.SEQUENCE_GAP", e.Seq, $"expected event_seq {expected}")); expected = e.Seq; }
            if (!e.Prev.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.PREV_HASH_MISMATCH", e.Seq, "prev_hash is not the row_hash of the predecessor"));
            if (e.Version != PeriodEventHash2.Version) problems.Add(new("CHAIN.HASH_VERSION_UNSUPPORTED", e.Seq, $"hash_version {e.Version}"));
            else if (!PeriodEventHash2.RowHash(tenantId, companyId, e.Seq, e.Prev, e.Body).AsSpan().SequenceEqual(e.Row))
                problems.Add(new("CHAIN.ROW_HASH_MISMATCH", e.Seq, "recomputed row_hash differs from the stored one"));
            prev = e.Row;
            expected = e.Seq + 1;
        }

        if (headFound)
        {
            long newest = events.Count == 0 ? 0 : events[^1].Seq;
            if (headSeq != newest) problems.Add(new("CHAIN.HEAD_MISMATCH", headSeq, $"head sequence {headSeq}, newest event {newest}"));
            else if (!headHash!.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", headSeq, "head hash differs from the newest row_hash"));
        }
        else if (events.Count > 0) problems.Add(new("CHAIN.HEAD_MISSING", null, "no PERIOD_EVENT chain head"));

        // The status of a year or period equals the to_status of its latest event (04, 3.4). Year-level events
        // (period_no null) apply to the year, and to the periods that have no event of their own.
        var yearStatus = new Dictionary<int, string>();
        var periodStatus = new Dictionary<(int, int), string>();
        foreach (var e in events)
        {
            int year = (int)(long)e.Body["fiscal_year"]!;
            string to = (string)e.Body["to_status"]!;
            if (e.Body["period_no"] is null) yearStatus[year] = to;
            else periodStatus[(year, (int)(long)e.Body["period_no"]!)] = to;
        }
        await using (var cmd = tx.Command("select fiscal_year, status from core.fiscal_year where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!yearStatus.TryGetValue(r.GetInt32(0), out string? s) || s != r.GetString(1))
                    problems.Add(new("PERIOD.STATUS_MISMATCH", null, $"year {r.GetInt32(0)}: status {r.GetString(1)}, events say {s ?? "nothing"}"));
        }
        await using (var cmd = tx.Command("select fiscal_year, period_no, status from core.fiscal_period where tenant_id = @t and company_id = @c"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int y = r.GetInt32(0), p = r.GetInt32(1);
                string expectedStatus = periodStatus.TryGetValue((y, p), out string? ps) ? ps : yearStatus.GetValueOrDefault(y, "?");
                if (expectedStatus != r.GetString(2))
                    problems.Add(new("PERIOD.STATUS_MISMATCH", null, $"year {y} period {p}: status {r.GetString(2)}, events say {expectedStatus}"));
            }
        }

        return new VerificationResult(problems.Count == 0, events.Count, problems);
    }

    /// <summary>
    /// chains:verify for the CLEARING chain (03, 5.5 and 6.1): recomputes every row hash and items digest from the stored columns and the stored
    /// row hash of the difference document, checks the links, the gapless chain_seq and the head, that every clearing nets to zero and that its
    /// difference document is of the right type and date, and recomputes the S columns of every open item from the clearing items (the cache
    /// of 03, 4.3: cleared amounts, status, most recent clearing, never over-cleared).
    /// </summary>
    public async Task<VerificationResult> VerifyClearingsAsync(Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var problems = new List<VerificationProblem>();
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);

        long headSeq = 0; byte[]? headHash = null; bool headFound = false;
        await using (var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'CLEARING' and scope_key = ''"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { headFound = true; headSeq = r.GetInt64(0); headHash = (byte[])r[1]; }
        }

        var clearings = new List<(long Seq, byte[] Prev, byte[] Row, int Version, Guid Id, string Type, DateOnly Date, Guid? Document, Dictionary<string, object?> Body)>();
        await using (var cmd = tx.Command(
            "select chain_seq, prev_hash, row_hash, hash_version, clearing_id, clearing_type, clearing_date, fiscal_year, fiscal_period, cleared_at, account_id, " +
            "partner_id, document_id, reset_of_clearing_id, clearing_method, matching_ref, source_system, idempotency_key, command_name, cleared_by, correlation_id " +
            "from core.clearing where tenant_id = @t and company_id = @c order by chain_seq"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                var body = new Dictionary<string, object?>
                {
                    ["clearing_id"] = r.GetGuid(4), ["clearing_type"] = r.GetString(5), ["clearing_date"] = r.GetFieldValue<DateOnly>(6),
                    ["fiscal_year"] = (long)r.GetInt32(7), ["fiscal_period"] = (long)r.GetInt32(8),
                    ["cleared_at"] = DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc), ["account_id"] = r.GetGuid(10), ["partner_id"] = G(11),
                    ["document_id"] = G(12), ["reset_of_clearing_id"] = G(13), ["clearing_method"] = r.GetString(14), ["matching_ref"] = Str(15),
                    ["source_system"] = r.GetString(16), ["idempotency_key"] = r.GetString(17), ["command_name"] = r.GetString(18),
                    ["cleared_by"] = r.GetString(19), ["correlation_id"] = r.GetString(20)
                };
                clearings.Add((r.GetInt64(0), (byte[])r[1], (byte[])r[2], r.GetInt16(3), r.GetGuid(4), r.GetString(5), r.GetFieldValue<DateOnly>(6), G(12), body));
            }
        }

        var itemsOf = new Dictionary<Guid, List<Dictionary<string, object?>>>();
        await using (var cmd = tx.Command(
            "select clearing_id, item_no, clearing_date, open_item_document_id, open_item_line_no, currency_tc, cleared_amount_tc, cleared_amount_lc, " +
            "cleared_amount_gc, cleared_amount_rc from core.clearing_item where tenant_id = @t and company_id = @c order by clearing_id, item_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid id = r.GetGuid(0);
                if (!itemsOf.TryGetValue(id, out var list)) itemsOf[id] = list = [];
                list.Add(new Dictionary<string, object?>
                {
                    ["tenant_id"] = tenantId, ["company_id"] = companyId, ["clearing_id"] = id, ["item_no"] = (long)r.GetInt32(1),
                    ["clearing_date"] = r.GetFieldValue<DateOnly>(2), ["open_item_document_id"] = r.GetGuid(3), ["open_item_line_no"] = (long)r.GetInt32(4),
                    ["currency_tc"] = r.GetString(5), ["cleared_amount_tc"] = r.GetDecimal(6), ["cleared_amount_lc"] = r.GetDecimal(7),
                    ["cleared_amount_gc"] = r.GetDecimal(8), ["cleared_amount_rc"] = r.IsDBNull(9) ? null : r.GetDecimal(9)
                });
            }
        }

        var documents = new Dictionary<Guid, (byte[] RowHash, string Type, DateOnly PostingDate)>();
        await using (var cmd = tx.Command(
            "select document_id, row_hash, document_type, posting_date from core.journal_document where tenant_id = @t and company_id = @c " +
            "and document_id in (select document_id from core.clearing where tenant_id = @t and company_id = @c and document_id is not null)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) documents[r.GetGuid(0)] = ((byte[])r[1], r.GetString(2), r.GetFieldValue<DateOnly>(3));
        }

        byte[] prev = LedgerHash2.Genesis(tenantId, companyId, ClearingHash2.ChainType, "");
        long expected = 1;
        foreach (var c in clearings)
        {
            if (c.Seq != expected) { problems.Add(new("CHAIN.SEQUENCE_GAP", c.Seq, $"expected chain_seq {expected}")); expected = c.Seq; }
            if (!c.Prev.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.PREV_HASH_MISMATCH", c.Seq, "prev_hash is not the row_hash of the predecessor"));
            List<Dictionary<string, object?>> items = itemsOf.GetValueOrDefault(c.Id) ?? [];
            byte[]? linked = null;
            if (c.Document is { } docId)
            {
                if (!documents.TryGetValue(docId, out var doc)) problems.Add(new("CLEARING.DOCUMENT_MISSING", c.Seq, $"difference document {docId} does not exist"));
                else
                {
                    linked = doc.RowHash;
                    string wanted = c.Type == "CLEAR" ? "CLEARING" : "CLEARING_RESET";
                    if (doc.Type != wanted || doc.PostingDate != c.Date)
                        problems.Add(new("CLEARING.DOCUMENT_MISMATCH", c.Seq, $"the difference document is {doc.Type} of {doc.PostingDate}, expected {wanted} of {c.Date}"));
                }
            }
            if (c.Version != ClearingHash2.Version) problems.Add(new("CHAIN.HASH_VERSION_UNSUPPORTED", c.Seq, $"hash_version {c.Version}"));
            else if (items.Count == 0) problems.Add(new("CLEARING.NO_ITEMS", c.Seq, "no items"));
            else if (c.Document is not null && linked is null) { /* reported above: the hash cannot be recomputed without the document */ }
            else
            {
                byte[] digest = ClearingHash2.ItemsDigest(items.Select(i => (IReadOnlyDictionary<string, object?>)i).ToList());
                byte[] row = ClearingHash2.RowHash(tenantId, companyId, c.Seq, c.Prev, c.Body, digest, linked);
                if (!row.AsSpan().SequenceEqual(c.Row)) problems.Add(new("CHAIN.ROW_HASH_MISMATCH", c.Seq, "recomputed row_hash differs from the stored one"));
            }
            if (items.Count > 0)
            {
                decimal tc = items.Sum(i => (decimal)i["cleared_amount_tc"]!), lc = items.Sum(i => (decimal)i["cleared_amount_lc"]!);
                decimal gc = items.Sum(i => (decimal)i["cleared_amount_gc"]!), rc = items.Sum(i => (decimal?)i["cleared_amount_rc"] ?? 0m);
                bool oneCurrency = items.Select(i => (string)i["currency_tc"]!).Distinct().Count() == 1;
                if (lc != 0 || gc != 0 || rc != 0 || (oneCurrency && tc != 0))
                    problems.Add(new("CLEARING.NOT_BALANCED", c.Seq, $"the items net to tc {tc}, lc {lc}, gc {gc}, rc {rc}"));
            }
            prev = c.Row;
            expected = c.Seq + 1;
        }

        if (headFound)
        {
            long newest = clearings.Count == 0 ? 0 : clearings[^1].Seq;
            if (headSeq != newest) problems.Add(new("CHAIN.HEAD_MISMATCH", headSeq, $"head sequence {headSeq}, newest clearing {newest}"));
            else if (!headHash!.AsSpan().SequenceEqual(prev)) problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", headSeq, "head hash differs from the newest row_hash"));
        }
        else if (clearings.Count > 0) problems.Add(new("CHAIN.HEAD_MISSING", null, "no CLEARING chain head"));

        // The cache of every open item (03, 4.3): recomputed from the clearing items, never over-cleared
        var seqOf = clearings.ToDictionary(c => c.Id, c => c.Seq);
        var sums = new Dictionary<(Guid, int), (decimal Tc, decimal Lc, decimal Gc, decimal Rc, long LastSeq, Guid Last)>();
        foreach (var c in clearings)
            foreach (var i in itemsOf.GetValueOrDefault(c.Id) ?? [])
            {
                var key = ((Guid)i["open_item_document_id"]!, (int)(long)i["open_item_line_no"]!);
                var cur = sums.GetValueOrDefault(key);
                sums[key] = (cur.Tc + (decimal)i["cleared_amount_tc"]!, cur.Lc + (decimal)i["cleared_amount_lc"]!, cur.Gc + (decimal)i["cleared_amount_gc"]!,
                    cur.Rc + ((decimal?)i["cleared_amount_rc"] ?? 0m), Math.Max(cur.LastSeq, c.Seq), c.Seq >= cur.LastSeq ? c.Id : cur.Last);
            }
        await using (var cmd = tx.Command(
            "select o.document_id, o.line_no, o.clearing_status, o.cleared_amount_tc, o.cleared_amount_lc, o.cleared_amount_gc, o.cleared_amount_rc, o.last_clearing_id, " +
            "l.amount_tc, l.amount_lc, l.amount_gc, l.amount_rc from core.journal_line_open_item o " +
            "join core.journal_line l on l.tenant_id = o.tenant_id and l.company_id = o.company_id and l.document_id = o.document_id and l.line_no = o.line_no " +
            "where o.tenant_id = @t and o.company_id = @c order by o.document_id, o.line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetGuid(0), r.GetInt32(1));
                var sum = sums.GetValueOrDefault(key);
                decimal lineTc = r.GetDecimal(8), lineLc = r.GetDecimal(9), lineGc = r.GetDecimal(10); decimal? lineRc = r.IsDBNull(11) ? null : r.GetDecimal(11);
                string where = $"open item {key.Item1} line {key.Item2}";
                if (sum.Tc < Math.Min(0, lineTc) || sum.Tc > Math.Max(0, lineTc) || sum.Lc < Math.Min(0, lineLc) || sum.Lc > Math.Max(0, lineLc))
                    problems.Add(new("OPEN_ITEM.OVER_CLEARED", null, $"{where}: cleared tc {sum.Tc}, lc {sum.Lc} against the line tc {lineTc}, lc {lineLc}"));
                decimal? storedRc = r.IsDBNull(6) ? null : r.GetDecimal(6);
                if (r.GetDecimal(3) != sum.Tc || r.GetDecimal(4) != sum.Lc || r.GetDecimal(5) != sum.Gc || (storedRc ?? 0m) != sum.Rc)
                    problems.Add(new("OPEN_ITEM.CACHE_MISMATCH", null, $"{where}: stored cleared amounts differ from the clearing items"));
                string status = ClearingRules.StatusOf(new FinanceCore.Core.Posting.OpenLine(lineTc, lineLc, lineGc, lineRc, 0m, 0m, 0m, null), sum.Tc, sum.Lc, sum.Gc, sum.Rc);
                if (r.GetString(2) != status)
                    problems.Add(new("OPEN_ITEM.CACHE_MISMATCH", null, $"{where}: clearing_status {r.GetString(2)}, the items give {status}"));
                Guid? last = r.IsDBNull(7) ? null : r.GetGuid(7);
                Guid? wantedLast = sum.LastSeq == 0 ? null : sum.Last;
                if (last != wantedLast)
                    problems.Add(new("OPEN_ITEM.CACHE_MISMATCH", null, $"{where}: last_clearing_id is not the most recent clearing of the item"));
            }
        }

        return new VerificationResult(problems.Count == 0, clearings.Count, problems);
    }

    public async Task<VerificationResult> VerifyLedgerAsync(Guid tenantId, Guid companyId, Guid ledgerId, CancellationToken ct = default)
    {
        var problems = new List<VerificationProblem>();
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct);

        long headSeq = 0;
        byte[]? headHash = null;
        bool headFound = false;
        await using (var cmd = tx.Command(
            "select last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c and chain_type = 'LEDGER' and scope_key = @k"))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("c", companyId);
            cmd.Parameters.AddWithValue("k", ledgerId.ToString("D"));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { headFound = true; headSeq = r.GetInt64(0); headHash = (byte[])r[1]; }
        }
        if (!headFound) problems.Add(new("CHAIN.HEAD_MISSING", null, "no LEDGER chain head for the ledger"));

        var docs = await LedgerReader.ReadDocumentsAsync(tx, tenantId, null, companyId, ledgerId, ct);

        byte[] prev = LedgerHash2.Genesis(tenantId, companyId, "LEDGER", ledgerId.ToString("D"));
        long expectedSeq = 1;
        var lastNumber = new Dictionary<(Guid Series, int Year), long>();

        foreach (var doc in docs)
        {
            long seq = doc.ChainSeq;
            if (seq != expectedSeq)
            {
                problems.Add(new("CHAIN.SEQUENCE_GAP", seq, $"expected chain_seq {expectedSeq}"));
                expectedSeq = seq;
            }
            if (!doc.PrevHash.AsSpan().SequenceEqual(prev))
                problems.Add(new("CHAIN.PREV_HASH_MISMATCH", seq, "prev_hash is not the row_hash of the predecessor"));

            long version = (long)doc.Header["hash_version"]!;
            if (version != LedgerHash2.Version)
            {
                problems.Add(new("CHAIN.HASH_VERSION_UNSUPPORTED", seq, $"hash_version {version}"));
            }
            else if (doc.Lines.Count == 0)
            {
                problems.Add(new("CHAIN.DOCUMENT_WITHOUT_LINES", seq, "no lines"));
            }
            else
            {
                var lineHashes = doc.Lines.OrderBy(l => (int)l["line_no"]!)
                    .Select(l => LedgerHash2.LineHash(new HashedLine(l, OpenItem: doc.OpenItems.GetValueOrDefault((int)l["line_no"]!)))).ToList();
                byte[] expected = LedgerHash2.RowHash(doc.Header, seq, doc.PrevHash, LedgerHash2.LineDigest(lineHashes));
                if (!expected.AsSpan().SequenceEqual(doc.RowHash))
                    problems.Add(new("CHAIN.ROW_HASH_MISMATCH", seq, "recomputed row_hash differs from the stored one"));
            }

            if (doc.Lines.Count > 0)
            {
                decimal tc = doc.Lines.Sum(l => (decimal)l["amount_tc"]!);
                decimal lc = doc.Lines.Sum(l => (decimal)l["amount_lc"]!);
                decimal gc = doc.Lines.Sum(l => (decimal)l["amount_gc"]!);
                if (tc != 0 || lc != 0 || gc != 0)
                    problems.Add(new("DOCUMENT.NOT_BALANCED", seq, $"tc {tc}, lc {lc}, gc {gc}"));
                var rc = doc.Lines.Select(l => (decimal?)l["amount_rc"]).ToList();
                if (rc.Any(x => x is not null) && (rc.Any(x => x is null) || rc.Sum(x => x!.Value) != 0))
                    problems.Add(new("DOCUMENT.NOT_BALANCED", seq, "reporting currency"));
            }

            // Number continuity per series and fiscal year: gapless (ADR-007). The counter starts at start_number - 1,
            // and every document takes the next number, so within the stored documents the numbers run without a hole.
            var key = ((Guid)doc.Header["number_series_id"]!, (int)doc.Header["fiscal_year"]!);
            long number = (long)doc.Header["sequence_no"]!;
            if (lastNumber.TryGetValue(key, out long last) && number != last + 1)
                problems.Add(new("NUMBER.GAP", seq, $"series {key.Item1} year {key.Item2}: {last} then {number}"));
            lastNumber[key] = number;

            prev = doc.RowHash;
            expectedSeq = seq + 1;
        }

        if (headFound)
        {
            long newest = docs.Count == 0 ? 0 : docs[^1].ChainSeq;
            if (headSeq != newest)
                problems.Add(new("CHAIN.HEAD_MISMATCH", headSeq, $"head sequence {headSeq}, newest document {newest}"));
            else if (docs.Count > 0 && !headHash!.AsSpan().SequenceEqual(docs[^1].RowHash))
                problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", headSeq, "head hash differs from the newest row_hash"));
            else if (docs.Count == 0 && !headHash!.AsSpan().SequenceEqual(prev))
                problems.Add(new("CHAIN.HEAD_HASH_MISMATCH", 0, "head hash is not the genesis value"));
        }

        return new VerificationResult(problems.Count == 0, docs.Count, problems);
    }
}
