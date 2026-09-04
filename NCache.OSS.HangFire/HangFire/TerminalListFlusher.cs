using System;
using System.Collections.Generic;
using Alachisoft.NCache.Client;
using Hangfire.Server;

namespace NCache.OSS.Hangfire
{ 
    internal class TerminalListFlusher : IBackgroundProcess
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
        private readonly NCacheStorage _storage;

        public TerminalListFlusher(NCacheStorage storage)
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
            foreach (var kvp in _storage.PendingListPrepends)
            {
                FlushOne(kvp.Key, kvp.Value);
            }
        }

        private void FlushOne(string listName, System.Collections.Concurrent.ConcurrentQueue<string> queue)
        {
            if (queue.IsEmpty) return;

            var batch = new List<string>();
            while (queue.TryDequeue(out var jobId))
                batch.Add(jobId);

            if (batch.Count == 0) return;

            var cache = _storage.Cache;
            var listKey = NCacheKeys.List(_storage.Prefix, listName);

            NCacheLockGuard.EnsureKeyExists(cache, listKey, () => new List<string>());

            NCacheLockGuard.Execute(
                cache,
                listKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var list = cache.Get<List<string>>(listKey) ?? new List<string>();
                     
                    foreach (var jobId in batch)
                    {
                        list.RemoveAll(id => id == jobId);
                        list.Insert(0, jobId);
                    }

                    var maxSize = listName == "succeeded"
                        ? _storage.Options.SucceededListSize
                        : listName == "deleted"
                            ? _storage.Options.DeletedListSize
                            : listName == "failed"
                                ? _storage.Options.FailedListSize
                                : int.MaxValue;

                    if (list.Count > maxSize)
                        list = list.GetRange(0, maxSize);

                    cache.Insert(listKey, new CacheItem(list));
                });
        }
    }
}