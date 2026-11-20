using System.Diagnostics;
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Medallion.Threading;
using NCache.DistributedLock.Primitives;
using NCache.OSS.Quartz.Keys;
using NCache.OSS.Quartz.Locking;
using Xunit;

namespace NCache.OSS.Quartz.Tests;

public sealed class NCacheQuartzDistributedLocksUnitTest : IAsyncLifetime
{
    private const string SchedulerName = "NCacheQuartzDistributedLocksUnitTest";

    private const string PackageLockKeyPrefix = "ncache#distLock#";

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _prefix = $"ncache:quartz:test:locks:{Guid.NewGuid():N}:";
    private ICache? _cache;

    public Task InitializeAsync()
    {
        _cache = TestCache.Get();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cache = null;
        return Task.CompletedTask;
    }

    private ICache Cache => _cache!;

    private NCacheDistributedLocks NewLocks(TimeSpan? expiration = null, ICache? cache = null, string? prefix = null, string? schedName = null)
        => new(cache ?? Cache, prefix ?? _prefix, schedName ?? SchedulerName, expiration);

    private string TriggerLockKey => PackageLockKeyPrefix + NCacheKeys.TriggerAccessLock(_prefix, SchedulerName);

    private string StateLockKey => PackageLockKeyPrefix + NCacheKeys.StateAccessLock(_prefix, SchedulerName);

    private string TriggerTicketKey => NCacheKeys.LockRemoteWaitingMarker(NCacheKeys.TriggerAccessLock(_prefix, SchedulerName));

    private DistributedLockAcquisition? Record(string lockKey) => Cache.Get<DistributedLockAcquisition>(lockKey);

    private NCacheDistributedLocks.WaitTicket? Ticket(string ticketKey) => Cache.Get<NCacheDistributedLocks.WaitTicket>(ticketKey);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static Func<int> CountCalls(FaultInjectingCache control, string methodName, string key)
    {
        var count = 0;
        var match = FaultInjectingCache.Call(methodName, k => k == key);

        control.DelayAfter = (method, args) =>
        {
            if (match(method, args))
            {
                Interlocked.Increment(ref count);
            }

            return null;
        };

        return () => Volatile.Read(ref count);
    }

    private static Task<IDistributedSynchronizationHandle> Acquire(NCacheDistributedLocks locks, bool state, TimeSpan? timeout = null, CancellationToken ct = default)
        => state ? locks.AcquireStateAccessAsync(timeout, ct) : locks.AcquireTriggerAccessAsync(timeout, ct);

    private static Task<IDistributedSynchronizationHandle?> TryAcquire(NCacheDistributedLocks locks, bool state, TimeSpan timeout = default, CancellationToken ct = default)
        => state ? locks.TryAcquireStateAccessAsync(timeout, ct) : locks.TryAcquireTriggerAccessAsync(timeout, ct);

    private string LockKey(bool state) => state ? StateLockKey : TriggerLockKey;

