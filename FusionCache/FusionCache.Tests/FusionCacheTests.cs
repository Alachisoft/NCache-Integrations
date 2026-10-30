using Alachisoft.NCache.Caching.Distributed;
using Alachisoft.NCache.Caching.Distributed.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NCache.ZiggyCreatures.FusionCache.Backplane;
using NCache.ZiggyCreatures.FusionCache.Backplane.Configuration;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace NCache.FusionCache.Tests;

public class FusionCacheTests : IDisposable
{
    private const string CacheName = "defaultCache";

    private readonly string _prefix = $"fc-{Guid.NewGuid():N}";
    private readonly ServiceProvider _nodeAProvider;
    private readonly ServiceProvider _nodeBProvider;
    private readonly IFusionCache _nodeA;
    private readonly IFusionCache _nodeB;

    public FusionCacheTests()
    {
        _nodeAProvider = BuildNode();
        _nodeBProvider = BuildNode();
        _nodeA = _nodeAProvider.GetRequiredService<IFusionCache>();
        _nodeB = _nodeBProvider.GetRequiredService<IFusionCache>();
    }

    // Each node has its own L1 memory cache but shares the NCache L2 and backplane, like two app servers.
    private ServiceProvider BuildNode()
    {
        var services = new ServiceCollection();

        services.AddFusionCache()
            .WithOptions(options => options.BackplaneChannelPrefix = _prefix)
            .WithDefaultEntryOptions(options => options.SetDuration(TimeSpan.FromMinutes(5)))
            .WithSerializer(new FusionCacheSystemTextJsonSerializer())
            .WithDistributedCache(new NCacheDistributedCache(Options.Create(new NCacheConfiguration { CacheName = CacheName })))
            .WithBackplane(new NCacheBackplane(CacheName, new NCacheBackplaneOptions()));

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _nodeAProvider.Dispose();
        _nodeBProvider.Dispose();
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> read, Func<T?, bool> done)
    {
        T? value = default;
        for (var i = 0; i < 50; i++)
        {
            value = await read();
            if (done(value)) break;
            await Task.Delay(200);
        }
        return value;
    }

    [Fact]
    public async Task Set_OnOneNode_IsReadableFromAnother()
    {
        var key = $"{_prefix}:product";

        await _nodeA.SetAsync(key, new Product(42, "Laptop"));

        var product = await _nodeB.GetOrDefaultAsync<Product>(key);

        Assert.Equal(new Product(42, "Laptop"), product);
    }

    [Fact]
    public async Task Remove_OnOneNode_ClearsTheOther()
    {
        var key = $"{_prefix}:remove";
        await _nodeA.SetAsync(key, new Product(1, "Mouse"));
        Assert.NotNull(await _nodeB.GetOrDefaultAsync<Product>(key));

        await _nodeA.RemoveAsync(key);

        var product = await WaitForAsync(() => _nodeB.GetOrDefaultAsync<Product>(key).AsTask(), p => p is null);
        Assert.Null(product);
    }

    [Fact]
    public async Task Backplane_InvalidatesOtherNodesL1_OnUpdate()
    {
        var key = $"{_prefix}:update";
        await _nodeA.SetAsync(key, new Product(7, "Old"));
        Assert.Equal("Old", (await _nodeB.GetOrDefaultAsync<Product>(key))!.Name);

        await _nodeA.SetAsync(key, new Product(7, "New"));

        var product = await WaitForAsync(() => _nodeB.GetOrDefaultAsync<Product>(key).AsTask(), p => p?.Name == "New");
        Assert.Equal("New", product!.Name);
    }

    private record Product(int Id, string Name);
}
