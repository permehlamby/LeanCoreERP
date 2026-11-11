using System.Text.Json;
using FinanceCore.Core.Commands;

namespace FinanceCore.Api.Http;

/// <summary>What a handler gets: the tenant and the envelope from the identity and the headers, the payload as the caller sent it.</summary>
public sealed record CommandInvocation(Guid TenantId, CommandContext Context, JsonElement? Body, IServiceProvider Services);

public sealed record CommandOutcome(
    CommandStatus Status, object? Result, IReadOnlyList<CommandError> Errors, IReadOnlyList<CommandError> Warnings, bool Replay, Guid? LogId)
{
    public static CommandOutcome Rejected(params CommandError[] errors) => new(CommandStatus.Rejected, null, errors, [], false, null);
}

public delegate Task<CommandOutcome> CommandHandler(CommandInvocation invocation, CancellationToken ct);

/// <summary>
/// A command of the API: its name as in the contracts (`journal-documents:post`), the types of its payload and result, and its handler.
/// `Permission` is the permission the command needs whatever the payload (06, 8). The endpoint checks it in P0a, from the token alone, before the
/// idempotency header and the payload are read (06, 3). Null for a command that checks its permission itself.
/// </summary>
public sealed record CommandRegistration(string Name, Type RequestType, Type ResultType, CommandHandler Handler, string? Permission = null);

/// <summary>
/// The commands the API serves (20, 7: `POST /v1/{resource}:{verb}`). Modules add theirs at start-up. The name is the only routing key, and the
/// list is what the OpenAPI document and the contract tests are generated from (A4).
/// </summary>
public sealed class CommandCatalog
{
    private readonly Dictionary<string, CommandRegistration> _commands = new(StringComparer.Ordinal);

    public IReadOnlyCollection<CommandRegistration> All => _commands.Values;

    public CommandRegistration? Find(string name) => _commands.GetValueOrDefault(name);

    /// <summary>
    /// Registers a command with the permission that the endpoint checks in P0a before it reads the payload. The command checks the same permission
    /// again in its own P0.
    /// </summary>
    public void Add<TRequest, TResult>(
        string name, string permission, Func<IServiceProvider, Guid, CommandContext, TRequest, CancellationToken, Task<CommandResult<TResult>>> handler)
        where TRequest : class
    {
        if (string.IsNullOrWhiteSpace(permission)) throw new ArgumentException("A permission is required", nameof(permission));
        Add(name, handler);
        _commands[name] = _commands[name] with { Permission = permission };
    }

    /// <summary>Registers a command whose payload is a request record and whose handler is a command of the Data layer. The command alone checks its permission.</summary>
    public void Add<TRequest, TResult>(
        string name, Func<IServiceProvider, Guid, CommandContext, TRequest, CancellationToken, Task<CommandResult<TResult>>> handler)
        where TRequest : class
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*:[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new ArgumentException("A command name is resource:verb in lower case with hyphens", nameof(name));
        if (_commands.ContainsKey(name)) throw new InvalidOperationException($"The command {name} is registered twice");
        _commands[name] = new CommandRegistration(name, typeof(TRequest), typeof(TResult), async (inv, ct) =>
        {
            TRequest? request;
            try
            {
                request = inv.Body is { ValueKind: not JsonValueKind.Null } body
                    ? body.Deserialize<TRequest>(ApiJson.Options)
                    : null;
            }
            catch (JsonException e)
            {
                return CommandOutcome.Rejected(JsonError(e));
            }
            if (request is null)
                return CommandOutcome.Rejected(new CommandError("SCHEMA.REQUIRED_FIELD_MISSING", "P1", "", new Dictionary<string, object?> { ["reason"] = "a payload is required" }));
            CommandResult<TResult> r = await handler(inv.Services, inv.TenantId, inv.Context, request, ct);
            return new CommandOutcome(r.Status, r.Value, r.Errors, r.Warnings, r.IdempotentReplay, r.CommandLogId);
        });
    }

    /// <summary>A payload that does not fit its type: an unknown field, a missing one or a value of the wrong type (06, 2.3, SCHEMA).</summary>
    internal static CommandError JsonError(JsonException e)
    {
        string path = (e.Path ?? "$").TrimStart('$', '.');
        string message = e.Message.Split(" Path:")[0];
        bool unknown = message.Contains("could not be mapped", StringComparison.Ordinal);
        bool missing = message.Contains("missing required", StringComparison.OrdinalIgnoreCase);
        bool nullValue = message.Contains("doesn't allow null", StringComparison.Ordinal);
        string code = unknown ? "SCHEMA.UNKNOWN_FIELD" : missing || nullValue ? "SCHEMA.REQUIRED_FIELD_MISSING" : "SCHEMA.INVALID_TYPE";
        // the exception names the field in quotes for an unknown or a missing one, the path already holds it for a null or a wrong value
        if ((unknown || missing) && message.IndexOf('\'') is var a and >= 0 && message.IndexOf('\'', a + 1) is var b and > 0)
        {
            string name = message[(a + 1)..b];
            path = string.IsNullOrEmpty(path) ? name : path + "." + name;
        }
        return new CommandError(code, "P1", path, new Dictionary<string, object?> { ["reason"] = message });
    }
}
