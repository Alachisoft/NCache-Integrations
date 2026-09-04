using Hangfire;
using NCache.OSS.Hangfire.Demo.Services;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;
using NCache.OSS.Hangfire.Demo.Models;
namespace NCache.OSS.Hangfire.Demo.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IRecurringJobManager _recurringJobs;
    private readonly JobStorage _jobStorage;
    private readonly DemoJobService _jobService;

    public JobsController(
        IBackgroundJobClient backgroundJobs,
        IRecurringJobManager recurringJobs,
        JobStorage jobStorage,
        DemoJobService jobService)
    {
        _backgroundJobs = backgroundJobs;
        _recurringJobs = recurringJobs;
        _jobStorage = jobStorage;
        _jobService = jobService;
    }

    // ── 1. Fire-and-Forget (Email) ──
    [HttpPost("enqueue-email")]
    public IActionResult EnqueueEmail([FromBody] EmailRequest req)
    {
        var jobId = _backgroundJobs.Enqueue(() =>
            _jobService.SendWelcomeEmail(req.Email, req.UserName));
        return Ok(new { JobId = jobId, Message = "Welcome email enqueued" });
    }
    //[HttpPost("enqueue-email")]
    //public IActionResult EnqueueEmail([FromBody] EmailRequest req)
    //{
    //    var client = new BackgroundJobClient(_jobStorage);
    //    var state = new EnqueuedState("testing");

    //    var jobId = client.Create(() =>
    //        _jobService.SendWelcomeEmail(req.Email, req.UserName), state);

    //    return Ok(new { JobId = jobId, Queue = "testing", Message = "Welcome email enqueued to testing" });
    //}

    // ── 2. Delayed Job (Report) ──
    [HttpPost("schedule-report")]
    public IActionResult ScheduleReport()
    {
        var jobId = _backgroundJobs.Schedule(() =>
            _jobService.GenerateMonthlyReport(DateTime.Now.Year, DateTime.Now.Month),
            TimeSpan.FromSeconds(15));
        return Ok(new { JobId = jobId, Message = "Report scheduled in 15 seconds" });
    }

    // ── 3. Recurring Job (Cleanup) ──
    //[HttpPost("recurring-cleanup")]
    //public IActionResult RecurringCleanup()
    //{
    //    _recurringJobs.AddOrUpdate("cleanup-old-logs",
    //        () => _jobService.CleanupOldLogFiles(30),
    //        Cron.Minutely);
    //    return Ok(new { Message = "Recurring job 'cleanup-old-logs' added (every minute)" });
    //}


    // ── 3. Recurring Job (Custom Cron) ──
    [HttpPost("recurring-cleanup")]
    public IActionResult RecurringCleanup([FromBody] RecurringJobRequest? req)
    {
        var cron = req?.CronExpression ?? Cron.Minutely();
        var jobId = req?.JobId ?? "cleanup-old-logs";

        // Validate cron syntax roughly
        if (string.IsNullOrWhiteSpace(cron) || cron.Split(' ').Length < 5)
        {
            return BadRequest(new { Message = "Invalid cron expression. Format: * * * * *" });
        }

        _recurringJobs.AddOrUpdate(jobId,
            () => _jobService.CleanupOldLogFiles(30),
            cron);

        return Ok(new { JobId = jobId, Cron = cron, Message = $"Recurring job '{jobId}' added with schedule: {cron}" });
    }

    // ── 4. Failing Job ──
    [HttpPost("failing-job")]
    public IActionResult FailingJob()
    {
        var jobId = _backgroundJobs.Enqueue(() => _jobService.ProcessFailedPayment());
        return Ok(new { JobId = jobId, Message = "Failing payment job enqueued" });
    }

    // ── 5. Continuation (Chained Jobs) ──
    [HttpPost("continuation")]
    public IActionResult Continuation()
    {
        var parentId = _backgroundJobs.Enqueue(() =>
            _jobService.GenerateMonthlyReport(2026, 8));
        var childId = _backgroundJobs.ContinueJobWith(parentId,
            () => _jobService.NotifyAdmin("Monthly report is ready"));
        return Ok(new { ParentJobId = parentId, ChildJobId = childId, Message = "Continuation chain created" });
    }

    // ── 6. Custom Queue (Critical) ──
    [HttpPost("custom-queue")]
    public IActionResult CustomQueue()
    {
        var client = new BackgroundJobClient(_jobStorage);
        var state = new EnqueuedState("critical");
        var jobId = client.Create(() => _jobService.ProcessCriticalOrder("ORDER-911"), state);
        return Ok(new { JobId = jobId, Queue = "critical", Message = "Critical job enqueued" });
    }

    // ── 7. Bulk Enqueue (Tests ExpirationManager Trimming) ──
    [HttpPost("bulk-emails")]
    public IActionResult BulkEmails()
    {
        var jobIds = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var id = _backgroundJobs.Enqueue(() =>
                _jobService.SendWelcomeEmail($"user{i}@test.com", $"User {i}"));
            jobIds.Add(id);
        }
        return Ok(new { Count = jobIds.Count, JobIds = jobIds, Message = "100 email jobs enqueued" });
    }

    // ── 10. Long-Running Job (So you can delete it from Dashboard) ──
    [HttpPost("long-job")]
    public IActionResult LongJob()
    {
        var jobId = _backgroundJobs.Enqueue(() => _jobService.LongRunningJob());
        return Ok(new { JobId = jobId, Message = "30-second job enqueued. Delete it from /hangfire" });
    }

    // ── 11. Delete Job Programmatically ──
    [HttpDelete("delete/{jobId}")]
    public IActionResult DeleteJob(string jobId)
    {
        _backgroundJobs.Delete(jobId);
        return Ok(new { JobId = jobId, Message = "Job deleted" });
    }

    // ── 12. Stats ──
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var monitoring = _jobStorage.GetMonitoringApi();
        var stats = monitoring.GetStatistics();
        var queues = monitoring.Queues();

        return Ok(new
        {
            Stats = stats,
            Queues = queues.Select(q => new { q.Name, q.Length, q.Fetched })
        });
    }
}

