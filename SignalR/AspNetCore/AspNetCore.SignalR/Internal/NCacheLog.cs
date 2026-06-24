using System;
using Microsoft.Extensions.Logging;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{  
    internal static class NCacheLog
    {
        private static readonly Action<ILogger, string, Exception> _connectingToCache =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "ConnectingToCache"), "Connecting to the cache: {CacheName}");

        private static readonly Action<ILogger, Exception> _connected =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, "Connected"), "Connected to Cache.");

        private static readonly Action<ILogger, string, Exception> _subscribing =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Subscribing"), "Subscribing to the event key: {EventKey}.");

        private static readonly Action<ILogger, string, Exception> _messageReceivedFromCache =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(4, "MessageReceivedFromCache"), "Received message against the event key: {EventKey}.");

        private static readonly Action<ILogger, string, Exception> _publishingMessage =
            LoggerMessage.Define<string>(LogLevel.Trace, new EventId(5, "PublishingMessage"), "Publishing message against the event key: {EventKey}.");

        private static readonly Action<ILogger, string, Exception> _unsubscribe =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(6, "Unsubscribe"), "Unsubscribing from channel: {Channel}.");

        private static readonly Action<ILogger, Exception> _notConnected =
            LoggerMessage.Define(LogLevel.Error, new EventId(7, "Connected"), "Not connected to cache.");

        private static readonly Action<ILogger, Exception> _connectionRestored =
            LoggerMessage.Define(LogLevel.Information, new EventId(8, "ConnectionRestored"), "Connection to cache restored.");

        private static readonly Action<ILogger, Exception> _connectionFailed =
            LoggerMessage.Define(LogLevel.Error, new EventId(9, "ConnectionFailed"), "Connection to cache failed.");

        private static readonly Action<ILogger, Exception> _failedWritingMessage =
            LoggerMessage.Define(LogLevel.Warning, new EventId(10, "FailedWritingMessage"), "Failed writing message.");

        public static void ConnectingToCache(ILogger logger, string cacheName)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                    _connectingToCache(logger, cacheName, null);
            }
        }

        public static void Connected(ILogger logger)
        {
            _connected(logger, null);
        }

        public static void Subscribing(ILogger logger, string eventKey)
        {
            _subscribing(logger, eventKey, null);
        }

        public static void MessageReceivedFromCache(ILogger logger, string eventKey)
        {
            _messageReceivedFromCache(logger, eventKey, null);
        }

        public static void PublishingMessage(ILogger logger, string eventKey)
        {
            _publishingMessage(logger, eventKey, null);
        }

        public static void Unsubscribe(ILogger logger, string eventKey)
        {
            _unsubscribe(logger, eventKey, null);
        }

        public static void NotConnected(ILogger logger)
        {
            _notConnected(logger, null);
        }

        public static void ConnectionRestored(ILogger logger)
        {
            _connectionRestored(logger, null);
        }

        public static void ConnectionFailed(ILogger logger, Exception exception)
        {
            _connectionFailed(logger, exception);
        }

        public static void FailedWritingMessage(ILogger logger, Exception exception)
        {
            _failedWritingMessage(logger, exception);
        }

    }
}
