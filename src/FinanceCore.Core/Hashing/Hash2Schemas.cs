namespace FinanceCore.Core.Hashing;

/// <summary>
/// The L1 column lists of 02, 9.3a for hash_version 2, in the order of the tables of 02 (sections 3, 5.1,
/// 5.2, 5.4, 7.1, 7.2, 5A). Changing any list or order is a new hash_version.
/// </summary>
public static class Hash2Schemas
{
    private static FieldSpec Id(string n, bool nullable = false) => new(n, FieldType.Id, nullable);
    private static FieldSpec Code(string n, bool nullable = false) => new(n, FieldType.Text, nullable);
    private static FieldSpec Int(string n, bool nullable = false) => new(n, FieldType.Int, nullable);
    private static FieldSpec Bool(string n) => new(n, FieldType.Bool);
    private static FieldSpec Date(string n, bool nullable = false) => new(n, FieldType.Date, nullable);
    private static FieldSpec Ts(string n) => new(n, FieldType.Timestamp);
    private static FieldSpec Amt(string n, bool nullable = false) => new(n, FieldType.Amount, nullable);
    private static FieldSpec Rate(string n, bool nullable = false) => new(n, FieldType.Rate, nullable);
    private static FieldSpec Hex(string n, bool nullable = false) => new(n, FieldType.Hex32, nullable);

    /// <summary>Header block: L1 columns of journal_document without chain_seq, prev_hash and row_hash (02, 3).</summary>
    public static readonly IReadOnlyList<FieldSpec> Document =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Id("ledger_id"),
        Code("document_type"), Bool("is_system_generated"), Id("number_series_id"),
        Int("fiscal_year"), Int("sequence_no"), Code("document_number"), Int("fiscal_period"),
        Bool("soft_close_override"), Id("migration_batch_id", true), Hex("detail_digest", true),
        Date("document_date"), Date("posting_date"), Ts("posted_at"), Code("transaction_currency"),
        Id("reversal_of_document_id", true), Code("reversal_reason_code", true), Date("auto_reversal_date", true),
        Id("ic_transaction_id", true), Id("ic_counterpart_company_id", true), Code("origin"),
        Code("source_system"), Code("source_document_ref", true), Code("idempotency_key"),
        Code("command_name"), Code("posted_by"), Code("correlation_id"), Int("hash_version")
    ];

    /// <summary>Line block: L1 columns of journal_line (02, 5.1, 5.2, 5.4).</summary>
    public static readonly IReadOnlyList<FieldSpec> Line =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("line_no"), Id("ledger_id"),
        Code("line_type"), Id("account_id"), Date("posting_date"), Int("fiscal_year"), Int("fiscal_period"),
        Int("reversed_line_no", true), Id("tax_code_id", true),
        Amt("amount_tc"), Amt("amount_lc"), Code("currency_lc"), Rate("rate_lc"), Rate("rate_lc_table", true),
        Amt("amount_gc"), Code("currency_gc"), Rate("rate_gc"), Rate("rate_gc_table", true),
        Amt("amount_rc", true), Code("currency_rc", true), Rate("rate_rc", true), Rate("rate_rc_table", true),
        Code("rate_type"), Date("rate_date"), Id("ic_counterpart_company_id", true)
    ];

    /// <summary>Tax block: all columns of journal_line_tax (02, 7.1).</summary>
    public static readonly IReadOnlyList<FieldSpec> Tax =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("line_no"),
        Id("tax_code_id"), Id("tax_code_version_id"), Code("tax_type"), Rate("tax_rate"),
        Amt("base_amount_tc"), Amt("base_amount_lc"), Amt("base_amount_gc"), Amt("base_amount_rc", true),
        Id("determined_tax_code_id"), Id("tax_class_id", true), Code("origin_country"), Code("destination_country"),
        Code("partner_vat_status", true), Date("tax_date"), Code("vat_currency"),
        Amt("vat_base_amount"), Amt("vat_tax_amount"), Rate("vat_rate"), Date("vat_rate_date", true),
        Bool("is_override"), Amt("tax_amount_supplied_tc", true), Code("rounding_mode"),
        Bool("origin_eu"), Bool("destination_eu"), Rate("deductible_pct"),
        Code("deduction_key", true), Rate("deduction_key_rate", true), Rate("deduction_share"),
        Amt("deduction_cap_amount_tc", true), Amt("gross_tax_amount_tc")
    ];

    /// <summary>Tax base block: columns of journal_line_tax_base (02, 7.2).</summary>
    public static readonly IReadOnlyList<FieldSpec> TaxBase =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("tax_line_no"), Int("base_line_no"),
        Amt("base_amount_tc"), Amt("nondeductible_tax_tc"), Int("nondeductible_line_no", true)
    ];

    /// <summary>Open item block: L1 columns of journal_line_open_item (02, 5A).</summary>
    public static readonly IReadOnlyList<FieldSpec> OpenItem =
    [
        Id("tenant_id"), Id("company_id"), Id("document_id"), Int("line_no"), Id("partner_id", true),
        Date("legacy_document_date", true), Date("legacy_posting_date", true), Code("legacy_document_ref", true)
    ];
}