    private static async Task<(int Counter, int Overlaps)> HammerAsync(IReadOnlyList<NCacheDistributedLocks> nodes, bool state, int tasksPerNode, int iterations)
    {
        var counter = 0;
        var inside = 0;
        var overlaps = 0;

        async Task Worker(NCacheDistributedLocks locks)
        {
            for (var i = 0; i < iterations; i++)
            {
                await using var handle = await Acquire(locks, state, TimeSpan.FromSeconds(60));

                if (Interlocked.Increment(ref inside) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                var read = counter;
                await Task.Yield();
                Thread.SpinWait(50);
                counter = read + 1;

                Interlocked.Decrement(ref inside);
            }
        }

        var workers = nodes.SelectMany(node => Enumerable.Range(0, tasksPerNode).Select(_ => Task.Run(() => Worker(node)))).ToList();
        await Task.WhenAll(workers);

        return (counter, overlaps);
    }

    [Fact]
    public void Constructor_NullCache_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NCacheDistributedLocks(null!, _prefix, SchedulerName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30_000)]
    public void Constructor_NonPositiveExpiration_Throws(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NewLocks(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("lockExpiration", ex.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void Constructor_ExpirationNotAboveMaxStickyHold_Throws(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NewLocks(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("lockExpiration", ex.ParamName);
    }

    [Fact]
    public void Constructor_ExpirationJustAboveMaxStickyHold_IsAccepted()
    {
        var expiration = NCacheDistributedLocks.MaxStickyHold + TimeSpan.FromMilliseconds(1);

        Assert.Equal(expiration, NewLocks(expiration).LockExpiration);
    }

    [Fact]
    public void Constructor_NoExpiration_UsesDefaultLockExpiration()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NCacheDistributedLocks.DefaultLockExpiration);
        Assert.Equal(NCacheDistributedLocks.DefaultLockExpiration, NewLocks().LockExpiration);
    }

    [Fact]
    public void Constructor_CustomExpiration_IsExposed()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), NewLocks(TimeSpan.FromSeconds(7)).LockExpiration);
    }

    [Fact]
    public void Constants_HandOffLimits_AreTheDocumentedOnes()
    {
        Assert.Equal(4, NCacheDistributedLocks.MaxLocalHandoffs);
        Assert.Equal(TimeSpan.FromMilliseconds(100), NCacheDistributedLocks.MaxStickyHold);
    }

    [Fact]
    public void Constructor_MakesNoCacheCalls()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);

        NewLocks(cache: proxy);

        Assert.Equal(0, control.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquire_WritesPackageLockRecord_WithLockExpiration(bool state)
    {
        var expiration = TimeSpan.FromSeconds(20);
        var locks = NewLocks(expiration);

        var before = DateTime.UtcNow;
        await using var handle = await Acquire(locks, state, Generous);
        var after = DateTime.UtcNow;

        var record = Record(LockKey(state));
        Assert.NotNull(record);
        Assert.NotEqual(Guid.Empty, record!.OwnershipId);
        Assert.InRange(record.Expiry, before + expiration - TimeSpan.FromSeconds(2), after + expiration + TimeSpan.FromSeconds(2));

        var item = Cache.GetCacheItem(LockKey(state));
        Assert.NotNull(item);
        Assert.Equal(ExpirationType.Absolute, item.Expiration?.Type);

        Assert.Null(Record(LockKey(!state)));
        Assert.Null(Ticket(NCacheKeys.LockRemoteWaitingMarker(state ? NCacheKeys.StateAccessLock(_prefix, SchedulerName) : NCacheKeys.TriggerAccessLock(_prefix, SchedulerName))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WithNobodyWaiting_RemovesRecord(bool state)
    {
        var locks = NewLocks();

        var handle = await Acquire(locks, state, Generous);
        Assert.NotNull(Record(LockKey(state)));

        handle.Dispose();

        Assert.Null(Record(LockKey(state)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WithNobodyWaiting_RemovesRecord(bool state)
    {
        var locks = NewLocks();

        var handle = await Acquire(locks, state, Generous);
        Assert.NotNull(Record(LockKey(state)));

        await handle.DisposeAsync();

        Assert.Null(Record(LockKey(state)));
    }

    [Fact]
    public async Task Acquire_Uncontended_AddsRecordExactlyOnce()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);
        var adds = CountCalls(control, "Add", TriggerLockKey);
        var locks = NewLocks(cache: proxy);

        await using (await locks.AcquireTriggerAccessAsync(Generous))
        {
            Assert.Equal(1, adds());
        }
    }

    [Fact]
    public async Task DoubleDispose_IsHarmless_AndDoesNotReleaseTheNextHolder()
    {
        var locks = NewLocks();

        var first = await locks.AcquireTriggerAccessAsync(Generous);
        first.Dispose();
        first.Dispose();
        await first.DisposeAsync();

        var second = await locks.AcquireTriggerAccessAsync(Generous);
        var secondToken = Record(TriggerLockKey)!.OwnershipId;

        first.Dispose();
        await first.DisposeAsync();

        Assert.Equal(secondToken, Record(TriggerLockKey)?.OwnershipId);
        Assert.Null(await locks.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(200)));

        await second.DisposeAsync();
        second.Dispose();

        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task HandleLostToken_WhileHeld_IsLiveAndNotCancelled_AfterDispose_Throws()
    {
        var locks = NewLocks();

        var handle = await locks.AcquireTriggerAccessAsync(Generous);

        Assert.True(handle.HandleLostToken.CanBeCanceled);
        Assert.False(handle.HandleLostToken.IsCancellationRequested);

        handle.Dispose();

        Assert.Throws<ObjectDisposedException>(() => handle.HandleLostToken);
    }

    [Fact]
    public async Task HandleLostToken_AfterDisposeAsync_Throws()
    {
        var locks = NewLocks();

        var handle = await locks.AcquireStateAccessAsync(Generous);
        await handle.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => handle.HandleLostToken);
    }

    [Fact]
    public async Task ReleasedLock_CanBeReacquired_ManyTimes()
    {
        var locks = NewLocks();
        var tokens = new HashSet<Guid>();

        for (var i = 0; i < 5; i++)
        {
            await using (await locks.AcquireTriggerAccessAsync(Generous))
            {
                tokens.Add(Record(TriggerLockKey)!.OwnershipId);
            }

            Assert.Null(Record(TriggerLockKey));
        }

        Assert.Equal(5, tokens.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameInstance_ConcurrentTasks_NeverOverlap_NoLostUpdates(bool state)
    {
        var locks = NewLocks();

        var (counter, overlaps) = await HammerAsync(new[] { locks }, state, tasksPerNode: 20, iterations: 5);

        Assert.Equal(0, overlaps);
        Assert.Equal(100, counter);
        Assert.Null(Record(LockKey(state)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoInstances_ConcurrentTasks_NeverOverlap_NoLostUpdates(bool state)
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();

        var (counter, overlaps) = await HammerAsync(new[] { nodeA, nodeB }, state, tasksPerNode: 8, iterations: 4);

        Assert.Equal(0, overlaps);
        Assert.Equal(64, counter);
        Assert.True(await WaitUntilAsync(() => Record(LockKey(state)) is null, TimeSpan.FromSeconds(5)), "lock record left behind after every holder released");
    }

    [Fact]
    public async Task TwoInstances_HeldOnOne_OtherCannotAcquire()
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();

        await using (await nodeA.AcquireTriggerAccessAsync(Generous))
        {
            Assert.Null(await nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(300)));
        }

        await using (await nodeB.AcquireTriggerAccessAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.Null(await nodeA.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(300)));
        }
    }

    [Fact]
    public async Task TriggerAndStateLocks_DoNotBlockEachOther()
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();

        await using (await nodeA.AcquireTriggerAccessAsync(Generous))
        {
            await using (var state = await nodeA.TryAcquireStateAccessAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(state);
            }

            await using (var state = await nodeB.TryAcquireStateAccessAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(state);
            }
        }

        await using (await nodeA.AcquireStateAccessAsync(Generous))
        {
            await using var trigger = await nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(trigger);
        }
    }

    [Fact]
    public async Task DifferentSchedulerNames_DoNotBlockEachOther()
    {
        var mine = NewLocks();
        var other = NewLocks(schedName: SchedulerName + "-other");

        await using (await mine.AcquireTriggerAccessAsync(Generous))
        await using (await mine.AcquireStateAccessAsync(Generous))
        {
            await using var trigger = await other.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
            await using var state = await other.TryAcquireStateAccessAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(trigger);
            Assert.NotNull(state);
            Assert.NotNull(Record(PackageLockKeyPrefix + NCacheKeys.TriggerAccessLock(_prefix, SchedulerName + "-other")));
        }
    }

    [Fact]
    public async Task DifferentPrefixes_DoNotBlockEachOther()
    {
        var otherPrefix = $"ncache:quartz:test:locks:{Guid.NewGuid():N}:";
        var mine = NewLocks();
        var other = NewLocks(prefix: otherPrefix);

        await using (await mine.AcquireTriggerAccessAsync(Generous))
        {
            await using var trigger = await other.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(trigger);
            Assert.NotNull(Record(PackageLockKeyPrefix + NCacheKeys.TriggerAccessLock(otherPrefix, SchedulerName)));
        }
    }

    [Fact]
    public async Task LocalWaiter_IsHandedTheHeldLock_WithoutTheRecordBeingReAdded()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);
        var adds = CountCalls(control, "Add", TriggerLockKey);
        var removes = 0;
        var locks = NewLocks(cache: proxy);

        var first = await locks.AcquireTriggerAccessAsync(Generous);
        var token = Record(TriggerLockKey)!.OwnershipId;

        var waiter = locks.AcquireTriggerAccessAsync(Generous);
        Assert.False(waiter.IsCompleted);

        var matchRemove = FaultInjectingCache.Call("Remove", k => k == TriggerLockKey);
        control.FailWhen = (method, args) =>
        {
            if (matchRemove(method, args))
            {
                Interlocked.Increment(ref removes);
            }

            return false;
        };

        first.Dispose();
        var second = await waiter.WaitAsync(Generous);

        Assert.Equal(token, Record(TriggerLockKey)?.OwnershipId);
        Assert.Equal(1, adds());
        Assert.Equal(0, Volatile.Read(ref removes));
        Assert.False(second.HandleLostToken.IsCancellationRequested);

        await second.DisposeAsync();

        Assert.Null(Record(TriggerLockKey));
        Assert.Equal(1, Volatile.Read(ref removes));
    }

    [Fact]
    public async Task HandOffs_AreCappedAtMaxLocalHandoffs_ThenTheLockIsReallyReleased()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);
        var adds = CountCalls(control, "Add", TriggerLockKey);
        var locks = NewLocks(cache: proxy);

        var first = await locks.AcquireTriggerAccessAsync(Generous);

        var waiters = Enumerable.Range(0, NCacheDistributedLocks.MaxLocalHandoffs + 1)
            .Select(_ => locks.AcquireTriggerAccessAsync(Generous))
            .ToList();

        first.Dispose();

        foreach (var waiter in waiters)
        {
            var handle = await waiter.WaitAsync(Generous);
            handle.Dispose();
        }

        Assert.InRange(adds(), 2, waiters.Count + 1);
        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task HoldBeyondMaxStickyHold_WaiterGetsAFreshRecord()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);
        var adds = CountCalls(control, "Add", TriggerLockKey);
        var locks = NewLocks(cache: proxy);

        var first = await locks.AcquireTriggerAccessAsync(Generous);
        var firstToken = Record(TriggerLockKey)!.OwnershipId;
        var waiter = locks.AcquireTriggerAccessAsync(Generous);

        await Task.Delay(NCacheDistributedLocks.MaxStickyHold + TimeSpan.FromMilliseconds(150));
        first.Dispose();

        await using var second = await waiter.WaitAsync(Generous);
        var secondToken = Record(TriggerLockKey)?.OwnershipId;

        Assert.NotNull(secondToken);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(2, adds());
    }

    [Fact]
    public async Task LostLease_IsNotHandedOff_WaiterAcquiresAFreshRecord()
    {
        var locks = NewLocks(TimeSpan.FromMilliseconds(1500));

        var first = await locks.AcquireTriggerAccessAsync(Generous);
        var firstToken = Record(TriggerLockKey)!.OwnershipId;

        Cache.Remove(TriggerLockKey);
        Assert.True(await WaitUntilAsync(() => first.HandleLostToken.IsCancellationRequested, Generous), "lock loss was not detected");

        var waiter = locks.AcquireTriggerAccessAsync(Generous);
        first.Dispose();

        await using var second = await waiter.WaitAsync(Generous);
        var record = Record(TriggerLockKey);

        Assert.NotNull(record);
        Assert.NotEqual(firstToken, record!.OwnershipId);
        Assert.False(second.HandleLostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UrgentWaiters_AreServedBeforeOrdinaryWaitersQueuedEarlier_EachLaneFifo()
    {
        var locks = NewLocks();
        var order = new List<string>();

        async Task Take(string name, Task<IDistributedSynchronizationHandle> acquisition)
        {
            await using var handle = await acquisition.WaitAsync(Generous);

            lock (order)
            {
                order.Add(name);
            }
        }

        var holder = await locks.AcquireTriggerAccessAsync(Generous);

        var o1 = Take("ordinary-1", locks.AcquireTriggerAccessAsync(Generous));
        var o2 = Take("ordinary-2", locks.AcquireTriggerAccessAsync(Generous));
        var u1 = Take("urgent-1", locks.AcquireTriggerAccessAsync(Generous, urgent: true));
        var u2 = Take("urgent-2", locks.AcquireTriggerAccessAsync(Generous, urgent: true));

        await holder.DisposeAsync();
        await Task.WhenAll(o1, o2, u1, u2);

        Assert.Equal(new[] { "urgent-1", "urgent-2", "ordinary-1", "ordinary-2" }, order);
        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task UrgentWaiter_IsServedFirst_AlsoWhenTheLockIsReallyReleased()
    {
        var locks = NewLocks();
        var order = new List<string>();

        async Task Take(string name, Task<IDistributedSynchronizationHandle> acquisition)
        {
            await using var handle = await acquisition.WaitAsync(Generous);

            lock (order)
            {
                order.Add(name);
            }
        }

        var holder = await locks.AcquireTriggerAccessAsync(Generous);

        var o1 = Take("ordinary", locks.AcquireTriggerAccessAsync(Generous));
        var u1 = Take("urgent", locks.AcquireTriggerAccessAsync(Generous, urgent: true));

        await Task.Delay(NCacheDistributedLocks.MaxStickyHold + TimeSpan.FromMilliseconds(150));
        await holder.DisposeAsync();
        await Task.WhenAll(o1, u1);

        Assert.Equal(new[] { "urgent", "ordinary" }, order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquire_HeldOnSameInstance_TimesOutWithTimeoutException(bool state)
    {
        var locks = NewLocks();

        await using var holder = await Acquire(locks, state, Generous);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => Acquire(locks, state, TimeSpan.FromMilliseconds(250)));

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(200), $"gave up too early ({sw.Elapsed})");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"gave up too late ({sw.Elapsed})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryAcquire_HeldOnSameInstance_ReturnsNull(bool state)
    {
        var locks = NewLocks();

        await using var holder = await Acquire(locks, state, Generous);

        Assert.Null(await TryAcquire(locks, state, TimeSpan.FromMilliseconds(250)));
        Assert.Null(await TryAcquire(locks, state));
    }

    [Fact]
    public async Task TryAcquire_ZeroTimeout_OnAFreeLock_Succeeds()
    {
        var locks = NewLocks();

        await using var handle = await locks.TryAcquireTriggerAccessAsync(TimeSpan.Zero);

        Assert.NotNull(handle);
        Assert.NotNull(Record(TriggerLockKey));
    }

    [Fact]
    public async Task TimedOutLocalWaiters_LeaveNoQueueResidue()
    {
        var (proxy, control) = FaultInjectingCache.Wrap(Cache);
        var adds = CountCalls(control, "Add", TriggerLockKey);
        var locks = NewLocks(cache: proxy);

        var holder = await locks.AcquireTriggerAccessAsync(Generous);

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await locks.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(50)));
        }

        await Assert.ThrowsAsync<TimeoutException>(() => locks.AcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(50), urgent: true));

        holder.Dispose();
        Assert.Null(Record(TriggerLockKey));

        var sw = Stopwatch.StartNew();
        await using var next = await locks.TryAcquireTriggerAccessAsync(TimeSpan.Zero);

        Assert.NotNull(next);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"acquisition after release took {sw.Elapsed}");
        Assert.Equal(2, adds());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteWait_TimesOut_LeavesNoTicketAndNoStrayLock(bool throwing)
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();

        var holder = await nodeA.AcquireTriggerAccessAsync(Generous);
        var holderToken = Record(TriggerLockKey)!.OwnershipId;

        if (throwing)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => nodeB.AcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(400)));
        }
        else
        {
            Assert.Null(await nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(400)));
        }

        Assert.Null(Ticket(TriggerTicketKey));
        Assert.Equal(holderToken, Record(TriggerLockKey)?.OwnershipId);
        Assert.False(holder.HandleLostToken.IsCancellationRequested);

        holder.Dispose();
        Assert.Null(Record(TriggerLockKey));

        await using var next = await nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task RemoteWait_ZeroTimeout_ReturnsNullPromptly_WithoutTicket()
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();

        await using var holder = await nodeA.AcquireTriggerAccessAsync(Generous);

        var sw = Stopwatch.StartNew();
        Assert.Null(await nodeB.TryAcquireTriggerAccessAsync());

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"zero-timeout attempt took {sw.Elapsed}");
        Assert.Null(Ticket(TriggerTicketKey));
    }

    [Fact]
    public async Task PreCancelledToken_OnAFreeLock_ThrowsAndWritesNothing()
    {
        var locks = NewLocks();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireTriggerAccessAsync(Generous, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.TryAcquireStateAccessAsync(Generous, cts.Token));

        Assert.Null(Record(TriggerLockKey));
        Assert.Null(Record(StateLockKey));

        await using var handle = await locks.TryAcquireTriggerAccessAsync(TimeSpan.Zero);
        Assert.NotNull(handle);
    }

    [Fact]
    public async Task PreCancelledToken_WhileHeldLocally_Throws()
    {
        var locks = NewLocks();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var holder = await locks.AcquireTriggerAccessAsync(Generous);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireTriggerAccessAsync(Generous, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireTriggerAccessAsync(cancellationToken: cts.Token));

        holder.Dispose();
        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task CancelWhileWaitingLocally_Throws_AndLeavesTheLockUsable()
    {
        var locks = NewLocks();
        using var cts = new CancellationTokenSource();

        var holder = await locks.AcquireTriggerAccessAsync(Generous);
        var waiter = locks.AcquireTriggerAccessAsync(cancellationToken: cts.Token);

        await Task.Delay(100);
        Assert.False(waiter.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Generous));

        holder.Dispose();
        Assert.Null(Record(TriggerLockKey));

        await using var next = await locks.TryAcquireTriggerAccessAsync(TimeSpan.Zero);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task CancelWhileWaitingRemotely_Throws_RemovesTicket_AndLeavesTheLockUsable()
    {
        var nodeA = NewLocks();
        var nodeB = NewLocks();
        using var cts = new CancellationTokenSource();

        var holder = await nodeA.AcquireTriggerAccessAsync(Generous);
        var holderToken = Record(TriggerLockKey)!.OwnershipId;

        var waiter = nodeB.AcquireTriggerAccessAsync(cancellationToken: cts.Token);
        Assert.True(await WaitUntilAsync(() => Ticket(TriggerTicketKey) is not null, TimeSpan.FromSeconds(10)), "remote waiter never wrote its ticket");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Generous));

        Assert.Null(Ticket(TriggerTicketKey));
        Assert.Equal(holderToken, Record(TriggerLockKey)?.OwnershipId);

        holder.Dispose();
        Assert.Null(Record(TriggerLockKey));

        await using var next = await nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task HandOffRacingCancellation_NeverLeaksTheLock()
    {
        var locks = NewLocks();

        for (var i = 0; i < 25; i++)
        {
            using var cts = new CancellationTokenSource();

            var holder = await locks.AcquireTriggerAccessAsync(Generous);
            var waiter = locks.AcquireTriggerAccessAsync(Generous, cts.Token);

            if (i % 2 == 0)
            {
                holder.Dispose();
                cts.Cancel();
            }
            else
            {
                await Task.WhenAll(Task.Run(() => cts.Cancel()), Task.Run(() => holder.Dispose()));
            }

            try
            {
                var handle = await waiter.WaitAsync(Generous);
                await handle.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }

            await using (var next = await locks.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.True(next is not null, $"iteration {i}: lock leaked");
            }

            Assert.True(Record(TriggerLockKey) is null, $"iteration {i}: record left behind");
        }
    }

    [Fact]
    public async Task RemoteWaiter_TakesOverOnRelease_ByTransfer_RecordNeverAbsent()
    {
        var (proxyB, controlB) = FaultInjectingCache.Wrap(Cache);
        var addsByB = CountCalls(controlB, "Add", TriggerLockKey);
        var nodeA = NewLocks();
        var nodeB = NewLocks(cache: proxyB);

        var holder = await nodeA.AcquireTriggerAccessAsync(Generous);
        var holderToken = Record(TriggerLockKey)!.OwnershipId;

        var waiter = nodeB.AcquireTriggerAccessAsync(Generous);
        Assert.True(await WaitUntilAsync(() => Ticket(TriggerTicketKey) is not null, TimeSpan.FromSeconds(10)), "remote waiter never wrote its ticket");

        var absentSeen = 0;
        using var stopWatching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!stopWatching.IsCancellationRequested)
            {
                if (Record(TriggerLockKey) is null)
                {
                    Interlocked.Increment(ref absentSeen);
                }

                await Task.Delay(1);
            }
        });

        var released = Stopwatch.StartNew();
        holder.Dispose();

        var transferred = Record(TriggerLockKey);
        Assert.NotNull(transferred);
        Assert.NotEqual(holderToken, transferred!.OwnershipId);

        var handle = await waiter.WaitAsync(Generous);
        var tookOver = released.Elapsed;

        stopWatching.Cancel();
        await watcher;

        Assert.True(tookOver < TimeSpan.FromSeconds(2), $"remote waiter took {tookOver} to take over");
        Assert.Equal(0, Volatile.Read(ref absentSeen));
        Assert.Equal(0, addsByB());
        Assert.Equal(transferred.OwnershipId, Record(TriggerLockKey)?.OwnershipId);
        Assert.Null(Ticket(TriggerTicketKey));
        Assert.False(handle.HandleLostToken.IsCancellationRequested);

        await handle.DisposeAsync();
        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task RemoteWaiter_ThatGivesUpAfterATransfer_ReleasesTheStrayTransfer()
    {
        var (proxyB, controlB) = FaultInjectingCache.Wrap(Cache);
        var nodeA = NewLocks();
        var nodeB = NewLocks(cache: proxyB);

        var holder = await nodeA.AcquireTriggerAccessAsync(Generous);
        var holderToken = Record(TriggerLockKey)!.OwnershipId;

        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = 0;
        var ticketRead = FaultInjectingCache.Call("Get", k => k == TriggerTicketKey);
        controlB.DelayAfter = (method, args) =>
        {
            if (ticketRead(method, args) && Interlocked.Exchange(ref stalled, 1) == 0)
            {
                cleanupReached.TrySetResult();
                return TimeSpan.FromMilliseconds(1500);
            }

            return null;
        };

        var waiter = nodeB.TryAcquireTriggerAccessAsync(TimeSpan.FromMilliseconds(400));

        await cleanupReached.Task.WaitAsync(Generous);
        holder.Dispose();

        var transferred = Record(TriggerLockKey);
        Assert.NotNull(transferred);
        Assert.NotEqual(holderToken, transferred!.OwnershipId);

        Assert.Null(await waiter.WaitAsync(Generous));

        Assert.Null(Record(TriggerLockKey));
        Assert.Null(Ticket(TriggerTicketKey));

        await using var next = await nodeA.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ManyRemoteWaiters_AllGetTheLockInTurn()
    {
        var holderNode = NewLocks();
        var waiterNodes = Enumerable.Range(0, 3).Select(_ => NewLocks()).ToList();

        var holder = await holderNode.AcquireTriggerAccessAsync(Generous);

        var acquired = 0;
        var waiters = waiterNodes.Select(node => Task.Run(async () =>
        {
            await using var handle = await node.AcquireTriggerAccessAsync(Generous);
            Interlocked.Increment(ref acquired);
            await Task.Delay(20);
        })).ToList();

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref acquired));

        holder.Dispose();
        await Task.WhenAll(waiters).WaitAsync(Generous);

        Assert.Equal(3, acquired);
        Assert.True(await WaitUntilAsync(() => Record(TriggerLockKey) is null && Ticket(TriggerTicketKey) is null, TimeSpan.FromSeconds(10)), "record or ticket left behind");
    }

    [Fact]
    public async Task RecordRemovedBehindTheHoldersBack_CancelsHandleLostToken()
    {
        var locks = NewLocks(TimeSpan.FromMilliseconds(1500));

        var handle = await locks.AcquireTriggerAccessAsync(Generous);
        Cache.Remove(TriggerLockKey);

        Assert.True(await WaitUntilAsync(() => handle.HandleLostToken.IsCancellationRequested, Generous), "lock loss was not detected");

        handle.Dispose();
        Assert.Null(Record(TriggerLockKey));
    }

    [Fact]
    public async Task RecordOverwrittenByAnotherToken_CancelsHandleLostToken_AndReleaseLeavesTheOtherRecord()
    {
        var locks = NewLocks(TimeSpan.FromMilliseconds(1500));

        var handle = await locks.AcquireStateAccessAsync(Generous);

        var foreign = new DistributedLockAcquisition(TimeSpan.FromSeconds(30));
        Cache.Insert(StateLockKey, new CacheItem(foreign) { Expiration = new Expiration(ExpirationType.Absolute, TimeSpan.FromSeconds(30)) });

        Assert.True(await WaitUntilAsync(() => handle.HandleLostToken.IsCancellationRequested, Generous), "lock loss was not detected");

        await handle.DisposeAsync();
        Assert.Equal(foreign.OwnershipId, Record(StateLockKey)?.OwnershipId);

        Cache.Remove(StateLockKey);
    }

    [Fact]
    public async Task RecordExpiring_CancelsHandleLostToken()
    {
        var locks = NewLocks(TimeSpan.FromSeconds(1));

        var handle = await locks.AcquireTriggerAccessAsync(Generous);

        Assert.True(await WaitUntilAsync(() => handle.HandleLostToken.IsCancellationRequested, TimeSpan.FromSeconds(60)), "lock loss after expiry was not detected");
        Assert.Null(Record(TriggerLockKey));

        handle.Dispose();

        await using var next = await locks.TryAcquireTriggerAccessAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }
}

