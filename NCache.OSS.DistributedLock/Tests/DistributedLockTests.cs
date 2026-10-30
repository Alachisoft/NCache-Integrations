using Alachisoft.NCache.Client;
using NCache.DistributedLock.Providers;

namespace IDistribtuedLockTest;

public class DistributedLockTests : IDisposable
{
    private readonly ICache _cache = CacheManager.GetCache("defaultCache");
    private readonly NCacheDistributedSynchronizationProvider _provider;

    public DistributedLockTests()
    {
        _provider = new NCacheDistributedSynchronizationProvider(_cache);
    }

    public void Dispose() => _cache.Dispose();

    private static string Name(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // Holds the lock on a background thread until released, so the caller can probe from the outside.
    private static (Task Holder, ManualResetEventSlim Release) Hold(Func<IDisposable> acquire)
    {
        var acquired = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var holder = Task.Run(() =>
        {
            using var handle = acquire();
            acquired.Set();
            release.Wait();
        });

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        return (holder, release);
    }

    [Fact]
    public void Mutex_Acquire_ReturnsHandle()
    {
        var lck = _provider.CreateLock(Name("mutex"));

        using var handle = lck.Acquire(TimeSpan.FromSeconds(5));

        Assert.NotNull(handle);
    }

    [Fact]
    public void Mutex_CanBeReacquiredAfterRelease()
    {
        var lck = _provider.CreateLock(Name("mutex"));

        lck.Acquire(TimeSpan.FromSeconds(5)).Dispose();
        using var second = lck.Acquire(TimeSpan.FromSeconds(5));

        Assert.NotNull(second);
    }

    [Fact]
    public void Mutex_SecondAcquire_TimesOutWhileHeld()
    {
        var lck = _provider.CreateLock(Name("mutex"));
        var (holder, release) = Hold(() => lck.Acquire(TimeSpan.FromSeconds(15)));

        try
        {
            Assert.Throws<TimeoutException>(() => lck.Acquire(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            release.Set();
            holder.Wait();
        }
    }

    [Fact]
    public void Semaphore_AllowsUpToMaxCount_ThenTimesOut()
    {
        var sem = _provider.CreateSemaphore(Name("sem"), 2);

        using var first = sem.Acquire(TimeSpan.FromSeconds(5));
        using var second = sem.Acquire(TimeSpan.FromSeconds(5));

        Assert.Throws<TimeoutException>(() => sem.Acquire(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Semaphore_SlotIsFreedOnDispose()
    {
        var sem = _provider.CreateSemaphore(Name("sem"), 1);

        sem.Acquire(TimeSpan.FromSeconds(5)).Dispose();
        using var again = sem.Acquire(TimeSpan.FromSeconds(5));

        Assert.NotNull(again);
    }

    [Fact]
    public void ReaderWriter_MultipleReadersCanHoldTheLock()
    {
        var rwl = _provider.CreateReaderWriterLock(Name("rwl"));

        using var reader1 = rwl.AcquireReadLock(TimeSpan.FromSeconds(5));
        using var reader2 = rwl.AcquireReadLock(TimeSpan.FromSeconds(5));

        Assert.NotNull(reader1);
        Assert.NotNull(reader2);
    }

    [Fact]
    public void ReaderWriter_WriterTimesOutWhileReaderHolds()
    {
        var rwl = _provider.CreateReaderWriterLock(Name("rwl"));
        var (holder, release) = Hold(() => rwl.AcquireReadLock(TimeSpan.FromSeconds(15)));

        try
        {
            Assert.Throws<TimeoutException>(() => rwl.AcquireWriteLock(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            release.Set();
            holder.Wait();
        }
    }

    [Fact]
    public void ReaderWriter_WriteLockIsExclusive()
    {
        var rwl = _provider.CreateReaderWriterLock(Name("rwl"));
        var (holder, release) = Hold(() => rwl.AcquireWriteLock(TimeSpan.FromSeconds(15)));

        try
        {
            Assert.Throws<TimeoutException>(() => rwl.AcquireWriteLock(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            release.Set();
            holder.Wait();
        }
    }
}
