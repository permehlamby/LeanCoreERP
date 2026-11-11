using FinanceCore.Core.Hashing;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>The rows of journal_line_tax and journal_line_tax_base as the hash sees them (02, 7.1, 7.2, 9.3a): written with a document, read back for
/// verification and copied, with opposite signs, into the mirror of a reversal.</summary>
internal static class TaxRows
{
    private static NpgsqlDbType DbType(FieldType t) => t switch
    {
        FieldType.Id => NpgsqlDbType.Uuid, FieldType.Int => NpgsqlDbType.Integer, FieldType.Bool => NpgsqlDbType.Boolean, FieldType.Date => NpgsqlDbType.Date,
        FieldType.Amount or FieldType.Rate => NpgsqlDbType.Numeric, _ => NpgsqlDbType.Text
    };

    private static Task InsertAsync(TenantTransaction tx, string table, IReadOnlyList<FieldSpec> schema, IReadOnlyDictionary<string, object?> row, CancellationToken ct)
    {
        // base_amount_lc and the other columns come from the schema, in its order: the table has exactly these columns
        var cmd = tx.Command($"insert into core.{table} ({string.Join(", ", schema.Select(f => f.Name))}) values ({string.Join(", ", schema.Select((_, i) => "@p" + i))})");
        for (int i = 0; i < schema.Count; i++)
            cmd.Parameters.Add(new NpgsqlParameter("p" + i, DbType(schema[i].Type)) { Value = row[schema[i].Name] ?? DBNull.Value });
        return Run(cmd, ct);
    }

    private static async Task Run(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using (cmd) await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static Task InsertTaxAsync(TenantTransaction tx, IReadOnlyDictionary<string, object?> row, CancellationToken ct) =>
        InsertAsync(tx, "journal_line_tax", Hash2Schemas.Tax, row, ct);

    internal static Task InsertBaseAsync(TenantTransaction tx, IReadOnlyDictionary<string, object?> row, CancellationToken ct) =>
        InsertAsync(tx, "journal_line_tax_base", Hash2Schemas.TaxBase, row, ct);

    /// <summary>The amounts a mirror carries with the opposite sign: the base and the tax in every currency, the VAT amounts, the supplied tax and the gross tax.</summary>
    private static readonly string[] SignedTax =
    [
        "base_amount_tc", "base_amount_lc", "base_amount_gc", "base_amount_rc", "vat_base_amount", "vat_tax_amount", "tax_amount_supplied_tc",
        "deduction_cap_amount_tc", "gross_tax_amount_tc"
    ];

    internal static Dictionary<string, object?> MirrorTax(IReadOnlyDictionary<string, object?> original, Guid mirrorId)
    {
        var m = new Dictionary<string, object?>(original) { ["document_id"] = mirrorId };
        foreach (string k in SignedTax) if (m[k] is decimal d) m[k] = d == 0m ? 0m : -d;
        return m;
    }

    internal static Dictionary<string, object?> MirrorBase(IReadOnlyDictionary<string, object?> original, Guid mirrorId)
    {
        var m = new Dictionary<string, object?>(original) { ["document_id"] = mirrorId };
        foreach (string k in new[] { "base_amount_tc", "nondeductible_tax_tc" }) if (m[k] is decimal d) m[k] = d == 0m ? 0m : -d;
        return m;
    }

    /// <summary>Reads the tax rows of the documents of one query: the rows by (company, document), the tax rows by line number and the base rows by tax line number.</summary>
    internal static async Task ReadAsync(
        TenantTransaction tx, Guid tenantId, string where, Action<NpgsqlCommand> parameters,
        Action<Guid, Guid, int, Dictionary<string, object?>> onTax, Action<Guid, Guid, int, Dictionary<string, object?>> onBase, CancellationToken ct)
    {
        await using (var cmd = tx.Command($"select company_id, document_id, line_no, {string.Join(", ", Hash2Schemas.Tax.Select(f => f.Name))} from core.journal_line_tax where tenant_id = @t and {where} order by document_id, line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); parameters(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) onTax(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), Row(r, Hash2Schemas.Tax, 3));
        }
        await using (var cmd = tx.Command($"select company_id, document_id, tax_line_no, {string.Join(", ", Hash2Schemas.TaxBase.Select(f => f.Name))} from core.journal_line_tax_base where tenant_id = @t and {where} order by document_id, tax_line_no, base_line_no"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); parameters(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) onBase(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), Row(r, Hash2Schemas.TaxBase, 3));
        }
    }

    private static Dictionary<string, object?> Row(NpgsqlDataReader r, IReadOnlyList<FieldSpec> schema, int offset)
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < schema.Count; i++)
        {
            int c = offset + i;
            row[schema[i].Name] = r.IsDBNull(c) ? null : schema[i].Type switch
            {
                FieldType.Id => r.GetGuid(c), FieldType.Int => r.GetInt32(c), FieldType.Bool => r.GetBoolean(c), FieldType.Date => r.GetFieldValue<DateOnly>(c),
                FieldType.Amount or FieldType.Rate => r.GetDecimal(c), _ => r.GetString(c).Trim()
            };
        }
        return row;
    }
}
