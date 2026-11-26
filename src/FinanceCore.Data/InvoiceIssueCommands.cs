using System.Text.Json;
using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// invoices:record-issue (12, 11, 22 delta 1): inserts one invoice_issue_record, the evidence of which file went out and when. The target is exactly one of a link record (invoice_id with the kind) and a
/// journal document of a direct invoice or credit note (document_id, the kind follows from its type). A reversal (link record kind REVERSAL, journal mirror) is no target. Several records of one
/// invoice are allowed (re-issues). The event invoice.issued carries references only (15, 6).
/// </summary>
public sealed class InvoiceIssueCommands(NpgsqlDataSource source)
{
    private const string CommandName = "invoices:record-issue";

    private static CommandError Error(string code, string phase, string path, params (string Key, object? Value)[] p) => JournalDocumentPoster.Error(code, phase, path, p);

    private static CommandResult<InvoiceIssueResult> Reject(CommandError e) => CommandResult<InvoiceIssueResult>.Rejected([e]);

    public async Task<CommandResult<InvoiceIssueResult>> RecordAsync(Guid tenantId, CommandContext ctx, RecordInvoiceIssueRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(InvoiceIssueRules.Permission)) return Reject(Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", InvoiceIssueRules.Permission)));
        var errors = MasterSupport.ValidateContext(ctx);
        void Invalid(string path, string reason) => errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (req.CompanyId == Guid.Empty) Invalid("/company_id", "required");
        if (string.IsNullOrWhiteSpace(req.Format) || req.Format.Length > 20) Invalid("/format", "required, at most 20 characters");
        if (string.IsNullOrWhiteSpace(req.DocumentReference) || req.DocumentReference.Length > 500) Invalid("/document_reference", "required, at most 500 characters");
        if (InvoiceIssueRules.ParseSha256(req.DocumentSha256) is null) Invalid("/document_sha256", "64 hexadecimal characters");
        if (!InvoiceIssueRules.Channels.Contains(req.DeliveryChannel ?? "")) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/delivery_channel", ("reason", "unknown channel"), ("allowed_values", InvoiceIssueRules.Channels)));
        if (req.InvoiceKind is not null && !InvoiceIssueRules.Kinds.Contains(req.InvoiceKind)) errors.Add(Error("SCHEMA.INVALID_VALUE", "P1", "/invoice_kind", ("reason", "unknown kind"), ("allowed_values", InvoiceIssueRules.Kinds)));
        if (errors.Count > 0) return CommandResult<InvoiceIssueResult>.Rejected(errors);
        // exactly one target; the link record needs its kind
        if ((req.InvoiceId is null) == (req.DocumentId is null) || req.InvoiceId == Guid.Empty || req.DocumentId == Guid.Empty || (req.InvoiceId is not null && req.InvoiceKind is null))
            return Reject(Error("ORDER.ISSUE_TARGET_INVALID", "P1", "/invoice_id", ("reason", "exactly one of invoice_id (with invoice_kind) and document_id")));
        byte[] fingerprint = InvoiceIssueRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
                CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
                if (earlier is not null)
                {
                    if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
                    return earlier.EntityId is null
                        ? CommandResult<InvoiceIssueResult>.Failed(Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""))
                        : CommandResult<InvoiceIssueResult>.Ok(await LoadAsync(tx, tenantId, req.CompanyId, earlier.EntityId.Value, ct), earlier.CommandLogId, replay: true);
                }
                return await RecordCoreAsync(tx, tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<InvoiceIssueResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<InvoiceIssueResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private static async Task<CommandResult<InvoiceIssueResult>> RecordCoreAsync(
        TenantTransaction tx, Guid tenantId, CommandContext ctx, RecordInvoiceIssueRequest req, byte[] fingerprint, CancellationToken ct)
    {
        string kind;
        if (req.InvoiceId is { } invoiceId)
        {
            kind = req.InvoiceKind!;
            string table = kind == "SALES" ? "sales_invoice" : "purchase_invoice", idColumn = kind == "SALES" ? "sales_invoice_id" : "purchase_invoice_id";
            string other = kind == "SALES" ? "purchase_invoice" : "sales_invoice", otherId = kind == "SALES" ? "purchase_invoice_id" : "sales_invoice_id";
            string? linkKind = await ScalarAsync(tx, $"select invoice_kind from core.{table} where tenant_id = @t and company_id = @c and {idColumn} = @i", tenantId, req.CompanyId, invoiceId, ct);
            if (linkKind is null)
            {
                bool inOther = await ScalarAsync(tx, $"select invoice_kind from core.{other} where tenant_id = @t and company_id = @c and {otherId} = @i", tenantId, req.CompanyId, invoiceId, ct) is not null;
                return Reject(inOther
                    ? Error("ORDER.ISSUE_TARGET_INVALID", "P2", "/invoice_kind", ("reason", "the link record is of the other kind"))
                    : Error("ORDER.ISSUE_TARGET_NOT_FOUND", "P2", "/invoice_id"));
            }
            if (linkKind == "REVERSAL") return Reject(Error("ORDER.ISSUE_TARGET_INVALID", "P2", "/invoice_id", ("reason", "a reversal is no target")));
        }
        else
        {
            Guid documentId = req.DocumentId!.Value;
            string? type = null; bool mirror = false;
            await using (var cmd = tx.Command("select document_type, reversal_of_document_id is not null from core.journal_document where tenant_id = @t and company_id = @c and document_id = @d"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("d", documentId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { type = r.GetString(0); mirror = r.GetBoolean(1); }
            }
            if (type is null) return Reject(Error("ORDER.ISSUE_TARGET_NOT_FOUND", "P2", "/document_id"));
            string? implied = InvoiceIssueRules.KindOfDocumentType(type);
            if (implied is null) return Reject(Error("ORDER.ISSUE_DOCUMENT_TYPE_INVALID", "P2", "/document_id", ("document_type", type)));
            if (mirror) return Reject(Error("ORDER.ISSUE_TARGET_INVALID", "P2", "/document_id", ("reason", "a reversal is no target")));
            if (req.InvoiceKind is not null && req.InvoiceKind != implied) return Reject(Error("ORDER.ISSUE_TARGET_INVALID", "P2", "/invoice_kind", ("reason", "the kind disagrees with the document type"), ("expected", implied)));
            kind = implied;
        }

        Guid issueId = Guid.NewGuid();
        DateTimeOffset issuedAt = req.IssuedAt ?? DateTimeOffset.UtcNow;
        await using (var cmd = tx.Command(
            "insert into core.invoice_issue_record (tenant_id, company_id, issue_id, invoice_kind, invoice_id, document_id, format, document_reference, document_sha256, issued_at, delivery_channel, recorded_by, " +
            "source_system, correlation_id, idempotency_key) values (@t, @c, @id, @k, @inv, @doc, @f, @ref, @sha, @at, @ch, @by, @src, @corr, @key)"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId); cmd.Parameters.AddWithValue("id", issueId); cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.Add(new NpgsqlParameter("inv", NpgsqlDbType.Uuid) { Value = (object?)req.InvoiceId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Uuid) { Value = (object?)req.DocumentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("f", req.Format); cmd.Parameters.AddWithValue("ref", req.DocumentReference); cmd.Parameters.AddWithValue("sha", InvoiceIssueRules.ParseSha256(req.DocumentSha256)!);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = issuedAt.UtcDateTime }); cmd.Parameters.AddWithValue("ch", req.DeliveryChannel);
            cmd.Parameters.AddWithValue("by", ctx.Actor); cmd.Parameters.AddWithValue("src", ctx.SourceSystem); cmd.Parameters.AddWithValue("corr", ctx.CorrelationId); cmd.Parameters.AddWithValue("key", ctx.IdempotencyKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, req.CompanyId, ctx, CommandName, fingerprint, "INVOICE_ISSUE", issueId, ct);
        await CommandSupport.WriteOutboxAsync(tx, tenantId, req.CompanyId, ctx, "invoice.issued", "INVOICE_ISSUE", $"{req.CompanyId:D}/{issueId:D}", "ISSUED", DateTime.UtcNow, isAudit: true, null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["invoice_kind"] = kind, ["invoice_id"] = req.InvoiceId, ["document_id"] = req.DocumentId, ["format"] = req.Format, ["delivery_channel"] = req.DeliveryChannel
            }), ct);
        await tx.CommitAsync(ct);
        return CommandResult<InvoiceIssueResult>.Ok(new InvoiceIssueResult(issueId, kind, req.InvoiceId, req.DocumentId, issuedAt), logId);
    }

    private static async Task<string?> ScalarAsync(TenantTransaction tx, string sql, Guid tenantId, Guid companyId, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Command(sql);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", id);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<InvoiceIssueResult> LoadAsync(TenantTransaction tx, Guid tenantId, Guid companyId, Guid issueId, CancellationToken ct)
    {
        await using var cmd = tx.Command("select invoice_kind, invoice_id, document_id, issued_at from core.invoice_issue_record where tenant_id = @t and company_id = @c and issue_id = @i");
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("i", issueId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new InvoiceIssueResult(issueId, r.GetString(0).Trim(), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero));
    }
}
