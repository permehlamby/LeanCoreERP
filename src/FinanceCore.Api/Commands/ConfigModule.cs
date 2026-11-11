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
        catalog.Add<CreatePartnerRequest, MasterResult>("business-partners:create", MasterRules.PartnerPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<UpdatePartnerRequest, MasterResult>("business-partners:update", MasterRules.PartnerPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().UpdateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("business-partners:deactivate", MasterRules.PartnerPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().DeactivateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("business-partners:reactivate", MasterRules.PartnerPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PartnerCommands>().ReactivateAsync(t, c, r, ct));

        catalog.Add<CreatePaymentTermsRequest, MasterResult>("payment-terms:create", MasterRules.ConfigPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().CreateAsync(t, c, r, ct));
        catalog.Add<UpdatePaymentTermsRequest, MasterResult>("payment-terms:update", MasterRules.ConfigPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().UpdateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("payment-terms:deactivate", MasterRules.ConfigPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().DeactivateAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("payment-terms:reactivate", MasterRules.ConfigPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PaymentTermsCommands>().ReactivateAsync(t, c, r, ct));

        catalog.Add<CreateDimensionTypeRequest, MasterResult>("dimension-types:create", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().CreateTypeAsync(t, c, r, ct));
        catalog.Add<UpdateDimensionTypeRequest, MasterResult>("dimension-types:update", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().UpdateTypeAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("dimension-types:deactivate", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().DeactivateTypeAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("dimension-types:reactivate", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().ReactivateTypeAsync(t, c, r, ct));
        catalog.Add<CreateDimensionValueRequest, MasterResult>("dimension-values:create", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().CreateValueAsync(t, c, r, ct));
        catalog.Add<UpdateDimensionValueRequest, MasterResult>("dimension-values:update", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().UpdateValueAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("dimension-values:deactivate", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().DeactivateValueAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("dimension-values:reactivate", MasterRules.DimensionPermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().ReactivateValueAsync(t, c, r, ct));
        catalog.Add<CreateAccountDimensionRuleRequest, MasterResult>("account-dimension-rules:create", DimensionCommands.RulePermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().CreateRuleAsync(t, c, r, ct));
        catalog.Add<UpdateAccountDimensionRuleRequest, MasterResult>("account-dimension-rules:update", DimensionCommands.RulePermission, (sp, t, c, r, ct) => sp.GetRequiredService<DimensionCommands>().UpdateRuleAsync(t, c, r, ct));

        catalog.Add<CreateTaxClassRequest, MasterResult>("tax-classes:create", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().CreateClassAsync(t, c, r, ct));
        catalog.Add<UpdateTaxClassRequest, MasterResult>("tax-classes:update", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().UpdateClassAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("tax-classes:deactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().DeactivateClassAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("tax-classes:reactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().ReactivateClassAsync(t, c, r, ct));
        catalog.Add<CreateVatRegistrationRequest, MasterResult>("company-vat-registrations:create", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().CreateRegistrationAsync(t, c, r, ct));
        catalog.Add<UpdateVatRegistrationRequest, MasterResult>("company-vat-registrations:update", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().UpdateRegistrationAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("company-vat-registrations:deactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().DeactivateRegistrationAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("company-vat-registrations:reactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().ReactivateRegistrationAsync(t, c, r, ct));
        catalog.Add<CreateTaxCodeRequest, MasterResult>("tax-codes:create", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().CreateCodeAsync(t, c, r, ct));
        catalog.Add<UpdateTaxCodeRequest, MasterResult>("tax-codes:update", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().UpdateCodeAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("tax-codes:deactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().DeactivateCodeAsync(t, c, r, ct));
        catalog.Add<MasterStatusRequest, MasterResult>("tax-codes:reactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().ReactivateCodeAsync(t, c, r, ct));
        catalog.Add<CreateTaxCodeVersionRequest, TaxCodeVersionResult>("tax-codes:create-version", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().CreateVersionAsync(t, c, r, ct));
        catalog.Add<CreateTaxReturnBoxRequest, MasterResult>("tax-return-boxes:create", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().CreateBoxAsync(t, c, r, ct));
        catalog.Add<UpdateTaxReturnBoxRequest, MasterResult>("tax-return-boxes:update", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().UpdateBoxAsync(t, c, r, ct));
        catalog.Add<TaxReturnBoxStatusRequest, MasterResult>("tax-return-boxes:deactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().DeactivateBoxAsync(t, c, r, ct));
        catalog.Add<TaxReturnBoxStatusRequest, MasterResult>("tax-return-boxes:reactivate", TaxCommands.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<TaxCommands>().ReactivateBoxAsync(t, c, r, ct));

        catalog.Add<AssignAccountRoleRequest, AssignedAccountRole>("account-role-assignments:create", AccountRoleAssigner.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<AccountRoleAssigner>().AssignAsync(t, c, r, ct));
        catalog.Add<CreateRetentionRuleVersionRequest, CreatedRetentionRule>("retention-rules:create-version", RetentionRules.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<RetentionRuleCommands>().CreateVersionAsync(t, c, r, ct));

        catalog.Add<OpenFiscalYearRequest, OpenedFiscalYear>("fiscal-years:open", FiscalYearOpener.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<FiscalYearOpener>().OpenAsync(t, c, r, ct));
        catalog.Add<PeriodRef, PeriodStatusChange>("periods:soft-close", PeriodStatusCommands.SoftClosePermission, (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().SoftCloseAsync(t, c, r, ct));
        catalog.Add<PeriodRef, PeriodStatusChange>("periods:hard-close", PeriodStatusCommands.HardClosePermission, (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().HardCloseAsync(t, c, r, ct));
        catalog.Add<ReopenPeriodRequest, PeriodStatusChange>("periods:reopen", PeriodStatusCommands.ReopenPermission, (sp, t, c, r, ct) => sp.GetRequiredService<PeriodStatusCommands>().ReopenAsync(t, c, r, ct));

        catalog.Add<VerifyChainRequest, ChainVerificationResult>("chains:verify", ChainVerification.Permission, (sp, t, c, r, ct) => sp.GetRequiredService<ChainVerification>().VerifyAsync(t, c, r, ct));
        catalog.Add<CompanyRequest, ReadinessReport>("companies:check-readiness", CompanyActivator.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<CompanyActivator>().CheckReadinessAsync(t, c, r.CompanyId, ct));
        catalog.Add<CompanyRequest, ActivatedCompany>("companies:activate", CompanyActivator.ActivatePermission, (sp, t, c, r, ct) => sp.GetRequiredService<CompanyActivator>().ActivateAsync(t, c, r.CompanyId, ct));
    }
}
