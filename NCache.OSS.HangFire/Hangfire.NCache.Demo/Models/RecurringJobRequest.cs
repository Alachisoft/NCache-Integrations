namespace NCache.OSS.Hangfire.Demo.Models
{
    public class RecurringJobRequest
    {
        public string? CronExpression { get; set; }
        public string? JobId { get; set; }
    }
}
