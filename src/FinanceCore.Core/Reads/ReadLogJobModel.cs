namespace FinanceCore.Core.Reads;

/// <summary>The result of the daily summary of the read access log (09, 5.2): the days and the access.summary events written, and the last day that is now summarised (UTC).</summary>
public sealed record ReadLogSummaryResult(int Days, int Events, DateOnly? SummarisedThrough);

/// <summary>The result of the purge of the read access log (09, Q6): the rows deleted, the cutoff used, and why nothing was purged when Skipped is set.</summary>
public sealed record ReadLogPurgeResult(long Deleted, DateTime? Cutoff, string? Skipped);

public static class ReadLogJobRules
{
    public const string SummaryEvent = "access.summary", Actor = "system:read-log-job", NotSummarised = "NOT_SUMMARISED", NoRule = "RETENTION.NO_RULE";
}
