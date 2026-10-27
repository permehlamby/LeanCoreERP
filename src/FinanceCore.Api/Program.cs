using FinanceCore.Api.Auth;
using FinanceCore.Api.Http;
using FinanceCore.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddApiAuthentication();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = CommandEndpoint.MaxBodyBytes);

// The application connection (role core_app): the command handlers switch to their command role inside each transaction (20, 3).
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(DbSettings.FromEnvironment().AppConnection));
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
