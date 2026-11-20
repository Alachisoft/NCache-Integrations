namespace NCache.OSS.Quartz.Model;

public sealed class NCacheCalendarData
{
    public required string SchedulerName { get; set; }
    public required string CalendarName { get; set; }
    public required string CalendarJson { get; set; }
}
