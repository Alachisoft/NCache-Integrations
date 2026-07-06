using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace NCache.ZiggyCreatures.FusionCache.Backplane
{
    public partial class NCacheBackplane
    {
        private void EnsureConnection(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (_cache is not null)
                return;

            _cacheLock.Wait(token);
            try
            {
                if (_cache is not null)
                    return;

                if (string.IsNullOrWhiteSpace(_cacheName))
                    throw new InvalidOperationException("CacheName must be specified");

                _cache = GetCache();

                if (_cache is not null)
                {
                    var tmp = _connectHandlerAsync;
                    if (tmp is not null)
                    {
                        tmp(new BackplaneConnectionInfo(false));
                    }
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            if (_cache is null)
                throw new NullReferenceException("A connection to NCache is not available");

            EnsureTopic();
        }

        /// <inheritdoc/>
        public void Subscribe(BackplaneSubscriptionOptions subscriptionOptions)
        {
            if (subscriptionOptions is null)
                throw new ArgumentNullException(nameof(subscriptionOptions));

            if (subscriptionOptions.ChannelName is null)
                throw new NullReferenceException("The BackplaneSubscriptionOptions.ChannelName cannot be null");

            if (subscriptionOptions.IncomingMessageHandler is null)
                throw new NullReferenceException("The BackplaneSubscriptionOptions.IncomingMessageHandler cannot be null");

            if (subscriptionOptions.ConnectHandler is null)
                throw new NullReferenceException("The BackplaneSubscriptionOptions.ConnectHandler cannot be null");

            if (subscriptionOptions.IncomingMessageHandlerAsync is null)
                throw new NullReferenceException("The BackplaneSubscriptionOptions.IncomingMessageHandlerAsync cannot be null");

            if (subscriptionOptions.ConnectHandlerAsync is null)
                throw new NullReferenceException("The BackplaneSubscriptionOptions.ConnectHandlerAsync cannot be null");

            _subscriptionOptions = subscriptionOptions;

            _topicName = _subscriptionOptions.ChannelName;
            if (_topicName is null)
                throw new NullReferenceException("The backplane topic name is null");

            _incomingMessageHandler = _subscriptionOptions.IncomingMessageHandler;
            _connectHandler = _subscriptionOptions.ConnectHandler;
            _incomingMessageHandlerAsync = _subscriptionOptions.IncomingMessageHandlerAsync;
            _connectHandlerAsync = _subscriptionOptions.ConnectHandlerAsync;

            // CONNECTION
            EnsureConnection();

            if (_topic is null)
                throw new NullReferenceException("The backplane topic is null");

            // SUBSCRIBE TO TOPIC
            _subscription = _topic.CreateSubscription(OnMessageReceived);
        }

        /// <inheritdoc/>
        public void Unsubscribe()
        {
            _ = Task.Run(() =>
            {
                _incomingMessageHandler = null;
                _incomingMessageHandlerAsync = null;
                _subscription?.UnSubscribe();
                _subscription = null;
                _subscriptionOptions = null;

                Disconnect();
            });
        }

        /// <inheritdoc/>
        public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
        {
            // CONNECTION
            EnsureConnection(token);

            var ncacheMessage = GetNCacheMessageFromMessage(message, _logger, _subscriptionOptions);

            token.ThrowIfCancellationRequested();

            _topic!.Publish(ncacheMessage, DeliveryOption.All, false);
        }
    }
}
