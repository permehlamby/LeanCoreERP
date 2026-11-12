using FinanceCore.Data;
using FinanceCore.Platform.Cli;
using Npgsql;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(PlatformCli.Usage);
    return args.Length == 0 ? 2 : 0;
}

DbSettings settings;
try { settings = DbSettings.FromEnvironment(); }
catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 2; }
if (settings.IsLocalTestDouble)
    Console.Error.WriteLine("warning: FINANCECORE_ADMIN_DB is not set, using the local test double on 127.0.0.1:5433");

await using NpgsqlDataSource source = NpgsqlDataSource.Create(settings.AdminConnection);
try
{
    return await new PlatformCli(new PlatformProvisioner(source), Console.Out, new MigrationRunner(source), new CountryPackageApplier(source)).RunAsync(args);
}
catch (NpgsqlException e)
{
    Console.Error.WriteLine("database error: " + e.Message);
    return 1;
}
