using Alachisoft.NCache.Caching.Distributed;
using Alachisoft.NCache.Caching.Distributed.Configuration;
using Microsoft.Extensions.Options;
using NCache.ZiggyCreatures.FusionCache.Backplane;
using NCache.ZiggyCreatures.FusionCache.Backplane.Configuration;
using System.Collections.Concurrent;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

// ------------------------------------------------------------------------------------
// FusionCache + NCache L2 + NCache Backplane - GUI backend
//
// Exposes two independent FusionCache instances ("NodeA" and "NodeB"), each with its
// own in-memory L1, both sharing the same NCache L2 (distributed cache) and the same
// NCache backplane. The single-page UI in wwwroot/index.html lets you Set/Update/Remove
// a cached product on either node and watch, in real time, how the backplane keeps the
// other node's L1 memory cache in sync.
//
// Prerequisites: a NCache server reachable at localhost:6379
//   docker run -d --name NCache -p 6379:6379 NCache:7
//
// Run:
//   dotnet run
//   then open the printed http://localhost:xxxx URL in a browser
// ------------------------------------------------------------------------------------

const string NCacheConnectionString = "localhost:6379";
const string CacheKey = "product:42";

var builder = WebApplication.CreateBuilder(args);

// Single shared NCache connection multiplexer, reused for both the L2 cache and the backplane.

var cacheName = "demoCache";

var config = new NCacheConfiguration
{
    CacheName = cacheName,
    EnableLogs = true,
    ExceptionsEnabled = true
};

var ncacheOptions = Options.Create(config);

//Backplane Options

var backplaneOptions = new NCacheBackplaneOptions
{
    ServerList = new List<FusionCacheServerInfo>
    {
        new FusionCacheServerInfo("10.0.0.1")
    },
    ClientBindIP = "10.0.0.2",
    LoadBalance = true,
    ConnectionTimeout = TimeSpan.FromSeconds(5),
    ConnectionRetries = 3,
    RetryInterval = TimeSpan.FromSeconds(2),
    AppName = "FusionCache-App"
};

// In-memory activity logs per node, shown in the UI.
var logs = new Dictionary<string, ConcurrentQueue<LogEntry>>
{
    ["NodeA"] = new(),
    ["NodeB"] = new()
};

void AddLog(string node, string message)
{
    var queue = logs[node];
    queue.Enqueue(new LogEntry(DateTime.Now.ToString("HH:mm:ss.fff"), message));
    while (queue.Count > 50 && queue.TryDequeue(out _)) { }
}

// Register both named FusionCache instances, sharing the same NCache L2 + backplane.
foreach (var nodeName in new[] { "NodeA", "NodeB" })
{
    builder.Services.AddFusionCache(nodeName)
        .WithOptions(options => options.BackplaneChannelPrefix = "SharedProductCache")
        .WithDefaultEntryOptions(options => options
            .SetDuration(TimeSpan.FromMinutes(5))
            .SetDistributedCacheDuration(TimeSpan.FromMinutes(30))
            .SetFailSafe(true, TimeSpan.FromHours(2)))
        .WithSerializer(new FusionCacheSystemTextJsonSerializer())
        .WithDistributedCache(new NCacheDistributedCache(ncacheOptions))
        .WithBackplane(
        new NCacheBackplane(
            cacheName: cacheName,
            options: backplaneOptions));
}

var app = builder.Build();

// Wire up backplane notification logging for each node, so the UI can show when a
// node's L1 gets invalidated because of a change made on the OTHER node.
var provider = app.Services.GetRequiredService<IFusionCacheProvider>();
foreach (var nodeName in new[] { "NodeA", "NodeB" })
{
  var cache = provider.GetCache(nodeName);
    //cache.Events.BackplaneMessageReceived += (_, e) =>
    //{
    //    AddLog(nodeName, $"⇦ backplane: received '{e.Message.Action}' for key '{e.Message.CacheKey}' (L1 will refresh from L2 on next access)");
    //};
    cache.Events.Backplane.MessageReceived += (_, e) =>
    {
        AddLog(nodeName, $"⇦ backplane: received '{e.Message.Action}' for key '{e.Message.CacheKey}' (L1 will refresh from L2 on next access)");
    };
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- API endpoints ----

app.MapGet("/api/{node}/value", async (string node, IFusionCacheProvider fcProvider) =>
{
    var cache = fcProvider.GetCache(node);
    var product = await cache.GetOrDefaultAsync<Product>(CacheKey);
    if(product == null)
    {
        return Results.NoContent();
    }

    return Results.Ok(product);
});

app.MapPost("/api/{node}/set", async (string node, ProductInput input, IFusionCacheProvider fcProvider) =>
{
    var cache = fcProvider.GetCache(node);
    var product = new Product(42, input.Name, input.Price);
    await cache.SetAsync(CacheKey, product, TimeSpan.FromMinutes(5));
    AddLog(node, $"⇨ SET '{product.Name}' (${product.Price}) — updates local L1, NCache L2, and publishes a backplane notification");
    return Results.Ok(product);
});

app.MapPost("/api/{node}/remove", async (string node, IFusionCacheProvider fcProvider) =>
{
    var cache = fcProvider.GetCache(node);
    await cache.RemoveAsync(CacheKey);
    AddLog(node, "⇨ REMOVE — clears local L1 + NCache L2, and publishes a backplane notification");
    return Results.Ok();
});

app.MapGet("/api/{node}/log", (string node) =>
{
    return Results.Ok(logs[node].ToArray().Reverse());
});

app.Run();

record Product(int Id, string Name, decimal Price);
record ProductInput(string Name, decimal Price);
record LogEntry(string Time, string Message);
