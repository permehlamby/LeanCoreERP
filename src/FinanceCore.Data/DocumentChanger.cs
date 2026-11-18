using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// journal-documents:change-open-fields (06, 5.4): changes the L2 fields of a posted document, compare-and-set, all or nothing,
/// one change_log row per changed field (02, 8). The first slice changes header_text and external_reference of the document,
/// line_text of a line and the open-item fields of a line (entity type LINE_OPEN_ITEM) except the payment terms and the bank account.
/// The typed dimensions (entity type LINE) and the free ones (LINE_DIMENSION, the sub_key is the type) change like a posting is checked (P4): the value must exist, be active, belong
/// to the type, lie within its validity at the posting date of the line and not be restricted to another company, and the rules of the account hold (a MANDATORY dimension is not cleared, a FORBIDDEN one not set). The payment terms and the bank account of an open item and the tax evidence answer with a LIMIT code. The exception of F2 (new tax evidence in a hard-closed period) is not built.
/// The database guard of migration 0013 accepts an L2 change only together with a matching change_log row of the same
/// transaction and never in a hard-closed period, so this command writes the log rows first.
/// Locks (06, 9): the document row and the lines it changes, the period row in share mode, outbox_head last. No chain head:
/// an L2 field is outside every hash and no chain node is written.
/// </summary>
public sealed class DocumentChanger(NpgsqlDataSource source)
{
    public const string CommandName = "journal-documents:change-open-fields";
    public const string Permission = "journal.change";
    public const string SoftClosedPermission = "change.soft_closed";
    public const int MaxChanges = 200;

    public async Task<CommandResult<ChangedDocument>> ChangeAsync(
        Guid tenantId, CommandContext ctx, ChangeOpenFieldsRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", Permission)));

        List<CommandError> shape = Validate(ctx, req);
        if (shape.Count > 0) return CommandResult<ChangedDocument>.Rejected(shape);

        byte[] fingerprint = OpenFieldRules.Fingerprint(req);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ChangeOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<ChangedDocument>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<ChangedDocument>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // P1: the shape of the request, without the database
    // ---------------------------------------------------------------------------------------------------------
    internal static List<CommandError> Validate(CommandContext ctx, ChangeOpenFieldsRequest req)
    {
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 100) Invalid("idempotency_key", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.CorrelationId) || ctx.CorrelationId.Length > 100) Invalid("correlation_id", "required, at most 100 characters");
        if (string.IsNullOrWhiteSpace(ctx.Actor) || ctx.Actor.Length > 100) Invalid("actor", "required, at most 100 characters");
        if (req.DocumentId == Guid.Empty) Invalid("document_id", "required");

