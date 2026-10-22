using FinanceCore.Core.Hashing;
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
                    .Select(l => LedgerHash2.LineHash(new HashedLine(l))).ToList();
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
