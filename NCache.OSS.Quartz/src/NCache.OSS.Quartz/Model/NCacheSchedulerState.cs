namespace NCache.OSS.Quartz.Model;

public sealed class NCacheSchedulerState
{
    public required string SchedulerName { get; set; }
    public required string InstanceName { get; set; }
    public DateTimeOffset LastCheckinTimeUtc { get; set; }
    public TimeSpan CheckinInterval { get; set; }
}
