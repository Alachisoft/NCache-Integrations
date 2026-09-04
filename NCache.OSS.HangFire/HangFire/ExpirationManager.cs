using Alachisoft.NCache.Client;
using Hangfire.Logging;
using Hangfire.Server;
using System;
using System.Collections.Generic;

namespace NCache.OSS.Hangfire
{
    internal class ExpirationManager : IBackgroundProcess
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ExpirationManager));
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        private readonly NCacheStorage _storage;

        public ExpirationManager(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public void Execute(BackgroundProcessContext context)
        {
            while (!context.IsStopping)
            {
                try
                {
                    TrimList("succeeded", _storage.Options.SucceededListSize);
                    TrimList("failed", _storage.Options.FailedListSize);
                    TrimList("deleted", _storage.Options.DeletedListSize);
                }
                catch (Exception ex)
                {
                    Logger.ErrorException("Error while trimming terminal-state lists.", ex);
                }

                context.Wait(CheckInterval);
            }
        }

        internal void TrimList(string listName, int maxSize)
        {
            var cache = _storage.Cache;
            var listKey = NCacheKeys.List(_storage.Prefix, listName);

            if (!cache.Contains(listKey))
                return;

            if (!NCacheLockGuard.TryExecute(
                    cache,
                    listKey,
                    _storage.Options.DistributedLockTimeout,
                    _storage.Options.LockAcquireMaxWait,
                    () =>
                    {
                        var list = cache.Get<List<string>>(listKey);

                        if (list == null || list.Count <= maxSize)
                            return;

                        var trimmed = list.GetRange(0, maxSize);
                        cache.Insert(listKey, new CacheItem(trimmed));

                        Logger.Debug(
                            $"Trimmed '{listName}' list from {list.Count} to {trimmed.Count} entries.");
                    }))
            {
                Logger.Warn(
                    $"Could not acquire lock on list '{listName}' to trim it — will retry next pass.");
            }
        }
    }
}