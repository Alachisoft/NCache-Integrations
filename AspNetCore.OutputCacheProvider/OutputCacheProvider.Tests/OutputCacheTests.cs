using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NCache.OSS.AspNetCore.OutputCaching;

namespace NCache.OutputCacheProvider.Tests;

public class OutputCacheTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddOutputCache(options =>
            options.AddBasePolicy(policy => policy.Expire(TimeSpan.FromSeconds(30))));
        builder.Services.AddNCacheOutputCacheProvider(options => options.CacheName = "defaultCache");

        _app = builder.Build();
        _app.UseOutputCache();

        // Each response embeds a fresh GUID, so identical bodies prove the response came from the cache.
        _app.MapGet("/nocache", () => Guid.NewGuid().ToString()).CacheOutput(p => p.NoCache());
        _app.MapGet("/cached", () => Guid.NewGuid().ToString()).CacheOutput();
        _app.MapGet("/products", (string id) => $"{id}:{Guid.NewGuid()}")
            .CacheOutput(p => p.SetVaryByQuery("id"));

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task CachedEndpoint_ReturnsSameBodyOnRepeatRequests()
    {
        var path = $"/cached?run={Guid.NewGuid():N}";

        var first = await _client.GetStringAsync(path);
        var second = await _client.GetStringAsync(path);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task NoCacheEndpoint_ReturnsFreshBodyEachTime()
    {
        var first = await _client.GetStringAsync("/nocache");
        var second = await _client.GetStringAsync("/nocache");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task VaryByQuery_CachesPerQueryValue()
    {
        var a = $"a-{Guid.NewGuid():N}";
        var b = $"b-{Guid.NewGuid():N}";

        var a1 = await _client.GetStringAsync($"/products?id={a}");
        var a2 = await _client.GetStringAsync($"/products?id={a}");
        var b1 = await _client.GetStringAsync($"/products?id={b}");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b1);
    }
}
