namespace NCache.OSS.Quartz.Model;

public sealed class NCacheFiredTriggerData
{
    public required string SchedulerName { get; set; }
    public required string EntryId { get; set; }
    public required string TriggerName { get; set; }
    public required string TriggerGroup { get; set; }
    public required string InstanceName { get; set; }
    public DateTimeOffset FiredTimeUtc { get; set; }
    public DateTimeOffset ScheduledTimeUtc { get; set; }
    public int Priority { get; set; }
    public required string State { get; set; }
    public string? JobName { get; set; }
    public string? JobGroup { get; set; }
    public bool IsNonConcurrent { get; set; }
    public bool RequestsRecovery { get; set; }
    public string? ExecutionGroup { get; set; }

    public bool PersistJobDataAfterExecution { get; set; }

    public int RetryAttempt { get; set; }

    public int OverlapPolicy { get; set; }

    public int? Progress { get; set; }
    public string? ProgressMessage { get; set; }
}

