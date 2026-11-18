namespace FinanceCore.Core.Chains;

/// <summary>Payload of chains:verify (06, 7.4). The slice verifies the whole chain: <c>from_seq</c> and <c>to_seq</c> are refused (LIMIT.CHAIN_RANGE_NOT_BUILT).</summary>
public sealed record VerifyChainRequest(Guid CompanyId, string ChainType, string? ScopeKey = null, long? FromSeq = null, long? ToSeq = null);

/// <summary>One break found by the verification. The kind is one of 06, 7.4 (hash, link, gap, balance, number_continuity, s_column_mismatch, digest_mismatch)
/// or, for what that row does not name, head, structure or snapshot. The code is the stable name of the check.</summary>
public sealed record ChainBreak(string Kind, string Code, long? ChainSeq, string Detail);

/// <summary>The answer of chains:verify: OK, or the breaks with the first broken sequence number (06, 7.4). A chain with breaks is a successful command.</summary>
public sealed record ChainVerificationResult(
    string ChainType, string ScopeKey, bool Ok, long Checked, long? FirstBreakSeq, IReadOnlyList<ChainBreak> Breaks);

public static class ChainRules
{
    public static readonly IReadOnlyList<string> ChainTypes = ["LEDGER", "CLEARING", "PERIOD_EVENT", "VAT_RETURN", "STOCK", "FULFILMENT"];
    public static readonly IReadOnlyList<string> BuiltChainTypes = ["LEDGER", "CLEARING", "PERIOD_EVENT", "VAT_RETURN"];

    /// <summary>The kind of break (06, 7.4) that a check belongs to.</summary>
    public static string KindOf(string code) => code switch
    {
        "CHAIN.ROW_HASH_MISMATCH" or "CHAIN.HASH_VERSION_UNSUPPORTED" => "hash",
        "CHAIN.PREV_HASH_MISMATCH" => "link",
        "CHAIN.SEQUENCE_GAP" => "gap",
        "DOCUMENT.NOT_BALANCED" or "CLEARING.NOT_BALANCED" or "OPEN_ITEM.OVER_CLEARED" => "balance",
        "NUMBER.GAP" => "number_continuity",
        "PERIOD.STATUS_MISMATCH" or "OPEN_ITEM.CACHE_MISMATCH" => "s_column_mismatch",
        "CLEARING.DOCUMENT_MISMATCH" => "digest_mismatch",
        "CHAIN.HEAD_MISSING" or "CHAIN.HEAD_MISMATCH" or "CHAIN.HEAD_HASH_MISMATCH" => "head",
        var c when c.StartsWith("SNAPSHOT.", StringComparison.Ordinal) => "snapshot",
        _ => "structure"
    };
}
