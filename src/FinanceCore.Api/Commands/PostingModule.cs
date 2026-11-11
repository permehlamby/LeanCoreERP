using FinanceCore.Api.Http;
using FinanceCore.Core.Posting;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>
/// The transaction commands (06, 5, 7): post a document, reverse it, change its open fields, post a clearing and reset it. The permission of each is
/// checked by the command in P0. The request types are the records of the core library, so the payload is the contract of 06 and nothing else.
/// </summary>
public sealed class PostingModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<PostJournalDocumentRequest, PostedDocument>("journal-documents:post", JournalDocumentPoster.PostPermission,
            (sp, tenant, ctx, req, ct) => sp.GetRequiredService<JournalDocumentPoster>().PostAsync(tenant, ctx, req, ct));
        catalog.Add<ReverseDocumentRequest, PostedDocument>("journal-documents:reverse", DocumentReverser.Permission,
            (sp, tenant, ctx, req, ct) => sp.GetRequiredService<DocumentReverser>().ReverseAsync(tenant, ctx, req, ct));
        catalog.Add<ChangeOpenFieldsRequest, ChangedDocument>("journal-documents:change-open-fields", DocumentChanger.Permission,
            (sp, tenant, ctx, req, ct) => sp.GetRequiredService<DocumentChanger>().ChangeAsync(tenant, ctx, req, ct));
        catalog.Add<PostClearingRequest, PostedClearing>("clearings:post", ClearingPoster.Permission,
            (sp, tenant, ctx, req, ct) => sp.GetRequiredService<ClearingPoster>().PostAsync(tenant, ctx, req, ct));
        catalog.Add<ResetClearingRequest, PostedClearing>("clearings:reset", ClearingPoster.ResetPermission,
            (sp, tenant, ctx, req, ct) => sp.GetRequiredService<ClearingPoster>().ResetAsync(tenant, ctx, req, ct));
    }
}
