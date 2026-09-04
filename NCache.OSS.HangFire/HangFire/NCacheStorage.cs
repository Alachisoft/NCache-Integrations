using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NCache.OSS.Hangfire
{
    /// <summary>
    /// Carries a job that was popped off a queue by a topic subscription callback,
    /// on its way to whichever worker thread is blocked in FetchNextJob waiting for it.
    /// </summary>
     

    public class NCacheStorage : JobStorage
    {
        private readonly NCacheStorageOptions _options;

        internal readonly ConcurrentDictionary<string, ConcurrentQueue<(long Delta, TimeSpan? ExpireIn)>> PendingCounterDeltas =
                    new ConcurrentDictionary<string, ConcurrentQueue<(long Delta, TimeSpan? ExpireIn)>>();
        internal string CurrentServerId { get; set; }
        // Tracks which queues we've already created a live topic subscription for,
        // so repeated FetchNextJob calls don't try to resubscribe every time.
        private readonly ConcurrentDictionary<string, byte> _subscribedQueues =
            new ConcurrentDictionary<string, byte>();

        private readonly ConcurrentDictionary<string, ITopic> _topics =
            new ConcurrentDictionary<string, ITopic>();

        private readonly object _subscriptionLock = new object();
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(NCacheStorage));


        internal readonly ConcurrentDictionary<string, ConcurrentQueue<string>> PendingListPrepends =
            new ConcurrentDictionary<string, ConcurrentQueue<string>>();
        /// <summary>
        /// Jobs land here the instant a topic subscription callback dequeues them.
        /// Worker threads block on JobAvailable.Take(...) in FetchNextJob instead of
        /// polling or waiting on a per-queue ManualResetEventSlim.
        /// </summary>
        internal BlockingCollection<string> JobAvailable { get; } =
            new BlockingCollection<string>();

        public ICache Cache { get; }
        public string Prefix => _options.Prefix;
        public NCacheStorageOptions Options => _options;

        public NCacheStorage(string cacheName)
            : this(cacheName, new NCacheStorageOptions())
        {
        }

        public NCacheStorage(string cacheName, NCacheStorageOptions options)
        {
            if (string.IsNullOrEmpty(cacheName))
                throw new ArgumentNullException(nameof(cacheName));

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.CacheName = cacheName;
            Cache = CacheManager.GetCache(cacheName);
        }

        public override IMonitoringApi GetMonitoringApi()
        {
            return new NCacheMonitoringApi(this);
        }

        public override IStorageConnection GetConnection()
        {
            return new NCacheConnection(this);
        }

        public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
        {
            yield return new ExpirationManager(this);
            yield return new FetchedJobsWatcher(this);
            yield return new CounterFlusher(this);
            yield return new TerminalListFlusher(this);
            yield return new QueueBacklogWatcher(this);
        }

        public override bool HasFeature(string featureId)
        {
            if (featureId == null) throw new ArgumentNullException(nameof(featureId));

            return featureId == JobStorageFeatures.ProcessesInsteadOfComponents
                   || base.HasFeature(featureId);
        }

        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Info("Using the following options for NCache job storage:");
            logger.Info($"    Cache name: {_options.CacheName}");
            logger.Info($"    Queue poll interval: {_options.QueuePollInterval}");
            logger.Info($"    Invisibility timeout: {_options.InvisibilityTimeout}");
            logger.Info($"    Pub/sub notifications: {(_options.EnablePubSubNotifications ? "enabled" : "disabled")}");
        }

        public override string ToString()
        {
            return $"NCache Job Storage: {_options.CacheName}";
        }

        /// <summary>
        /// Gets (or lazily creates) the non-durable Topic used to notify subscribers
        /// that a job was enqueued on <paramref name="queue"/>. Cached for the storage's
        /// lifetime, so publishers don't pay a lookup/create cost on every enqueue.
        /// </summary>
        internal ITopic GetOrCreateTopic(string queue)
        {
            var topicName = NCacheKeys.QueueTopic(_options.Prefix, queue);
            return _topics.GetOrAdd(topicName, name =>
                Cache.MessagingService.GetTopic(name) ?? Cache.MessagingService.CreateTopic(name));
        }

        /// <summary>
        /// Ensures a topic subscription exists for <paramref name="queue"/>. The subscription
        /// callback itself dequeues the job and pushes it into <see cref="JobAvailable"/> —
        /// callers don't need to do anything after this beyond blocking on JobAvailable.Take.
        ///
        /// Safe to call repeatedly; after the first successful subscribe it's just a
        /// dictionary lookup. If pub/sub is disabled, or subscribing fails (cache unreachable,
        /// feature unavailable, etc.), this silently does nothing — FetchNextJob's own
        /// startup dequeue pass and the FetchedJobsWatcher/orphan-recovery process remain
        /// the safety net for jobs that would otherwise sit unnoticed.
        /// </summary>
        internal void EnsureQueueSubscription(string queue)
        {
            if (!_options.EnablePubSubNotifications)
                return;

            if (_subscribedQueues.ContainsKey(queue))
                return;

            lock (_subscriptionLock)
            {
                if (_subscribedQueues.ContainsKey(queue))
                    return;

                try
                {
                    var topic = GetOrCreateTopic(queue);
                    topic.CreateSubscription((sender, args) => OnQueueMessage(queue));
                    _subscribedQueues[queue] = 0;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not subscribe to queue topic for queue '{queue}' — falling back to the startup dequeue pass only for this attempt: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Runs on an NCache client callback thread when a message is delivered for
        /// <paramref name="queue"/>. DeliveryOption.Any on the publish side guarantees only
        /// one subscriber cluster-wide receives this callback per message, so the dequeue
        /// here is inherently "one job, one worker" — no extra coordination needed before
        /// handing it off to whichever thread is blocked on JobAvailable.Take.
        /// </summary>
        private void OnQueueMessage(string queue)
        {
            try
            {
                JobAvailable.Add(queue);   // Just wake up a worker
            }
            catch (Exception ex)
            {
                Logger.Warn($"Queue notification error for '{queue}': {ex.Message}");
            }
        }
        internal void DrainBacklog(string queue, NCacheJobQueue jobQueue)
        {
            var length = jobQueue.GetTotalLength(queue);
            for (int i = 0; i < length; i++)
                JobAvailable.Add(queue);
        }
    }
}