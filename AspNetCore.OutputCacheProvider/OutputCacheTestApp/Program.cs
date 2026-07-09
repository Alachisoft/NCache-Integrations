using Microsoft.AspNetCore.OutputCaching;
using NCache.OSS.AspNetCore.OutputCaching;

var builder = WebApplication.CreateBuilder(args);

// Add Output Cache services
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(policy =>
        policy.Expire(TimeSpan.FromSeconds(60)));
});

builder.Services.AddNCacheOutputCacheProvider(options =>
{
    options.CacheName = "demoCache";

    options.ServerList = new List<NCacheOutputCacheOptions.ServerConfig>
    {
        new NCacheOutputCacheOptions.ServerConfig
        {
            Ip = "127.0.0.1"
        }
    };

    options.EnabledLogs = true;
    options.EnableDetailLogs = true;
});

// Alternative way via appsettings.json
//builder.Services.AddNCacheOutputCacheProvider(builder.Configuration.GetSection("NCacheOutputCache"));

var app = builder.Build();

// Enable middleware
app.UseOutputCache();

// --------------------------------------------------------------------------------
// Endpoint WITHOUT cache (control)
app.MapGet("/nocache", () =>
{
    return $"No cache: {DateTime.Now}";
}).CacheOutput(policy => policy.NoCache());
// --------------------------------------------------------------------------------


// --------------------------------------------------------------------------------
// Vary by query example
app.MapGet("/products", (int page, int pageSize) =>
{
    return $"Products page={page}, size={pageSize}, time={DateTime.Now}";
})
.CacheOutput(policy =>
{
    // KEY: vary cache by query string
    policy.SetVaryByQuery("page", "pageSize");
});
// --------------------------------------------------------------------------------


// --------------------------------------------------------------------------------
// Vary by header example
app.MapGet("/localized", (HttpContext ctx) =>
{
    var lang = ctx.Request.Headers["Accept-Language"].ToString();
    return $"Language: {lang}, Time: {DateTime.Now}";
})
.CacheOutput(policy =>
{
    // vary by request headers
    policy.SetVaryByHeader("Accept-Language");
});
// --------------------------------------------------------------------------------


// --------------------------------------------------------------------------------
// Cached endpoint
app.MapGet("/cached", () =>
{
    Console.WriteLine("Endpoint executed");
    return $"Cached: {DateTime.Now}";
})
.CacheOutput();
// --------------------------------------------------------------------------------


// --------------------------------------------------------------------------------
// Cached endpoint with TAG (tags are not supported in OSS)
app.MapGet("/tagged", () =>
{
    return $"Tagged: {DateTime.Now}";
})
.CacheOutput(policy => policy.Tag("demo-tag"));
// --------------------------------------------------------------------------------


// --------------------------------------------------------------------------------
// Evict by tag (evict using tags is not supported in OSS)
app.MapGet("/evict", async (IOutputCacheStore store) =>
{
    await store.EvictByTagAsync("demo-tag", default);
    return "Tag evicted";
});
// --------------------------------------------------------------------------------

app.Run();

// How to use the application:
// Use the following url in your browser:
// https://localhost:55771/(endpoint)
// Remember to replace (endpoint) with above endpoints which need to be hit e.g. evict, tagged, cached etc