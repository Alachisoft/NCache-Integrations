using NCache.OSS.Quartz.Demo.Services;
using Quartz;

namespace NCache.OSS.Quartz.Demo.Jobs;

public sealed class SendWelcomeEmailJob : IJob
{
    public const string EmailKey = "email";
    public const string UserNameKey = "userName";

    private readonly DemoJobService _service;

    public SendWelcomeEmailJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var data = context.MergedJobDataMap;
        return _service.SendWelcomeEmail(data.GetString(EmailKey) ?? "user@test.com", data.GetString(UserNameKey) ?? "Test User", cancellationToken);
    }
}

public sealed class GenerateMonthlyReportJob : IJob
{
    public const string YearKey = "year";
    public const string MonthKey = "month";

    private readonly DemoJobService _service;

    public GenerateMonthlyReportJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var data = context.MergedJobDataMap;
        return _service.GenerateMonthlyReport(data.GetInt(YearKey), data.GetInt(MonthKey), cancellationToken);
    }
}

public sealed class CleanupOldLogFilesJob : IJob
{
    public const string DaysToKeepKey = "daysToKeep";

    private readonly DemoJobService _service;

    public CleanupOldLogFilesJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        _service.CleanupOldLogFiles(context.MergedJobDataMap.GetInt(DaysToKeepKey));
        return ValueTask.CompletedTask;
    }
}

public sealed class ProcessFailedPaymentJob : IJob
{
    private readonly DemoJobService _service;

    public ProcessFailedPaymentJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => _service.ProcessFailedPayment(context.RetryAttempt, cancellationToken);
}

public sealed class NotifyAdminJob : IJob
{
    public const string MessageKey = "message";

    private readonly DemoJobService _service;

    public NotifyAdminJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => _service.NotifyAdmin(context.MergedJobDataMap.GetString(MessageKey) ?? string.Empty, cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class ProcessCriticalOrderJob : IJob
{
    public const string OrderIdKey = "orderId";

    private static int _running;

    private readonly DemoJobService _service;
    private readonly ExecutionLog _log;

    public ProcessCriticalOrderJob(DemoJobService service, ExecutionLog log)
    {
        _service = service;
        _log = log;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var concurrent = Interlocked.Increment(ref _running);

        try
        {
            if (concurrent > 1)
            {
                _log.Write("OVERLAP", $"{concurrent} executions of the non-concurrent job are running at once!", "error");
            }

            await _service.ProcessCriticalOrder(context.MergedJobDataMap.GetString(OrderIdKey) ?? "ORDER-911", cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

public sealed class CompleteItselfJob : IJob
{
    private readonly DemoJobService _service;

    public CompleteItselfJob(DemoJobService service) => _service = service;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await _service.NotifyAdmin("one-time task done; asking Quartz to mark my trigger Complete", cancellationToken);
        throw new JobExecutionException("done") { UnscheduleFiringTrigger = true };
    }
}

public sealed class SlowTickJob : IJob
{
    public const string SecondsKey = "seconds";

    private readonly DemoJobService _service;

    public SlowTickJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => _service.SlowTick(
            $"{context.Trigger.Key.Name} @ {context.ScheduledFireTimeUtc?.ToLocalTime():HH:mm:ss}",
            TimeSpan.FromSeconds(Math.Max(1, context.MergedJobDataMap.GetInt(SecondsKey))),
            cancellationToken);
}

public sealed class ProgressReportingJob : IJob
{
    public const string StepsKey = "steps";

    private readonly DemoJobService _service;

    public ProgressReportingJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => _service.ReportingWork(Math.Max(1, context.MergedJobDataMap.GetInt(StepsKey)), context.ReportProgress, cancellationToken);
}

public sealed class LongRunningJob : IJob
{
    public const string SecondsKey = "seconds";

    private readonly DemoJobService _service;

    public LongRunningJob(DemoJobService service) => _service = service;

    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => _service.LongRunningJob(TimeSpan.FromSeconds(Math.Max(1, context.MergedJobDataMap.GetInt(SecondsKey))), cancellationToken);
}

