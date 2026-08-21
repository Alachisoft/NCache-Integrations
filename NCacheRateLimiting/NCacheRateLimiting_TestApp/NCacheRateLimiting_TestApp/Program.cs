using Alachisoft.NCache.Client;
using NCache.OSS.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
//builder.Services.AddRateLimiter(options =>
//{
//    options.AddNCacheConcurrencyLimiter(
//        "local",
//        limiter =>
//        {

//            limiter.PermitLimit = 10;

//            limiter.QueueLimit = 5;

//            limiter.LockTimeout =
//                TimeSpan.FromSeconds(10);

//            limiter.CacheName = "demoCache";
//        });
//});
builder.Services.AddRateLimiter(options =>
{
    options.AddNCacheConcurrencyLimiter(
        "local",
        builder.Configuration.GetSection("RateLimiting:Concurrency"));
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    Console.WriteLine($"ARRIVED {context.TraceIdentifier} Time={DateTime.Now:HH:mm:ss.fff}");
    await next();
});
app.UseRateLimiter();

app.MapGet("/", async context =>
{
    var id = Guid.NewGuid().ToString("N")[..6];

    Console.WriteLine(
        $"START {id} " +
        $"Thread={Environment.CurrentManagedThreadId} " +
        $"Time={DateTime.Now:HH:mm:ss.fff}");

    await Task.Delay(5000);

    Console.WriteLine(
        $"END   {id} " +
        $"Thread={Environment.CurrentManagedThreadId} " +
        $"Time={DateTime.Now:HH:mm:ss.fff}");

    await context.Response.WriteAsync(id);
})
.RequireRateLimiting("local");

app.Run();