using FinanceCore.Core.Chains;
using FinanceCore.Core.Commands;
using Npgsql;

namespace FinanceCore.Data;

/// <summary>
/// chains:verify (06, 7.4, permission chain.verify): read-only, it writes no event, no run record and no command_log entry. It recomputes the chain with the
/// verifier of the library and answers OK or the breaks with the first broken sequence number. A chain with breaks is a successful command: the breaks
/// are the result. The slice verifies LEDGER, CLEARING, PERIOD_EVENT and VAT_RETURN, whole chains only.
/// </summary>
public sealed class ChainVerification(NpgsqlDataSource source)
{
    public const string Permission = "chain.verify";
    public const string CommandName = "chains:verify";

    public async Task<CommandResult<ChainVerificationResult>> VerifyAsync(
        Guid tenantId, CommandContext ctx, VerifyChainRequest req, CancellationToken ct = default)
    {
        if (!ctx.Permissions.Contains(Permission))
            return Reject(new CommandError("AUTH.PERMISSION_MISSING", "P0", "", new Dictionary<string, object?> { ["permission"] = Permission }));

        var errors = new List<CommandError>();
        if (req.CompanyId == Guid.Empty) errors.Add(Invalid("company_id", "required"));
        if (!ChainRules.ChainTypes.Contains(req.ChainType))
            errors.Add(new CommandError("SCHEMA.INVALID_VALUE", "P1", "chain_type", new Dictionary<string, object?> { ["allowed_values"] = ChainRules.ChainTypes }));
        if (req.ScopeKey is { Length: > 100 }) errors.Add(Invalid("scope_key", "at most 100 characters"));
        if (req.FromSeq is < 1 || req.ToSeq is < 1 || req is { FromSeq: { } f, ToSeq: { } t } && f > t) errors.Add(Invalid("from_seq", "a range of sequence numbers from 1, from not after to"));
        if (errors.Count > 0) return CommandResult<ChainVerificationResult>.Rejected(errors);
        if (req.FromSeq is not null || req.ToSeq is not null)
            return Reject(new CommandError("LIMIT.CHAIN_RANGE_NOT_BUILT", "P1", req.FromSeq is not null ? "from_seq" : "to_seq"));
        if (!ChainRules.BuiltChainTypes.Contains(req.ChainType))
            return Reject(new CommandError("LIMIT.CHAIN_TYPE_NOT_BUILT", "P1", "chain_type", new Dictionary<string, object?> { ["allowed_values"] = ChainRules.BuiltChainTypes }));

        Guid? ledgerId = null;
        await using (TenantTransaction tx = await TenantTransaction.BeginAsync(source, tenantId, CommandRole.Read, ct: ct))
        {
            await using (var cmd = tx.Command("select 1 from core.company where tenant_id = @t and company_id = @c"))
            {
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                if (await cmd.ExecuteScalarAsync(ct) is null) return Reject(new CommandError("COMPANY.NOT_FOUND", "P2", "company_id"));
            }
            if (req.ChainType == "LEDGER")
            {
                // the scope of a ledger chain is the ledger: the default ledger of the company if none is named
                await using var cmd = tx.Command(
                    "select ledger_id from core.ledger where tenant_id = @t and company_id = @c and " +
                    (req.ScopeKey is null ? "is_default and active" : "ledger_id::text = @k"));
                cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", req.CompanyId);
                if (req.ScopeKey is not null) cmd.Parameters.AddWithValue("k", req.ScopeKey.ToLowerInvariant());
                ledgerId = await cmd.ExecuteScalarAsync(ct) as Guid?;
                if (ledgerId is null) return Reject(new CommandError("LEDGER.NOT_FOUND", "P2", "scope_key"));
            }
            else if (req.ScopeKey is { Length: > 0 })
                return Reject(new CommandError("SCHEMA.INVALID_VALUE", "P1", "scope_key", new Dictionary<string, object?> { ["reason"] = $"a {req.ChainType} chain has no scope" }));
        }

        var verifier = new ChainVerifier(source);
        VerificationResult result = req.ChainType switch
        {
            "LEDGER" => await verifier.VerifyLedgerAsync(tenantId, req.CompanyId, ledgerId!.Value, ct),
            "CLEARING" => await verifier.VerifyClearingsAsync(tenantId, req.CompanyId, ct),
            "VAT_RETURN" => await verifier.VerifyVatReturnsAsync(tenantId, req.CompanyId, ct),
            _ => await verifier.VerifyPeriodEventsAsync(tenantId, req.CompanyId, ct)
        };
        var breaks = result.Problems.Select(p => new ChainBreak(ChainRules.KindOf(p.Code), p.Code, p.ChainSeq, p.Detail)).ToList();
        long? first = breaks.Where(b => b.ChainSeq is not null).Select(b => b.ChainSeq).Min();
        // read-only: no command_log entry, so no id
        return new CommandResult<ChainVerificationResult>(CommandStatus.Ok,
            new ChainVerificationResult(req.ChainType, ledgerId?.ToString("D") ?? "", breaks.Count == 0, result.DocumentsChecked, first, breaks), [], [], false, null);
    }

    private static CommandError Invalid(string path, string reason) =>
        new("SCHEMA.INVALID_VALUE", "P1", path, new Dictionary<string, object?> { ["reason"] = reason });

    private static CommandResult<ChainVerificationResult> Reject(CommandError e) => CommandResult<ChainVerificationResult>.Rejected([e]);
}
