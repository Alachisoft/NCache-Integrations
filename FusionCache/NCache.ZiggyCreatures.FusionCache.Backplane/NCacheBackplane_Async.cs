using Alachisoft.NCache.Client;
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
        private async ValueTask EnsureConnectionAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (_cache is not null)
                return;

            await _cacheLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_cache is not null)
                    return;

                if (string.IsNullOrWhiteSpace(_cacheName))
                    throw new InvalidOperationException("CacheName must be specified");

                _cache = await Task.Run(() =>
                    GetCache(), token)
                    .ConfigureAwait(false);

                if (_cache is not null)
                {
                    var tmp = _connectHandlerAsync;
                    if (tmp is not null)
                    {
                        await tmp(new BackplaneConnectionInfo(false)).ConfigureAwait(false);
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
        public async ValueTask SubscribeAsync(BackplaneSubscriptionOptions subscriptionOptions)
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
            await EnsureConnectionAsync().ConfigureAwait(false);

            if (_topic is null)
                throw new NullReferenceException("The backplane topic is null");

            // SUBSCRIBE TO TOPIC
            _subscription = await Task.Run(() =>
                _topic.CreateSubscription(OnMessageReceived))
                .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async ValueTask UnsubscribeAsync()
        {
            await Task.Run(() =>
            {
                _incomingMessageHandler = null;
                _incomingMessageHandlerAsync = null;
                _subscription?.UnSubscribe();
                _subscription = null;
                _subscriptionOptions = null;

                Disconnect();
            }).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
        {
            // CONNECTION
            await EnsureConnectionAsync(token).ConfigureAwait(false);

            var ncacheMessage = GetNCacheMessageFromMessage(message, _logger, _subscriptionOptions);

            token.ThrowIfCancellationRequested();

            await Task.Run(() =>
                _topic!.Publish(ncacheMessage, Alachisoft.NCache.Runtime.Caching.DeliveryOption.All, false), token)
                .ConfigureAwait(false);
        }
    }
}
