using Alachisoft.NCache.Client;
using Hangfire.Logging;
using Hangfire.Storage;
using System;
using System.Collections.Generic;
using System.Threading;

namespace NCache.OSS.Hangfire
{
    internal class NCacheFetchedJob : IFetchedJob
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(NCacheFetchedJob));

        private readonly NCacheStorage _storage;
        private readonly NCacheJobQueue _jobQueue;
        private readonly object _stateLock = new object();
        private Timer _heartbeatTimer;
        private bool _removedFromQueue;
        private bool _requeued;
        private bool _heartbeatStopped;

        public NCacheFetchedJob(NCacheStorage storage, NCacheJobQueue jobQueue, string jobId, string queue)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
            JobId = jobId ?? throw new ArgumentNullException(nameof(jobId));
            Queue = queue ?? throw new ArgumentNullException(nameof(queue));

            // Renew at 1/3 of InvisibilityTimeout so a couple of missed ticks (GC pause,
            // transient cache hiccup) don't cause a false reclaim of a still-live worker.
            var invisibility = _storage.Options.InvisibilityTimeout;
            var renewEvery = TimeSpan.FromTicks(invisibility.Ticks / 3);

            _heartbeatTimer = new Timer(_ => RenewProcessingEntry(), null, renewEvery, renewEvery);
        }

        public string JobId { get; }
        public string Queue { get; }

        public void RemoveFromQueue()
        {
            if (_removedFromQueue) return;
            StopHeartbeat();
            RemoveFromProcessingSet();
            _removedFromQueue = true;
        }

        public void Requeue()
        {
            if (_requeued) return;
            StopHeartbeat();
            _jobQueue.Requeue(Queue, JobId);
            RemoveFromProcessingSet();
            _requeued = true;
        }

        public void Dispose()
        {
            StopHeartbeat();
            if (!_removedFromQueue && !_requeued)
                Requeue();
        }

        private void StopHeartbeat()
        {
            lock (_stateLock)
            {
                if (_heartbeatStopped) return;
                _heartbeatStopped = true;
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
            }
        }

        // Pushes this job's FetchedAt forward so FetchedJobsWatcher doesn't reclaim it
        // while we're still legitimately processing. If the entry is already gone
        // (meaning we missed enough heartbeats that the watcher already reclaimed and
        // requeued it elsewhere), we do NOT recreate it — another server may already
        // own it, and re-inserting here would race with that server's own completion.
        // We just stop trying; whatever happens next is the double-execution case,
        // same as today, only now much rarer since heartbeats normally prevent it.
        private void RenewProcessingEntry()
        {
            lock (_stateLock)
            {
                if (_heartbeatStopped) return;
            }

            try
            {
                var cache = _storage.Cache;
                var processingKey = NCacheKeys.Processing(_storage.Prefix, _storage.CurrentServerId);

                var acquired = NCacheLockGuard.TryExecute(
                    cache,
                    processingKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var processing = cache.Get<Dictionary<string, NCacheProcessingEntry>>(processingKey);
                        if (processing == null || !processing.TryGetValue(JobId, out var entry))
                        {
                            // Already reclaimed elsewhere — stop heartbeating, nothing to renew.
                            Logger.Warn($"Heartbeat for job {JobId} found no processing entry — " +
                                        "it may already have been reclaimed as stale. Stopping heartbeat.");
                            StopHeartbeat();
                            return;
                        }

                        entry.FetchedAt = DateTime.UtcNow;
                        cache.Insert(processingKey, new CacheItem(processing));
                    });

                if (!acquired)
                {
                    Logger.Warn($"Could not acquire lock to renew heartbeat for job {JobId} this tick — will retry next interval.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Heartbeat renewal failed for job {JobId}: {ex.Message}");
            }
        }

        private void RemoveFromProcessingSet()
        {
            var cache = _storage.Cache;
            var processingKey = NCacheKeys.Processing(_storage.Prefix, _storage.CurrentServerId);
            var removed = NCacheLockGuard.TryExecute(
                cache,
                processingKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var processing = cache.Get<Dictionary<string, NCacheProcessingEntry>>(processingKey)
                                     ?? new Dictionary<string, NCacheProcessingEntry>();
                    processing.Remove(JobId);
                    cache.Insert(processingKey, new CacheItem(processing));
                });
            if (!removed)
            {
                Logger.Warn($"Could not clear job {JobId} from the processing set — it may be re-executed after InvisibilityTimeout.");
            }
        }
    }
}