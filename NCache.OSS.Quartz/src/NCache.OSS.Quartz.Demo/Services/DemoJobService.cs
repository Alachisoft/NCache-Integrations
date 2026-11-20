namespace NCache.OSS.Quartz.Demo.Services;

public class DemoJobService
{
    private readonly ExecutionLog _log;

    public DemoJobService(ExecutionLog log) => _log = log;

    public async ValueTask SendWelcomeEmail(string email, string userName, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        Report("EMAIL", $"Welcome email sent to {email} for {userName}");
    }

    public async ValueTask GenerateMonthlyReport(int year, int month, CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        Report("REPORT", $"Generated report for {month}/{year}");
    }

    public void CleanupOldLogFiles(int daysToKeep)
    {
        Report("CLEANUP", $"Removed logs older than {daysToKeep} days");
    }

    public async ValueTask ProcessCriticalOrder(string orderId, CancellationToken ct)
    {
        await Task.Delay(1000, ct);
        Report("CRITICAL", $"Processed urgent order {orderId}");
    }

    public async ValueTask LongRunningJob(TimeSpan duration, CancellationToken ct)
    {
        Report("LONG", $"Starting {duration.TotalSeconds:0}-second job");
        await Task.Delay(duration, ct);
        Report("LONG", "Finished long job");
    }

    public async ValueTask NotifyAdmin(string message, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        Report("ADMIN", $"Notification: {message}");
    }

    public async ValueTask ProcessFailedPayment(int attempt, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        throw new InvalidOperationException($"Failed Payment! Intentional failure for testing retries (attempt {attempt}).");
    }

    public async ValueTask SlowTick(string run, TimeSpan duration, CancellationToken ct)
    {
        Report("OVERLAP", $"Run {run} started ({duration.TotalSeconds:0}s of work)");

        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            _log.Write("OVERLAP", $"Run {run} was interrupted before it finished", "warn");
            throw;
        }

        Report("OVERLAP", $"Run {run} finished");
    }

    public async ValueTask ReportingWork(int steps, Action<int, string> report, CancellationToken ct)
    {
        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(1000, ct);
            report(step * 100 / steps, $"step {step} of {steps}");
        }

        Report("PROGRESS", $"Finished {steps} steps");
    }

    private void Report(string tag, string message)
    {
        Console.WriteLine($"[{tag}] {message} at {DateTime.Now:HH:mm:ss}");
        _log.Write(tag, message);
    }
}

