namespace NCache.OSS.Quartz.Demo.Models;

public class EmailRequest
{
    public string Email { get; set; } = "user@test.com";
    public string UserName { get; set; } = "Test User";
}

public class RecurringJobRequest
{
    public string? CronExpression { get; set; }
    public string? JobId { get; set; }
}

public class UpdateDetailsRequest
{
    public string Group { get; set; } = "recurring";
    public string Name { get; set; } = "cleanup-old-logs";
    public int Priority { get; set; } = 9;
}

public class ContinuationRequest
{
    public bool ParentFails { get; set; }
}

public class CalendarJobRequest
{
    public string Mode { get; set; } = "weekends";
}

public class PinnedJobRequest
{
    public string Node { get; set; } = "auto";
}

public class MisfireRequest
{
    public string Instruction { get; set; } = "FireAndProceed";
}

public class OverlapRequest
{
    public string Policy { get; set; } = "Skip";
}

public class PauseRequest
{
    public string? Reason { get; set; }
    public string? RequestedBy { get; set; }
}

public class ConflictRequest
{
    public string Rule { get; set; } = "Keep";
    public int DelaySeconds { get; set; } = 30;
}

public class IntervalJobRequest
{
    public string Kind { get; set; } = "daily";
}

