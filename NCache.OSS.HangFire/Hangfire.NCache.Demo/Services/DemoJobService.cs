using Hangfire.States;
using Hangfire;
namespace NCache.OSS.Hangfire.Demo.Services;

public class DemoJobService
{
    public void SendWelcomeEmail(string email, string userName)
    {
        Thread.Sleep(500);
        Console.WriteLine($"[EMAIL] Welcome email sent to {email} for {userName} at {DateTime.Now:HH:mm:ss}");
    }

    public void ProcessOrderPayment(string orderId, decimal amount)
    {
        Thread.Sleep(800);
        Console.WriteLine($"[PAYMENT] Processed ${amount} for order {orderId} at {DateTime.Now:HH:mm:ss}");
    }

    public void GenerateMonthlyReport(int year, int month)
    {
        Thread.Sleep(2000);
        Console.WriteLine($"[REPORT] Generated report for {month}/{year} at {DateTime.Now:HH:mm:ss}");
    }

    public void SendPushNotification(string deviceToken, string message)
    {
        Thread.Sleep(300);
        Console.WriteLine($"[PUSH] Sent to {deviceToken}: {message} at {DateTime.Now:HH:mm:ss}");
    }

    public void CleanupOldLogFiles(int daysToKeep)
    {
        Console.WriteLine($"[CLEANUP] Removed logs older than {daysToKeep} days at {DateTime.Now:HH:mm:ss}");
    }

    public void ProcessCriticalOrder(string orderId)
    {
        Thread.Sleep(1000);
        Console.WriteLine($"[CRITICAL] Processed urgent order {orderId} at {DateTime.Now:HH:mm:ss}");
    }

    public void LongRunningJob()
    {
        Console.WriteLine($"[LONG] Starting 30-second job at {DateTime.Now:HH:mm:ss}");
        Thread.Sleep(200000);
        Console.WriteLine($"[LONG] Finished long job at {DateTime.Now:HH:mm:ss}");
    }

    public void NotifyAdmin(string message)
    {
        Thread.Sleep(300);
        Console.WriteLine($"[ADMIN] Notification: {message} at {DateTime.Now:HH:mm:ss}");
    }

    [AutomaticRetry(Attempts = 0)]
    public void ProcessFailedPayment()
    {
        Thread.Sleep(200);
        throw new InvalidOperationException("Failed Payment! Intentional failure for testing FailedState. ");
    }

   
   
   
}