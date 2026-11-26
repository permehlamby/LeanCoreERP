using System.Globalization;
using FinanceCore.Core.Masters;

namespace FinanceCore.Core.Posting;

// ---- intercompany-documents:post (06, 5.5, ADR-016, ADR-226, permission intercompany.post)
/// <summary>One side of the pair: the company, the document type and the base lines (06, 5.2). The line of the IC account is generated, not supplied.</summary>
public sealed record IntercompanySide(
    Guid CompanyId, string DocumentType, IReadOnlyList<RequestLine> Lines, Guid? LedgerId = null, Guid? PartnerId = null, string? HeaderText = null, string? ExternalReference = null);

public sealed record PostIntercompanyDocumentRequest(
    DateOnly DocumentDate, DateOnly PostingDate, string TransactionCurrency, string IcReference, IReadOnlyList<IntercompanySide> Sides);

/// <summary>The two documents in the order of the sides of the request.</summary>
public sealed record IntercompanyPostResult(Guid IcTransactionId, IReadOnlyList<PostedDocument> Documents);

public static class IntercompanyRules
{
    public const string PostPermission = "intercompany.post";

    /// <summary>The line ref of the generated IC account line (the balance of the base lines and the tax).</summary>
    public const string BalancingLineRef = "IC";

    /// <summary>The role of the generated line: a debit balance is a receivable on the counterpart, a credit balance a payable (ADR-016).</summary>
    public static string RoleOf(decimal balanceTc) => balanceTc > 0m ? "IC_RECEIVABLE" : "IC_PAYABLE";

    /// <summary>The key of the document of a side: the base key and the position of the side in the request (06, 5.5).</summary>
    public static string SideKey(string baseKey, int position) => $"{baseKey}#{position + 1}";

    public static PostJournalDocumentRequest SideRequest(PostIntercompanyDocumentRequest r, IntercompanySide s) =>
        new(s.CompanyId, s.LedgerId, s.DocumentType, r.DocumentDate, r.PostingDate, r.TransactionCurrency, s.HeaderText, s.ExternalReference, r.IcReference, s.Lines, s.PartnerId);

    public static byte[] Fingerprint(PostIntercompanyDocumentRequest r) =>
        MasterRules.Fingerprint("intercompany-documents:post", string.Join("|", r.IcReference, string.Join("#", r.Sides.Select(s =>
            Convert.ToHexString(RequestFingerprint.Compute(SideRequest(r, s)))))));
}

// ---- intercompany-documents:reverse (06, 5.5, permission intercompany.post): both sides of a pair, atomically. reversal_date defaults to the posting date of each document.
public sealed record ReverseIntercompanyDocumentRequest(Guid IcTransactionId, string ReasonCode, DateOnly? ReversalDate = null, string? ReasonText = null);

/// <summary>The two mirror documents, in company order (the order of the keys #1 and #2).</summary>
public sealed record IntercompanyReversalResult(Guid IcTransactionId, IReadOnlyList<PostedDocument> Documents);

public static class IntercompanyReversalRules
{
    public static byte[] Fingerprint(ReverseIntercompanyDocumentRequest r) =>
        MasterRules.Fingerprint("intercompany-documents:reverse", $"{r.IcTransactionId:D}|{r.ReversalDate:yyyy-MM-dd}|{r.ReasonCode}|{r.ReasonText}");
}
