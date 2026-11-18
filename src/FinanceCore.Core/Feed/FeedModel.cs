using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceCore.Core.Hashing;

namespace FinanceCore.Core.Feed;

public sealed record RegisterAppRequest(string AppCode, string Name, IReadOnlyList<Guid>? CompanyScope = null);

/// <summary>apps:update. CompanyScope replaces the scope, AllCompanies true removes it. Neither: the scope is unchanged.</summary>
public sealed record UpdateAppRequest(Guid AppId, int ExpectedVersion, string? Name = null, IReadOnlyList<Guid>? CompanyScope = null, bool AllCompanies = false);

public sealed record AppStatusRequest(Guid AppId, int ExpectedVersion, string ReasonText);

public sealed record SetSubscriptionsRequest(Guid AppId, int ExpectedVersion, IReadOnlyList<string> EventTypes);

public sealed record AppResult(Guid AppId, string AppCode, string Name, bool Active, IReadOnlyList<Guid>? CompanyScope, int RowVersion, IReadOnlyList<string> EventTypes);

public sealed record ListEventsRequest(long? SincePosition = null, int? Limit = null, string? EventType = null, Guid? CompanyId = null, string? EntityType = null, string? EntityKey = null);

public sealed record FeedEvent(
    long Position, string EventType, int EventVersion, Guid? CompanyId, string EntityType, string EntityKey, long? EntityVersion, string Change, DateTime OccurredAt,
    string ActorRef, string CorrelationId, bool IsAudit, string? ChainRef, string? Summary);

/// <summary>NextPosition is the position to pass as since_position next time: the last event returned, else the one asked for.</summary>
public sealed record EventList(IReadOnlyList<FeedEvent> Items, long NextPosition, long HeadPosition);

public sealed record EventsHeadRequest;

public sealed record EventsHead(long HeadPosition);

public sealed record AckEventsRequest(string ConsumerId, long Position);

public sealed record RewindConsumerRequest(Guid AppId, string ConsumerId, long Position);

public sealed record ConsumerOffset(Guid AppId, string ConsumerId, long LastPosition);

public sealed record PurgeResult(long Deleted, long PurgedThrough, long? BlockedAtPosition);

public static class FeedRules
{
    public const int DefaultLimit = 500, MaxLimit = 5000;

    /// <summary>The families of the event types the core writes (15, 6). A subscription names one of these, exactly or as a family with a trailing star.</summary>
    public static readonly IReadOnlySet<string> Families = new HashSet<string>(StringComparer.Ordinal)
    {
        "ledger", "clearing", "period", "year", "revaluation", "open_item", "vat", "stock", "reservation", "order", "delivery", "receipt", "return", "invoice", "gr_ir",
        "master", "config", "package", "company", "extension", "retention", "partner", "dsr", "legal_hold", "export", "deletion", "access", "anchor", "migration", "app"
    };

    /// <summary>The sensitive families (15, 3.3, 09, 2.3a): they reach only a caller that also holds read.audit, whatever the subscription says.</summary>
    public static bool IsSensitive(string eventTypeOrFamily)
    {
        string family = eventTypeOrFamily.Split('.')[0];
        return family is "access" or "retention" or "dsr" or "export" or "deletion" or "app"
            || eventTypeOrFamily is "partner.anonymised" or "partner.restricted" or "legal_hold.changed" or "partner.*";
    }

    public static bool IsKnown(string eventTypeOrFamily)
    {
        string[] parts = eventTypeOrFamily.Split('.');
        return parts.Length == 2 && Families.Contains(parts[0]) && parts[1].Length > 0
            && (parts[1] == "*" || parts[1].All(c => c is (>= 'a' and <= 'z') or '_'));
    }

    public static string SubscriptionDigest(IEnumerable<string> types) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', types.Distinct().OrderBy(t => t, StringComparer.Ordinal))))).ToLowerInvariant();

    private static readonly FieldSpec[] PayloadSpec = [new("payload", FieldType.Text)];

    /// <summary>The fingerprint of a registry command: the canonical JSON of its payload under the command name.</summary>
    public static byte[] Fingerprint(string commandName, object request) =>
        Periods.CommandFingerprint.Compute(commandName, PayloadSpec, new Dictionary<string, object?> { ["payload"] = JsonSerializer.Serialize(request, request.GetType()) });
}
