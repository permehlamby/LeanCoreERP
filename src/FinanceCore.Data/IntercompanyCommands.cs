using FinanceCore.Core.Commands;
using FinanceCore.Core.Posting;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// intercompany-documents:post (06, 5.5, ADR-016, ADR-226; permission intercompany.post): two documents, one per company, in one transaction. Both carry the same ic_transaction_id and the
/// other company as counterpart. The IC account line of each side is generated: the balance of the base lines on the role IC_RECEIVABLE (debit) or IC_PAYABLE (credit) of the counterpart.
/// The base lines of the sides must offset in the transaction currency; the group amount is then the same on both sides because both use the rate of the same date. The marker
/// ic_counterpart_company_id is set on the BASE lines, including the generated one, never on tax or rounding lines. The periods of both companies must be open for the shared
/// posting date (no shifting). The sides are posted in company order, so the two ledger heads are locked in the same order by every IC command (06, 9, X4). Idempotency keys of the documents are
/// the key of the command with #1 and #2.
/// </summary>
public sealed class IntercompanyCommands(NpgsqlDataSource source)
{
    private readonly JournalDocumentPoster _poster = new(source);
    private const string CommandName = "intercompany-documents:post";

    private static CommandResult<IntercompanyPostResult> Reject(CommandError e) => CommandResult<IntercompanyPostResult>.Rejected([e]);

    public async Task<CommandResult<IntercompanyPostResult>> PostAsync(Guid tenantId, CommandContext ctx, PostIntercompanyDocumentRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(IntercompanyRules.PostPermission)) return Reject(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", IntercompanyRules.PostPermission)));
        var errors = new List<CommandError>();
        void Invalid(string path, string reason) => errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", path, ("reason", reason)));
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) Invalid("idempotency_key", "required, at most 90 characters (the sides add #1 and #2)");
        if (string.IsNullOrWhiteSpace(req.IcReference) || req.IcReference.Length > 100) Invalid("ic_reference", "required, at most 100 characters");
        if (req.Sides is null || req.Sides.Count != 2) Invalid("sides", "exactly two sides");
        else
        {
            if (req.Sides![0].CompanyId == req.Sides[1].CompanyId) Invalid("sides", "two different companies");
            for (int i = 0; i < 2; i++)
            {
                if (req.Sides[i].Lines is null or { Count: 0 }) Invalid($"sides[{i}]/lines", "at least one line");
                else if (req.Sides[i].Lines!.Any(l => l.LineRef == IntercompanyRules.BalancingLineRef)) Invalid($"sides[{i}]/lines", "the line ref IC is reserved for the generated IC account line");
            }
        }
        if (errors.Count > 0) return CommandResult<IntercompanyPostResult>.Rejected(errors);
        // the shape of each side, with a placeholder IC line so that the balance check of the shape is the one of the posting
        for (int i = 0; i < 2; i++)
            foreach (CommandError e in JournalDocumentPoster.ValidateShape(ctx, IntercompanyRules.SideRequest(req, req.Sides![i]) with { Lines = [.. req.Sides![i].Lines, new RequestLine(IntercompanyRules.BalancingLineRef, req.Sides![i].Lines[0].AccountId, 0m)] }))
                errors.Add(e with { Path = $"sides[{i}]/{e.Path}" });
        if (errors.Count > 0) return CommandResult<IntercompanyPostResult>.Rejected(errors);
        decimal[] base_ = [req.Sides![0].Lines.Sum(l => l.AmountTc), req.Sides![1].Lines.Sum(l => l.AmountTc)];
        if (base_[0] + base_[1] != 0m || base_[0] == 0m)
            return Reject(JournalDocumentPoster.Error("BALANCE.UNBALANCED_TC", "P6", "sides", ("currency", "TC"), ("difference", base_[0] + base_[1]), ("reason", "the base lines of the two sides must offset and not be zero")));
        byte[] fingerprint = IntercompanyRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(tenantId, ctx, req, base_, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<IntercompanyPostResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<IntercompanyPostResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<IntercompanyPostResult>> PostOnceAsync(
        Guid tenantId, CommandContext ctx, PostIntercompanyDocumentRequest req, decimal[] baseSums, byte[] fingerprint, CancellationToken ct)
    {
        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Post, ct: ct);
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Reject(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            if (earlier.EntityId is not { } earlierId) return CommandResult<IntercompanyPostResult>.Failed(JournalDocumentPoster.Error("SYSTEM.REPLAY_RESULT_MISSING", "P1", ""));
            var ids = new List<Guid>();
            await using (var cmd = tx.Command("select document_id from core.journal_document where tenant_id = @t and ic_transaction_id = @i and reversal_of_document_id is null order by idempotency_key"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", earlierId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
            }
            var docs = new List<PostedDocument>();
            foreach (Guid id in ids) docs.Add((await LedgerReader.LoadDocumentAsync(tx, tenantId, id, ct))!);
            return CommandResult<IntercompanyPostResult>.Ok(new IntercompanyPostResult(earlierId, docs), earlier.CommandLogId, replay: true);
        }

        Guid icId = Guid.NewGuid();
        var posted = new PostedDocument?[2];
        // the sides in company order: the ledger heads are locked in the same order whatever the order of the request
        foreach (int i in Enumerable.Range(0, 2).OrderBy(i => req.Sides[i].CompanyId))
        {
            IntercompanySide side = req.Sides[i];
            Guid counterpart = req.Sides[1 - i].CompanyId;
            string role = IntercompanyRules.RoleOf(-baseSums[i]);
            Guid? account = await AccountRoleResolver.ResolveAsync(tx, tenantId, side.CompanyId, role, null, counterpart, null, ct);
            if (account is null)
                return Reject(JournalDocumentPoster.Error("CONFIG.ROLE_NOT_ASSIGNED", "P2", $"sides[{i}]", ("role_code", role), ("counterpart_company_id", counterpart)));
            PostJournalDocumentRequest journal = IntercompanyRules.SideRequest(req, side) with { Lines = [.. side.Lines, new RequestLine(IntercompanyRules.BalancingLineRef, account.Value, 0m, req.IcReference)] };
            CommandContext sideCtx = ctx with { IdempotencyKey = IntercompanyRules.SideKey(ctx.IdempotencyKey, i) };
            var embedding = new JournalDocumentPoster.PostEmbedding(CommandName, "EXTERNAL", IntercompanyRules.BalancingLineRef, null, false, false, null,
                IcTransactionId: icId, IcCounterpartCompanyId: counterpart);
            var result = await _poster.PostCoreAsync(tx, tenantId, sideCtx, journal, RequestFingerprintOf(journal), embedding, ct);
            if (result.Status != CommandStatus.Ok)
                return CommandResult<IntercompanyPostResult>.Rejected(result.Errors.Select(e => e with { Path = $"sides[{i}]/{e.Path}" }).ToList()) with { Status = result.Status };
            posted[i] = result.Value!;
        }
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, CommandName, fingerprint, "IC_TRANSACTION", icId, ct);
        await tx.CommitAsync(ct);
        return CommandResult<IntercompanyPostResult>.Ok(new IntercompanyPostResult(icId, [posted[0]!, posted[1]!]), logId);
    }

