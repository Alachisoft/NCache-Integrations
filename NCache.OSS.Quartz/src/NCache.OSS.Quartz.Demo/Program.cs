using NCache.OSS.Quartz.Demo.Services;
using NCache.OSS.Quartz.Extensions;
using Quartz;
using System.Runtime.Intrinsics.X86;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.AddQuartz(q =>
{
    q.UseNCacheStore(builder.Configuration, "Quartz:NCache");

    // The second-node profile refuses the 'reports' execution group (card 21); the first node serves every group.
    var refusedGroups = builder.Configuration.GetSection("Demo:RefusedExecutionGroups").Get<string[]>() ?? [];

    if (refusedGroups.Length > 0)
    {
        q.RefuseExecutionGroups(refusedGroups);
    }

    q.ConfigureScheduler(o =>
    { 
        o.MaxBatchSize = 5;
        o.IdleWaitTime = TimeSpan.FromSeconds(1); 
    });

    q.UseDefaultThreadPool(10);

    q.UseExecutionLimits(limits => limits
        .ForGroup("serial", 1, ExecutionLimitScope.Cluster)
        .ForGroup("emails", 3, ExecutionLimitScope.Node));
});

builder.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddQuartzHttpApi();
builder.Services.AddQuartzDashboard();

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<ExecutionLog>();
builder.Services.AddSingleton<DemoJobService>();

var app = builder.Build();

var scheduler = await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
scheduler.ListenerManager.AddJobListener(app.Services.GetRequiredService<ExecutionLog>());
scheduler.ListenerManager.AddTriggerListener(app.Services.GetRequiredService<ExecutionLog>());

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers();

app.MapQuartzHttpApi("/quartz-api").AllowAnonymous();
app.MapQuartzDashboard("/quartz").AllowAnonymous();
app.MapGet("/health", () => Results.Ok("alive"));
app.Run();

