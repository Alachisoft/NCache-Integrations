using Alachisoft.NCache.Client;
using Hangfire.Logging;
using Hangfire.Server;
using System;
using System.Collections.Generic;

namespace NCache.OSS.Hangfire
{
    internal class QueueBacklogWatcher : IBackgroundProcess
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(QueueBacklogWatcher));
        private readonly NCacheStorage _storage;

        public QueueBacklogWatcher(NCacheStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public void Execute(BackgroundProcessContext context)
        {
            while (!context.IsStopping)
            {
                CheckAndSignal();
                context.Wait(CheckInterval);
            }
        }

        internal void CheckAndSignal()
        {
            var cache = _storage.Cache;
            var registryKey = NCacheKeys.QueueRegistry(_storage.Prefix);

            if (!cache.Contains(registryKey))
                return;

            var queueNames = cache.Get<HashSet<string>>(registryKey);
            if (queueNames == null || queueNames.Count == 0)
                return;

            var jobQueue = new NCacheJobQueue(_storage);

            foreach (var queue in queueNames)
            {
                try
                {
                    var depth = jobQueue.GetTotalLength(queue); 

                    var signals = Math.Min(depth, 100);
                    for (int i = 0; i < signals; i++)
                        _storage.JobAvailable.Add(queue);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"BacklogWatcher error for '{queue}': {ex.Message}");
                }
            }
        }
    }
}