    private static byte[] RequestFingerprintOf(PostJournalDocumentRequest r) => RequestFingerprint.Compute(r);

    // ---------------------------------------------------------------------------------------------------------
    // intercompany-documents:reverse (06, 5.5)
    // ---------------------------------------------------------------------------------------------------------
    private const string ReverseCommandName = "intercompany-documents:reverse";

    /// <summary>
    /// Reverses both documents of an IC pair in one transaction (permission intercompany.post). The mirrors keep the document type of their original (the owning command, 06, 6.1) and carry the
    /// same ic_transaction_id, so the pair and its mirrors are found through one id. A side that cannot be reversed (a cleared open item, a closed period in either company, a reversed
    /// document) refuses the whole command. The sides are reversed in company order, the keys are the key of the command with #1 and #2.
    /// </summary>
    public async Task<CommandResult<IntercompanyReversalResult>> ReverseAsync(Guid tenantId, CommandContext ctx, ReverseIntercompanyDocumentRequest req, CancellationToken ct = default)
    {
        static CommandResult<IntercompanyReversalResult> Fail(CommandError e) => CommandResult<IntercompanyReversalResult>.Rejected([e]);
        if (!ctx.Permissions.Contains(IntercompanyRules.PostPermission)) return Fail(JournalDocumentPoster.Error("AUTH.PERMISSION_MISSING", "P0", "", ("permission", IntercompanyRules.PostPermission)));
        var errors = new List<CommandError>();
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey) || ctx.IdempotencyKey.Length > 90) errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "idempotency_key", ("reason", "required, at most 90 characters")));
        if (req.IcTransactionId == Guid.Empty) errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "ic_transaction_id", ("reason", "required")));
        if (req.ReasonText?.Length > 500) errors.Add(JournalDocumentPoster.Error("SCHEMA.INVALID_VALUE", "P1", "reason_text", ("reason", "at most 500 characters")));
        if (errors.Count > 0) return CommandResult<IntercompanyReversalResult>.Rejected(errors);
        if (string.IsNullOrWhiteSpace(req.ReasonCode)) return Fail(JournalDocumentPoster.Error("REVERSAL.REASON_REQUIRED", "P1", "reason_code"));
        if (!ReversalRules.ExternalReasons.Contains(req.ReasonCode)) return Fail(JournalDocumentPoster.Error("REVERSAL.REASON_NOT_ALLOWED", "P1", "reason_code", ("allowed_values", ReversalRules.ExternalReasons)));
        byte[] fingerprint = IntercompanyReversalRules.Fingerprint(req);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ReverseOnceAsync(tenantId, ctx, req, fingerprint, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.TableName == "command_log" && attempt == 0)
            {
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return CommandResult<IntercompanyReversalResult>.Failed(new CommandError("SYSTEM.LOCK_TIMEOUT", "P7", "", null, true));
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                return CommandResult<IntercompanyReversalResult>.Failed(new CommandError("SYSTEM.CONFLICT", "P7", "", null, true));
            }
        }
    }

    private async Task<CommandResult<IntercompanyReversalResult>> ReverseOnceAsync(Guid tenantId, CommandContext ctx, ReverseIntercompanyDocumentRequest req, byte[] fingerprint, CancellationToken ct)
    {
        // the originals, and whether any has open items: the automatic clearing of a mirror needs the clearing role (03, 5.7)
        var originals = new List<(Guid DocumentId, string Type)>();
        bool openItems;
        await using (TenantTransaction probe = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        {
            await using (var cmd = probe.Command("select document_id, document_type from core.journal_document where tenant_id = @t and ic_transaction_id = @i and reversal_of_document_id is null order by company_id"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.IcTransactionId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) originals.Add((r.GetGuid(0), r.GetString(1)));
            }
            await using var oi = probe.Command("select exists (select 1 from core.journal_line_open_item where tenant_id = @t and document_id = any(@d))");
            oi.Parameters.AddWithValue("t", tenantId); oi.Parameters.AddWithValue("d", originals.Select(o => o.DocumentId).ToArray());
            openItems = (bool)(await oi.ExecuteScalarAsync(ct))!;
        }

        await using TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, openItems ? CommandRole.Clearing : CommandRole.Post, ct: ct);
        CommandSupport.EarlierCommand? earlier = await CommandSupport.FindEarlierAsync(tx, tenantId, ctx.IdempotencyKey, ct);
        if (earlier is not null)
        {
            if (!earlier.Fingerprint.AsSpan().SequenceEqual(fingerprint)) return Fail(JournalDocumentPoster.Error("IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST", "P1", "idempotency_key"));
            var ids = new List<Guid>();
            await using (var cmd = tx.Command("select document_id from core.journal_document where tenant_id = @t and ic_transaction_id = @i and reversal_of_document_id is not null order by idempotency_key"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("i", req.IcTransactionId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
            }
            var docs = new List<PostedDocument>();
            foreach (Guid id in ids) docs.Add((await LedgerReader.LoadDocumentAsync(tx, tenantId, id, ct))!);
            return CommandResult<IntercompanyReversalResult>.Ok(new IntercompanyReversalResult(req.IcTransactionId, docs), earlier.CommandLogId, replay: true);
        }
        if (originals.Count != 2) return Fail(JournalDocumentPoster.Error("REVERSAL.NOT_FOUND", "P2", "ic_transaction_id"));

        var reverser = new DocumentReverser(source);
        var mirrors = new List<PostedDocument>();
        for (int i = 0; i < 2; i++)
        {
            var (documentId, type) = originals[i];
            var mode = new DocumentReverser.ReverseMode(ReverseCommandName, type, "EXTERNAL", (_, _, _) => Task.FromResult<CommandError?>(null), (_, _, _) => Task.CompletedTask);
            CommandContext sideCtx = ctx with { IdempotencyKey = IntercompanyRules.SideKey(ctx.IdempotencyKey, i) };
            var result = await reverser.ReverseCoreAsync(tx, tenantId, sideCtx, new ReverseDocumentRequest(documentId, req.ReversalDate, req.ReasonCode, req.ReasonText), fingerprint, ct, mode, owned: false);
            if (result.Status != CommandStatus.Ok)
                return CommandResult<IntercompanyReversalResult>.Rejected(result.Errors.Select(e => e with { Path = $"sides[{i}]/{e.Path}" }).ToList()) with { Status = result.Status };
            mirrors.Add(result.Value!);
        }
        Guid logId = await CommandSupport.WriteLogAsync(tx, tenantId, null, ctx, ReverseCommandName, fingerprint, "IC_TRANSACTION", req.IcTransactionId, ct);
        await tx.CommitAsync(ct);
        return CommandResult<IntercompanyReversalResult>.Ok(new IntercompanyReversalResult(req.IcTransactionId, mirrors), logId);
    }

    private static CommandResult<IntercompanyReversalResult> Fail(CommandError e) => CommandResult<IntercompanyReversalResult>.Rejected([e]);
}
