using FinanceCore.Core.Closing;
using Npgsql;
using NpgsqlTypes;

namespace FinanceCore.Data;

/// <summary>
/// The anchor requests of a close (02, 9.5, A2): one row PENDING with its digest for every chain head of the company, written in the transaction of
/// periods:hard-close and year-end:close. The timestamp token is fetched after commit by a job that does not exist yet, so a close needs the request and not the response.
/// The heads were locked by the command, so the digests match a committed head.
/// </summary>
internal static class AnchorRequests
{
    public static async Task<int> WriteAsync(TenantTransaction tx, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var heads = new List<(string Type, string Scope, long Seq, byte[] Hash)>();
        await using (var cmd = tx.Command(
            "select chain_type, scope_key, last_seq, last_hash from core.chain_head where tenant_id = @t and company_id = @c order by chain_type, scope_key"))
        {
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) heads.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), (byte[])r[3]));
        }
        DateTime now = DateTime.UtcNow;
        DateTime at = new(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        foreach (var (type, scope, seq, hash) in heads)
        {
            await using var cmd = tx.Command(
                "insert into core.chain_anchor (tenant_id, anchor_id, company_id, chain_type, scope_key, chain_seq, row_hash, anchored_at, anchor_digest, anchor_state) " +
                "values (@t, gen_random_uuid(), @c, @type, @scope, @seq, @hash, @at, @digest, 'PENDING')");
            cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("c", companyId); cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("scope", scope); cmd.Parameters.AddWithValue("seq", seq); cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            cmd.Parameters.AddWithValue("digest", AnchorHash.Digest(tenantId, companyId, type, scope, seq, hash, at));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return heads.Count;
    }
}
