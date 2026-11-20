namespace NCache.OSS.Quartz.Index;

internal sealed class NCacheNextFireTimeIndexEntry
{
    public required string TriggerGroup { get; set; }
    public required string TriggerName { get; set; }
    public DateTimeOffset NextFireTimeUtc { get; set; }
    public int Priority { get; set; }
}
