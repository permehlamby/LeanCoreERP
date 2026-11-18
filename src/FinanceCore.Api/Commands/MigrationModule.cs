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
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:rollback", commit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().RollbackAsync(t, c, r, ct));
        catalog.Add<BatchRef, MigrationBatchResult>("migration-batches:approve", commit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationCommands>().ApproveAsync(t, c, r, ct));

        const string audit = MigrationReads.Permission;
        catalog.Add<GetBatchRequest, BatchDetail>("migration-batches:get", audit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationReads>().GetBatchAsync(t, c, r, ct));
        catalog.Add<ListBatchesRequest, BatchList>("migration-batches:list", audit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationReads>().ListBatchesAsync(t, c, r, ct));
        catalog.Add<ListRecordsRequest, MigrationRecordList>("migration-records:list", audit, (sp, t, c, r, ct) => sp.GetRequiredService<MigrationReads>().ListRecordsAsync(t, c, r, ct));
    }
}
