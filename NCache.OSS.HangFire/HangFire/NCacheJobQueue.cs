//NCacheJobQueue
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Hangfire.Logging;
using System;
using System.Collections.Generic;

namespace NCache.OSS.Hangfire
{
    internal class NCacheJobQueue
    {
        private readonly NCacheStorage _storage;
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(NCacheJobQueue));
        public NCacheJobQueue(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }
        public void Enqueue(string queue, string jobId)
        {
            var cache = _storage.Cache;
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            NCacheLockGuard.EnsureKeyExists(cache, tailKey, () => 0);

            NCacheLockGuard.Execute(
                cache,
                tailKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;
                    var itemKey = NCacheKeys.QueueItem(_storage.Prefix, queue, tail);
                    cache.Insert(itemKey, new CacheItem(jobId));
                    cache.Insert(tailKey, new CacheItem(tail + 1));
                });

            RegisterQueueName(queue);

            if (_storage.Options.EnablePubSubNotifications)
                PublishEnqueueNotification(queue);
        }

        public string TryDequeue(string queue)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            if (!cache.Contains(tailKey))
                return null; // nothing has ever been enqueued to this queue

            string jobId = null;

            NCacheLockGuard.EnsureKeyExists(cache, headKey, () => 0);

            var acquired = NCacheLockGuard.TryExecute(
                cache,
                headKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
                    var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;

                    if (head >= tail) return; // empty

                    var itemKey = NCacheKeys.QueueItem(_storage.Prefix, queue, head);
                    if (!cache.Contains(itemKey))
                    {
                        // Slot was skipped (shouldn't normally happen) — advance past it 
                        cache.Insert(headKey, new CacheItem(head + 1));
                        return;
                    }

                    jobId = cache.Get<string>(itemKey);
                    cache.Remove(itemKey);
                    cache.Insert(headKey, new CacheItem(head + 1));
                });

            return acquired ? jobId : null;
        }

        // IMPORTANT: NOT a delegate to Enqueue. Requeue must put the job back at the
        // FRONT of the queue (its original priority position), not the back — otherwise
        // FetchedJobsWatcher's orphan recovery silently reorders jobs behind everything
        // enqueued since the crash, which breaks FIFO/priority guarantees for recovered jobs.
        // We do this by decrementing head and writing the job back into the freed slot,
        // when possible; if another dequeue already moved past that slot, fall back to Enqueue.
        public void Requeue(string queue, string jobId)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            // Queue was never initialized
            if (!cache.Contains(headKey) && !cache.Contains(tailKey))
            {
                Enqueue(queue, jobId);
                return;
            }
            NCacheLockGuard.EnsureKeyExists(cache, headKey, () => 0);

            bool placedAtFront = false;

            NCacheLockGuard.TryExecute(
                cache,
                headKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
                    var newHead = head - 1;
                    var itemKey = NCacheKeys.QueueItem(_storage.Prefix, queue, newHead);

                    cache.Insert(itemKey, new CacheItem(jobId));
                    cache.Insert(headKey, new CacheItem(newHead));
                    placedAtFront = true;
                });

            if (!placedAtFront)
            {
                Enqueue(queue, jobId);
                return;
            }

            if (_storage.Options.EnablePubSubNotifications)
                PublishEnqueueNotification(queue);
        }

        public int GetTotalLength(string queue)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
            var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;

            return Math.Max(0, tail - head);
        }

        public List<string> GetAllIds(string queue)
        {
            var cache = _storage.Cache;
            var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
            var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

            var head = cache.Contains(headKey) ? cache.Get<int>(headKey) : 0;
            var tail = cache.Contains(tailKey) ? cache.Get<int>(tailKey) : 0;

            var result = new List<string>();
            for (int i = head; i < tail; i++)
            {
                var itemKey = NCacheKeys.QueueItem(_storage.Prefix, queue, i);
                if (cache.Contains(itemKey))
                    result.Add(cache.Get<string>(itemKey));
            }
            return result;
        }
       
        private void RegisterQueueName(string queue)
        {
            var cache = _storage.Cache;
            var registryKey = NCacheKeys.QueueRegistry(_storage.Prefix);

            if (cache.Contains(registryKey))
            {
                var existing = cache.Get<HashSet<string>>(registryKey);  
                if (existing != null && existing.Contains(queue)) return;
            }

            NCacheLockGuard.EnsureKeyExists(cache, registryKey, () => new HashSet<string>());
            NCacheLockGuard.TryExecute(cache, registryKey, _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait, () =>
                {
                    var registry = cache.Get<HashSet<string>>(registryKey) ?? new HashSet<string>();
                    if (registry.Add(queue)) cache.Insert(registryKey, new CacheItem(registry));
                });
        }

        private void PublishEnqueueNotification(string queue)
        {
            try
            {
                var topic = _storage.GetOrCreateTopic(queue);
                topic.Publish(new Message(queue), DeliveryOption.Any);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not publish enqueue notification for queue '{queue}': {ex.Message}");
            }
        } 
    }
}