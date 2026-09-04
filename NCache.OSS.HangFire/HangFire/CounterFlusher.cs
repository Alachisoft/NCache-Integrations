using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Hangfire.Server;
using System;

namespace NCache.OSS.Hangfire
{
    internal class CounterFlusher : IBackgroundProcess
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
        private readonly NCacheStorage _storage;

        public CounterFlusher(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public void Execute(BackgroundProcessContext context)
        {
            while (!context.IsStopping)
            {
                FlushAll();
                context.Wait(FlushInterval);
            }
            FlushAll();
        }

        internal void FlushAll()
        {
            foreach (var kvp in _storage.PendingCounterDeltas)
                FlushOne(kvp.Key, kvp.Value);
        }

        private void FlushOne(string counterName, System.Collections.Concurrent.ConcurrentQueue<(long Delta, TimeSpan? ExpireIn)> queue)
        {
            if (queue.IsEmpty) return;

            long sum = 0;
            TimeSpan? latestExpireIn = null;

            while (queue.TryDequeue(out var entry))
            {
                sum += entry.Delta;
                if (entry.ExpireIn != null)
                    latestExpireIn = entry.ExpireIn;
            }

            if (sum == 0 && latestExpireIn == null) return;

            var cache = _storage.Cache;
            var counterKey = NCacheKeys.Counter(_storage.Prefix, counterName);

            NCacheLockGuard.EnsureKeyExists(cache, counterKey, () => 0L);

            NCacheLockGuard.Execute(
                cache,
                counterKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var current = cache.Contains(counterKey) ? cache.Get<long>(counterKey) : 0L;
                    var item = new CacheItem(current + sum);
                    if (latestExpireIn != null)
                        item.Expiration = new Expiration(ExpirationType.Absolute, latestExpireIn.Value);
                    cache.Insert(counterKey, item);
                });
        }
    }
}