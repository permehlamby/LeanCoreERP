using System.Text.Json;

namespace FinanceCore.Core.Reads;

// The second slice of 09 (F-IMPL-45): aging, chain status, audit events, the read access log, the document context and the partner reads.

// ---- aging:query (09, 4.5)
public sealed record AgingRequest(
    Guid CompanyId, DateOnly? AsOfDate = null, string? Basis = null, IReadOnlyList<int>? Buckets = null, IReadOnlyList<string>? GroupBy = null,
    Guid? AccountId = null, Guid? PartnerId = null, string? Currency = null, long? MinLedgerSeq = null);

public sealed record AgingBucketRead(string Label, int? FromDays, int? ToDays, Amounts Remaining, long ItemCount);

public sealed record AgingRow(IReadOnlyDictionary<string, string?> Keys, IReadOnlyList<AgingBucketRead> Buckets, Amounts Total, long ItemCount);

public sealed record AgingResult(
    DateOnly AsOfDate, string Basis, IReadOnlyList<int> Buckets, CurrencyCodes Currencies, IReadOnlyList<AgingRow> Rows, AgingRow GrandTotal, ReadSnapshot Snapshot);

/// <summary>The buckets of an aging run (09, 4.5, Q8): the boundaries are days past the basis date. 0, 30, 60, 90 give: current (not past the date), 1-30, 31-60, 61-90 and over 90.</summary>
public static class AgingRules
{
    public static readonly IReadOnlyList<int> DefaultBuckets = [0, 30, 60, 90];
    public const int MaxBuckets = 12;

    public static bool ValidBuckets(IReadOnlyList<int> bounds) =>
        bounds.Count is >= 1 and <= MaxBuckets && bounds.Zip(bounds.Skip(1), (a, b) => a < b).All(x => x);

    public static IReadOnlyList<(string Label, int? From, int? To)> Layout(IReadOnlyList<int> bounds)
    {
        var list = new List<(string, int?, int?)> { ($"<={bounds[0]}", null, bounds[0]) };
        for (int i = 1; i < bounds.Count; i++) list.Add(($"{bounds[i - 1] + 1}-{bounds[i]}", bounds[i - 1] + 1, bounds[i]));
        list.Add(($">{bounds[^1]}", bounds[^1] + 1, null));
        return list;
    }

    /// <summary>The index of the bucket of an item that is `daysPast` days past its basis date (negative when not yet due).</summary>
    public static int BucketOf(IReadOnlyList<int> bounds, int daysPast)
    {
        for (int i = 0; i < bounds.Count; i++) if (daysPast <= bounds[i]) return i;
        return bounds.Count;
    }
}

// ---- chains:status (09, 4.9)
public sealed record ChainsStatusRequest(Guid CompanyId, string? ChainType = null, string? ScopeKey = null);

public sealed record AnchorRead(Guid AnchorId, long ChainSeq, DateTime AnchoredAt, string State, string? TsaId);

public sealed record ChainStatusEntry(string ChainType, string ScopeKey, long LastSeq, string LastHash, int HashVersion, DateTime UpdatedAt, AnchorRead? LastAnchor);

public sealed record ChainsStatusResult(IReadOnlyList<ChainStatusEntry> Chains, DateTime TakenAt);

// ---- audit-events:query (09, 4.8)
public sealed record AuditEventsRequest(
    Guid? CompanyId = null, DateTime? From = null, DateTime? To = null, string? Actor = null, IReadOnlyList<string>? EventTypes = null, Guid? AppId = null,
    string? EntityType = null, string? EntityKey = null, string? CorrelationId = null, int? Limit = null, string? Cursor = null);

public sealed record AuditEventRead(
    long Position, DateTime EventTime, string Actor, Guid? AppId, string EventType, string? ViewType, string EntityType, string EntityKey, Guid? CompanyId,
    string CorrelationId, string? ReasonCode, JsonElement? Detail, JsonElement? Summary);

public sealed record AuditEventList(IReadOnlyList<AuditEventRead> Items, ReadPage Page, long HeadPosition, DateTime TakenAt);

