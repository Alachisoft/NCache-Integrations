using System;
using System.Collections.Generic;
using System.Linq;
using Alachisoft.NCache.Client;
using Hangfire.Logging;
using Hangfire.Server;

namespace NCache.OSS.Hangfire
{
    internal class FetchedJobsWatcher : IBackgroundProcess
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(FetchedJobsWatcher));
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
        private readonly NCacheStorage _storage;
        private readonly NCacheJobQueue _jobQueue;

        public FetchedJobsWatcher(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _jobQueue = new NCacheJobQueue(storage);
        }

        public void Execute(BackgroundProcessContext context)
        {
            RequeueTimedOutJobs();
            context.Wait(CheckInterval);
        }

        internal void RequeueTimedOutJobs()
        {
            foreach (var serverId in GetKnownServerIds())
            {
                var staleEntries = RequeueTimedOutJobsInProcessingSet(serverId);
                if (staleEntries == null) continue;

                foreach (var entry in staleEntries)
                {
                    _jobQueue.Requeue(entry.Value.Queue, entry.Key);
                }
            }
        }

        private List<string> GetKnownServerIds()
        {
            var cache = _storage.Cache;
            var serversKey = NCacheKeys.Servers(_storage.Prefix);

            if (!cache.Contains(serversKey))
                return new List<string>();

            var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);
            return servers != null ? servers.Keys.ToList() : new List<string>();
        }

        private List<KeyValuePair<string, NCacheProcessingEntry>> RequeueTimedOutJobsInProcessingSet(string serverId)
        {
            var cache = _storage.Cache;
            var processingKey = NCacheKeys.Processing(_storage.Prefix, serverId);

            if (!cache.Contains(processingKey))
                return null;
             
            List<KeyValuePair<string, NCacheProcessingEntry>> staleEntries = null;

            var acquired = NCacheLockGuard.TryExecute(
                cache,
                processingKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var processing = cache.Get<Dictionary<string, NCacheProcessingEntry>>(processingKey);
                    if (processing == null || processing.Count == 0)
                        return;

                    var cutoff = DateTime.UtcNow.Subtract(_storage.Options.InvisibilityTimeout);
                    staleEntries = processing
                        .Where(kvp => kvp.Value.FetchedAt < cutoff)
                        .ToList();

                    if (staleEntries.Count == 0)
                        return;

                    foreach (var entry in staleEntries)
                        processing.Remove(entry.Key);

                    cache.Insert(processingKey, new CacheItem(processing));
                });

            if (!acquired)
            {
                Logger.Warn($"Could not acquire lock on processing set '{processingKey}' — will retry next pass.");
            }

            return staleEntries;
        }
    }
}
