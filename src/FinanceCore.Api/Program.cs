using FinanceCore.Api.Auth;
using FinanceCore.Api.Commands;
using FinanceCore.Api.Http;
using FinanceCore.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddApiAuthentication();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = CommandEndpoint.MaxBodyBytes);

// The application connection (role core_app): the command handlers switch to their command role inside each transaction (20, 3).
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(DbSettings.FromEnvironment().AppConnection));
// The command classes of the data layer: stateless, one instance each over the shared data source
builder.Services.AddSingleton(sp => new JournalDocumentPoster(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new DocumentReverser(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new DocumentChanger(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new ClearingPoster(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PartnerCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PaymentTermsCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new DimensionCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new TaxCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new TaxDeterminationCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new VatReturnCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new VatSettlement(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new DeductionCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new AccountRoleAssigner(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new RetentionRuleCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new FiscalYearOpener(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PeriodStatusCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new CompanyActivator(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new ChainVerification(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ICommandModule, PostingModule>();
builder.Services.AddSingleton<ICommandModule, VatModule>();
builder.Services.AddSingleton<ICommandModule, ConfigModule>();
builder.Services.AddSingleton<CommandCatalog>(sp =>
{
    var catalog = new CommandCatalog();
    foreach (ICommandModule module in sp.GetServices<ICommandModule>()) module.Register(catalog);
    return catalog;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
CommandEndpoint.Map(app);
app.Run();

public partial class Program;
