namespace NCache.OSS.Quartz.Model;

public enum NCacheTriggerKind
{
    Simple,
    Cron,
    CalendarInterval,
    DailyTimeInterval,
    Custom,
    Recurrence
}

public sealed class NCacheTriggerData
{
    public string? ExecutionGroup { get; set; }
    public required string SchedulerName { get; set; }
    public required string TriggerName { get; set; }
    public required string TriggerGroup { get; set; }
    public required string JobName { get; set; }
    public required string JobGroup { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? NextFireTimeUtc { get; set; }
    public DateTimeOffset? PreviousFireTimeUtc { get; set; }
    public int Priority { get; set; } = 5;
    public required string TriggerState { get; set; }
    public required NCacheTriggerKind TriggerKind { get; set; }
    public DateTimeOffset StartTimeUtc { get; set; }
    public DateTimeOffset? EndTimeUtc { get; set; }
    public string? CalendarName { get; set; }
    public int MisfireInstruction { get; set; }
    public TimeSpan? MisfireThreshold { get; set; }
    public string? RetryPolicy { get; set; }
    public int RetryAttempt { get; set; }
    public string? PreferredNode { get; set; }
    public bool PreferredNodeIsAutomatic { get; set; }
    public long? IndexBucketId { get; set; }

    public string? ContinuesAfterTriggerName { get; set; }
    public string? ContinuesAfterTriggerGroup { get; set; }
    public int? ContinuationCondition { get; set; }

    public bool HasContinuationParent => !string.IsNullOrWhiteSpace(ContinuesAfterTriggerName);

    public int OverlapPolicy { get; set; }

    public bool OverlapPolicyUnsettled { get; set; }

    public NCachePauseData? Pause { get; set; }

    public NCacheTriggerJobFacts? Job { get; set; }
    public Dictionary<string, object?> JobDataMap { get; set; } = new();

    public NCacheSimpleTriggerData? Simple { get; set; }
    public NCacheCronTriggerData? Cron { get; set; }
    public NCacheCalendarIntervalTriggerData? CalendarInterval { get; set; }
    public NCacheDailyTimeIntervalTriggerData? DailyTimeInterval { get; set; }
    public NCacheRecurrenceTriggerData? Recurrence { get; set; }
    public string? CustomTriggerJson { get; set; }
}

public sealed class NCachePauseData
{
    public string? Reason { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset PausedAtUtc { get; set; }
}

public sealed class NCacheTriggerJobFacts
{
    public required string JobClassName { get; set; }
    public bool IsNonConcurrent { get; set; }
    public bool RequestsRecovery { get; set; }
    public bool IsUpdateData { get; set; }

    public static NCacheTriggerJobFacts From(NCacheJobData job) => new()
    {
        JobClassName = job.JobClassName,
        IsNonConcurrent = job.IsNonConcurrent,
        RequestsRecovery = job.RequestsRecovery,
        IsUpdateData = job.IsUpdateData
    };
}

public sealed class NCacheRecurrenceTriggerData
{
    public required string RecurrenceRule { get; set; }
    public string? TimeZoneId { get; set; }
    public int TimesTriggered { get; set; }
}

public sealed class NCacheSimpleTriggerData
{
    public int RepeatCount { get; set; }
    public TimeSpan RepeatInterval { get; set; }
    public int TimesTriggered { get; set; }
}

public sealed class NCacheCronTriggerData
{
    public required string CronExpression { get; set; }
    public string? TimeZoneId { get; set; }
}

public sealed class NCacheCalendarIntervalTriggerData
{
    public int RepeatInterval { get; set; }
    public required string RepeatIntervalUnit { get; set; }
    public int TimesTriggered { get; set; }
    public string? TimeZoneId { get; set; }
    public bool PreserveHourOfDayAcrossDaylightSavings { get; set; }
    public bool SkipDayIfHourDoesNotExist { get; set; }
}

public sealed class NCacheDailyTimeIntervalTriggerData
{
    public int RepeatInterval { get; set; }
    public required string RepeatIntervalUnit { get; set; }
    public int TimesTriggered { get; set; }
    public string? TimeZoneId { get; set; }
    public required string StartTimeOfDay { get; set; }
    public required string EndTimeOfDay { get; set; }
    public HashSet<int>? DaysOfWeek { get; set; }
    public int RepeatCount { get; set; } = -1;
}

public static class TriggerStates
{
    public const string Waiting = "Waiting";
    public const string Acquired = "Acquired";
    public const string Paused = "Paused";
    public const string Blocked = "Blocked";
    public const string PausedAndBlocked = "PausedAndBlocked";
    public const string Complete = "Complete";
    public const string Error = "Error";

    public const string Awaiting = "Awaiting";
}

public static class FiredTriggerStates
{
    public const string Acquired = "Acquired";
    public const string Executing = "Executing";
    public const string Complete = "Complete";
    public const string Blocked = "Blocked";
    public const string Error = "Error";
    public const string Vetoed = "Vetoed";
}