/// <summary>The event types of the view of 4.8, by the outbox event type. A type that is_audit but is not here has no view type: a missing source is a missing type (Q11).</summary>
public static class AuditViewTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["ledger.document_posted"] = "POSTED", ["ledger.document_reversed"] = "REVERSED", ["ledger.line_changed"] = "L2_CHANGE",
        ["clearing.posted"] = "CLEARED", ["clearing.reset"] = "CLEARING_RESET",
        ["year.opened"] = "YEAR_OPENED", ["year.closed"] = "YEAR_CLOSED", ["year.reopened"] = "YEAR_REOPENED",
        ["revaluation.run_completed"] = "REVALUATION_RUN", ["revaluation.rerun"] = "REVALUATION_RUN",
        ["vat.return_prepared"] = "VAT_PREPARED", ["vat.return_filed"] = "VAT_FILED", ["vat.settled"] = "VAT_SETTLED",
        ["vat.settlement_reversed"] = "VAT_SETTLEMENT_REVERSED", ["vat.evidence_added"] = "VAT_EVIDENCE_ADDED",
        ["master.created"] = "MASTER_CREATE", ["master.changed"] = "MASTER_UPDATE", ["master.deactivated"] = "MASTER_DEACTIVATE", ["master.reactivated"] = "MASTER_REACTIVATE",
        ["partner.anonymised"] = "ANONYMISE", ["retention.applied"] = "ANONYMISATION_RUN", ["retention.extended"] = "RETENTION_EXTENDED", ["retention.recomputed"] = "RETENTION_RECOMPUTED",
        ["package.applied"] = "PACKAGE_APPLIED", ["config.version_created"] = "CONFIG_VERSION_CREATED", ["company.activated"] = "COMPANY_ACTIVATED",
        ["anchor.created"] = "ANCHOR_CREATED",
        ["stock.document_posted"] = "STOCK_POSTED", ["stock.document_reversed"] = "STOCK_REVERSED",
        ["migration.sealed"] = "MIGRATION_SEALED",
        ["dsr.received"] = "DSR_RECEIVED", ["dsr.decided"] = "DSR_DECIDED", ["partner.restricted"] = "PARTNER_RESTRICTED",
        ["export.downloaded"] = "EXPORT_DOWNLOADED",
        ["deletion.cancelled"] = "TENANT_DELETION_CANCELLED", ["deletion.executed"] = "TENANT_DELETION_EXECUTED",
        ["invoice.issued"] = "INVOICE_ISSUED",
        ["gr_ir.written_off"] = "GR_IR_WRITTEN_OFF", ["gr_ir.write_off_reversed"] = "GR_IR_WRITE_OFF_REVERSED",
        ["access.summary"] = "ACCESS_SUMMARY",
        ["extension.defined"] = "EXTENSION_DEFINED", ["app.registered"] = "APP_REGISTERED", ["app.changed"] = "APP_CHANGED",
        ["app.reactivated"] = "APP_REACTIVATED", ["app.consumer_rewound"] = "APP_CONSUMER_REWOUND"
    };

    /// <param name="status">The status in the summary of the event (period.status_changed, migration.batch_status_changed, export.created).</param>
    public static string? Of(string eventType, string? status = null, string? kind = null)
    {
        if (Map.TryGetValue(eventType, out string? v)) return v;
        return eventType switch
        {
            "period.status_changed" => status is null ? "PERIOD_STATUS_CHANGED" : "PERIOD_" + status.ToUpperInvariant(),
            "migration.batch_status_changed" => status is null ? null : "MIGRATION_" + status.ToUpperInvariant(),
            "export.created" => status == "COMPLETED" ? "EXPORT_COMPLETED" : "EXPORT_REQUESTED",
            "deletion.scheduled" => kind == "APPROVAL" ? "TENANT_DELETION_APPROVED" : "TENANT_DELETION_REQUESTED",
            "legal_hold.changed" => kind == "RELEASED" ? "HOLD_RELEASED" : "HOLD_PLACED",
            _ => null
        };
    }
}

// ---- read-access-log:list (09, 5.2)
public sealed record ReadAccessLogRequest(
    Guid? PartnerId = null, string? Actor = null, DateTime? From = null, DateTime? To = null, Guid? ExportJobId = null, int? Limit = null, string? Cursor = null);

public sealed record ReadAccessLogRead(
    Guid ReadId, DateTime ReadAt, string Actor, string Operation, Guid? CompanyId, Guid? PartnerId, int? PartnerCount, Guid? ExportJobId, Guid? DocumentId,
    string FieldGroup, string? PurposeCode, string CorrelationId, int? ResultPosition);

