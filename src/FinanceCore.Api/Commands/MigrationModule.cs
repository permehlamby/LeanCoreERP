using FinanceCore.Api.Http;
using FinanceCore.Core.Migration;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The migration batch (13, 7): open, control totals, staging, validate, reconcile, commit, seal and abort.</summary>
public sealed class MigrationModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        const string manage = MigrationCommands.ManagePermission, stage = MigrationCommands.StagePermission, commit = MigrationCommands.CommitPermission;
        catalog.Add<OpenBatchRequest, MigrationBatchResult>("migration-batches:open", manage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().OpenAsync(t, c, r, ct));
        catalog.Add<DeclareTotalsRequest, MigrationBatchResult>("migration-batches:declare-totals", manage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().DeclareTotalsAsync(t, c, r, ct));
        catalog.Add<StageRecordsRequest, MigrationBatchResult>("migration-records:stage", stage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().StageAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:validate", manage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().ValidateAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:reconcile", manage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().ReconcileAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:commit", commit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().CommitAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:seal", commit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().SealAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:abort", manage, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().AbortAsync(t, c, r, ct));
    }
}
