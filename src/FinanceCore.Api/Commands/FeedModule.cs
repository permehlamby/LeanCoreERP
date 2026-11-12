using FinanceCore.Api.Http;
using FinanceCore.Core.Feed;
using FinanceCore.Data;

namespace FinanceCore.Api.Commands;

/// <summary>The event feed (15, 4.5, 5.3): the app registry, the subscriptions, the events of a subscribed app, the acknowledgement and the rewind.</summary>
public sealed class FeedModule : ICommandModule
{
    public void Register(CommandCatalog catalog)
    {
        catalog.Add<RegisterAppRequest, AppResult>("apps:register", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().RegisterAsync(t, c, r, ct));
        catalog.Add<UpdateAppRequest, AppResult>("apps:update", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().UpdateAsync(t, c, r, ct));
        catalog.Add<AppStatusRequest, AppResult>("apps:deactivate", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().DeactivateAsync(t, c, r, ct));
        catalog.Add<AppStatusRequest, AppResult>("apps:reactivate", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().ReactivateAsync(t, c, r, ct));
        catalog.Add<SetSubscriptionsRequest, AppResult>("app-subscriptions:set", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().SetSubscriptionsAsync(t, c, r, ct));
        catalog.Add<ListEventsRequest, EventList>("events:list", EventFeed.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().ListAsync(t, c, r, ct));
        catalog.Add<EventsHeadRequest, EventsHead>("events:head", EventFeed.ReadPermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().HeadAsync(t, c, ct));
        catalog.Add<AckEventsRequest, ConsumerOffset>("events:ack", EventFeed.ConsumePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().AckAsync(t, c, r, ct));
        catalog.Add<RewindConsumerRequest, ConsumerOffset>("events:rewind", EventFeed.ManagePermission, (sp, t, c, r, ct) => sp.GetRequiredService<EventFeed>().RewindAsync(t, c, r, ct));
    }
}
