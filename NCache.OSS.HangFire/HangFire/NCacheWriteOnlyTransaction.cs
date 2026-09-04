//NCacheWrieOnlyTransactions
using System;
using System.Collections.Generic;
using System.Linq;
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace NCache.OSS.Hangfire
{

    internal class NCacheWriteOnlyTransaction : JobStorageTransaction
    {
        private readonly NCacheStorage _storage;
        private readonly List<Action> _commands = new List<Action>();

        public NCacheWriteOnlyTransaction(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public override void Commit()
        {

            foreach (var action in _commands)
            {
                action();
            }

            _commands.Clear();
        }
        public override void ExpireJob(string jobId, TimeSpan expireIn)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));


            QueueCommand(() => SetKeyExpiration(NCacheKeys.Job(_storage.Prefix, jobId), expireIn));
            QueueCommand(() => SetKeyExpiration(NCacheKeys.JobParameters(_storage.Prefix, jobId), expireIn));
            QueueCommand(() => SetKeyExpiration(NCacheKeys.JobState(_storage.Prefix, jobId), expireIn));
            QueueCommand(() => SetKeyExpiration(NCacheKeys.JobHistory(_storage.Prefix, jobId), expireIn));

        }
        
        public override void SetJobState(string jobId, IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));

            QueueCommand(() =>
            {
                WriteJobState(jobId, state);
                AppendJobHistory(jobId, state);
                TrackTerminalStateIfNeeded(jobId, state);
            });
        }

        // Hangfire core doesn't maintain "succeeded"/"failed"/"deleted" lists for us — only
        // Succeeded and deleted gets a handler, and that just increments a counter (see
        // GlobalStateHandlers / SucceededState.Handler). SqlServer and Redis storages solve this
        // by tracking terminal states themselves at write time; we do the same here.

        private void TrackTerminalStateIfNeeded(string jobId, IState state)
        {
            string listName =
                string.Equals(state.Name, SucceededState.StateName, StringComparison.OrdinalIgnoreCase) ? "succeeded" :
                string.Equals(state.Name, FailedState.StateName, StringComparison.OrdinalIgnoreCase) ? "failed" :
                string.Equals(state.Name, DeletedState.StateName, StringComparison.OrdinalIgnoreCase) ? "deleted" :
                null;

            if (listName == null) return;

            var queue = _storage.PendingListPrepends.GetOrAdd(listName, _ => new System.Collections.Concurrent.ConcurrentQueue<string>());
            queue.Enqueue(jobId);

            if (listName == "failed")
            {
                ChangeCounter("stats:failed", 1, null);
            }
        }
        public override void AddJobState(string jobId, IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));

            QueueCommand(() =>
            {

                AppendJobHistory(jobId, state);
            });
        }

        public override void AddToQueue(string queue, string jobId)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            QueueCommand(() => new NCacheJobQueue(_storage).Enqueue(queue, jobId));
        }

        public override void IncrementCounter(string key)
        {
            QueueCommand(() =>
            {

                ChangeCounter(key, 1, null);
            });
        }
        public override void PersistJob(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            QueueCommand(() => ClearKeyExpiration(NCacheKeys.Job(_storage.Prefix, jobId)));
            QueueCommand(() => ClearKeyExpiration(NCacheKeys.JobParameters(_storage.Prefix, jobId)));
            QueueCommand(() => ClearKeyExpiration(NCacheKeys.JobState(_storage.Prefix, jobId)));
            QueueCommand(() => ClearKeyExpiration(NCacheKeys.JobHistory(_storage.Prefix, jobId)));
        }

        public override void IncrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(() =>
            {

                ChangeCounter(key, 1, expireIn);
            });
        }
        public override void DecrementCounter(string key)
        {
            QueueCommand(() =>
            {
                ChangeCounter(key, -1, null);
            });
        }

        public override void DecrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(() =>
            {
                ChangeCounter(key, -1, expireIn);
            });
        }


        public override void AddToSet(string key, string value) => AddToSet(key, value, 0.0);

        public override void AddToSet(string key, string value, double score)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var setKey = NCacheKeys.Set(_storage.Prefix, key);

                NCacheLockGuard.EnsureKeyExists(cache, setKey, () => new Dictionary<string, double>());

                NCacheLockGuard.Execute(
                    cache,
                    setKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var set = cache.Get<Dictionary<string, double>>(setKey)
                                  ?? new Dictionary<string, double>();

                        set[value] = score;

                        cache.Insert(setKey, new CacheItem(set));
                    });
            });
        }

        public override void RemoveFromSet(string key, string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var setKey = NCacheKeys.Set(_storage.Prefix, key);

                if (!cache.Contains(setKey))
                    return;

                NCacheLockGuard.Execute(
                    cache,
                    setKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var set = cache.Get<Dictionary<string, double>>(setKey);

                        set?.Remove(value);

                        cache.Insert(
                            setKey,
                            new CacheItem(set ?? new Dictionary<string, double>()));
                    });
            });
        }

        public override void InsertToList(string key, string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var listKey = NCacheKeys.List(_storage.Prefix, key);

                NCacheLockGuard.EnsureKeyExists(cache, listKey, () => new List<string>());

                NCacheLockGuard.Execute(
                    cache,
                    listKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var list = cache.Get<List<string>>(listKey)
                                   ?? new List<string>();

                        list.Insert(0, value);

                        cache.Insert(listKey, new CacheItem(list));
                    });
            });
        }

        public override void RemoveFromList(string key, string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var listKey = NCacheKeys.List(_storage.Prefix, key);

                if (!cache.Contains(listKey))
                    return;

                NCacheLockGuard.Execute(
                    cache,
                    listKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var list = cache.Get<List<string>>(listKey);

                        list?.RemoveAll(v => v == value);

                        cache.Insert(
                            listKey,
                            new CacheItem(list ?? new List<string>()));
                    });
            });
        }



        public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var listKey = NCacheKeys.List(_storage.Prefix, key);

                if (!cache.Contains(listKey))
                    return;

                NCacheLockGuard.Execute(
                    cache,
                    listKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var list = cache.Get<List<string>>(listKey)
                                   ?? new List<string>();

                        var trimmed = list
                            .Skip(keepStartingFrom)
                            .Take(Math.Max(0, keepEndingAt - keepStartingFrom + 1))
                            .ToList();

                        cache.Insert(listKey, new CacheItem(trimmed));
                    });
            });
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var hashKey = NCacheKeys.Hash(_storage.Prefix, key);

                NCacheLockGuard.EnsureKeyExists(
                    cache,
                    hashKey,
                    () => new Dictionary<string, string>());

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
                        {
                            hash[kvp.Key] = kvp.Value;
                        }

                        cache.Insert(hashKey, new CacheItem(hash));
                    });
            });
        }
         public override void RemoveHash(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueueCommand(() =>
            {
                var cache = _storage.Cache;
                var hashKey = NCacheKeys.Hash(_storage.Prefix, key);

                if (!cache.Contains(hashKey)) return;

                NCacheLockGuard.Execute(
                    cache,
                    hashKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        if (cache.Contains(hashKey)) cache.Remove(hashKey);
                    });
            });
        }
        private void WriteJobState(string jobId, IState state)
        {
            var cache = _storage.Cache;
            var stateKey = NCacheKeys.JobState(_storage.Prefix, jobId);

            NCacheLockGuard.EnsureKeyExists(cache, stateKey, () => new Dictionary<string, string>());

            NCacheLockGuard.Execute(
                cache,
                stateKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var entry = new Dictionary<string, string>(state.SerializeData())
                    {
                        ["State"] = state.Name,
                        ["Reason"] = state.Reason,
                        ["ChangedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow)
                    };

                    cache.Insert(stateKey, new CacheItem(entry));
                });
        }

        private void AppendJobHistory(string jobId, IState state)
        {
            var cache = _storage.Cache;
            var historyKey = NCacheKeys.JobHistory(_storage.Prefix, jobId);

            NCacheLockGuard.EnsureKeyExists(cache, historyKey, () => new List<Dictionary<string, string>>());

            NCacheLockGuard.Execute(
                cache,
                historyKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var history = cache.Get<List<Dictionary<string, string>>>(historyKey)
                                  ?? new List<Dictionary<string, string>>();

                    var entry = new Dictionary<string, string>(state.SerializeData())
                    {
                        ["State"] = state.Name,
                        ["Reason"] = state.Reason,
                        ["CreatedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow)
                    };

                    history.Insert(0, entry);
                    cache.Insert(historyKey, new CacheItem(history));
                });
        }


        private void ChangeCounter(string key, long delta, TimeSpan? expireIn)
        {
            var queue = _storage.PendingCounterDeltas.GetOrAdd(key,
                _ => new System.Collections.Concurrent.ConcurrentQueue<(long Delta, TimeSpan? ExpireIn)>());

            queue.Enqueue((delta, expireIn));

            RegisterCounterName(key);
        }
       
        
        private void RegisterCounterName(string name)
        {
            var cache = _storage.Cache;
            var registryKey = NCacheKeys.CounterRegistry(_storage.Prefix);

            if (cache.Contains(registryKey))
            {
                var existing = cache.Get<HashSet<string>>(registryKey);
                if (existing != null && existing.Contains(name))
                    return;
            }

            NCacheLockGuard.EnsureKeyExists(cache, registryKey, () => new HashSet<string>());

            NCacheLockGuard.TryExecute(
                cache,
                registryKey,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    var registry = cache.Get<HashSet<string>>(registryKey) ?? new HashSet<string>();

                    if (registry.Add(name))
                        cache.Insert(registryKey, new CacheItem(registry));
                });
        }

         
         
        private void SetKeyExpiration(string key, TimeSpan expireIn)
        {
            var cache = _storage.Cache;
            if (!cache.Contains(key)) return;

            NCacheLockGuard.Execute(
                cache,
                key,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    if (!cache.Contains(key)) return;
                    var value = cache.Get<object>(key);
                    cache.Insert(key, new CacheItem(value) { Expiration = new Expiration(ExpirationType.Absolute, expireIn) });
                });
        }

 
        private void ClearKeyExpiration(string key)
        {
            var cache = _storage.Cache;
            if (!cache.Contains(key)) return;

            NCacheLockGuard.Execute(
                cache,
                key,
                _storage.Options.DistributedLockTimeout,
                _storage.Options.LockAcquireMaxWait,
                () =>
                {
                    if (!cache.Contains(key)) return;
                    var value = cache.Get<object>(key);
                    cache.Insert(key, new CacheItem(value));
                });
        } 
        private void QueueCommand(Action action) => _commands.Add(action);
         
    }
}