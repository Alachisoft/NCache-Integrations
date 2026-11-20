using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NCache.OSS.Quartz.Demo.Jobs;
using NCache.OSS.Quartz.Demo.Models;
using NCache.OSS.Quartz.Demo.Services;
using Quartz;
using Quartz.Impl.Calendar;

namespace NCache.OSS.Quartz.Demo.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IScheduler _scheduler;
    private readonly ExecutionLog _log;
    private readonly TimeSpan _misfireThreshold;

    public JobsController(ISchedulerFactory schedulerFactory, ExecutionLog log, IConfiguration configuration)
    {
        _scheduler = schedulerFactory.GetScheduler().GetAwaiter().GetResult();
        _log = log;
        _misfireThreshold = configuration.GetValue<TimeSpan?>("Quartz:NCache:MisfireThreshold") ?? TimeSpan.FromSeconds(60);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private static TimeOnly TruncateToSeconds(TimeOnly t) => new(t.Hour, t.Minute, t.Second);

    [HttpPost("enqueue-email")]
    public async Task<IActionResult> EnqueueEmail([FromBody] EmailRequest req)
    {
        var name = Unique("email");

        var job = JobBuilder.Create<SendWelcomeEmailJob>()
            .WithIdentity(name, "emails")
            .UsingJobData(SendWelcomeEmailJob.EmailKey, req.Email)
            .UsingJobData(SendWelcomeEmailJob.UserNameKey, req.UserName)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "emails")
            .StartNow()
            .Build();

        var firstFire = await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), FirstFireAt = firstFire, Message = "Welcome email scheduled to run now" });
    }
     
    [HttpPost("schedule-report")]
    public async Task<IActionResult> ScheduleReport()
    {
        var name = Unique("report");

        var job = JobBuilder.Create<GenerateMonthlyReportJob>()
            .WithIdentity(name, "reports")
            .UsingJobData(GenerateMonthlyReportJob.YearKey, DateTime.Now.Year)
            .UsingJobData(GenerateMonthlyReportJob.MonthKey, DateTime.Now.Month)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "reports")
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(15))
            .Build();

        var firstFire = await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), FirstFireAt = firstFire, Message = "Report scheduled in 15 seconds" });
    }

    [HttpPost("recurring-cleanup")]
    public async Task<IActionResult> RecurringCleanup([FromBody] RecurringJobRequest? req)
    {
        var cron = string.IsNullOrWhiteSpace(req?.CronExpression) ? "0 * * * * ?" : req.CronExpression.Trim();
        var jobId = string.IsNullOrWhiteSpace(req?.JobId) ? "cleanup-old-logs" : req.JobId.Trim();

        if (!CronExpression.TryParse(cron, out _))
        {
            return BadRequest(new { Message = $"'{cron}' is not a valid Quartz cron expression. Format: sec min hour day-of-month month day-of-week [year], e.g. 0 * * * * ?" });
        }

        var job = JobBuilder.Create<CleanupOldLogFilesJob>()
            .WithIdentity(jobId, "recurring")
            .StoreDurably()
            .UsingJobData(CleanupOldLogFilesJob.DaysToKeepKey, 30)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(jobId, "recurring")
            .ForJob(job.Key)
            .WithCronSchedule(cron, x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();

        await _scheduler.AddJob(job, new AddJobOptions { Replace = true });

        var rescheduled = await _scheduler.Exists(trigger.Key);

        DateTimeOffset? firstFire = rescheduled
            ? await _scheduler.RescheduleJob(trigger.Key, trigger)
            : await _scheduler.ScheduleJob(trigger);

        return Ok(new
        {
          JobKey = job.Key.ToString(),
            Cron = cron,
            FirstFireAt = firstFire,
            Rescheduled = rescheduled,
            Message = rescheduled
                ? $"Recurring job '{jobId}' rescheduled in place to: {cron} (previous fire time kept)"
                : $"Recurring job '{jobId}' registered with schedule: {cron}"
        });
    }

    [HttpPost("update-details")]
    public async Task<IActionResult> UpdateDetails([FromBody] UpdateDetailsRequest req)
    {
        var key = new TriggerKey(req.Name, req.Group);

        if (!await _scheduler.Exists(key))
        {
            return NotFound(new { TriggerKey = key.ToString(), Message = "No such trigger — register the recurring job first" });
        }

        try
        {
            await _scheduler.UpdateTriggerDetails(key, new TriggerDetailsUpdate().WithPriority(req.Priority));
            return Ok(new { TriggerKey = key.ToString(), Supported = true, Message = $"Priority set to {req.Priority} without rescheduling" });
        }
        catch (NotSupportedException ex)
        {
            return Ok(new
            {
                TriggerKey = key.ToString(),
                Supported = false,
                Message = "UpdateTriggerDetails is not supported by the NCache store (expected). Use RescheduleJob / ReplaceTrigger instead, as the Recurring card does.",
                Detail = ex.Message
            });
        }
    }

    // Changes a stored trigger's priority the way Quartz always allowed: rebuild it from its own builder and swap it in with RescheduleJob.
    [HttpPost("update-details-reschedule")]
    public async Task<IActionResult> UpdateDetailsByReschedule([FromBody] UpdateDetailsRequest req)
    {
        var key = new TriggerKey(req.Name, req.Group);
        var stored = await _scheduler.GetTrigger(key);

        if (stored is null)
        {
            return NotFound(new { TriggerKey = key.ToString(), Message = "No such trigger — register the recurring job first" });
        }

        var updated = stored.GetTriggerBuilder()
            .WithPriority(req.Priority)
            .StartNow()
            .Build();

        var nextFire = await _scheduler.RescheduleJob(key, updated);
        var after = await _scheduler.GetTrigger(key);

        return Ok(new
        {
            TriggerKey = key.ToString(),
            PriorityBefore = stored.Priority,
            PriorityAfter = after?.Priority,
            LastFireKept = after?.PreviousFireTimeUtc?.ToLocalTime().ToString("HH:mm:ss"),
            NextFireAt = nextFire,
            Message = $"Priority {stored.Priority} -> {after?.Priority} by rebuilding the trigger (GetTriggerBuilder) and RescheduleJob; the store's ReplaceTrigger kept its state, pause and last fire time"
        });
    }

    [HttpPost("failing-job")]
    public async Task<IActionResult> FailingJob()
    {
        var name = Unique("payment");

        var job = JobBuilder.Create<ProcessFailedPaymentJob>()
            .WithIdentity(name, "payments")
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "payments")
            .StartNow()
            .WithRetryPolicy(RetryPolicy.Fixed(3, TimeSpan.FromSeconds(5)))
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Message = "Failing payment job scheduled: it will fail, then retry 3 times 5s apart" });
    }

    [HttpPost("continuation")]
    public async Task<IActionResult> Continuation([FromBody] ContinuationRequest? req)
    {
        var parentFails = req?.ParentFails == true;
        var name = Unique("chain");

        var parentJob = parentFails
            ? JobBuilder.Create<ProcessFailedPaymentJob>().WithIdentity(name, "chain").Build()
            : JobBuilder.Create<GenerateMonthlyReportJob>()
                .WithIdentity(name, "chain")
                .UsingJobData(GenerateMonthlyReportJob.YearKey, DateTime.Now.Year)
                .UsingJobData(GenerateMonthlyReportJob.MonthKey, DateTime.Now.Month)
                .Build();

        var parent = TriggerBuilder.Create()
            .WithIdentity(name, "chain")
            .StartNow()
            .Build();

        var notify = JobBuilder.Create<NotifyAdminJob>()
            .WithIdentity($"{name}-notify", "chain")
            .UsingJobData(NotifyAdminJob.MessageKey, $"report {name} is ready (released because the report succeeded)")
            .Build();

        var notifyTrigger = TriggerBuilder.Create()
            .WithIdentity($"{name}-notify", "chain")
            .StartAfter(parent.Key)
            .Build();

        var alert = JobBuilder.Create<NotifyAdminJob>()
            .WithIdentity($"{name}-alert", "chain")
            .UsingJobData(NotifyAdminJob.MessageKey, $"report {name} did not get there (released because it failed)")
            .Build();

        var alertTrigger = TriggerBuilder.Create()
            .WithIdentity($"{name}-alert", "chain")
            .StartAfter(parent.Key, ContinuationCondition.OnFailure | ContinuationCondition.OnCancellation)
            .Build();

        await _scheduler.ScheduleJobs(new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>
        {
            [parentJob] = new[] { parent },
            [notify] = new[] { notifyTrigger },
            [alert] = new[] { alertTrigger }
        }, new ScheduleJobOptions { Replace = false });

        return Ok(new
        {
            ParentTrigger = parent.Key.ToString(),
            OnSuccess = notifyTrigger.Key.ToString(),
            OnFailure = alertTrigger.Key.ToString(),
            Message = parentFails
                ? "Parent runs now and fails: the alert (OnFailure) is released and runs, the notification (OnSuccess) is discarded. Both show as Awaiting until then."
                : "Parent runs now and succeeds: the notification (OnSuccess) is released and runs, the alert (OnFailure) is discarded. Both show as Awaiting until then."
        });
    }
    [HttpPost("non-concurrent")]
    public async Task<IActionResult> NonConcurrent()
    {
        var name = Unique("critical");

        var job = JobBuilder.Create<ProcessCriticalOrderJob>()
            .WithIdentity(name, "critical")
            .UsingJobData(ProcessCriticalOrderJob.OrderIdKey, "ORDER-911")
            .Build();

        var triggers = new[] { "a", "b" }.Select(suffix => (ITrigger) TriggerBuilder.Create()
            .WithIdentity($"{name}-{suffix}", "critical")
            .StartNow()
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(1)).WithRepeatCount(4).WithMisfireInstruction(SimpleTriggerMisfireInstruction.NowWithRemainingCount))
            .Build()).ToList();

        await _scheduler.ScheduleJob(job, triggers, new ScheduleJobOptions { Replace = false });
        return Ok(new { JobKey = job.Key.ToString(), Triggers = triggers.Select(t => t.Key.ToString()), Message = "Non-concurrent job with two triggers, 5 occurrences each 1s apart, each run ~1s: while one trigger's run is in progress the other shows Blocked, and nothing ever overlaps" });
    }

    [HttpPost("priority")]
    public async Task<IActionResult> Priority()
    {
        var fireAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var batch = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        foreach (var (label, priority) in new[] { ("low", 1), ("normal", 5), ("high", 10) })
        {
            var job = JobBuilder.Create<SendWelcomeEmailJob>()
                .WithIdentity($"{label}-priority-{suffix}", "priority")
                .UsingJobData(SendWelcomeEmailJob.EmailKey, $"{label}@priority.test")
                .UsingJobData(SendWelcomeEmailJob.UserNameKey, $"{label} priority ({priority})") 
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"{label}-priority-{suffix}", "priority")
                .StartAt(fireAt)
                .WithPriority(priority)
                .WithExecutionGroup("serial")
                .Build();

            batch[job] = new[] { trigger };
        }

        await _scheduler.ScheduleJobs(batch, new ScheduleJobOptions { Replace = false });
        return Ok(new { FireAt = fireAt, Message = "3 jobs due at the same instant with priorities 1, 5 and 10 in the 'serial' execution group (limit 1) — they run high → normal → low" });
    }

    [HttpPost("bulk-emails")]
    public async Task<IActionResult> BulkEmails()
    {
        const int count = 25;
        var batch = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < count; i++)
        {
            var job = JobBuilder.Create<SendWelcomeEmailJob>()
                .WithIdentity($"bulk-{suffix}-{i:D3}", "emails")
                .UsingJobData(SendWelcomeEmailJob.EmailKey, $"user{i}@test.com")
                .UsingJobData(SendWelcomeEmailJob.UserNameKey, $"User {i}")
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"bulk-{suffix}-{i:D3}", "emails")
                .StartNow()
                .WithExecutionGroup("emails")
                .Build();

            batch[job] = new[] { trigger };
        }

        await _scheduler.ScheduleJobs(batch, new ScheduleJobOptions { Replace = false });
        return Ok(new { Count = count, Message = $"{count} email jobs scheduled in one batch; at most 3 run at a time per node ('emails' group, node scope)" });
    }
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok("alive");
    }
    [HttpPost("long-job")]
    public async Task<IActionResult> LongJob()
    {
        var name = Unique("long");

        var job = JobBuilder.Create<LongRunningJob>()
            .WithIdentity(name, "long")
            .UsingJobData(LongRunningJob.SecondsKey, 30)
            .RequestRecovery()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "long")
            .StartNow()
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Message = "30-second job scheduled (RequestsRecovery: kill this node mid-run and another node re-runs it). A running execution cannot be paused - its one-shot trigger has already fired for the last time - but it can be interrupted from the table below or from /quartz" });
    }

    [HttpPost("calendar-job")]
    public async Task<IActionResult> CalendarJob([FromBody] CalendarJobRequest req)
    {
        string calendarName;
        ICalendar calendar;
        string effect;

        if (string.Equals(req.Mode, "today", StringComparison.OrdinalIgnoreCase))
        {
            calendarName = "skip-today";
            var holiday = new HolidayCalendar();
            holiday.AddExcludedDay(DateOnly.FromDateTime(DateTime.Today));
            calendar = holiday;
            effect = "today is excluded, so the next fire time jumps to tomorrow 00:00";
        }
        else
        {
            calendarName = "weekdays-only";
            var weekly = new WeeklyCalendar();
            weekly.AddExcludedDay(DayOfWeek.Saturday);
            weekly.AddExcludedDay(DayOfWeek.Sunday);
            calendar = weekly;
            effect = DateTime.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? "it is the weekend, so the next fire time jumps to Monday"
                : "weekends are excluded; it fires every 30s today and skips Saturday/Sunday";
        }

        await _scheduler.AddCalendar(calendarName, calendar, new AddCalendarOptions { Replace = true, UpdateTriggers = true });

        var name = Unique("calendar");

        var job = JobBuilder.Create<CleanupOldLogFilesJob>()
            .WithIdentity(name, "calendar")
            .StoreDurably()
            .UsingJobData(CleanupOldLogFilesJob.DaysToKeepKey, 7)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "calendar")
            .ForJob(job.Key)
            .WithCalendarName(calendarName)
            .WithCronSchedule("*/30 * * * * ?")
            .Build();

        var firstFire = await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Calendar = calendarName, FirstFireAt = firstFire, Message = $"Cron trigger every 30s modified by calendar '{calendarName}': {effect}" });
    }

    [HttpPost("pinned-job")]
    public async Task<IActionResult> PinnedJob([FromBody] PinnedJobRequest req)
    {
        var auto = string.IsNullOrWhiteSpace(req.Node) || string.Equals(req.Node, "auto", StringComparison.OrdinalIgnoreCase);
        var preferred = auto ? PreferredNode.Auto : PreferredNode.For(req.Node.Trim());
        var name = auto ? "auto-pinned" : $"pinned-to-{req.Node.Trim()}";

        var job = JobBuilder.Create<NotifyAdminJob>()
            .WithIdentity(name, "affinity")
            .StoreDurably()
            .UsingJobData(NotifyAdminJob.MessageKey, auto ? "auto-pinned job ran" : $"job pinned to {req.Node} ran")
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "affinity")
            .ForJob(job.Key)
            .WithPreferredNode(preferred)
            .WithCronSchedule("*/10 * * * * ?", x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();

        await _scheduler.ScheduleJob(job, new[] { trigger }, new ScheduleJobOptions { Replace = true });

        return Ok(new
        {
            JobKey = job.Key.ToString(),
            PreferredNode = auto ? "Auto" : req.Node,
            Message = auto
                ? "Every 10s; the first node to fire it claims it and keeps it until that node dies"
                : $"Every 10s on '{req.Node}' only; other nodes skip it while '{req.Node}' is alive and take over if it dies"
        });
    }
     
    [HttpPost("crash")]
    public async Task<IActionResult> Crash()
    {
        var name = Unique("crash");
        var me = _scheduler.SchedulerInstanceId;

        var job = JobBuilder.Create<LongRunningJob>()
            .WithIdentity(name, "crash")
            .UsingJobData(LongRunningJob.SecondsKey, 20)
            .RequestRecovery()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "crash")
            .StartNow()
            .WithPreferredNode(PreferredNode.For(me))
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        _log.Write("CRASH", $"{me} will be killed in 4 seconds while {job.Key} is running — watch the other node", "warn");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Process.GetCurrentProcess().Kill();
        });

        return Ok(new { JobKey = job.Key.ToString(), Node = me, Message = $"20s recovery job started on {me}; this process dies in 4s. Watch the other node's log for [RECOVERING]" });
    }

    [HttpPost("misfire")]
    public async Task<IActionResult> Misfire([FromBody] MisfireRequest req)
    {
        if (!Enum.TryParse<CronTriggerMisfireInstruction>(req.Instruction, ignoreCase: true, out var instruction))
        {
            return BadRequest(new { Message = "Instruction must be FireAndProceed, DoNothing or IgnoreMisfires" });
        }

        var name = $"misfire-{instruction}".ToLowerInvariant();

        var job = JobBuilder.Create<CleanupOldLogFilesJob>()
            .WithIdentity(name, "misfire")
            .StoreDurably()
            .UsingJobData(CleanupOldLogFilesJob.DaysToKeepKey, 1)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "misfire")
            .ForJob(job.Key)
            .WithCronSchedule("*/10 * * * * ?", x => x.WithMisfireInstruction(instruction))
            .Build();

        await _scheduler.ScheduleJob(job, new[] { trigger }, new ScheduleJobOptions { Replace = true });
        await _scheduler.PauseTrigger(trigger.Key);

        var pausedFor = _misfireThreshold + TimeSpan.FromSeconds(12);
        _log.Write("MISFIRE", $"{trigger.Key} paused for {pausedFor.TotalSeconds:0}s (misfire threshold is {_misfireThreshold.TotalSeconds:0}s), instruction {instruction}", "warn");

        _ = Task.Run(async () =>
        {
            await Task.Delay(pausedFor);
            await _scheduler.ResumeTrigger(trigger.Key);
            var resumed = await _scheduler.GetTrigger(trigger.Key);
            _log.Write("MISFIRE", $"{trigger.Key} resumed; {instruction} → next fire {resumed?.NextFireTimeUtc?.ToLocalTime():HH:mm:ss}", "warn");
        });

        return Ok(new { TriggerKey = trigger.Key.ToString(), Instruction = instruction.ToString(), MisfireThresholdSeconds = _misfireThreshold.TotalSeconds, ResumesInSeconds = pausedFor.TotalSeconds, Message = $"Paused; resumes automatically in {pausedFor.TotalSeconds:0}s, then the misfire instruction decides what happens" });
    }

    // Stores two equal jobs paused, one trigger with its own short misfire threshold, and resumes both equally late: only that one misfires. 4.6
    [HttpPost("misfire-threshold")]
    public async Task<IActionResult> MisfireThreshold()
    {
        var name = Unique("threshold");
        var ownThreshold = TimeSpan.FromSeconds(3);
        var startAt = DateTimeOffset.UtcNow.AddSeconds(1);
        var late = TimeSpan.FromSeconds(Math.Round((ownThreshold + _misfireThreshold).TotalSeconds / 2));
        var batch = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();

        foreach (var (suffix, threshold) in new[] { ("own", (TimeSpan?) ownThreshold), ("store", null) })
        {
            var job = JobBuilder.Create<CleanupOldLogFilesJob>()
                .WithIdentity($"{name}-{suffix}", "misfire-threshold")
                .UsingJobData(CleanupOldLogFilesJob.DaysToKeepKey, 1)
                .Build();

            var builder = TriggerBuilder.Create()
                .WithIdentity($"{name}-{suffix}", "misfire-threshold")
                .StartAt(startAt)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(30)).RepeatForever().WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount));

            if (threshold is { } own)
            {
                builder = builder.WithMisfireThreshold(own);
            }

            batch[job] = new[] { builder.Build() };
        }

        await _scheduler.ScheduleJobs(batch, new ScheduleJobOptions { PauseReason = $"held {late.TotalSeconds:0}s past its fire time", PauseRequestedBy = "misfire-threshold demo" });
        _log.Write("MISFIRE", $"{name}-own (own threshold {ownThreshold.TotalSeconds:0}s) and {name}-store (store threshold {_misfireThreshold.TotalSeconds:0}s) stored paused; both resume {late.TotalSeconds:0}s after their fire time", "warn");

        _ = Task.Run(async () =>
        {
            await Task.Delay(startAt + late - DateTimeOffset.UtcNow);

            foreach (var job in batch.Keys)
            {
                await _scheduler.ResumeJob(job.Key);
            }

            var own = await _scheduler.GetTrigger(new TriggerKey($"{name}-own", "misfire-threshold"));
            _log.Write("MISFIRE", $"resumed {late.TotalSeconds:0}s late: {name}-own is past its {ownThreshold.TotalSeconds:0}s threshold, misfired and skips to {own?.NextFireTimeUtc?.ToLocalTime():HH:mm:ss}; {name}-store is within the store's {_misfireThreshold.TotalSeconds:0}s and runs now", "warn");
        });

        return Ok(new
        {
            Jobs = batch.Keys.Select(j => j.Key.ToString()),
            OwnThresholdSeconds = ownThreshold.TotalSeconds,
            StoreThresholdSeconds = _misfireThreshold.TotalSeconds,
            ResumesLateBySeconds = late.TotalSeconds,
            Message = $"Two identical jobs stored paused; both resume {late.TotalSeconds:0}s after their fire time. Only '-store' runs then: '-own' has its own {ownThreshold.TotalSeconds:0}s misfire threshold, so it counts as misfired and skips to its next occurrence"
        });
    }

    // Schedules a batch of 'reports' and 'emails' jobs so the log shows which node each group's runs land on. 4.6
    [HttpPost("execution-groups")]
    public async Task<IActionResult> ExecutionGroups()
    {
        var meta = await _scheduler.GetMetadata();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var batch = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();

        foreach (var (group, index) in new[] { "reports", "emails" }.SelectMany(g => Enumerable.Range(1, 4).Select(i => (g, i))))
        {
            var job = JobBuilder.Create<NotifyAdminJob>()
                .WithIdentity($"{group}-{suffix}-{index}", "execution-groups")
                .UsingJobData(NotifyAdminJob.MessageKey, $"'{group}' job {index} ran")
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"{group}-{suffix}-{index}", "execution-groups")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(index))
                .WithExecutionGroup(group)
                .Build();

            batch[job] = new[] { trigger };
        }

        await _scheduler.ScheduleJobs(batch, new ScheduleJobOptions { Replace = false });

        var refused = meta.RefusedExecutionGroups is { Length: > 0 } groups ? string.Join(", ", groups) : null;
        _log.Write("ROUTING", $"{meta.SchedulerInstanceId} {(refused is null ? "serves every execution group" : $"refuses: {refused}")}; scheduled 4 'reports' + 4 'emails' jobs", "info");

        return Ok(new
        {
            Node = meta.SchedulerInstanceId,
            RefusedExecutionGroups = meta.RefusedExecutionGroups,
            ServedExecutionGroups = meta.ServedExecutionGroups,
            Message = "4 'reports' and 4 'emails' jobs, 1s apart. With both nodes up, 'reports' only ever runs on demo-node-1 (the second-node profile refuses it); 'emails' runs on either. Each [RUN] line in the log names the node."
        });
    }

    [HttpPost("finite-job")]
    public async Task<IActionResult> FiniteJob()
    {
        var name = Unique("finite");

        var job = JobBuilder.Create<SendWelcomeEmailJob>()
            .WithIdentity(name, "finite")
            .StoreDurably()
            .UsingJobData(SendWelcomeEmailJob.EmailKey, "finite@test.com")
            .UsingJobData(SendWelcomeEmailJob.UserNameKey, "Finite")
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "finite")
            .StartNow()
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(5)).WithRepeatCount(2))
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Message = "Fires 3 times 5s apart; after the last one Quartz deletes the trigger and the durable job stays without triggers" });
    }

    [HttpPost("complete-job")]
    public async Task<IActionResult> CompleteJob()
    {
        var name = Unique("complete");

        var job = JobBuilder.Create<CompleteItselfJob>()
            .WithIdentity(name, "finite")
            .StoreDurably()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "finite")
            .StartNow()
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(10)).RepeatForever())
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Message = "Repeating trigger whose job unschedules it on the first run: the trigger stays in the store as Complete" });
    }

    [HttpPost("interval-job")]
    public async Task<IActionResult> IntervalJob([FromBody] IntervalJobRequest req)
    {
        var kind = (req.Kind ?? "daily").ToLowerInvariant();
        var name = Unique(kind);

        var job = JobBuilder.Create<NotifyAdminJob>()
            .WithIdentity(name, "kinds")
            .StoreDurably()
            .UsingJobData(NotifyAdminJob.MessageKey, $"{kind} trigger fired")
            .Build();

        var builder = TriggerBuilder.Create().WithIdentity(name, "kinds").ForJob(job.Key);
        string description;

        switch (kind)
        {
            case "daily":
            {
                var start = TruncateToSeconds(TimeOnly.FromDateTime(DateTime.Now.AddMinutes(-1)));
                var end = TruncateToSeconds(TimeOnly.FromDateTime(DateTime.Now.AddHours(1)));

                builder = builder.WithDailyTimeIntervalSchedule(x => x
                    .WithInterval(30, IntervalUnit.Second)
                    .StartingDailyAt(start)
                    .EndingDailyAt(end)
                    .OnEveryDay()
                    .WithMisfireInstruction(DailyTimeIntervalTriggerMisfireInstruction.DoNothing));

                description = $"DailyTimeIntervalTrigger: every 30s between {start:HH:mm} and {end:HH:mm} local time, every day";
                break;
            }

            case "calendar":
                builder = builder
                    .StartNow()
                    .WithCalendarIntervalSchedule(x => x
                        .WithInterval(1, IntervalUnit.Minute)
                        .WithMisfireInstruction(CalendarIntervalTriggerMisfireInstruction.DoNothing));
                description = "CalendarIntervalTrigger: every 1 minute (calendar-aware interval, honours DST)";
                break;

            case "recurrence":
                builder = builder
                    .StartNow()
                    .WithRecurrenceSchedule("FREQ=MINUTELY;INTERVAL=1");
                description = "RecurrenceTrigger: RRULE FREQ=MINUTELY;INTERVAL=1";
                break;

            default:
                return BadRequest(new { Message = "Kind must be daily, calendar or recurrence" });
        }

        var trigger = builder.Build();
        var firstFire = await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Kind = kind, FirstFireAt = firstFire, Message = description });
    }

    [HttpPost("overlap")]
    public async Task<IActionResult> Overlap([FromBody] OverlapRequest req)
    {
        if (!Enum.TryParse<OverlapPolicy>(req.Policy, ignoreCase: true, out var policy) || !Enum.IsDefined(policy))
        {
            return BadRequest(new { Message = "Policy must be Default, Skip, BufferOne, CancelPrevious or AllowAll" });
        }

        var name = Unique($"overlap-{policy}".ToLowerInvariant());

        var job = JobBuilder.Create<SlowTickJob>()
            .WithIdentity(name, "overlap")
            .UsingJobData(SlowTickJob.SecondsKey, 5)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "overlap")
            .StartNow()
            .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(2)).WithRepeatCount(5).WithMisfireInstruction(SimpleTriggerMisfireInstruction.NowWithRemainingCount))
            .WithOverlapPolicy(policy)
            .Build();

        await _scheduler.ScheduleJob(job, trigger);

        var effect = policy switch
        {
            OverlapPolicy.Skip => "an occurrence that comes due while a run is in progress is dropped (SKIP in the log) and the trigger moves on",
            OverlapPolicy.BufferOne => "the trigger is Blocked while its run is in progress, then fires the occurrence it held back: runs follow each other and never overlap",
            OverlapPolicy.CancelPrevious => "each new occurrence interrupts the run before it (outcome Cancelled) and takes its place",
            _ => "runs overlap: every occurrence starts on time beside the ones still running"
        };

        return Ok(new { JobKey = job.Key.ToString(), Policy = policy.ToString(), Message = $"6 occurrences 2s apart, each run takes 5s, overlap policy {policy}: {effect}" });
    }

    [HttpPost("progress-job")]
    public async Task<IActionResult> ProgressJob()
    {
        var name = Unique("progress");

        var job = JobBuilder.Create<ProgressReportingJob>()
            .WithIdentity(name, "progress")
            .UsingJobData(ProgressReportingJob.StepsKey, 20)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, "progress")
            .StartNow()
            .Build();

        await _scheduler.ScheduleJob(job, trigger);
        return Ok(new { JobKey = job.Key.ToString(), Message = "20-step job started; it reports its progress every second and the store keeps the latest report on the firing record (see the job's row, from any node)" });
    }

    [HttpPost("schedule-conflict")]
    public async Task<IActionResult> ScheduleConflict([FromBody] ConflictRequest req)
    {
        if (!Enum.TryParse<TriggerConflict>(req.Rule, ignoreCase: true, out var rule) || !Enum.IsDefined(rule))
        {
            return BadRequest(new { Message = "Rule must be Throw, Replace, Keep or KeepEarlier" });
        }

        var job = JobBuilder.Create<NotifyAdminJob>()
            .WithIdentity("debounced", "conflict")
            .StoreDurably()
            .UsingJobData(NotifyAdminJob.MessageKey, "the debounced job ran")
            .Build();

        await _scheduler.AddJob(job, new AddJobOptions { Replace = true });

        var fireAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(req.DelaySeconds, 1, 3600));

        var trigger = TriggerBuilder.Create()
            .WithIdentity("debounced", "conflict")
            .ForJob(job.Key)
            .StartAt(fireAt)
            .Build();

        try
        {
            var result = await _scheduler.ScheduleTrigger(trigger, rule);

            return Ok(new
            {
                TriggerKey = trigger.Key.ToString(),
                Rule = rule.ToString(),
                Outcome = result.Outcome.ToString(),
                Requested = fireAt.ToLocalTime().ToString("HH:mm:ss"),
                FiresAt = result.NextFireTimeUtc.ToLocalTime().ToString("HH:mm:ss"),
                Message = result.Outcome switch
                {
                    ScheduleOutcome.Created => "Nothing was stored under this key: the trigger was created",
                    ScheduleOutcome.Replaced => "The stored trigger was replaced by this one",
                    _ => "A pending trigger was already stored and was kept; this one was not stored"
                }
            });
        }
        catch (ObjectAlreadyExistsException ex)
        {
            return Conflict(new { TriggerKey = trigger.Key.ToString(), Rule = rule.ToString(), Outcome = "Refused", Message = ex.Message });
        }
    }

    [HttpDelete("delete/{group}/{name}")]
    public async Task<IActionResult> DeleteJob(string group, string name)
    {
        var deleted = await _scheduler.DeleteJob(new JobKey(name, group));
        return Ok(new { JobKey = $"{group}.{name}", Deleted = deleted, Message = deleted ? "Job and its triggers deleted" : "No such job" });
    }

    [HttpPost("pause/{group}/{name}")]
    public async Task<IActionResult> PauseJob(string group, string name, [FromBody] PauseRequest? req = null)
    {
        var jobKey = new JobKey(name, group);
        var exists = await _scheduler.PauseJobWith(jobKey, ToDetails(req));
        var triggers = await _scheduler.GetTriggersOfJob(jobKey);
        var states = new Dictionary<string, TriggerState>();

        foreach (var trigger in triggers)
        {
            states[trigger.Key.ToString()] = await _scheduler.GetTriggerState(trigger.Key);
        }

        var paused = states.Values.Count(s => s == TriggerState.Paused);

        return Ok(new
        {
            JobKey = jobKey.ToString(),
            Exists = exists,
            Paused = paused,
            Triggers = states.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            Message = !exists
                ? "No such job"
                : paused == states.Count && paused > 0
                    ? "Job paused: its triggers will not fire until resumed (a run already in progress finishes)"
                    : paused > 0
                        ? $"{paused} of {states.Count} triggers paused; the others are executing their last occurrence, complete, or waiting on a parent"
                        : states.Count == 0
                            ? "Nothing to pause: the job has no triggers"
                            : "Nothing to pause: a running one-shot has already fired for the last time (interrupt it instead), and a continuation waiting on its parent cannot be paused"
        });
    }

    [HttpPost("resume/{group}/{name}")]
    public async Task<IActionResult> ResumeJob(string group, string name)
    {
        var jobKey = new JobKey(name, group);
        var resumed = await _scheduler.ResumeJob(jobKey);

        return Ok(new
        {
            JobKey = jobKey.ToString(),
            Resumed = resumed,
            Message = resumed
                ? "Job resumed (misfired occurrences are handled by each trigger's misfire policy)"
                : "Nothing to resume: no trigger of this job was paused"
        });
    }

    [HttpPost("interrupt/{group}/{name}")]
    public async Task<IActionResult> InterruptJob(string group, string name)
    {
        var jobKey = new JobKey(name, group);
        var interrupted = await _scheduler.Interrupt(jobKey);

        return Ok(new
        {
            JobKey = jobKey.ToString(),
            Interrupted = interrupted,
            Message = interrupted
                ? "Interrupt requested: the running execution's cancellation token was signalled on this node"
                : "Nothing to interrupt: no execution of this job is running on this node (an execution on another node must be interrupted there)"
        });
    }

    [HttpPost("pause-all")]
    public async Task<IActionResult> PauseAll([FromBody] PauseRequest? req = null)
    {
        var details = ToDetails(req);
        await _scheduler.PauseAllWith(details);
        return Ok(new { Message = details is null ? "All triggers paused" : $"All triggers paused; recorded reason '{details.Reason}' by '{details.RequestedBy}'" });
    }

    private static PauseDetails? ToDetails(PauseRequest? req)
        => string.IsNullOrWhiteSpace(req?.Reason) && string.IsNullOrWhiteSpace(req?.RequestedBy)
            ? null
            : new PauseDetails { Reason = req!.Reason, RequestedBy = req.RequestedBy };

    [HttpPost("resume-all")]
    public async Task<IActionResult> ResumeAll()
    {
        await _scheduler.ResumeAll();
        return Ok(new { Message = "All triggers resumed" });
    }

    [HttpPost("clear")]
    public async Task<IActionResult> Clear()
    {
        await _scheduler.Clear();
        _log.Write("CLEAR", "All jobs, triggers and calendars removed from the store", "warn");
        return Ok(new { Message = "Store cleared" });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var meta = await _scheduler.GetMetadata();
        var jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        var nodes = await _scheduler.QueryClusterNodes();

        var executing = (await _scheduler.QueryFireInstances(new FireInstanceQuery { State = FireInstanceState.Executing, Take = 1000 })).Items;

        var jobs = new List<object>();
        var triggerCount = 0;
        var paused = 0;
        var blocked = 0;
        var errors = 0;
        var awaiting = 0;

        foreach (var jobKey in jobKeys.OrderBy(k => k.Group).ThenBy(k => k.Name))
        {
            var detail = await _scheduler.GetJobDetail(jobKey);
            var triggers = await _scheduler.GetTriggersOfJob(jobKey);
            var triggerRows = new List<object>();

            foreach (var trigger in triggers)
            {
                var state = await _scheduler.GetTriggerState(trigger.Key);
                var pause = state == TriggerState.Paused ? await _scheduler.GetTriggerPause(trigger.Key) : null;
                triggerCount++;

                switch (state)
                {
                    case TriggerState.Paused: paused++; break;
                    case TriggerState.Blocked: blocked++; break;
                    case TriggerState.Error: errors++; break;
                    case TriggerState.Awaiting: awaiting++; break;
                }

                triggerRows.Add(new
                {
                    Key = trigger.Key.ToString(),
                    State = state.ToString(),
                    Type = trigger switch
                    {
                        ICronTrigger c => $"cron {c.CronExpressionString}",
                        ISimpleTrigger s => s.RepeatCount == 0 ? "one-shot" : $"simple every {s.RepeatInterval.TotalSeconds:0}s × {(s.RepeatCount < 0 ? "∞" : (s.RepeatCount + 1).ToString())}",
                        IDailyTimeIntervalTrigger d => $"daily every {d.RepeatInterval} {d.RepeatIntervalUnit.ToString().ToLowerInvariant()} {d.StartTimeOfDay:HH:mm}–{d.EndTimeOfDay:HH:mm}",
                        ICalendarIntervalTrigger ci => $"calendar-interval every {ci.RepeatInterval} {ci.RepeatIntervalUnit.ToString().ToLowerInvariant()}",
                        IRecurrenceTrigger r => $"rrule {r.RecurrenceRule}",
                        _ => trigger.GetType().Name
                    },
                    NextFireAt = trigger.NextFireTimeUtc?.ToLocalTime().ToString("HH:mm:ss"),
                    PreviousFireAt = trigger.PreviousFireTimeUtc?.ToLocalTime().ToString("HH:mm:ss"),
                    trigger.Priority,
                    Calendar = trigger.CalendarName,
                    Node = trigger.PreferredNode.IsAutomatic ? "auto" : trigger.PreferredNode.Node,
                    ExecutionGroup = trigger.ExecutionGroup,
                    RetryAttempt = trigger.RetryAttempt,
                    RetryPolicy = trigger.RetryPolicy?.ToString(),
                    WaitsFor = trigger.Continuation.IsNone ? null : $"{trigger.Continuation.Parent} ({trigger.Continuation.When})",
                    Overlap = trigger.OverlapPolicy == OverlapPolicy.Default ? null : trigger.OverlapPolicy.ToString(),
                    MisfireThreshold = trigger.MisfireThreshold?.TotalSeconds,
                    PauseReason = pause?.Reason,
                    PausedBy = pause?.RequestedBy,
                    PausedAt = pause?.PausedAtUtc.ToLocalTime().ToString("HH:mm:ss")
                });
            }

            var running = executing.Where(e => jobKey.Equals(e.JobKey)).ToList();
            var reporting = running.Where(e => e.Progress.HasValue).OrderByDescending(e => e.FireTimeUtc).FirstOrDefault();

            jobs.Add(new
            {
                Key = jobKey.ToString(),
                jobKey.Group,
                jobKey.Name,
                JobType = detail?.JobType.FullName.Split('.').Last(),
                Durable = detail?.Durable,
                NonConcurrent = detail?.ConcurrentExecutionDisallowed,
                RequestsRecovery = detail?.RequestsRecovery,
                Executing = running.Count > 0,
                Running = running.Count,
                Progress = reporting?.Progress,
                ProgressMessage = reporting?.ProgressMessage,
                Triggers = triggerRows
            });
        }

        return Ok(new
        {
            Scheduler = new
            {
                meta.SchedulerName,
                meta.SchedulerInstanceId,
                Status = meta.Status.ToString(),
                meta.RunningSince,
                meta.JobsExecuted,
                meta.ThreadPoolSize,
                JobStore = meta.JobStoreTypeName.Split(',')[0].Split('.').Last(),
                meta.JobStoreClustered,
                meta.JobStorePersistent
            },
            Stats = new
            {
                Jobs = jobKeys.Count,
                Triggers = triggerCount,
                Executing = executing.Count,
                ExecutingHere = meta.LocalExecutingJobs,
                Fired = meta.JobsExecuted,
                Completed = _log.Completed,
                Failed = _log.Failed,
                Paused = paused,
                Blocked = blocked,
                Errors = errors,
                Awaiting = awaiting,
                Nodes = nodes.Count
            },
            Nodes = nodes.Select(n => new
            {
                n.InstanceId,
                State = n.State.ToString(),
                n.IsCurrentNode,
                LastCheckIn = n.LastCheckInUtc?.ToLocalTime().ToString("HH:mm:ss"),
                CheckInInterval = n.CheckInInterval?.TotalSeconds
            }),
            Executing = executing.Select(e => new
            {
                Job = e.JobKey?.ToString(),
                Trigger = e.TriggerKey.ToString(),
                e.FireInstanceId,
                Node = e.SchedulerInstanceId,
                e.Progress,
                e.ProgressMessage,
                RunningFor = (DateTimeOffset.UtcNow - e.FireTimeUtc).TotalSeconds
            }),
            Jobs = jobs
        });
    }

    [HttpGet("log")]
    public IActionResult GetLog([FromQuery] long since = 0)
    {
        var (latest, entries) = _log.ReadSince(since);
        return Ok(new { Latest = latest, Entries = entries });
    }
}

