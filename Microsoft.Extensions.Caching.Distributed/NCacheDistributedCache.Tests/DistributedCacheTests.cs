using Alachisoft.NCache.Caching.Distributed;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace NCache.DistributedCache.Tests;

public class DistributedCacheTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IDistributedCache _cache;

    public DistributedCacheTests()
    {
        var services = new ServiceCollection();
        services.AddNCacheDistributedCache(options => options.CacheName = "defaultCache");

        _provider = services.BuildServiceProvider();
        _cache = _provider.GetRequiredService<IDistributedCache>();
    }

    public void Dispose() => _provider.Dispose();

    private static string Key() => $"dc-{Guid.NewGuid():N}";

    [Fact]
    public void Set_Then_Get_ReturnsBytes()
    {
        var key = Key();

        _cache.Set(key, Encoding.UTF8.GetBytes("hello"));

        Assert.Equal("hello", Encoding.UTF8.GetString(_cache.Get(key)!));
    }

    [Fact]
    public async Task SetAsync_Then_GetAsync_ReturnsBytes()
    {
        var key = Key();

        await _cache.SetStringAsync(key, "async hello");

        Assert.Equal("async hello", await _cache.GetStringAsync(key));
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var key = Key();
        _cache.SetString(key, "value");

        _cache.Remove(key);

        Assert.Null(_cache.Get(key));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        Assert.Null(_cache.Get(Key()));
    }

    [Fact]
    public void Set_WithSlidingExpiration_IsReadableAndRefreshable()
    {
        var key = Key();
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) };

        _cache.Set(key, Encoding.UTF8.GetBytes("sliding"), options);
        _cache.Refresh(key);

        Assert.Equal("sliding", Encoding.UTF8.GetString(_cache.Get(key)!));
    }
}
