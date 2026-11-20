using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Medallion.Threading;
using NCache.DistributedLock.Primitives;
using NCache.DistributedLock.SynchronizationHandles;
using NCache.OSS.Quartz.Keys;

namespace NCache.OSS.Quartz.Locking;

public sealed class NCacheDistributedLocks
{
    private const string PackageLockKeyPrefix = "ncache#distLock#";

    public static readonly TimeSpan DefaultLockExpiration = TimeSpan.FromSeconds(30);

    public const int MaxLocalHandoffs = 4;

    public static readonly TimeSpan MaxStickyHold = TimeSpan.FromMilliseconds(100);

    private const int TightPollMs = 1;

    private static readonly TimeSpan TightPollFor = TimeSpan.FromSeconds(1);

    private const int MaxPollMs = 50;

    private const int TicketRewriteEveryPolls = 5;

    private static readonly TimeSpan ClaimSettle = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan ForeignActivityWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TicketMaxAge = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(5);

    private readonly ICache _cache;
    private readonly string _prefix;
    private readonly string _schedName;

    private readonly LockSlot _triggerAccess;
    private readonly LockSlot _stateAccess;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LockSlot> _firingsLocks = new(StringComparer.Ordinal);

    [Serializable]
    public sealed class WaitTicket
    {
        public DistributedLockAcquisition? Token { get; set; }
        public DateTime WrittenAtUtc { get; set; }
    }

    private sealed record Grant(Lease? Lease);

    private sealed class Lease
    {
        public Lease(IDistributedSynchronizationHandle handle, DistributedLockAcquisition token)
        {
            Handle = handle;
            Token = token;
            AcquiredAt = DateTime.UtcNow;
        }

        public IDistributedSynchronizationHandle Handle { get; }
        public DistributedLockAcquisition Token { get; }
        public DateTime AcquiredAt { get; }
        public int Handoffs { get; set; }

        public bool CanHandOff =>
            Handoffs < MaxLocalHandoffs &&
            DateTime.UtcNow - AcquiredAt < MaxStickyHold &&
            !Handle.HandleLostToken.IsCancellationRequested;
    }

    private sealed class LockSlot
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource<Grant>> _urgentWaiters = new();
        private readonly Queue<TaskCompletionSource<Grant>> _waiters = new();
        private bool _held;

        public LockSlot(string name, string key)
        {
            Name = name;
            Key = key;
            LockKey = PackageLockKeyPrefix + key;
            TicketKey = NCacheKeys.LockRemoteWaitingMarker(key);
        }

        public string Name { get; }
        public string Key { get; }
        public string LockKey { get; }
        public string TicketKey { get; }

        private long _lastForeignTicks = DateTime.MinValue.Ticks;

        public void NoteForeignNode() => Interlocked.Exchange(ref _lastForeignTicks, DateTime.UtcNow.Ticks);