public sealed record ReadAccessLogList(IReadOnlyList<ReadAccessLogRead> Items, ReadPage Page, DateTime TakenAt);

// ---- documents:get-context (09, 4.7)
public sealed record DocumentContextRequest(Guid DocumentId, IReadOnlyList<string>? Include = null, string? PurposeCode = null, long? MinLedgerSeq = null);

public sealed record DocumentOpenItemRead(
    int LineNo, Guid? PartnerId, DateOnly? BaselineDate, DateOnly? DueDate, string? PaymentBlock, string ClearingStatus, decimal ClearedTc, Guid? LastClearingId);

public sealed record DocumentReversalRead(
    Guid? ReversalOfDocumentId, string? ReversalOfNumber, Guid? ReversedByDocumentId, string? ReversedByNumber, string? ReasonCode);

public sealed record DocumentClearingRead(
    Guid ClearingId, string ClearingType, DateOnly ClearingDate, int LineNo, string CurrencyTc, decimal ClearedTc, decimal ClearedLc, Guid? DifferenceDocumentId, string? MatchingRef);

public sealed record DocumentChangeRead(
    DateTime ChangedAt, string ChangedBy, string EntityType, int? LineNo, string FieldName, string? OldValue, string? NewValue, string ReasonCode, string? ReasonText, string CorrelationId);

public sealed record DocumentOriginRead(string CommandName, string Actor, string CorrelationId, DateTime ExecutedAt);

public sealed record DocumentIntegrityRead(long ChainSeq, string PrevHash, string RowHash, AnchorRead? LastAnchor);

public sealed record LegalSnapshotRead(
    Guid? PartnerId, string? LegalName, string? AddressLine1, string? AddressLine2, string? PostalCode, string? City, string? CountryCode, string? PartnerVatId,
    string? ElectronicAddress, string? LegalRegistrationId, DateTime? AnonymisedAt, DateOnly RetentionUntil);

public sealed record DocumentContextResult(
    JournalDocumentRead Document, IReadOnlyList<DocumentOpenItemRead>? OpenItems, DocumentReversalRead? Reversal, IReadOnlyList<DocumentClearingRead>? Clearings,
    IReadOnlyList<DocumentChangeRead>? Changes, DocumentOriginRead? Origin, DocumentIntegrityRead? Integrity, LegalSnapshotRead? LegalSnapshot,
    IReadOnlyList<string> Omitted, ReadSnapshot Snapshot);

public static class DocumentContextRules
{
    public static readonly IReadOnlyList<string> Includes =
        ["lines", "open_items", "reversal", "clearings", "changes", "origin", "integrity", "legal_snapshot"];
}

// ---- business-partners:get and :list (09, 3, 5)
public sealed record GetPartnerRequest(Guid PartnerId, bool IncludePii = false, string? PurposeCode = null);

public sealed record ListPartnersRequest(
    string? CodePrefix = null, string? PartnerKind = null, bool? IsCustomer = null, bool? IsSupplier = null, string? Status = null, bool IncludePii = false,
    string? PurposeCode = null, int? Limit = null, string? Cursor = null);

public sealed record PartnerPiiRead(
    string LegalName, string? TradeName, string? AddressLine1, string? AddressLine2, string? PostalCode, string? City, string CountryCode, string? Email, string? Phone,
    string? ContactPerson, string? ElectronicAddress, string? ElectronicAddressScheme, string? LegalRegistrationId);

public sealed record PartnerRead(
    Guid PartnerId, string PartnerCode, string PartnerKind, bool IsCustomer, bool IsSupplier, Guid? IcCompanyId, string Status, string RestrictionStatus,
    DateOnly? RetentionUntil, DateTime? AnonymisedAt, long RowVersion, PartnerPiiRead? Pii);

public sealed record PartnerResult(PartnerRead Partner, bool PiiOmitted);

public sealed record PartnerList(IReadOnlyList<PartnerRead> Items, ReadPage Page, bool PiiOmitted);

public static class PartnerReadRules
{
    public const string ConfigPermission = "read.config", PiiPermission = "read.pii", LegalSnapshotPermission = "read.legal_snapshot", AuditPermission = "read.audit";

    /// <summary>The purpose that lets a read of a RESTRICTED partner through (12, 5.3). 09 names no list of purposes (Q5), so this one is fixed here (F-IMPL-45).</summary>
    public const string StatutoryPurpose = "STATUTORY";
}