        if (string.IsNullOrWhiteSpace(req.ReasonCode))
            errors.Add(JournalDocumentPoster.Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "reason_code"));
        else if (req.ReasonCode.Length > 50 || req.ReasonCode == OpenFieldRules.ReservedReason)
            Invalid("reason_code", req.ReasonCode == OpenFieldRules.ReservedReason ? "SYSTEM is reserved for the retention job" : "at most 50 characters");
        if (req.ReasonText?.Length > 500) Invalid("reason_text", "at most 500 characters");

        if (req.Changes is null || req.Changes.Count == 0)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "changes"));
            return errors;
        }
        if (req.Changes.Count > MaxChanges)
        {
            errors.Add(JournalDocumentPoster.Error("SCHEMA.BATCH_TOO_LARGE", "P1", "changes", ("limit", MaxChanges)));
            return errors;
        }

        var seen = new HashSet<(string, int?, string)>();
        for (int i = 0; i < req.Changes.Count; i++)
        {
            FieldChange c = req.Changes[i];
            string at = $"/changes/{i}";
            if (OpenFieldRules.EntityTypesNotBuilt.Contains(c.EntityType))
            {
                errors.Add(JournalDocumentPoster.Error("LIMIT.ENTITY_TYPE_NOT_BUILT", "P1", $"{at}/entity_type", ("entity_type", c.EntityType)));
                continue;
            }
            if (!OpenFieldRules.EntityTypes.Contains(c.EntityType))
            {
                Invalid($"{at}/entity_type", "DOCUMENT, LINE, LINE_OPEN_ITEM, LINE_DIMENSION or TAX_EVIDENCE");
                continue;
            }
            if (c.EntityType == "DOCUMENT" && c.LineNo is not null) Invalid($"{at}/line_no", "a header change has no line_no");
            if (c.EntityType != "DOCUMENT" && (c.LineNo is null || c.LineNo < 1)) Invalid($"{at}/line_no", $"required for {c.EntityType}, at least 1");
            if (string.IsNullOrWhiteSpace(c.FieldName)) { Invalid($"{at}/field_name", "required"); continue; }
            if (c.EntityType == "LINE_DIMENSION")
            {
                if (c.SubKey is null || !Guid.TryParseExact(c.SubKey, "D", out Guid subType) || subType == Guid.Empty) Invalid($"{at}/sub_key", "the id of the dimension type");
                if (c.FieldName != OpenFieldRules.FreeDimensionField) Invalid($"{at}/field_name", OpenFieldRules.FreeDimensionField);
            }
            else if (c.EntityType == "TAX_EVIDENCE")
            {
                // an evidence row is added, never changed (08, 5.3): the sub_key is its number, the value is given, the old value is not
                if (c.SubKey is null || !int.TryParse(c.SubKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int no) || no < 1
                    || no.ToString(System.Globalization.CultureInfo.InvariantCulture) != c.SubKey)
                    Invalid($"{at}/sub_key", "the evidence_no, a number from 1");
                if (c.NewValue is null) Invalid($"{at}/new_value", "required: evidence is added, not cleared");
                if (c.FieldName == "evidence_type" && c.NewValue is not null && !OpenFieldRules.EvidenceTypes.Contains(c.NewValue))
                    errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", $"{at}/new_value", ("allowed_values", OpenFieldRules.EvidenceTypes)));
            }
            else if (c.SubKey is not null) Invalid($"{at}/sub_key", "only a LINE_DIMENSION or TAX_EVIDENCE change has a sub_key");
            if (!seen.Add((c.EntityType, c.LineNo, c.FieldName + "|" + c.SubKey))) Invalid($"{at}/field_name", "the same field twice in one command");

            // A text field cannot be an empty string: null clears it. A value that does not fit its column or its kind (a date, an amount)
            // is rejected here, not by the database.
            if (OpenFieldRules.KindOf(c.EntityType, c.FieldName) is { } kind)
            {
                OpenFieldRules.Canonicalise(kind, c.NewValue, out string? reason);
                if (reason is not null) Invalid($"{at}/new_value", reason);
            }
        }
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------
    private async Task<CommandResult<ChangedDocument>> ChangeOnceAsync(
        Guid tenantId, CommandContext ctx, ChangeOpenFieldsRequest req, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);

        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint))
                return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            ChangedDocument? replay = await ReadLoggedAsync(tx, tenantId, req.DocumentId, ctx.IdempotencyKey, ct);
            return replay is null
                ? CommandResult<ChangedDocument>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                : CommandResult<ChangedDocument>.Ok(replay, earlier.CommandLogId, replay: true);
        }

        // The new values in canonical text form (a date as yyyy-MM-dd, an amount with four fraction digits). Validate has accepted them.
        var changes = req.Changes.Select(c => OpenFieldRules.KindOf(c.EntityType, c.FieldName) is { } k
            ? c with { NewValue = OpenFieldRules.Canonicalise(k, c.NewValue, out _), SubKey = c.SubKey?.ToLowerInvariant() } : c).ToList();

        // P2 and P7: the document row is locked, so what is compared below is what is changed.
        Header? header = await LockHeaderAsync(tx, tenantId, req.DocumentId, ct);
        if (header is null)
            return Reject(JournalDocumentPoster.Error("DOCUMENT.NOT_FOUND", "P2", "document_id"));

        int[] lineNos = changes.Where(c => c.EntityType is "LINE" or "LINE_DIMENSION" or "TAX_EVIDENCE").Select(c => c.LineNo!.Value).Distinct().Order().ToArray();
        Dictionary<int, Dictionary<string, string?>> lines = await LockLinesAsync(tx, tenantId, header.CompanyId, req.DocumentId, lineNos, ct);
        Dictionary<(int, Guid), string?> freeRows = await LockFreeDimensionsAsync(tx, tenantId, header.CompanyId, req.DocumentId, lineNos, ct);
        // All lines named by any change must exist, whether the change is on the line or on its open item
        int[] namedLines = changes.Where(c => c.EntityType != "DOCUMENT").Select(c => c.LineNo!.Value).Distinct().Order().ToArray();
        HashSet<int> existingLines = await ReadLineNosAsync(tx, tenantId, header.CompanyId, req.DocumentId, namedLines, ct);
        int[] openItemLines = changes.Where(c => c.EntityType == "LINE_OPEN_ITEM").Select(c => c.LineNo!.Value).Distinct().Order().ToArray();
        Dictionary<int, Dictionary<string, string?>> openItems = await LockOpenItemsAsync(tx, tenantId, header.CompanyId, req.DocumentId, openItemLines, ct);
        int tcScale = await ReadMinorUnitsAsync(tx, tenantId, header.CompanyId, req.DocumentId, ct);

        // P3: the period of the document header. OPEN allows, SOFT_CLOSED needs change.soft_closed, HARD_CLOSED is L3.
        string? periodStatus = await LockPeriodAsync(tx, tenantId, header, ct);
        var errors = new List<CommandError>();
        // Evidence is added in every period status (08, 5.3, a controlled exception to L3): a command of evidence only is not stopped by the period.
        bool evidenceOnly = changes.All(c => c.EntityType == "TAX_EVIDENCE");
        if (periodStatus is null)
            errors.Add(JournalDocumentPoster.Error("PERIOD.NOT_FOUND", "P3", "document_id", ("fiscal_year", header.FiscalYear), ("period", header.FiscalPeriod)));
        else if (evidenceOnly) { }
        else if (periodStatus == "HARD_CLOSED")
            errors.Add(JournalDocumentPoster.Error("LOCK.FIELD_LOCKED_L3", "P3", "document_id", ("fiscal_year", header.FiscalYear), ("period", header.FiscalPeriod)));
        else if (periodStatus == "SOFT_CLOSED" && !ctx.Permissions.Contains(SoftClosedPermission))
            errors.Add(JournalDocumentPoster.Error("PERIOD.SOFT_CLOSED_NOT_AUTHORISED", "P3", "document_id",
                ("fiscal_year", header.FiscalYear), ("period", header.FiscalPeriod), ("permission", SoftClosedPermission)));

        // P2: which fields can change at all
        HashSet<string> documentColumns = await ReadColumnsAsync(tx, "journal_document", ct);
        HashSet<string> lineColumns = await ReadColumnsAsync(tx, "journal_line", ct);
        HashSet<string> openItemColumns = await ReadColumnsAsync(tx, "journal_line_open_item", ct);
        for (int i = 0; i < changes.Count; i++)
        {
            FieldChange c = changes[i];
            string path = $"/changes/{i}/field_name";
            if (c.EntityType == "DOCUMENT")
            {
                if (OpenFieldRules.HeaderFields.ContainsKey(c.FieldName)) continue;
                errors.Add(documentColumns.Contains(c.FieldName)
                    ? JournalDocumentPoster.Error("LOCK.FIELD_LOCKED_L1", "P2", path, ("field_name", c.FieldName))
                    : JournalDocumentPoster.Error("LOCK.FIELD_UNKNOWN", "P2", path, ("field_name", c.FieldName)));
            }
            else
            {
                bool open = c.EntityType == "LINE_OPEN_ITEM";
                if (OpenFieldRules.KindOf(c.EntityType, c.FieldName) is null)
                {
                    if (open ? OpenFieldRules.OpenItemFieldsNotBuilt.Contains(c.FieldName) : OpenFieldRules.LineFieldsNotBuilt.Contains(c.FieldName))
                        errors.Add(JournalDocumentPoster.Error("LIMIT.FIELD_NOT_BUILT", "P2", path, ("field_name", c.FieldName)));
                    else
                        errors.Add((open ? openItemColumns : lineColumns).Contains(c.FieldName)
                            ? JournalDocumentPoster.Error("LOCK.FIELD_LOCKED_L1", "P2", path, ("field_name", c.FieldName))
                            : JournalDocumentPoster.Error("LOCK.FIELD_UNKNOWN", "P2", path, ("field_name", c.FieldName)));
                }
                if (!existingLines.Contains(c.LineNo!.Value))
                    errors.Add(JournalDocumentPoster.Error("DOCUMENT.LINE_NOT_FOUND", "P2", $"/changes/{i}/line_no", ("line_no", c.LineNo)));
                else if (open && !openItems.ContainsKey(c.LineNo!.Value))
                    errors.Add(JournalDocumentPoster.Error("DOCUMENT.OPEN_ITEM_NOT_FOUND", "P2", $"/changes/{i}/line_no", ("line_no", c.LineNo)));
                if (open && c.FieldName == "discount_amount_tc" && c.NewValue is not null
                    && decimal.Parse(c.NewValue, System.Globalization.CultureInfo.InvariantCulture) is var da && Math.Round(da, tcScale) != da)
                    errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", $"/changes/{i}/new_value",
                        ("reason", $"the document currency has {tcScale} fraction digits")));
            }
        }
        // The references and the mandatory fields of an open item (06, 5.4 P4): payment terms that exist and are active, and the due date of a
        // control account stays
        for (int i = 0; i < changes.Count; i++)
        {
            FieldChange c = changes[i];
            if (c.EntityType != "LINE_OPEN_ITEM" || !openItems.ContainsKey(c.LineNo ?? 0)) continue;
            if (c.FieldName == "payment_terms_id" && c.NewValue is not null && !await TermsActiveAsync(tx, tenantId, Guid.Parse(c.NewValue), ct))
                errors.Add(JournalDocumentPoster.Error("CONFIG.INVALID_REFERENCE", "P2", $"/changes/{i}/new_value", ("payment_terms_id", c.NewValue)));
            if (c.FieldName == "due_date" && c.NewValue is null && await IsControlLineAsync(tx, tenantId, header.CompanyId, req.DocumentId, c.LineNo!.Value, ct))
                errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.DUE_DATE_REQUIRED", "P4", $"/changes/{i}/new_value", ("line_no", c.LineNo)));
        }
        errors.AddRange(await CheckDimensionChangesAsync(tx, tenantId, header.CompanyId, req.DocumentId, changes, ct));

        // Evidence: the line is a tax line, a row that exists is never changed, and the numbers are the next ones of the line
        if (changes.Any(c => c.EntityType == "TAX_EVIDENCE"))
        {
            var evidence = await ReadEvidenceAsync(tx, tenantId, header.CompanyId, req.DocumentId, ct);
            foreach (var byLine in changes.Select((c, i) => (c, i)).Where(x => x.c.EntityType == "TAX_EVIDENCE" && existingLines.Contains(x.c.LineNo!.Value)).GroupBy(x => x.c.LineNo!.Value))
            {
                if (!evidence.TaxLines.Contains(byLine.Key))
                {
                    errors.Add(JournalDocumentPoster.Error("DOCUMENT.TAX_LINE_NOT_FOUND", "P2", $"/changes/{byLine.First().i}/line_no", ("line_no", byLine.Key)));
                    continue;
                }
                int max = evidence.MaxNo.GetValueOrDefault(byLine.Key);
                var numbers = byLine.Select(x => int.Parse(x.c.SubKey!, System.Globalization.CultureInfo.InvariantCulture)).Where(n => n > max).Distinct().Order().ToArray();
                foreach (var (c, i) in byLine)
                    if (int.Parse(c.SubKey!, System.Globalization.CultureInfo.InvariantCulture) <= max)
                        errors.Add(JournalDocumentPoster.Error("LOCK.FIELD_LOCKED_L1", "P2", $"/changes/{i}/sub_key", ("field_name", c.FieldName), ("reason", "evidence is append-only")));
                // a new row has its type and its reference
                foreach (var row in byLine.Where(x => int.Parse(x.c.SubKey!, System.Globalization.CultureInfo.InvariantCulture) > max).GroupBy(x => x.c.SubKey))
                    foreach (string required in new[] { "evidence_type", "reference" })
                        if (row.All(x => x.c.FieldName != required))
                            errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", $"/changes/{row.First().i}/field_name", ("reason", $"evidence {row.Key} of line {byLine.Key} needs {required}")));
                if (!numbers.SequenceEqual(Enumerable.Range(max + 1, numbers.Length)))
                    errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P2", $"/changes/{byLine.First().i}/sub_key", ("reason", $"the next evidence_no of line {byLine.Key} is {max + 1}")));
            }
        }
        if (errors.Count > 0) return CommandResult<ChangedDocument>.Rejected(errors);

        // P6 under the locks: compare and set. All mismatches are reported, none is applied.
        var effective = new List<FieldChange>();
        var warnings = new List<CommandError>();
        for (int i = 0; i < changes.Count; i++)
        {
            FieldChange c = changes[i];
            string? current = Current(header, lines, openItems, freeRows, c);
            if (current != c.ExpectedOldValue)
            {
                errors.Add(JournalDocumentPoster.Error("CONFLICT.EXPECTED_VALUE_MISMATCH", "P6", $"/changes/{i}/expected_old_value",
                    ("field_name", c.FieldName), ("line_no", c.LineNo), ("current_value", current)));
                continue;
            }
            if (c.ExpectedOldValue == c.NewValue)
                warnings.Add(JournalDocumentPoster.Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", $"/changes/{i}", ("field_name", c.FieldName)) with { Severity = "WARNING" });
            else
                effective.Add(c);
        }
        if (errors.Count > 0) return CommandResult<ChangedDocument>.Rejected(errors);
        if (effective.Count == 0)
            return Reject(JournalDocumentPoster.Error("SCHEMA.NO_EFFECTIVE_CHANGE", "P6", "changes"));

        // P7: the log rows first, because the guard of the table asks for them, then the new values
        Guid changeSetId = Guid.NewGuid();
        foreach (FieldChange c in effective)
            await InsertLogAsync(tx, tenantId, header.CompanyId, req, ctx, changeSetId, c, periodStatus!, ct);
        foreach (FieldChange c in effective.Where(c => c.EntityType == "DOCUMENT"))
            await UpdateAsync(tx, "journal_document", c.FieldName, c.NewValue, tenantId, req.DocumentId, null, ct);
        foreach (FieldChange c in effective.Where(c => c.EntityType == "LINE"))
            await UpdateAsync(tx, "journal_line", c.FieldName, c.NewValue, tenantId, req.DocumentId, c.LineNo, ct);
        foreach (FieldChange c in effective.Where(c => c.EntityType == "LINE_DIMENSION"))
            await ApplyFreeDimensionAsync(tx, tenantId, header.CompanyId, req.DocumentId, c, freeRows.ContainsKey((c.LineNo!.Value, Guid.Parse(c.SubKey!))), ct);
        foreach (FieldChange c in effective.Where(c => c.EntityType == "LINE_OPEN_ITEM"))
            await UpdateAsync(tx, "journal_line_open_item", c.FieldName, c.NewValue, tenantId, req.DocumentId, c.LineNo, ct);
        var addedEvidence = new List<(int LineNo, int EvidenceNo)>();
        foreach (var row in effective.Where(c => c.EntityType == "TAX_EVIDENCE").GroupBy(c => (LineNo: c.LineNo!.Value, No: int.Parse(c.SubKey!, System.Globalization.CultureInfo.InvariantCulture))).OrderBy(g => g.Key.LineNo).ThenBy(g => g.Key.No))
        {
            await InsertEvidenceAsync(tx, tenantId, header.CompanyId, req.DocumentId, row.Key.LineNo, row.Key.No, row.ToDictionary(c => c.FieldName, c => c.NewValue), ct);
            addedEvidence.Add((row.Key.LineNo, row.Key.No));
        }

        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, header.CompanyId, ctx, CommandName, fingerprint, "DOCUMENT", req.DocumentId, ct);
        DateTime now = DateTime.UtcNow;
        await CommandSupport.WriteOutboxAsync(tx, tenantId, header.CompanyId, ctx, "ledger.line_changed", "DOCUMENT",
            $"{header.CompanyId:D}/{req.DocumentId:D}", "CHANGED", new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: true, null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["document_id"] = req.DocumentId, ["change_set_id"] = changeSetId,
                ["changes"] = effective.Select(c => new Dictionary<string, object?>
                    { ["entity_type"] = c.EntityType, ["line_no"] = c.LineNo, ["field_name"] = c.FieldName, ["sub_key"] = c.SubKey }).ToArray()
            }), ct);

        foreach (var (evidenceLine, evidenceNo) in addedEvidence)
            await CommandSupport.WriteOutboxAsync(tx, tenantId, header.CompanyId, ctx, "vat.evidence_added", "DOCUMENT",
                $"{header.CompanyId:D}/{req.DocumentId:D}", "EVIDENCE_ADDED", new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc), isAudit: true, null,
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["document_id"] = req.DocumentId, ["line_no"] = evidenceLine, ["evidence_no"] = evidenceNo }), ct);

        await tx.CommitAsync(ct);
        var changed = effective.Select(c => new ChangedField(c.EntityType, c.LineNo, c.FieldName, c.ExpectedOldValue, c.NewValue, c.SubKey)).ToList();
        return CommandResult<ChangedDocument>.Ok(new ChangedDocument(req.DocumentId, changeSetId, periodStatus!, changed), logId, warnings);
    }

    // ---------------------------------------------------------------------------------------------------------
    private sealed record Header(Guid CompanyId, int FiscalYear, int FiscalPeriod, string? HeaderText, string? ExternalReference);

    private static string? Current(
        Header header, Dictionary<int, Dictionary<string, string?>> lines, Dictionary<int, Dictionary<string, string?>> openItems, Dictionary<(int, Guid), string?> freeRows, FieldChange c) =>
        c.EntityType switch
        {
            "DOCUMENT" => c.FieldName switch { "header_text" => header.HeaderText, _ => header.ExternalReference },
            "LINE" => lines[c.LineNo!.Value][c.FieldName],
            "LINE_DIMENSION" => freeRows.GetValueOrDefault((c.LineNo!.Value, Guid.Parse(c.SubKey!))),
            "TAX_EVIDENCE" => null,
            _ => openItems[c.LineNo!.Value][c.FieldName]
        };

    private static async Task<Header?> LockHeaderAsync(TenantTransaction tx, Guid tenantId, Guid documentId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select company_id, fiscal_year, fiscal_period, header_text, external_reference from core.journal_document " +
            "where tenant_id = @t and document_id = @d for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Header(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    /// <summary>The changeable columns of the named lines as text (line_text and the four typed dimensions), locked.</summary>
    private static async Task<Dictionary<int, Dictionary<string, string?>>> LockLinesAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int[] lineNos, CancellationToken ct)
    {
        var lines = new Dictionary<int, Dictionary<string, string?>>();
        if (lineNos.Length == 0) return lines;
        await using var cmd = tx.Command(
            "select line_no, line_text, cost_center_id::text, project_id::text, profit_center_id::text, business_unit_id::text from core.journal_line " +
            "where tenant_id = @t and company_id = @c and document_id = @d and line_no = any(@n) order by line_no for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = lineNos });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines[r.GetInt32(0)] = new Dictionary<string, string?>
            {
                ["line_text"] = r.IsDBNull(1) ? null : r.GetString(1), ["cost_center_id"] = r.IsDBNull(2) ? null : r.GetString(2),
                ["project_id"] = r.IsDBNull(3) ? null : r.GetString(3), ["profit_center_id"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["business_unit_id"] = r.IsDBNull(5) ? null : r.GetString(5)
            };
        return lines;
    }

    /// <summary>The free dimension rows of the named lines, locked: (line, type) to the value as text, null for a cleared value. A row that does not exist is not in the map.</summary>
    private static async Task<Dictionary<(int, Guid), string?>> LockFreeDimensionsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int[] lineNos, CancellationToken ct)
    {
        var rows = new Dictionary<(int, Guid), string?>();
        if (lineNos.Length == 0) return rows;
        await using var cmd = tx.Command(
            "select line_no, dimension_type_id, dimension_value_id::text from core.journal_line_dimension " +
            "where tenant_id = @t and company_id = @c and document_id = @d and line_no = any(@n) order by line_no, dimension_type_id for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = lineNos });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows[(r.GetInt32(0), r.GetGuid(1))] = r.IsDBNull(2) ? null : r.GetString(2);
        return rows;
    }

    /// <summary>A change of a dimension is checked like a posting (06, 5.4 P4): the type is the typed column's or a FREE type, the value is usable at the posting date of the
    /// line, and the rules of the account hold. Every error carries the path of the change.</summary>
    private static async Task<List<CommandError>> CheckDimensionChangesAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, List<FieldChange> changes, CancellationToken ct)
    {
        var errors = new List<CommandError>();
        var targets = changes.Select((c, i) => (c, i)).Where(x => x.c.EntityType == "LINE_DIMENSION" || x.c.EntityType == "LINE" && OpenFieldRules.TypedDimensionFields.ContainsKey(x.c.FieldName)).ToList();
        if (targets.Count == 0) return errors;

        var types = await DimensionChecks.ReadTypesAsync(tx, tenantId, ct);
        var standard = types.Values.Where(t => t.StandardRole is not null).ToDictionary(t => t.StandardRole!, t => t.TypeId);
        int[] lineNos = targets.Select(x => x.c.LineNo!.Value).Distinct().ToArray();
        var lines = new Dictionary<int, (Guid Account, DateOnly PostingDate)>();
        await using (var cmd = tx.Command("select line_no, account_id, posting_date from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d and line_no = any(@n)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = lineNos });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines[r.GetInt32(0)] = (r.GetGuid(1), r.GetFieldValue<DateOnly>(2));
        }
        var values = await DimensionChecks.ReadValuesAsync(tx, tenantId,
            targets.Where(x => x.c.NewValue is not null).Select(x => Guid.Parse(x.c.NewValue!)).Distinct().ToArray(), ct);
        var rules = await DimensionChecks.ReadRulesAsync(tx, tenantId, companyId, lines.Values.Select(l => l.Account).Distinct().ToArray(), ct);

        foreach (var (c, i) in targets)
        {
            if (!lines.TryGetValue(c.LineNo!.Value, out var line)) continue;   // DOCUMENT.LINE_NOT_FOUND is reported by the caller
            Guid? typeId;
            if (c.EntityType == "LINE") typeId = standard.GetValueOrDefault(OpenFieldRules.TypedDimensionFields[c.FieldName]);
            else
            {
                typeId = Guid.Parse(c.SubKey!);
                if (!types.TryGetValue(typeId.Value, out var type)) { errors.Add(DimensionChecks.Invalid($"/changes/{i}/sub_key", "TYPE_NOT_FOUND", ("dimension_type_id", typeId))); continue; }
                if (type.Kind != "FREE") { errors.Add(DimensionChecks.Invalid($"/changes/{i}/sub_key", "TYPE_NOT_FREE", ("dimension_type_id", typeId), ("type_code", type.Code))); continue; }
            }
            string path = $"/changes/{i}/new_value";
            if (c.NewValue is { } text)
            {
                var given = new DimensionChecks.Given(path, typeId, Guid.Parse(text), c.EntityType == "LINE_DIMENSION");
                if (DimensionChecks.CheckValue(given, values.GetValueOrDefault(given.ValueId), types, line.PostingDate, companyId) is { } error) errors.Add(error);
            }
            if (typeId is { } tid && rules.TryGetValue(line.Account, out var byType) && byType.TryGetValue(tid, out string? rule) && types.TryGetValue(tid, out var ruleType) && ruleType.Active)
            {
                if (rule == "MANDATORY" && c.NewValue is null)
                    errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.DIMENSION_REQUIRED", "P4", path, ("account_id", line.Account), ("dimension_type_id", tid), ("type_code", ruleType.Code)));
                else if (rule == "FORBIDDEN" && c.NewValue is not null)
                    errors.Add(JournalDocumentPoster.Error("ACCOUNT_RULE.DIMENSION_FORBIDDEN", "P4", path, ("account_id", line.Account), ("dimension_type_id", tid), ("type_code", ruleType.Code)));
            }
        }
        return errors;
    }

    /// <summary>The value of a free dimension: the row exists, so its value changes (a cleared value is a null, the row stays), or it does not, so it is inserted.</summary>
    private static async Task ApplyFreeDimensionAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, FieldChange c, bool rowExists, CancellationToken ct)
    {
        await using var cmd = tx.Command(rowExists
            ? "update core.journal_line_dimension set dimension_value_id = @v where tenant_id = @t and company_id = @c and document_id = @d and line_no = @n and dimension_type_id = @ty"
            : "insert into core.journal_line_dimension (tenant_id, company_id, document_id, line_no, dimension_type_id, dimension_value_id) values (@t, @c, @d, @n, @ty, @v)");
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Uuid) { Value = c.NewValue is null ? DBNull.Value : Guid.Parse(c.NewValue) });
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("n", c.LineNo!.Value); cmd.Parameters.AddWithValue("ty", Guid.Parse(c.SubKey!));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("journal_line_dimension: the change of a free dimension changed no row");
    }

    private sealed record EvidenceState(HashSet<int> TaxLines, Dictionary<int, int> MaxNo);

    /// <summary>The tax lines of the document and the highest evidence number of each.</summary>
    private static async Task<EvidenceState> ReadEvidenceAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, CancellationToken ct)
    {
        var state = new EvidenceState([], []);
        await using var cmd = tx.Command(
            "select t.line_no, coalesce((select max(e.evidence_no) from core.journal_line_tax_evidence e where e.tenant_id = t.tenant_id and e.company_id = t.company_id " +
            "and e.document_id = t.document_id and e.line_no = t.line_no), 0) from core.journal_line_tax t where t.tenant_id = @t and t.company_id = @c and t.document_id = @d");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { state.TaxLines.Add(r.GetInt32(0)); state.MaxNo[r.GetInt32(0)] = r.GetInt32(1); }
        return state;
    }

    private static async Task InsertEvidenceAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int lineNo, int evidenceNo, Dictionary<string, string?> fields, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.journal_line_tax_evidence (tenant_id, company_id, document_id, line_no, evidence_no, evidence_type, reference, evidence_date, note) " +
            "values (@t, @c, @d, @l, @n, @type, @ref, @date, @note)");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("l", lineNo); cmd.Parameters.AddWithValue("n", evidenceNo);
        cmd.Parameters.AddWithValue("type", fields["evidence_type"]!); cmd.Parameters.AddWithValue("ref", fields["reference"]!);
        cmd.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = fields.GetValueOrDefault("evidence_date") is { } d ? DateOnly.Parse(d) : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)fields.GetValueOrDefault("note") ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<int>> ReadLineNosAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int[] lineNos, CancellationToken ct)
    {
        var found = new HashSet<int>();
        if (lineNos.Length == 0) return found;
        await using var cmd = tx.Command(
            "select line_no from core.journal_line where tenant_id = @t and company_id = @c and document_id = @d and line_no = any(@n)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = lineNos });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add(r.GetInt32(0));
        return found;
    }

    /// <summary>The open item rows of the named lines, locked, with every changeable column as canonical text (a date as yyyy-MM-dd, an amount with four fraction digits).</summary>
    private static async Task<Dictionary<int, Dictionary<string, string?>>> LockOpenItemsAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int[] lineNos, CancellationToken ct)
    {
        var rows = new Dictionary<int, Dictionary<string, string?>>();
        if (lineNos.Length == 0) return rows;
        string[] columns = OpenFieldRules.OpenItemFields.Keys.ToArray();
        await using var cmd = tx.Command(
            "select line_no, " + string.Join(", ", columns.Select(c => c + "::text")) + " from core.journal_line_open_item " +
            "where tenant_id = @t and company_id = @c and document_id = @d and line_no = any(@n) order by line_no for update");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = lineNos });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new Dictionary<string, string?>();
            for (int i = 0; i < columns.Length; i++) row[columns[i]] = r.IsDBNull(i + 1) ? null : r.GetString(i + 1);
            rows[r.GetInt32(0)] = row;
        }
        return rows;
    }

    /// <summary>Fraction digits of the transaction currency of the document, for an amount that is stated in it.</summary>
    private static async Task<int> ReadMinorUnitsAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select c.minor_units from core.journal_document d join platform.ref_currency c on c.currency_code = d.transaction_currency " +
            "where d.tenant_id = @t and d.company_id = @c and d.document_id = @d");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 4);
    }

    private static async Task<bool> TermsActiveAsync(TenantTransaction tx, Guid tenantId, Guid terms, CancellationToken ct)
    {
        await using var cmd = tx.Command("select 1 from core.payment_terms where tenant_id = @t and payment_terms_id = @p and active");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("p", terms);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IsControlLineAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid documentId, int lineNo, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select a.open_item_mode from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id " +
            "where l.tenant_id = @t and l.company_id = @c and l.document_id = @d and l.line_no = @n");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("d", documentId); cmd.Parameters.AddWithValue("n", lineNo);
        return await cmd.ExecuteScalarAsync(ct) as string == "CONTROL";
    }

    private static async Task<string?> LockPeriodAsync(TenantTransaction tx, Guid tenantId, Header header, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "select status from core.fiscal_period where tenant_id = @t and company_id = @c and fiscal_year = @y and period_no = @p for share");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", header.CompanyId);
        cmd.Parameters.AddWithValue("y", header.FiscalYear);
        cmd.Parameters.AddWithValue("p", header.FiscalPeriod);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(TenantTransaction tx, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>();
        await using var cmd = tx.Command("select column_name from information_schema.columns where table_schema = 'core' and table_name = @n");
        cmd.Parameters.AddWithValue("n", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) columns.Add(r.GetString(0));
        return columns;
    }

    /// <summary>changed_at is the time of the transaction (now()), because the guard of the table compares it with transaction_timestamp().</summary>
    private static async Task InsertLogAsync(
        TenantTransaction tx, Guid tenantId, Guid companyId, ChangeOpenFieldsRequest req, CommandContext ctx, Guid changeSetId,
        FieldChange c, string periodStatus, CancellationToken ct)
    {
        await using var cmd = tx.Command(
            "insert into core.change_log (tenant_id, change_set_id, company_id, entity_type, document_id, line_no, sub_key, field_name, value_type, " +
            "old_value, new_value, lock_class, period_status, reason_code, reason_text, changed_by, changed_at, correlation_id, idempotency_key) " +
            "values (@t, @cs, @c, @et, @d, @ln, @sk, @f, @vt, @old, @new, 'L2', @ps, @rc, @rt, @by, now(), @corr, @key)");
        cmd.Parameters.Add(new NpgsqlParameter("sk", NpgsqlDbType.Text) { Value = (object?)c.SubKey ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("cs", changeSetId);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("et", c.EntityType);
        cmd.Parameters.AddWithValue("d", req.DocumentId);
        cmd.Parameters.Add(new NpgsqlParameter("ln", NpgsqlDbType.Integer) { Value = (object?)c.LineNo ?? DBNull.Value });
        cmd.Parameters.AddWithValue("f", c.FieldName);
        cmd.Parameters.AddWithValue("vt", OpenFieldRules.KindOf(c.EntityType, c.FieldName)!.Kind switch
        {
            OpenFieldRules.Kind.Date => "DATE", OpenFieldRules.Kind.Amount => "AMOUNT", OpenFieldRules.Kind.Code => "CODE", OpenFieldRules.Kind.Id => "ID", _ => "TEXT"
        });
        cmd.Parameters.Add(new NpgsqlParameter("old", NpgsqlDbType.Text) { Value = (object?)c.ExpectedOldValue ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("new", NpgsqlDbType.Text) { Value = (object?)c.NewValue ?? DBNull.Value });
        cmd.Parameters.AddWithValue("ps", periodStatus);
        cmd.Parameters.AddWithValue("rc", req.ReasonCode);
        cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Text) { Value = (object?)req.ReasonText ?? DBNull.Value });
        cmd.Parameters.AddWithValue("by", ctx.Actor);
        cmd.Parameters.AddWithValue("corr", ctx.CorrelationId);
        cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The table and column come from the fixed lists of OpenFieldRules, never from the caller's text.</summary>
    private static async Task UpdateAsync(
        TenantTransaction tx, string table, string column, string? value, Guid tenantId, Guid documentId, int? lineNo, CancellationToken ct)
    {
        if (!(table == "journal_document" && OpenFieldRules.HeaderFields.ContainsKey(column)) &&
            !(table == "journal_line" && (OpenFieldRules.LineFields.ContainsKey(column) || OpenFieldRules.TypedDimensionFields.ContainsKey(column))) &&
            !(table == "journal_line_open_item" && OpenFieldRules.OpenItemFields.ContainsKey(column)))
            throw new InvalidOperationException($"{table}.{column} is not an open column");
        string cast = table == "journal_line_open_item" ? "::" + OpenFieldRules.OpenItemFields[column].DbType : OpenFieldRules.TypedDimensionFields.ContainsKey(column) ? "::uuid" : "";
        await using var cmd = tx.Command(
            $"update core.{table} set {column} = @v{cast} where tenant_id = @t and document_id = @d" + (lineNo is null ? "" : " and line_no = @ln"));
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", documentId);
        if (lineNo is not null) cmd.Parameters.AddWithValue("ln", lineNo.Value);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException($"{table}: the update of {column} changed no row");
    }

    private static async Task<ChangedDocument?> ReadLoggedAsync(TenantTransaction tx, Guid tenantId, Guid documentId, string key, CancellationToken ct)
    {
        var changed = new List<ChangedField>();
        Guid? set = null; string status = "";
        await using var cmd = tx.Command(
            "select change_set_id, period_status, entity_type, line_no, field_name, old_value, new_value, sub_key from core.change_log " +
            "where tenant_id = @t and document_id = @d and idempotency_key = @k order by entity_type desc, line_no nulls first, field_name, sub_key");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("d", documentId);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            set = r.GetGuid(0); status = r.GetString(1);
            changed.Add(new ChangedField(r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return set is null ? null : new ChangedDocument(documentId, set.Value, status, changed);
    }

    private static CommandResult<ChangedDocument> Reject(CommandError error) => CommandResult<ChangedDocument>.Rejected([error]);
}
