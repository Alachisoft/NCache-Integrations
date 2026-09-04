using Hangfire;
using NCache.OSS.Hangfire;
using NCache.OSS.Hangfire.Demo.Services;
using Microsoft.Extensions.Logging;
using YourApp.Logging;

var builder = WebApplication.CreateBuilder(args);

// ── Logging (so Hangfire's ILogProvider has sinks) ──
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ── NCache Storage Setup ──
var cacheName = builder.Configuration.GetValue<string>("NCache:CacheName") ?? "mycache";

var storageOptions = new NCacheStorageOptions
{
    QueuePollInterval = TimeSpan.FromSeconds(60),
    EnablePubSubNotifications = true,
    SucceededListSize = 5,
    DeletedListSize = 5,
    FailedListSize = 5
};

builder.Services.AddHangfire(config => config
    .UseNCacheStorage(cacheName, storageOptions)
    .WithJobExpirationTimeout(TimeSpan.FromDays(1))); 

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 20;
    options.Queues = new[] { "default", "critical", "testing" };
});

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<DemoJobService>();

var app = builder.Build();

// Bridge Hangfire's internal logs into Microsoft.Extensions.Logging
GlobalConfiguration.Configuration.UseLogProvider(
    new MicrosoftLoggingProvider(app.Services.GetRequiredService<ILoggerFactory>()));

app.UseStaticFiles();
app.UseRouting();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();
app.MapHangfireDashboard("/hangfire");
app.Run();