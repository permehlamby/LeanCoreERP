using FinanceCore.Api.Http;
using FinanceCore.Core.Closing;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The closing commands (05): the revaluation run and its preview, and the close and reopen of a fiscal year. Each command checks its permission in P0.</summary>
public sealed class ClosingModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<RunRevaluationRequest, RevaluationResult>("revaluation:run", RevaluationCommands.RunPermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<RevaluationCommands>().RunAsync(t, c, r, ct));
        catalog.Add<RunRevaluationRequest, RevaluationResult>("revaluation:preview", RevaluationCommands.PreviewPermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<RevaluationCommands>().PreviewAsync(t, c, r, ct));
        catalog.Add<CloseYearRequest, YearEndResult>("year-end:close", YearEndCommands.ClosePermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<YearEndCommands>().CloseAsync(t, c, r, ct));
        catalog.Add<ReopenYearRequest, YearReopenResult>("year-end:reopen", YearEndCommands.ReopenPermission,
            (sp, t, c, r, ct) => sp.GetRequiredService<YearEndCommands>().ReopenAsync(t, c, r, ct));
    }
}
