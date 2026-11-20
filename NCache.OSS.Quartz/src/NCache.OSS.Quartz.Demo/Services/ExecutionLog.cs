using Quartz;

namespace NCache.OSS.Quartz.Demo.Services;

public sealed record ExecutionLogEntry(long Sequence, DateTimeOffset At, string Level, string Tag, string Message);

public sealed class ExecutionLog : IJobListener, ITriggerListener
{
    private const int Capacity = 500;

    private readonly object _sync = new();
    private readonly LinkedList<ExecutionLogEntry> _entries = new();
    private long _sequence;
    private long _started;
    private long _completed;
    private long _failed;

    public string Name => "demo-execution-log";

    public long Started => Interlocked.Read(ref _started);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);

    public void Write(string tag, string message, string level = "success")
    {
        lock (_sync)
        {
            _entries.AddLast(new ExecutionLogEntry(++_sequence, DateTimeOffset.Now, level, tag, message));

            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }
    }

    public (long Latest, IReadOnlyList<ExecutionLogEntry> Entries) ReadSince(long sequence)
    {
        lock (_sync)
        {
            return (_sequence, _entries.Where(e => e.Sequence > sequence).ToList());
        }
    }

    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var attempt = context.RetryAttempt > 0 ? $" (retry attempt {context.RetryAttempt})" : string.Empty;
        var recovering = context.Recovering ? " [RECOVERING]" : string.Empty;

        var scheduled = context.ScheduledFireTimeUtc.HasValue ? $" [occurrence {context.ScheduledFireTimeUtc.Value.ToLocalTime():HH:mm:ss}]" : string.Empty;

        Interlocked.Increment(ref _started);
        Write("RUN", $"{context.JobDetail.Key} fired by {context.Trigger.Key} on {context.Scheduler.SchedulerInstanceId}{scheduled}{attempt}{recovering}", "info");
        return ValueTask.CompletedTask;
    }

    public ValueTask TriggerSkipped(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
    {
        Write("SKIP", $"{trigger.Key} due at {trigger.NextFireTimeUtc?.ToLocalTime():HH:mm:ss} was skipped on {scheduler.SchedulerInstanceId}: its earlier firing is still running (overlap policy {trigger.OverlapPolicy})", "warn");
        return ValueTask.CompletedTask;
    }

    public ValueTask TriggerMisfired(ITrigger trigger, IScheduler scheduler, CancellationToken cancellationToken = default)
    {
        Write("MISFIRE", $"{trigger.Key} misfired (was due {trigger.NextFireTimeUtc?.ToLocalTime():HH:mm:ss})", "warn");
        return ValueTask.CompletedTask;
    }

    public ValueTask TriggerRetriesExhausted(ITrigger trigger, IJobExecutionContext context, JobExecutionException exception, CancellationToken cancellationToken = default)
    {
        Write("RETRY", $"{trigger.Key} gave up after attempt {context.RetryAttempt}: {exception.GetBaseException().Message}", "error");
        return ValueTask.CompletedTask;
    }

    public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        Write("VETO", $"{context.JobDetail.Key} was vetoed", "warn");
        return ValueTask.CompletedTask;
    }

    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (jobException is not null)
        {
            var retry = context.RetryScheduled
                ? $" — retry scheduled for {context.Trigger.NextFireTimeUtc?.ToLocalTime():HH:mm:ss}"
                : context.Trigger.RetryPolicy is not null ? " — retries exhausted" : string.Empty;

            Interlocked.Increment(ref _failed);
            Write("FAIL", $"{context.JobDetail.Key} failed after {context.JobRunTime.TotalMilliseconds:0} ms ({context.Outcome}): {jobException.GetBaseException().Message}{retry}", "error");
        }
        else
        {
            Interlocked.Increment(ref _completed);
            Write("DONE", $"{context.JobDetail.Key} completed in {context.JobRunTime.TotalMilliseconds:0} ms ({context.Outcome})", "success");
        }

        return ValueTask.CompletedTask;
    }
}