        public bool SawForeignNodeWithin(TimeSpan window) => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastForeignTicks) < window.Ticks;

        public TaskCompletionSource<Grant>? Enter(bool urgent)
        {
            lock (_sync)
            {
                if (!_held)
                {
                    _held = true;
                    return null;
                }

                var tcs = new TaskCompletionSource<Grant>(TaskCreationOptions.RunContinuationsAsynchronously);
                (urgent ? _urgentWaiters : _waiters).Enqueue(tcs);
                return tcs;
            }
        }

        private bool TryDequeue(out TaskCompletionSource<Grant> waiter)
        {
            if (_urgentWaiters.Count > 0)
            {
                waiter = _urgentWaiters.Dequeue();
                return true;
            }

            if (_waiters.Count > 0)
            {
                waiter = _waiters.Dequeue();
                return true;
            }

            waiter = null!;
            return false;
        }

        public bool TryHandOff(Lease lease)
        {
            lock (_sync)
            {
                lease.Handoffs++;

                while (TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult(new Grant(lease)))
                    {
                        return true;
                    }
                }

                lease.Handoffs--;
                return false;
            }
        }

        public void Leave()
        {
            lock (_sync)
            {
                while (TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult(new Grant(null)))
                    {
                        return;
                    }
                }

                _held = false;
            }
        }
    }

    public NCacheDistributedLocks(ICache cache, string prefix, string schedName, TimeSpan? lockExpiration = null)
    {
        ArgumentNullException.ThrowIfNull(cache);

        LockExpiration = lockExpiration ?? DefaultLockExpiration;

        if (LockExpiration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockExpiration), "Lock expiration must be positive.");
        }

        if (MaxStickyHold >= LockExpiration)
        {
            throw new ArgumentOutOfRangeException(nameof(lockExpiration), $"Lock expiration must exceed the local hand-off window ({MaxStickyHold}).");
        }

        _cache = cache;
        _prefix = prefix;
        _schedName = schedName;

        _triggerAccess = new LockSlot("TRIGGER_ACCESS", NCacheKeys.TriggerAccessLock(prefix, schedName));
        _stateAccess = new LockSlot("STATE_ACCESS", NCacheKeys.StateAccessLock(prefix, schedName));
    }

    public Task<IDistributedSynchronizationHandle> AcquireFiringsAccessAsync(string instanceName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var slot = _firingsLocks.GetOrAdd(instanceName, name => new LockSlot($"FIRINGS_{name}", NCacheKeys.FiringsAccessLock(_prefix, _schedName, name)));

        return AcquireAsync(slot, timeout, cancellationToken, urgent: false);
    }

    public TimeSpan LockExpiration { get; }

    public Task<IDistributedSynchronizationHandle> AcquireTriggerAccessAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default, bool urgent = false) =>
        AcquireAsync(_triggerAccess, timeout, cancellationToken, urgent);

    public Task<IDistributedSynchronizationHandle?> TryAcquireTriggerAccessAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default) =>
        TryAcquireAsync(_triggerAccess, timeout, cancellationToken, urgent: false);

    public Task<IDistributedSynchronizationHandle> AcquireStateAccessAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        AcquireAsync(_stateAccess, timeout, cancellationToken, urgent: false);

    public Task<IDistributedSynchronizationHandle?> TryAcquireStateAccessAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default) =>
        TryAcquireAsync(_stateAccess, timeout, cancellationToken, urgent: false);

    private async Task<IDistributedSynchronizationHandle> AcquireAsync(LockSlot slot, TimeSpan? timeout, CancellationToken ct, bool urgent) =>
        await TryAcquireAsync(slot, timeout, ct, urgent) ?? throw new TimeoutException($"Could not acquire the {slot.Name} lock within {timeout}.");

    private async Task<IDistributedSynchronizationHandle?> TryAcquireAsync(LockSlot slot, TimeSpan? timeout, CancellationToken ct, bool urgent)
    {
        var deadline = timeout.HasValue ? DateTime.UtcNow + timeout.Value : (DateTime?)null;

        var queued = slot.Enter(urgent);

        if (queued is not null)
        {
            var grant = await WaitForTurnAsync(queued, timeout, ct);

            if (grant is null)
            {
                return null;
            }

            if (grant.Lease is not null)
            {
                if (ct.IsCancellationRequested)
                {
                    await new GatedHandle(this, slot, grant.Lease).DisposeAsync();
                    ct.ThrowIfCancellationRequested();
                }

                return new GatedHandle(this, slot, grant.Lease);
            }
        }

        try
        {
            var remaining = deadline.HasValue ? deadline.Value - DateTime.UtcNow : (TimeSpan?)null;
            var lease = await TryAcquireRemoteAsync(slot, remaining, ct);

            if (lease is null)
            {
                slot.Leave();
                return null;
            }

            return new GatedHandle(this, slot, lease);
        }
        catch
        {
            slot.Leave();
            throw;
        }
    }

    private static async Task<Grant?> WaitForTurnAsync(TaskCompletionSource<Grant> waiter, TimeSpan? timeout, CancellationToken ct)
    {
        if (!timeout.HasValue && !ct.CanBeCanceled)
        {
            return await waiter.Task.ConfigureAwait(false);
        }

        using var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = Task.Delay(timeout ?? Timeout.InfiniteTimeSpan, stopTimer.Token);
        var first = await Task.WhenAny(waiter.Task, limit).ConfigureAwait(false);

        if (first == waiter.Task)
        {
            stopTimer.Cancel();
            return await waiter.Task.ConfigureAwait(false);
        }

        if (waiter.TrySetCanceled())
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }

        return await waiter.Task.ConfigureAwait(false);
    }

    private async Task<Lease?> TryAcquireRemoteAsync(LockSlot slot, TimeSpan? remaining, CancellationToken ct)
    {
        var deadline = remaining.HasValue ? DateTime.UtcNow + remaining.Value : (DateTime?)null;

        ct.ThrowIfCancellationRequested();

        var token = new DistributedLockAcquisition(LockExpiration);
        var ticketWritten = false;
        var acquired = false;

        try
        {
            var handle = await TryClaimAndAcquireAsync(slot, token, ct);
            ticketWritten = true;

            if (handle is not null)
            {
                acquired = true;
                return new Lease(handle, token);
            }

            var waitingSince = DateTime.UtcNow;
            var pollsSinceTicket = 0;

            while (true)
            {
                if (deadline.HasValue && DateTime.UtcNow >= deadline.Value)
                {
                    return null;
                }

                var delayMs = DateTime.UtcNow - waitingSince < TightPollFor ? TightPollMs : MaxPollMs;
                await Task.Delay(delayMs + Random.Shared.Next(0, delayMs), ct);

                var (adopted, held) = await Task.Run(() => TryAdopt(slot, token), ct);

                if (adopted is not null)
                {
                    acquired = true;
                    return new Lease(adopted, token);
                }

                if (held)
                {
                    slot.NoteForeignNode();
                }

                if (!held)
                {
                    handle = await TryClaimAndAcquireAsync(slot, token, ct);

                    if (handle is not null)
                    {
                        acquired = true;
                        return new Lease(handle, token);
                    }

                    pollsSinceTicket = 0;
                }

                if (++pollsSinceTicket >= TicketRewriteEveryPolls)
                {
                    pollsSinceTicket = 0;
                    ticketWritten |= await WriteTicketAsync(slot, token);
                }
            }
        }
        finally
        {
            if (ticketWritten)
            {
                await RemoveTicketAsync(slot, token);
            }

            if (!acquired)
            {
                await ReleaseStrayTransferAsync(slot, token);
            }
        }
    }

    private async Task<IDistributedSynchronizationHandle?> TryClaimAndAcquireAsync(LockSlot slot, DistributedLockAcquisition token, CancellationToken ct)
    {
        if (!slot.SawForeignNodeWithin(ForeignActivityWindow))
        {
            var handle = await TryAddAsync(slot, token, ct);

            if (handle is null)
            {
                slot.NoteForeignNode();
                await WriteTicketAsync(slot, token);
            }

            return handle;
        }

        if (!await WriteTicketAsync(slot, token))
        {
            return await TryAddAsync(slot, token, ct);
        }

        await Task.Run(Settle, ct);

        var winner = await Task.Run(() => ReadTicketToken(slot), ct);

        if (winner is not null && !winner.IsEqual(token))
        {
            slot.NoteForeignNode();

            await WriteTicketAsync(slot, token);
            return null;
        }

        return await TryAddAsync(slot, token, ct);
    }

    private static void Settle()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed < ClaimSettle)
        {
            Thread.SpinWait(200);
        }
    }

    private Task<IDistributedSynchronizationHandle?> TryAddAsync(LockSlot slot, DistributedLockAcquisition token, CancellationToken ct) =>
        Task.Run<IDistributedSynchronizationHandle?>(() =>
        {
            try
            {
                if (_cache.Contains(slot.LockKey))
                {
                    return null;
                }

                _cache.Add(slot.LockKey, LockRecord(token));
                return new NCacheDistributedLockSynchronizationHandle(slot.LockKey, _cache, token, LockExpiration);
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException)
            {
                return null;
            }
        }, ct);

    private (IDistributedSynchronizationHandle? Handle, bool Held) TryAdopt(LockSlot slot, DistributedLockAcquisition token)
    {
        DistributedLockAcquisition? current;

        try
        {
            current = _cache.Get<DistributedLockAcquisition>(slot.LockKey);
        }
        catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException)
        {
            return (null, true);
        }

        if (current is not null && current.IsEqual(token))
        {
            return (new NCacheDistributedLockSynchronizationHandle(slot.LockKey, _cache, token, LockExpiration), true);
        }

        return (null, current is not null);
    }

    private bool TransferTo(LockSlot slot, Lease lease, DistributedLockAcquisition next)
    {
        var current = _cache.Get<DistributedLockAcquisition>(slot.LockKey);

        if (current is null || !current.IsEqual(lease.Token))
        {
            return false;
        }

        _cache.Insert(slot.LockKey, LockRecord(next));
        return true;
    }

    private CacheItem LockRecord(DistributedLockAcquisition token) =>
        new(token)
        {
            Expiration = new Expiration(ExpirationType.Absolute, LockExpiration)
        };

    private DistributedLockAcquisition? ReadTicketToken(LockSlot slot)
    {
        try
        {
            return _cache.Get<WaitTicket>(slot.TicketKey)?.Token;
        }
        catch
        {
            return null;
        }
    }

    private Task<bool> WriteTicketAsync(LockSlot slot, DistributedLockAcquisition token) =>
        Task.Run(() =>
        {
            try
            {
                var item = new CacheItem(new WaitTicket { Token = token, WrittenAtUtc = DateTime.UtcNow })
                {
                    Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, TicketTtl)
                };

                _cache.Insert(slot.TicketKey, item);
                return true;
            }
            catch
            {
                return false;
            }
        });

    private Task RemoveTicketAsync(LockSlot slot, DistributedLockAcquisition token) =>
        Task.Run(() =>
        {
            try
            {
                var ticket = _cache.Get<WaitTicket>(slot.TicketKey);

                if (ticket?.Token is not null && ticket.Token.IsEqual(token))
                {
                    _cache.Remove(slot.TicketKey);
                }
            }
            catch
            {
            }
        });

    private DistributedLockAcquisition? ReadFreshTicket(LockSlot slot)
    {
        try
        {
            var ticket = _cache.Get<WaitTicket>(slot.TicketKey);

            if (ticket?.Token is null || DateTime.UtcNow - ticket.WrittenAtUtc > TicketMaxAge)
            {
                return null;
            }

            return ticket.Token;
        }
        catch
        {
            return null;
        }
    }

    private Task ReleaseStrayTransferAsync(LockSlot slot, DistributedLockAcquisition token) =>
        Task.Run(() =>
        {
            try
            {
                TryAdopt(slot, token).Handle?.Dispose();
            }
            catch
            {
            }
        });

    private bool ReleaseOrTransfer(LockSlot slot, Lease lease)
    {
        var next = ReadFreshTicket(slot);
        var transferred = false;

        if (next is not null)
        {
            slot.NoteForeignNode();

            try
            {
                transferred = TransferTo(slot, lease, next);
            }
            catch
            {
            }
        }

        lease.Handle.Dispose();
        return transferred;
    }

    private sealed class GatedHandle : IDistributedSynchronizationHandle
    {
        private readonly NCacheDistributedLocks _owner;
        private readonly LockSlot _slot;
        private Lease? _lease;

        public GatedHandle(NCacheDistributedLocks owner, LockSlot slot, Lease lease)
        {
            _owner = owner;
            _slot = slot;
            _lease = lease;
        }

        public CancellationToken HandleLostToken => (_lease?.Handle ?? throw new ObjectDisposedException(nameof(GatedHandle))).HandleLostToken;

        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref _lease, null);

            if (lease is null)
            {
                return;
            }

            if (lease.CanHandOff && _slot.TryHandOff(lease))
            {
                return;
            }

            try
            {
                _owner.ReleaseOrTransfer(_slot, lease);
            }
            finally
            {
                _slot.Leave();
            }
        }

        public async ValueTask DisposeAsync()
        {
            var lease = Interlocked.Exchange(ref _lease, null);

            if (lease is null)
            {
                return;
            }

            if (lease.CanHandOff && _slot.TryHandOff(lease))
            {
                return;
            }

            try
            {
                await Task.Run(() => _owner.ReleaseOrTransfer(_slot, lease));
            }
            finally
            {
                _slot.Leave();
            }
        }
    }
}

