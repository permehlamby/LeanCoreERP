using FinanceCore.Api.Http;
using FinanceCore.Core.Chains;
using FinanceCore.Core.Masters;
using FinanceCore.Core.Periods;
using FinanceCore.Core.Retention;
using FinanceCore.Core.Roles;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The payload of the commands that name only a company.</summary>
public sealed record CompanyRequest(Guid CompanyId);

/// <summary>
/// The master data and configuration commands (07, 11.1, 06, 7): partners, payment terms, account roles, retention rules, fiscal years, period status
/// and the readiness and activation of a company. The permission of each is checked by the command. Creating a tenant or a company is not here: it is
/// the platform CLI's (F-IMPL-30).
/// </summary>
public sealed class ConfigModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<CreatePartnerRequest, MasterResult>("business-partners:create", (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<UpdatePartnerRequest, MasterResult>("business-partners:update", (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().UpdateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("business-partners:deactivate", (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().DeactivateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("business-partners:reactivate", (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().ReactivateAsync(t, c, r, ct));

        catalog.Add<CreatePaymentTermsRequest, MasterResult>("payment-terms:create", (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<UpdatePaymentTermsRequest, MasterResult>("payment-terms:update", (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().UpdateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("payment-terms:deactivate", (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().DeactivateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("payment-terms:reactivate", (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().ReactivateAsync(t, c, r, ct));

        catalog.Add<AssignAccountRoleRequest, AssignedAccountRole>("account-role-assignments:create", (sp, t, c, r, ct) => sp.GetRequiredService<AccountRoleAssigner>().AssignAsync(t, c, r, ct));
        catalog.Add<CreateRetentionRuleVersionRequest, CreatedRetentionRule>("retention-rules:create-version", (sp, t, c, r, ct) => sp.GetRequiredService<RetentionRuleCommands>().CreateVersionAsync(t, c, r, ct));

        catalog.Add<OpenFiscalYearRequest, OpenedFiscalYear>("fiscal-years:open", (sp, t, c, r, ct) => sp.GetRequiredService<FiscalYearOpener>().OpenAsync(t, c, r, ct));
        catalog.Add<PeriodRef, PeriodStatusChange>("periods:soft-close", (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().SoftCloseAsync(t, c, r, ct));
        catalog.Add<PeriodRef, PeriodStatusChange>("periods:hard-close", (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().HardCloseAsync(t, c, r, ct));
        catalog.Add<ReopenPeriodRequest, PeriodStatusChange>("periods:reopen", (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().ReopenAsync(t, c, r, ct));

        catalog.Add<VerifyChainRequest, ChainVerificationResult>("chains:verify", (sp, t, c, r, ct) => sp.GetRequiredService<ChainVerification>().VerifyAsync(t, c, r, ct));
        catalog.Add<CompanyRequest, ReadinessReport>("companies:check-readiness", (sp, t, c, r, ct) => sp.GetRequiredService<CompanyActivator>().CheckReadinessAsync(t, c, r.CompanyId, ct));
        catalog.Add<CompanyRequest, ActivatedCompany>("companies:activate", (sp, t, c, r, ct) => sp.GetRequiredService<CompanyActivator>().ActivateAsync(t, c, r.CompanyId, ct));
    }
}
