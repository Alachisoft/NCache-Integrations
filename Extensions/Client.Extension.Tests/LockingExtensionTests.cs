using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;

namespace NCache.ClientExtension.Tests;

public class LockingExtensionTests : IDisposable
{
    private readonly ICache _cache = CacheManager.GetCache("defaultCache");

    public void Dispose() => _cache.Dispose();

    private string NewKey()
    {
        var key = $"lock-target-{Guid.NewGuid():N}";
        _cache.Insert(key, "value");
        return key;
    }

    [Fact]
    public void LockKey_OnExistingKey_Succeeds()
    {
        var key = NewKey();

        var locked = _cache.LockKey(key, out var token);

        Assert.True(locked);
        Assert.NotNull(token);
        _cache.UnlockKey(key, token!);
    }

    [Fact]
    public void LockKey_WhenAlreadyLocked_Fails()
    {
        var key = NewKey();
        _cache.LockKey(key, out var token);

        var lockedAgain = _cache.LockKey(key, out _);

        Assert.False(lockedAgain);
        _cache.UnlockKey(key, token!);
    }

    [Fact]
    public void UnlockKey_WithOwnerToken_AllowsRelock()
    {
        var key = NewKey();
        _cache.LockKey(key, out var token);

        _cache.UnlockKey(key, token!);

        Assert.True(_cache.LockKey(key, out var second));
        _cache.UnlockKey(key, second!);
    }

    [Fact]
    public void UnlockKey_WithForeignToken_KeepsLock()
    {
        var key = NewKey();
        _cache.LockKey(key, out var token);

        _cache.UnlockKey(key, new LockToken());

        Assert.False(_cache.LockKey(key, out _));
        _cache.UnlockKey(key, token!);
    }

    [Fact]
    public void LockKey_OnMissingKey_Fails()
    {
        Assert.False(_cache.LockKey($"missing-{Guid.NewGuid():N}", out var token));
        Assert.Null(token);
    }

    [Fact]
    public void LockKey_WithExpiration_ReleasesAfterExpiry()
    {
        var key = NewKey();
        _cache.LockKey(key, out _, TimeSpan.FromSeconds(1));

        var relocked = false;
        for (var i = 0; i < 40 && !relocked; i++)
        {
            Thread.Sleep(500);
            relocked = _cache.LockKey(key, out _);
        }

        Assert.True(relocked);
    }
}
