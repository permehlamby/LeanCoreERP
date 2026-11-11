using FinanceCore.Data;
using Npgsql;

namespace FinanceCore.Data.Tests;

/// <summary>
/// The migration runner (F-IMPL-32) against the test database, which has every migration applied. The probe migrations have versions from 9000 and
/// are removed again, with their rows, at the end of each test.
/// </summary>
[Collection("db")]
public class MigrationRunnerTests : IAsyncLifetime
{
    private static readonly string RealDirectory = FindMigrations();
    private readonly List<string> _temp = [];
    private readonly MigrationRunner _runner = new(TestTenant.AdminSource);

    private static string FindMigrations()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "db", "migrations"))) return Path.Combine(d.FullName, "db", "migrations");
        throw new DirectoryNotFoundException("db/migrations was not found");
    }

    public Task InitializeAsync() => CleanAsync();

    public async Task DisposeAsync()
    {
        await CleanAsync();
        foreach (string d in _temp) Directory.Delete(d, true);
    }

    private static async Task CleanAsync()
    {
        await using var c = await TestTenant.AdminSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("drop table if exists platform.runner_probe; delete from platform.schema_migration where version >= '9000'", c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A copy of the real migrations plus the given probe files, in a directory of its own.</summary>
    private string Directory_(params (string Name, string Sql)[] probes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fc-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        foreach (string f in Directory.GetFiles(RealDirectory, "*.sql")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        foreach (var (name, sql) in probes) File.WriteAllText(Path.Combine(dir, name), sql);
        return dir;
    }

    private static async Task<long> CountAsync(string sql)
    {
        await using var c = await TestTenant.AdminSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ADatabaseThatIsUpToDateHasNothingPendingAndTheRunnerAppliesNothing()
    {
        Assert.Empty(await _runner.MigrateAsync(RealDirectory));
        var status = await _runner.StatusAsync(RealDirectory);
        Assert.NotEmpty(status);
        Assert.All(status, s => { Assert.Equal(MigrationState.Applied, s.State); Assert.True(s.ChecksumRecorded, s.File); });
        Assert.Equal(["0001", "0002"], status.Take(2).Select(s => s.Version));
    }

    [Fact]
    public async Task APendingFileIsAppliedOnceAndAFailureKeepsNothingOfIt()
    {
        string dir = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);"));
        Assert.Equal(["9001"], await _runner.MigrateAsync(dir, dryRun: true));
        Assert.Equal(0, await CountAsync("select count(*) from pg_tables where schemaname = 'platform' and tablename = 'runner_probe'"));

        Assert.Equal(["9001"], await _runner.MigrateAsync(dir));
        Assert.Equal(1, await CountAsync("select count(*) from pg_tables where schemaname = 'platform' and tablename = 'runner_probe'"));
        Assert.Equal(1, await CountAsync("select count(*) from platform.schema_migration where version = '9001' and checksum is not null"));
        Assert.Empty(await _runner.MigrateAsync(dir));   // the table exists and the script has no `if not exists`: a second run would fail

        string bad = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);"),
            ("9002_runner_failing.sql", "create table platform.runner_probe_two (id int); select 1 / 0;"));
        var failed = await Assert.ThrowsAsync<ProvisioningException>(() => _runner.MigrateAsync(bad));
        Assert.Equal("MIGRATION.FAILED", failed.Code);
        Assert.Equal(0, await CountAsync("select count(*) from pg_tables where schemaname = 'platform' and tablename = 'runner_probe_two'"));
        Assert.Equal(0, await CountAsync("select count(*) from platform.schema_migration where version = '9002'"));
    }

    [Fact]
    public async Task AnAppliedFileThatWasEditedIsRefused()
    {
        string dir = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);"));
        await _runner.MigrateAsync(dir);

        File.AppendAllText(Path.Combine(dir, "9001_runner_probe.sql"), "\n-- edited\n");
        Assert.Equal(MigrationState.Changed, (await _runner.StatusAsync(dir)).Single(s => s.Version == "9001").State);
        Assert.Equal("MIGRATION.CHANGED", (await Assert.ThrowsAsync<ProvisioningException>(() => _runner.MigrateAsync(dir))).Code);
    }

    [Fact]
    public async Task LineEndingsDoNotChangeTheChecksum()
    {
        string dir = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);\n-- end\n"));
        await _runner.MigrateAsync(dir);
        File.WriteAllText(Path.Combine(dir, "9001_runner_probe.sql"), "create table platform.runner_probe (id int primary key);\r\n-- end\r\n");
        Assert.Equal(MigrationState.Applied, (await _runner.StatusAsync(dir)).Single(s => s.Version == "9001").State);
    }

    [Fact]
    public async Task ARecordedVersionWithoutAFileAndAPendingFileBelowAnAppliedOneAreRefused()
    {
        string dir = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);"));
        await _runner.MigrateAsync(dir);

        string lateDir = Directory_(("9001_runner_probe.sql", "create table platform.runner_probe (id int primary key);"), ("9000_late.sql", "select 1;"));
        Assert.Equal("MIGRATION.OUT_OF_ORDER", (await Assert.ThrowsAsync<ProvisioningException>(() => _runner.MigrateAsync(lateDir))).Code);

        File.Delete(Path.Combine(dir, "9001_runner_probe.sql"));
        Assert.Equal("MIGRATION.FILE_MISSING", (await Assert.ThrowsAsync<ProvisioningException>(() => _runner.MigrateAsync(dir))).Code);
    }

    [Fact]
    public async Task ARowThatIsNotANumberedVersionIsNotAMigration()
    {
        // the dev seed records the version seed-dev in the same table
        await using (var c = await TestTenant.AdminSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("insert into platform.schema_migration (version, description) values ('9000-seed-probe', 'probe') on conflict do nothing", c))
            await cmd.ExecuteNonQueryAsync();
        var status = await _runner.StatusAsync(RealDirectory);
        Assert.DoesNotContain(status, s => s.Version == "9000-seed-probe");
        Assert.Empty(await _runner.MigrateAsync(RealDirectory));
    }

    [Fact]
    public async Task AFileNameThatIsNotNumberedOrIsUsedTwiceIsRefused()
    {
        Assert.Equal("MIGRATION.NAME_INVALID", (await Assert.ThrowsAsync<ProvisioningException>(() => _runner.StatusAsync(Directory_(("add stuff.sql", "select 1;"))))).Code);
        Assert.Equal("MIGRATION.VERSION_DUPLICATE", (await Assert.ThrowsAsync<ProvisioningException>(() =>
            _runner.StatusAsync(Directory_(("9001_a.sql", "select 1;"), ("9001_b.sql", "select 1;"))))).Code);
        Assert.Equal("MIGRATION.DIRECTORY_MISSING", (await Assert.ThrowsAsync<ProvisioningException>(() => _runner.StatusAsync("no-such-directory"))).Code);
    }
}
