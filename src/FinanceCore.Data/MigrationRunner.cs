using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace FinanceCore.Data;

public enum MigrationState
{
    /// <summary>Recorded in `platform.schema_migration` and the file is what was recorded (or nothing was recorded about its content yet).</summary>
    Applied,
    /// <summary>A file that no database row names.</summary>
    Pending,
    /// <summary>Recorded, but the file changed since: a numbered migration is never edited once applied (20, 12).</summary>
    Changed,
    /// <summary>Recorded, but there is no file for it in the directory.</summary>
    FileMissing
}

public sealed record MigrationStatus(string Version, string File, MigrationState State, bool ChecksumRecorded);

/// <summary>
/// Applies the numbered migrations of db/migrations to a database that exists, once each and in order (F-IMPL-32). The file
/// db/dist/finance-core-schema.sql is for an empty database only. The runner works with the migration role (FINANCECORE_ADMIN_DB).
///
/// The state is `platform.schema_migration`. Every migration records its own row (version, description). The runner adds a checksum column and
/// stores the SHA-256 of the file next to the version, so a later edit of an applied file is found and refused. A row without a checksum (a
/// database that was set up before the runner) takes the checksum of the file at the first run: from then on the file is held to it.
///
/// Refusals (a stable code each): MIGRATION.CHANGED (an applied file was edited), MIGRATION.FILE_MISSING (an applied version has no file),
/// MIGRATION.OUT_OF_ORDER (a pending file numbered below an applied one), MIGRATION.NAME_INVALID, MIGRATION.VERSION_DUPLICATE (two files with one
/// version), MIGRATION.FAILED (the script failed: nothing of that file is kept, earlier files stay applied).
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource admin)
{
    [GeneratedRegex(@"^(?<v>\d{4,})_[a-z0-9_]+\.sql$")]
    private static partial Regex FileName();

    private sealed record MigrationFile(string Version, string Name, string Path, string Checksum);

    private const string Bootstrap = """
        create schema if not exists platform;
        create table if not exists platform.schema_migration (
            version     text primary key,
            description text not null,
            applied_at  timestamptz not null default now()
        );
        alter table platform.schema_migration add column if not exists checksum text;
        """;

    public async Task<IReadOnlyList<MigrationStatus>> StatusAsync(string directory, CancellationToken ct = default)
    {
        List<MigrationFile> files = ReadFiles(directory);
        var recorded = await ReadRecordedAsync(ct);
        return Compare(files, recorded);
    }

    /// <summary>Applies the pending files in order. With `dryRun` it only names them. Returns the versions applied (or that would be).</summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(string directory, bool dryRun = false, CancellationToken ct = default)
    {
        List<MigrationFile> files = ReadFiles(directory);
        var recorded = await ReadRecordedAsync(ct);
        IReadOnlyList<MigrationStatus> status = Compare(files, recorded);

        if (status.FirstOrDefault(s => s.State == MigrationState.Changed) is { } changed)
            throw new ProvisioningException("MIGRATION.CHANGED",
                $"{changed.File} was edited after it was applied. A change to an applied migration is a new numbered file (20, 12)");
        if (status.FirstOrDefault(s => s.State == MigrationState.FileMissing) is { } missing)
            throw new ProvisioningException("MIGRATION.FILE_MISSING", $"Version {missing.Version} is applied but {directory} has no file for it");

        string? newestApplied = status.Where(s => s.State == MigrationState.Applied).Select(s => s.Version).Order(StringComparer.Ordinal).LastOrDefault();
        MigrationStatus[] pending = status.Where(s => s.State == MigrationState.Pending).ToArray();
        if (newestApplied is not null && pending.FirstOrDefault(p => string.CompareOrdinal(p.Version, newestApplied) < 0) is { } late)
            throw new ProvisioningException("MIGRATION.OUT_OF_ORDER",
                $"{late.File} is pending but version {newestApplied} is already applied. Number it above {newestApplied}");

        if (dryRun) return pending.Select(p => p.Version).ToList();

        var applied = new List<string>();
        foreach (MigrationFile f in files.Where(f => pending.Any(p => p.Version == f.Version)))
        {
            await using NpgsqlConnection connection = await admin.OpenConnectionAsync(ct);
            await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await using (var run = new NpgsqlCommand(await File.ReadAllTextAsync(f.Path, ct), connection, tx))
                    await run.ExecuteNonQueryAsync(ct);
                await StoreChecksumAsync(connection, tx, f, ct);
                await tx.CommitAsync(ct);
            }
            catch (PostgresException e)
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw new ProvisioningException("MIGRATION.FAILED", $"{f.Name}: {e.MessageText} ({e.SqlState})");
            }
            applied.Add(f.Version);
        }

        // the rows of a database from before the runner get the checksum of their file
        await using (NpgsqlConnection connection = await admin.OpenConnectionAsync(ct))
            foreach (MigrationFile f in files.Where(f => status.Any(s => s.Version == f.Version && s.State == MigrationState.Applied && !s.ChecksumRecorded)))
                await StoreChecksumAsync(connection, null, f, ct);
        return applied;
    }

    private static async Task StoreChecksumAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, MigrationFile f, CancellationToken ct)
    {
        // a migration records its own row; a file that does not still gets one, so the version is never lost
        await using var cmd = new NpgsqlCommand("""
            insert into platform.schema_migration (version, description, checksum) values (@v, @d, @c)
            on conflict (version) do update set checksum = excluded.checksum
            """, connection, tx);
        cmd.Parameters.AddWithValue("v", f.Version);
        cmd.Parameters.AddWithValue("d", f.Name);
        cmd.Parameters.AddWithValue("c", f.Checksum);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<string, string?>> ReadRecordedAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync(ct);
        await using (var boot = new NpgsqlCommand(Bootstrap, connection)) await boot.ExecuteNonQueryAsync(ct);
        var recorded = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var read = new NpgsqlCommand("select version, checksum from platform.schema_migration", connection);
        await using NpgsqlDataReader r = await read.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) recorded[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return recorded;
    }

    private static IReadOnlyList<MigrationStatus> Compare(List<MigrationFile> files, Dictionary<string, string?> recorded)
    {
        var result = new List<MigrationStatus>();
        foreach (MigrationFile f in files)
        {
            if (!recorded.TryGetValue(f.Version, out string? checksum)) result.Add(new(f.Version, f.Name, MigrationState.Pending, false));
            else if (checksum is not null && checksum != f.Checksum) result.Add(new(f.Version, f.Name, MigrationState.Changed, true));
            else result.Add(new(f.Version, f.Name, MigrationState.Applied, checksum is not null));
        }
        // a row whose version is not a number is not a migration (the dev seed records `seed-dev`): the runner leaves it alone
        foreach (string v in recorded.Keys.Where(v => v.All(char.IsAsciiDigit) && files.All(f => f.Version != v)).Order(StringComparer.Ordinal))
            result.Add(new(v, "(no file)", MigrationState.FileMissing, recorded[v] is not null));
        return result;
    }

    private static List<MigrationFile> ReadFiles(string directory)
    {
        if (!Directory.Exists(directory))
            throw new ProvisioningException("MIGRATION.DIRECTORY_MISSING", $"No migration directory at {Path.GetFullPath(directory)} (--dir)");
        var files = new List<MigrationFile>();
        foreach (string path in Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            Match m = FileName().Match(name);
            if (!m.Success) throw new ProvisioningException("MIGRATION.NAME_INVALID", $"{name} is not <number>_<name>.sql (lower case, digits and underscores)");
            string version = m.Groups["v"].Value;
            if (files.Any(f => f.Version == version)) throw new ProvisioningException("MIGRATION.VERSION_DUPLICATE", $"More than one file has the version {version}");
            // line endings do not change the checksum: the files are text and a checkout may convert them
            string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            files.Add(new MigrationFile(version, name, path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()));
        }
        return files;
    }
}
