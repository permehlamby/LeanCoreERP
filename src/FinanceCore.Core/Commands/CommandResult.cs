namespace FinanceCore.Core.Commands;

public enum CommandStatus { Ok, Rejected, Failed }

/// <summary>
/// One structured error of 06, 2.3: a stable code (AREA.NAME), the phase of the pipeline, the path of the request
/// field, parameters to render and act on the error, and whether the same request may succeed without change.
/// Codes with the area LIMIT are not part of the specs: they mark what the first implementation slice does not support yet.
/// </summary>
public sealed record CommandError(
    string Code,
    string Phase,
    string Path = "",
    IReadOnlyDictionary<string, object?>? Params = null,
    bool Retryable = false,
    string Severity = "ERROR");

/// <summary>The response envelope of 06, 2.2.</summary>
public sealed record CommandResult<T>(
    CommandStatus Status,
    T? Value,
    IReadOnlyList<CommandError> Errors,
    IReadOnlyList<CommandError> Warnings,
    bool IdempotentReplay = false,
    Guid? CommandLogId = null)
{
    public static CommandResult<T> Ok(T value, Guid commandLogId, IReadOnlyList<CommandError>? warnings = null, bool replay = false) =>
        new(CommandStatus.Ok, value, [], warnings ?? [], replay, commandLogId);

    public static CommandResult<T> Rejected(IReadOnlyList<CommandError> errors) =>
        new(CommandStatus.Rejected, default, errors, []);

    public static CommandResult<T> Failed(CommandError error) =>
        new(CommandStatus.Failed, default, [error], []);
}

/// <summary>The request envelope of 06, 2.1 as far as the first slice needs it.</summary>
public sealed record CommandContext(
    string IdempotencyKey,
    string CorrelationId,
    string Actor,
    IReadOnlySet<string> Permissions,
    string SourceSystem = "API");
