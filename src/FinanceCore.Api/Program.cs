using FinanceCore.Api.Auth;
using FinanceCore.Api.Commands;
using FinanceCore.Api.Http;
using FinanceCore.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddApiAuthentication();
builder.AddApiTransport();
builder.AddApiRateLimit();
// The key that signs the cursors of the list reads (20, 7): the same on every instance, or a cursor from one instance is refused by the next
string? cursorKey = builder.Configuration["Api:CursorKey"];
if (!string.IsNullOrWhiteSpace(cursorKey))
{
    byte[] keyBytes;
    try { keyBytes = Convert.FromBase64String(cursorKey); } catch (FormatException) { keyBytes = []; }
    if (keyBytes.Length < 32) throw new InvalidOperationException("Api:CursorKey is a base64 string of at least 32 bytes.");
    FinanceCore.Core.Reads.ReadRules.UseCursorKey(keyBytes);
}
else if (builder.Environment.IsProduction())
    throw new InvalidOperationException("Set Api:CursorKey (base64, at least 32 bytes) in Production: the cursors of the list reads are signed with it.");
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
builder.Services.AddSingleton(sp => new InventoryMasterCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new AccountDeterminationCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new StockCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new StockReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new FulfilmentCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new ReturnCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new SalesInvoiceCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new IntercompanyCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new InvoiceIssueCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new SalesCreditNoteCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PurchaseInvoiceCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new OrderCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new OrderSettingCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new IcOrderCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new IcInvoiceCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new ConsolidationSettingCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new RateUsageCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new GroupChartCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new AccountGroupMappingCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new OrderReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new MasterReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new TaxDeterminationCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new VatReturnCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new VatSettlement(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new DeductionCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new AccountRoleAssigner(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new RetentionRuleCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new FiscalYearOpener(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PeriodStatusCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new RevaluationCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new YearEndCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new LedgerReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new IcReconciliationReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new EventFeed(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new AuditReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new PartnerReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new MigrationReads(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new MigrationCommands(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new CompanyActivator(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(sp => new ChainVerification(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ICommandModule, PostingModule>();
builder.Services.AddSingleton<ICommandModule, VatModule>();
builder.Services.AddSingleton<ICommandModule, ClosingModule>();
builder.Services.AddSingleton<ICommandModule, ReadModule>();
builder.Services.AddSingleton<ICommandModule, FeedModule>();
builder.Services.AddSingleton<ICommandModule, MigrationModule>();
builder.Services.AddSingleton<ICommandModule, InventoryModule>();
builder.Services.AddSingleton<ICommandModule, OrderModule>();
builder.Services.AddSingleton<ICommandModule, StockModule>();
builder.Services.AddSingleton<ICommandModule, FulfilmentModule>();
builder.Services.AddSingleton<ICommandModule, ReturnModule>();
builder.Services.AddSingleton<ICommandModule, InvoiceModule>();
builder.Services.AddSingleton<ICommandModule, GrIrReadModule>();
builder.Services.AddSingleton<ICommandModule, ConfigModule>();
builder.Services.AddSingleton<CommandCatalog>(sp =>
{
    var catalog = new CommandCatalog();
    foreach (ICommandModule module in sp.GetServices<ICommandModule>()) module.Register(catalog);
    return catalog;
});

var app = builder.Build();
app.UseSecurityHeaders();
app.UseAuthentication();
app.UseApiRateLimit();
app.UseClientCertificateBinding();
app.UseAuthorization();
CommandEndpoint.Map(app);
app.Run();

public partial class Program;
