using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Runtime.Exceptions;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace NCache.OSS.Hangfire
{


    [Serializable]
    internal class NCacheServerData
    {
        public int WorkerCount { get; set; }
        public string[] Queues { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime Heartbeat { get; set; }
    }

    internal class NCacheConnection : JobStorageConnection
    {
        private readonly NCacheStorage _storage;
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(NCacheConnection));

        public NCacheConnection(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public override IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new NCacheWriteOnlyTransaction(_storage);
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(resource))
                throw new ArgumentNullException(nameof(resource));

            var lockKey = NCacheKeys.Hash(_storage.Prefix, $"lock:{resource}");
            var cache = _storage.Cache;

            if (!cache.Contains(lockKey))
            {
                try
                {
                    cache.Add(lockKey, new CacheItem("lock"));
                }
                catch (OperationFailedException)
                {
                    // lost the race to a concurrent process/thread — fine, key exists now
                }
            }

            var gate = NCacheLockGuard.For(lockKey);

            var sw = Stopwatch.StartNew();
            if (!gate.Wait(timeout))
                throw new DistributedLockTimeoutException(resource);

            var remaining = timeout - sw.Elapsed;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            LockToken token;
            try
            {
                token = NCacheLockGuard.Acquire(
                    cache,
                    lockKey,
                    _storage.Options.DistributedLockTimeout,
                    remaining);
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.TimeoutException)
            {
                gate.Release();
                throw new DistributedLockTimeoutException(resource);
            }

            return new NCacheDistributedLock(cache, lockKey, token, gate);
        }
        public override string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var jobId = Guid.NewGuid().ToString();
            var invocationData = InvocationData.SerializeJob(job);

            var entry = new Dictionary<string, string>
            {
                ["Type"] = invocationData.Type,
                ["Method"] = invocationData.Method,
                ["ParameterTypes"] = invocationData.ParameterTypes,
                ["Arguments"] = invocationData.Arguments,
                ["CreatedAt"] = JobHelper.SerializeDateTime(createdAt)
            };

            var cache = _storage.Cache;
            var expiration = new Expiration(ExpirationType.Absolute, expireIn);

            var jobKey = NCacheKeys.Job(_storage.Prefix, jobId);
            cache.Insert(jobKey, new CacheItem(entry) { Expiration = expiration });

            var paramsKey = NCacheKeys.JobParameters(_storage.Prefix, jobId);
            cache.Insert(paramsKey, new CacheItem(new Dictionary<string, string>(parameters)) { Expiration = expiration });

            return jobId;
        }
        public override void SetJobParameter(string id, string name, string value)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            var cache = _storage.Cache;
            var jobKey = NCacheKeys.Job(_storage.Prefix, id);

            if (!cache.Contains(jobKey))
                throw new InvalidOperationException($"Job {id} does not exist — cannot set parameter '{name}'.");

            var paramsKey = NCacheKeys.JobParameters(_storage.Prefix, id);
            NCacheLockGuard.EnsureKeyExists(cache, paramsKey, () => new Dictionary<string, string>());

            NCacheLockGuard.Execute(
                cache,
                paramsKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var entry = cache.Get<Dictionary<string, string>>(paramsKey) ?? new Dictionary<string, string>();
                    entry[name] = value;
                    cache.Insert(paramsKey, new CacheItem(entry));
                });
        }


        public override string GetJobParameter(string id, string name)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            var paramsKey = NCacheKeys.JobParameters(_storage.Prefix, id);
            if (!_storage.Cache.Contains(paramsKey)) return null;

            var entry = _storage.Cache.Get<Dictionary<string, string>>(paramsKey);
            return entry != null && entry.TryGetValue(name, out var value) ? value : null;
        }

        public override JobData GetJobData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var jobKey = NCacheKeys.Job(_storage.Prefix, jobId);
            if (!_storage.Cache.Contains(jobKey)) return null;

            var entry = _storage.Cache.Get<Dictionary<string, string>>(jobKey);
            if (entry == null) return null;

            entry.TryGetValue("Type", out var type);
            entry.TryGetValue("Method", out var method);
            entry.TryGetValue("ParameterTypes", out var parameterTypes);
            entry.TryGetValue("Arguments", out var arguments);

            var invocationData = new InvocationData(type, method, parameterTypes, arguments);

            Job job = null;
            JobLoadException loadException = null;

            try
            {
                job = invocationData.DeserializeJob();
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }

            var stateData = GetStateData(jobId);

            return new JobData
            {
                Job = job,
                LoadException = loadException,
                CreatedAt = entry.TryGetValue("CreatedAt", out var createdAtRaw)
                    ? JobHelper.DeserializeDateTime(createdAtRaw)
                    : DateTime.MinValue,
                State = stateData?.Name
            };
        }

        public override StateData GetStateData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var stateKey = NCacheKeys.JobState(_storage.Prefix, jobId);
            if (!_storage.Cache.Contains(stateKey)) return null;

            var stateEntry = _storage.Cache.Get<Dictionary<string, string>>(stateKey);
            if (stateEntry == null || !stateEntry.TryGetValue("State", out var stateName)) return null;

            stateEntry.TryGetValue("Reason", out var reason);

            var data = new Dictionary<string, string>(stateEntry);
            data.Remove("State");
            data.Remove("Reason");
            data.Remove("ChangedAt");

            return new StateData { Name = stateName, Reason = reason, Data = data };
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (toScore < fromScore) throw new ArgumentException("'toScore' must be >= 'fromScore'.");

            var setKey = NCacheKeys.Set(_storage.Prefix, key);
            if (!_storage.Cache.Contains(setKey)) return null;

            var scores = _storage.Cache.Get<Dictionary<string, double>>(setKey);
            if (scores == null || scores.Count == 0) return null;

            string result = null;
            var lowest = double.MaxValue;

            foreach (var kvp in scores)
            {
                if (kvp.Value >= fromScore && kvp.Value <= toScore && kvp.Value < lowest)
                {
                    lowest = kvp.Value;
                    result = kvp.Key;
                }
            }

            return result;
        }
        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var setKey = NCacheKeys.Set(_storage.Prefix, key);
            if (!_storage.Cache.Contains(setKey)) return new HashSet<string>();

            var scores = _storage.Cache.Get<Dictionary<string, double>>(setKey);
            return scores != null ? new HashSet<string>(scores.Keys) : new HashSet<string>();
        }
        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            var hashKey = NCacheKeys.Hash(_storage.Prefix, key);
            var cache = _storage.Cache;

            NCacheLockGuard.EnsureKeyExists(cache, hashKey, () => new Dictionary<string, string>());

            NCacheLockGuard.Execute(
                cache,
                hashKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var hash = cache.Get<Dictionary<string, string>>(hashKey)
                              ?? new Dictionary<string, string>();

                    foreach (var kvp in keyValuePairs)
                        hash[kvp.Key] = kvp.Value;

                    cache.Insert(hashKey, new CacheItem(hash));
                });
        }
        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var hashKey = NCacheKeys.Hash(_storage.Prefix, key);
            return _storage.Cache.Contains(hashKey)
                ? _storage.Cache.Get<Dictionary<string, string>>(hashKey)
                : null;
        }
 
        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (context == null) throw new ArgumentNullException(nameof(context));

            _storage.CurrentServerId = serverId;

            var serversKey = NCacheKeys.Servers(_storage.Prefix);
            var cache = _storage.Cache;

            NCacheLockGuard.EnsureKeyExists(cache, serversKey, () => new Dictionary<string, NCacheServerData>());

            NCacheLockGuard.Execute(
                cache,
                serversKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey)
                                  ?? new Dictionary<string, NCacheServerData>();

                    servers[serverId] = new NCacheServerData
                    {
                        WorkerCount = context.WorkerCount,
                        Queues = context.Queues,
                        StartedAt = DateTime.UtcNow,
                        Heartbeat = DateTime.UtcNow
                    };

                    cache.Insert(serversKey, new CacheItem(servers));
                });

            if (context.Queues != null)
            {
                var jobQueue = new NCacheJobQueue(_storage);

                foreach (var queue in context.Queues)
                {
                    var headKey = NCacheKeys.QueueHead(_storage.Prefix, queue);
                    var tailKey = NCacheKeys.QueueTail(_storage.Prefix, queue);

                    if (!cache.Contains(headKey))
                    {
                        try { cache.Add(headKey, new CacheItem(0)); }
                        catch (OperationFailedException) { /* another server already warmed it */ }
                    }
                    if (!cache.Contains(tailKey))
                    {
                        try { cache.Add(tailKey, new CacheItem(0)); }
                        catch (OperationFailedException) { }
                    }

                    _storage.EnsureQueueSubscription(queue);

                    // One-time drain per server, per queue — catches any jobs already
                    // sitting in the queue at startup (or missed by a racing/failed
                    // pub/sub notification) without every worker thread independently
                    // sweeping TryDequeue on every FetchNextJob call.
                    _storage.DrainBacklog(queue, jobQueue);
                }
            }

            // Per-server processing key — pre-warmed here so MarkAsProcessing/
            // RemoveFromProcessingSet never need EnsureKeyExists on the hot path.
            var processingKey = NCacheKeys.Processing(_storage.Prefix, serverId);
            if (!cache.Contains(processingKey))
            {
                try { cache.Add(processingKey, new CacheItem(new Dictionary<string, NCacheProcessingEntry>())); }
                catch (OperationFailedException) { }
            }

            var counterRegistryKey = NCacheKeys.CounterRegistry(_storage.Prefix);
            if (!cache.Contains(counterRegistryKey))
            {
                try { cache.Add(counterRegistryKey, new CacheItem(new HashSet<string>())); }
                catch (OperationFailedException) { }
            }
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            var serversKey = NCacheKeys.Servers(_storage.Prefix);
            var cache = _storage.Cache;
            if (!cache.Contains(serversKey)) return;

            var removed = NCacheLockGuard.TryExecute(
                cache,
                serversKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);
                    servers?.Remove(serverId);
                    cache.Insert(serversKey, new CacheItem(servers ?? new Dictionary<string, NCacheServerData>()));
                });

            if (!removed)
            {
                Logger.Warn($"Could not acquire lock on servers registry to remove '{serverId}' — it will be cleaned up by RemoveTimedOutServers once its heartbeat goes stale.");
            }
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            var serversKey = NCacheKeys.Servers(_storage.Prefix);
            var cache = _storage.Cache;

            if (!cache.Contains(serversKey))
                return;

            NCacheLockGuard.TryExecute(
                cache,
                serversKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);

                    if (servers != null && servers.TryGetValue(serverId, out var data))
                    {
                        data.Heartbeat = DateTime.UtcNow;
                        cache.Insert(serversKey, new CacheItem(servers));
                    }
                });
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
                throw new ArgumentException("'timeOut' must be positive.", nameof(timeOut));

            var serversKey = NCacheKeys.Servers(_storage.Prefix);
            var cache = _storage.Cache;

            if (!cache.Contains(serversKey))
                return 0;

            var removed = 0;

            NCacheLockGuard.TryExecute(
                cache,
                serversKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var servers = cache.Get<Dictionary<string, NCacheServerData>>(serversKey);

                    if (servers == null)
                        return;

                    var cutoff = DateTime.UtcNow.Add(-timeOut);

                    var staleIds = servers
                        .Where(kvp => kvp.Value.Heartbeat < cutoff)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var id in staleIds)
                        servers.Remove(id);

                    if (staleIds.Count > 0)
                        cache.Insert(serversKey, new CacheItem(servers));

                    removed = staleIds.Count;
                });

            return removed;
        }
        
        private void MarkAsProcessing(string jobId, string queue)
        {
            var cache = _storage.Cache;
            var processingKey = NCacheKeys.Processing(_storage.Prefix, _storage.CurrentServerId);
             
            NCacheLockGuard.Execute(
                cache,
                processingKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var processing = cache.Get<Dictionary<string, NCacheProcessingEntry>>(processingKey)
                                     ?? new Dictionary<string, NCacheProcessingEntry>();

                    processing[jobId] = new NCacheProcessingEntry
                    {
                        Queue = queue,
                        FetchedAt = DateTime.UtcNow
                    };

                    cache.Insert(processingKey, new CacheItem(processing));
                });
        }


        private sealed class NCacheDistributedLock : IDisposable
        {
            private static readonly ILog Logger = LogProvider.GetLogger(typeof(NCacheDistributedLock));

            private readonly ICache _cache;
            private readonly string _key;
            private readonly LockToken _token;
            private readonly SemaphoreSlim _gate;
            private bool _disposed;

            public NCacheDistributedLock(ICache cache, string key, LockToken token, SemaphoreSlim gate)
            {
                _cache = cache;
                _key = key;
                _token = token;
                _gate = gate;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _cache.UnlockKey(_key, _token);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not unlock '{_key}' on dispose — lock may have already expired: {ex.Message}");
                }
                try
                {
                    _cache.Remove(_key);
                }
                catch
                {
                    // Swallow — key may already be gone.
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        public override DateTime GetUtcDateTime()
        {
            // Hangfire uses this to get a consistent server-side time.
            // NCache doesn't have a native TIME command, so we return local UTC.
            // For distributed scenarios, consider using an NCache atomic counter or 
            // a well-known key that you increment to generate a logical timestamp.
            return DateTime.UtcNow;
        }

        public override long GetSetCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var setKey = NCacheKeys.Set(_storage.Prefix, key);
            if (!_storage.Cache.Contains(setKey)) return 0;

            var scores = _storage.Cache.Get<Dictionary<string, double>>(setKey);
            return scores?.Count ?? 0;
        }

        public override bool GetSetContains(string key, string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var setKey = NCacheKeys.Set(_storage.Prefix, key);
            if (!_storage.Cache.Contains(setKey)) return false;

            var scores = _storage.Cache.Get<Dictionary<string, double>>(setKey);
            return scores != null && scores.ContainsKey(value);
        }
        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var setKey = NCacheKeys.Set(_storage.Prefix, key);
            if (!_storage.Cache.Contains(setKey)) return new List<string>();

            var scores = _storage.Cache.Get<Dictionary<string, double>>(setKey);
            if (scores == null || scores.Count == 0) return new List<string>();

            return scores
                .OrderBy(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => kvp.Key)
                .Skip(startingFrom)
                .Take(Math.Max(0, endingAt - startingFrom + 1))
                .ToList();
        }
        public override string GetValueFromHash(string key, string name)
        {
            var hashKey = NCacheKeys.Hash(_storage.Prefix, key);
            if (!_storage.Cache.Contains(hashKey)) return null;
            var hash = _storage.Cache.Get<Dictionary<string, string>>(hashKey);
            return hash != null && hash.TryGetValue(name, out var value) ? value : null;
        }

        public override long GetHashCount(string key)
        {
            var hashKey = NCacheKeys.Hash(_storage.Prefix, key);
            if (!_storage.Cache.Contains(hashKey)) return 0;
            return _storage.Cache.Get<Dictionary<string, string>>(hashKey)?.Count ?? 0;
        }

        public override long GetListCount(string key)
        {
            var listKey = NCacheKeys.List(_storage.Prefix, key);
            if (!_storage.Cache.Contains(listKey)) return 0;
            return _storage.Cache.Get<List<string>>(listKey)?.Count ?? 0;
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            var listKey = NCacheKeys.List(_storage.Prefix, key);
            if (!_storage.Cache.Contains(listKey)) return new List<string>();
            var list = _storage.Cache.Get<List<string>>(listKey) ?? new List<string>();
            return list.Skip(startingFrom).Take(Math.Max(0, endingAt - startingFrom + 1)).ToList();
        }

        public override List<string> GetAllItemsFromList(string key)
        {
            var listKey = NCacheKeys.List(_storage.Prefix, key);
            if (!_storage.Cache.Contains(listKey)) return new List<string>();
            return _storage.Cache.Get<List<string>>(listKey) ?? new List<string>();
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0) throw new ArgumentNullException(nameof(queues));

            var jobQueue = new NCacheJobQueue(_storage);

            foreach (var queue in queues)
                _storage.EnsureQueueSubscription(queue);

            if (!_storage.Options.EnablePubSubNotifications)
            {
                // Poll fallback — unchanged
                while (true)
                {
                    cancellationToken.WaitHandle.WaitOne(_storage.Options.QueuePollInterval);
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var queue in queues)
                    {
                        var jobId = jobQueue.TryDequeue(queue);
                        if (jobId != null)
                            return CompleteAndWrap(jobQueue, jobId, queue);
                    }
                }
            }

            // Pub/sub mode: block on signal, then race to dequeue
            while (true)
            {
                var queue = _storage.JobAvailable.Take(cancellationToken);

                if (Array.IndexOf(queues, queue) < 0)
                    continue; // not a queue we care about

                var jobId = jobQueue.TryDequeue(queue);
                if (jobId != null)
                    return CompleteAndWrap(jobQueue, jobId, queue);

                // Another worker got it first — go back to sleep
            }
        }
        private IFetchedJob CompleteAndWrap(NCacheJobQueue jobQueue, string jobId, string queue)
        {
            try
            {
                MarkAsProcessing(jobId, queue);
            }
            catch
            {
                jobQueue.Requeue(queue, jobId);
                throw;
            }

            return new NCacheFetchedJob(_storage, jobQueue, jobId, queue);
        }

        public override long GetCounter(string key) => NCacheCounters.GetValue(_storage, key);

    }
}

