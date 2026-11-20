namespace NCache.OSS.Quartz.Model;

public sealed class NCacheJobHeaderData
{
    public required string SchedulerName { get; set; }
    public required string JobName { get; set; }
    public required string JobGroup { get; set; }
    public string? Description { get; set; }
    public required string JobClassName { get; set; }
    public bool IsDurable { get; set; }
    public bool IsNonConcurrent { get; set; }
    public bool IsUpdateData { get; set; }
    public bool RequestsRecovery { get; set; }
}
