using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NCache.ZiggyCreatures.FusionCache.Backplane.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace NCache.ZiggyCreatures.FusionCache.Backplane
{
    /// <summary>
    /// An NCache based implementation of a FusionCache backplane.
    /// </summary>
    public partial class NCacheBackplane
        : IFusionCacheBackplane
    {
        private readonly string _cacheName;
        private BackplaneSubscriptionOptions? _subscriptionOptions;
        private readonly ILogger? _logger;

        private readonly SemaphoreSlim _cacheLock;
        private ICache? _cache;
        private NCacheBackplaneOptions? _cacheoptions;

        private string? _topicName = null;
        private ITopic? _topic;
        private ITopicSubscription? _subscription;

        private Action<BackplaneConnectionInfo>? _connectHandler;
        private Action<BackplaneMessage>? _incomingMessageHandler;
        private Func<BackplaneConnectionInfo, ValueTask>? _connectHandlerAsync;
        private Func<BackplaneMessage, ValueTask>? _incomingMessageHandlerAsync;
        /// <summary>
        /// Initializes a new instance of the NCacheBackplane class.
        /// </summary>
        /// <param name="optionsAccessor">The set of options to use with this instance of the backplane.</param>
        /// <param name="logger">The <see cref="ILogger{TCategoryName}"/> instance to use. If null, logging will be completely disabled.</param>
        public NCacheBackplane(string cacheName, NCacheBackplaneOptions? options = null, ILogger<NCacheBackplane>? logger = null)
        {
            if (String.IsNullOrEmpty(cacheName))
                throw new ArgumentNullException(nameof(cacheName));

            // OPTIONS
            _cacheName = cacheName;

            _cacheoptions = options;
            // LOGGING
            if (logger is NullLogger<NCacheBackplane>)
            {
                // IGNORE NULL LOGGER (FOR BETTER PERF)
                _logger = null;
            }
            else
            {
                _logger = logger;
            }

            _cacheLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        }

       
        private ICache GetCache()
        {
            CacheConnectionOptions cacheOptions = null;

            if(_cacheoptions != null)
            {
                cacheOptions = _cacheoptions.GetCacheConnectionOptions();
            }

            ICache cache = CacheManager.GetCache(_cacheName, cacheOptions);

            return cache;
        }

        private void EnsureTopic()
        {
            if (_topic is null && _cache is not null && !string.IsNullOrWhiteSpace(_topicName))
            {
                _topic = _cache.MessagingService.GetTopic(_topicName);
                if (_topic is null)
                {
                    _topic = _cache.MessagingService.CreateTopic(_topicName);
                }
            }
        }

        private void Disconnect()
        {
            _connectHandler = null;
            _connectHandlerAsync = null;

            if (_cache is null)
                return;

            try
            {
                _subscription?.UnSubscribe();
                _subscription = null;
                _topic = null;
                _cache.Dispose();
            }
            catch (Exception exc)
            {
                if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error) ?? false)
                    _logger.Log(Microsoft.Extensions.Logging.LogLevel.Error, exc, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] An error occurred while disconnecting from NCache {CacheName}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, _cacheName);
            }

            _cache = null;
        }

        private static BackplaneMessage? GetMessageFromNCacheMessage(IMessage message, ILogger? logger, BackplaneSubscriptionOptions? subscriptionOptions)
        {
            try
            {
                if (message.Payload is string)
                {
                    return JsonConvert.DeserializeObject<BackplaneMessage>((string)message.Payload);
                }
            }
            catch (Exception exc)
            {
                if (logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning) ?? false)
                    logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, exc, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] an error occurred while converting an NCache Message into a BackplaneMessage", subscriptionOptions?.CacheName, subscriptionOptions?.CacheInstanceId);
            }

            return null;
        }

        private static Message GetNCacheMessageFromMessage(BackplaneMessage message, ILogger? logger, BackplaneSubscriptionOptions? subscriptionOptions)
        {
            try
            {
                var jsonMessage = JsonConvert.SerializeObject(message);
                return new Message(jsonMessage);
            }
            catch (Exception exc)
            {
                if (logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning) ?? false)
                    logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, exc, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] an error occurred while converting a BackplaneMessage into an NCache Message", subscriptionOptions?.CacheName, subscriptionOptions?.CacheInstanceId);

                throw;
            }
        }

        internal async ValueTask OnMessageAsync(BackplaneMessage message)
        {
            var tmp = _incomingMessageHandlerAsync;
            if (tmp is null)
            {
                if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) ?? false)
                    _logger.Log(Microsoft.Extensions.Logging.LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] incoming message handler was null", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId);
                return;
            }

            await tmp(message).ConfigureAwait(false);
        }

        private void OnMessageReceived(object sender, MessageEventArgs args)
        {
            var message = GetMessageFromNCacheMessage(args.Message, _logger, _subscriptionOptions);
            if (message is null)
                return;

            _ = Task.Run(async () =>
            {
                await OnMessageAsync(message).ConfigureAwait(false);
            });
        }
    }
}
