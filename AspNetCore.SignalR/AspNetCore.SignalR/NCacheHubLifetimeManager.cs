using Alachisoft.NCache.AspNetCore.SignalR.Internal;
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Common.FeatureUsageData;
using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Runtime.Exceptions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Alachisoft.NCache.AspNetCore.SignalR
{
    public sealed class NCacheHubLifetimeManager<THub> : HubLifetimeManager<THub>, IDisposable where THub : Hub
    {
        private readonly HubConnectionStore _connections = new HubConnectionStore();
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1);

        private ConcurrentDictionary<string, HashSet<string>> _groups = new ConcurrentDictionary<string, HashSet<string>>();
        private ConcurrentDictionary<string, HashSet<string>> _userIds = new ConcurrentDictionary<string, HashSet<string>>();

        private ICache _cache;
        private ITopic _topic;
        private ITopicSubscription _topicSubscription;
        public event Action<Exception> CacheStopped;

        private readonly string CacheName;
        private readonly string EventKey;

        private readonly ILogger _logger;
        private SignalRConnectionOptions signalRConnectionOptions = null;
        public NCacheHubLifetimeManager(IOptions<NCacheConfiguration> provider, ILogger<NCacheHubLifetimeManager<THub>> logger)
        {
            _logger = logger;

            CacheName = provider.Value.CacheName;
            EventKey = provider.Value.EventKey;

            // Backward compatibility: ApplicationID should only be used when EventKey is not provided
            if (EventKey == null && provider.Value.ApplicationID != null)
                EventKey = provider.Value.ApplicationID;


            signalRConnectionOptions = provider.Value.ConnectionOptions;
            if (signalRConnectionOptions == null)
                signalRConnectionOptions = new SignalRConnectionOptions();

            NCacheLog.ConnectingToCache(_logger, CacheName);
            _ = EnsureNCacheServerConnection();
        }

        /// <summary>
        /// 1. Ensure that connection to NCache is established.
        /// 2. Add this new connections to the connections pool.
        /// 3. Maintain userId's list
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public override async Task OnConnectedAsync(HubConnectionContext connection)
        {
            // Ensure connection with NCache server
            await EnsureNCacheServerConnection().ConfigureAwait(false);

            //Add newly connected client to connections list
            _connections.Add(connection);

            //Add the connection to UserId list
            var userTask = AddToUserIds(connection);

            await Task.WhenAll(userTask).ConfigureAwait(false);
        }

        /// <summary>
        /// 1. Remove connection from connection's list.
        /// 2. Remove connection from groups if any.
        /// 3. Remove connection from userId's list.
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public override Task OnDisconnectedAsync(HubConnectionContext connection)
        {
            // Remove from connection's list
            _connections.Remove(connection);

            //Remove from groups if any
            var groupRemoveTask = RemoveFromGroup(connection);

            //Remove connection from userId's
            var userIDRemoveTask = RemoveFromUserIds(connection);

            Task.WhenAll(groupRemoveTask, userIDRemoveTask);

            return Task.CompletedTask;
        }
        public override Task SendAllAsync(string methodName, object[] args, CancellationToken cancellationToken = default(CancellationToken))
        {
            var payload = new NCacheInvocationMessageWrapper(new NCacheInvocationMessage(methodName, args), InvocationMessageType.NCacheInvocationMessage);

            return Publish(payload);
        }
        public override Task SendAllExceptAsync(string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithConnections(methodName, args, excludedConnectionIds, true),
                InvocationMessageType.NCacheInvocationMessageWithConnections);

            return Publish(payload);
        }
        public override Task SendConnectionAsync(string connectionId, string methodName, object[] args, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (connectionId == null)
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            // Avoiding sending the message through hub if the connections are present locally. This is done due to following reasons.
            // We require sticky sessions
            // Performance improvement as it will skip serialization and deseiralization of the message.
            var connection = _connections[connectionId];
            if (connection != null)
            {
                return connection.WriteAsync(new InvocationMessage(methodName, args)).AsTask();
            }

            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithConnections(methodName, args, new List<string>() { connectionId }, false),
                InvocationMessageType.NCacheInvocationMessageWithConnections);

            return Publish(payload);
        }
        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object[] args, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithConnections(methodName, args, connectionIds, false),
                InvocationMessageType.NCacheInvocationMessageWithConnections);

            return Publish(payload);
        }
        public override Task SendGroupAsync(string groupName, string methodName, object[] args, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithGroups(methodName, args, new List<string> { groupName }),
                InvocationMessageType.NCacheInvocationMessageWithGroups);

            return Publish(payload);
        }
        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object[] args, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithGroups(methodName, args, groupNames),
                InvocationMessageType.NCacheInvocationMessageWithGroups);

            return Publish(payload);
        }
        public override Task SendGroupExceptAsync(string groupName, string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithGroupAndConnections(methodName, args, groupName, excludedConnectionIds),
                InvocationMessageType.NCacheInvocationMessageWithGroupAndConnections);

            return Publish(payload);

        }
        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(connectionId, null))
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            if (ReferenceEquals(groupName, null))
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            var connection = _connections[connectionId]; // Get connection object from connections list.

            if (connection != null)
            {
                _groups.TryAdd(groupName, new HashSet<string>());
                _groups[groupName].Add(connectionId); // Add the new connectionId in the already existing group
            }

            return Task.CompletedTask;
        }
        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(connectionId, null))
            {
                throw new ArgumentNullException(nameof(connectionId));
            }

            if (ReferenceEquals(groupName, null))
            {
                throw new ArgumentNullException(nameof(groupName));
            }

            return Task.Run(() =>
            {
                if (_groups.ContainsKey(groupName)) //Check if group exists
                {
                    var conList = _groups[groupName];

                    conList.Remove(connectionId); //Remove connectionId from group if exists

                    if (conList.Count == 0) //Remove the group from dictionary if it has no connections left in it.
                    {
                        _groups.TryRemove(groupName, out var x);
                    }
                }
            });
        }
        public override Task SendUserAsync(string userId, string methodName, object[] args, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithUserIds(methodName, args, new List<string>() { userId }),
                InvocationMessageType.NCacheInvocationMessageWithUserIds);

            return Publish(payload);

        }
        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object[] args, CancellationToken cancellationToken = default)
        {
            var payload = new NCacheInvocationMessageWrapper(
                new NCacheInvocationMessageWithUserIds(methodName, args, userIds),
                InvocationMessageType.NCacheInvocationMessageWithUserIds);

            return Publish(payload);
        }

        #region Private Methods
        private async Task EnsureNCacheServerConnection()
        {
            if (_cache == null)
            {
                await _connectionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_cache == null)
                    {
                        try
                        {
                            CacheConnectionOptions cacheConnectionOptions = signalRConnectionOptions.CacheConnectionOptions;

                            if (string.IsNullOrEmpty(cacheConnectionOptions.AppName))
                                cacheConnectionOptions.AppName = FeatureUsageCollector.FeatureTag + FeatureEnum.aspnetcore_signalr;

                            _cache = CacheManager.GetCache(CacheName, cacheConnectionOptions: cacheConnectionOptions);

                            NCacheLog.Connected(_logger);

                            //Subscribe to the topic
                            Subscribe();
                        }
                        catch (Exception ex)
                        {
                            NCacheLog.ConnectionFailed(_logger, ex);

                            if (_cache != null)
                            {
                                _cache.Dispose();
                                _cache = null;
                            }
                        }
                    }
                }

                finally
                {
                    _connectionLock.Release();
                }
            }
        }
        private void Subscribe()
        {
            NCacheLog.Subscribing(_logger, EventKey);

            // Gets the topic.
            _topic = _cache.MessagingService.GetTopic(EventKey);

            // Creates the topic if it doesn't exist.
            if (_topic == null)
                _topic = _cache.MessagingService.CreateTopic(EventKey);

            // Subscribes to the topic.
            if (_topicSubscription == null)
                _topicSubscription = _topic.CreateSubscription(messageReceivedCallback);
        }
        private Task Publish(object payload)
        {
            if (ReferenceEquals(_cache, null))
            {
                throw new InvalidOperationException(ErrorMessage.CacheNotInitialized);
            }

            if (ReferenceEquals(_topic, null))
            {
                throw new InvalidOperationException(ErrorMessage.SubscriptionNotExisting);
            }

            NCacheLog.PublishingMessage(_logger, EventKey);

            return Task.Run(() =>
            {
                bool retrySendingMessage = false;
                int retryCount = 3;
                do
                {
                    try
                {
                    _topic.Publish(new Message(payload), DeliveryOption.All, false);
                        retrySendingMessage = false;
                    }
                    catch (OperationFailedException ex)
                    {
                        retryCount--;
                        retrySendingMessage = true;
                        if (ex.Message == $"Topic '{EventKey}' does not exists.")
                            Subscribe();
                        else
                        {
                            retrySendingMessage = false;
                            NCacheLog.FailedWritingMessage(_logger, ex);
                        }
                    }
                } while (retryCount >= 1 && retrySendingMessage);

            });
        }
        private async Task AddToUserIds(HubConnectionContext connection)
        {
            await Task.Run(() =>
            {
                var userIdentifier = connection.UserIdentifier;

                if (userIdentifier == null)
                    return;

                _userIds.TryAdd(userIdentifier, new HashSet<string>());
                _userIds[userIdentifier].Add(connection.ConnectionId);

            }).ConfigureAwait(false);
        }
        private Task RemoveFromUserIds(HubConnectionContext connection)
        {
            return Task.Run(() =>
            {
                var userIdentifier = connection.UserIdentifier;

                if (userIdentifier == null)
                    return;

                if (_userIds.ContainsKey(userIdentifier))
                {
                    var conList = _userIds[userIdentifier]; //Get connection list against userId if it exists.

                    conList.Remove(connection.ConnectionId);

                    if (conList.Count == 0) //Remove the UserId if no connection exists against it.
                    {
                        _userIds.TryRemove(userIdentifier, out var x);
                    }

                }
            });
        }
        private Task RemoveFromGroup(HubConnectionContext connection)
        {
            return Task.Run(() =>
             {
                 foreach (var group in _groups) //Iterate through all groups
                 {
                     if (group.Value.Contains(connection.ConnectionId))
                     {
                         group.Value.Remove(connection.ConnectionId); //Remove from the connection list if exists. 
                     }

                     if (group.Value.Count == 0) //Remove the group from dictionary if it has no connections left in it.
                     {
                         _groups.TryRemove(group.Key, out var x);
                     }
                 }
             });
        }
        private void SendToAllConnections(NCacheInvocationMessage nCacheInvocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(nCacheInvocationMessage.Target, nCacheInvocationMessage.Arguments));

            var tasks = new List<Task>(_connections.Count);

            foreach (var connection in _connections)
            {
                tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
            }

            Task.WhenAll(tasks);
        }
        private void SendToSpecificConnections(NCacheInvocationMessageWithConnections invocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(invocationMessage.Target, invocationMessage.Arguments));

            var tasks = new List<Task>(invocationMessage.ConnectionIds.Count);

            foreach (var connectionId in invocationMessage.ConnectionIds)
            {
                var connection = _connections[connectionId];

                if (connection != null)
                {
                    tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
                }
            }

            Task.WhenAll(tasks);
        }
        private void SendToConnectionsExcept(NCacheInvocationMessageWithConnections invocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(invocationMessage.Target, invocationMessage.Arguments));

            var tasks = new List<Task>(_connections.Count);

            foreach (var connection in _connections)
            {
                if (!invocationMessage.ConnectionIds.Contains(connection.ConnectionId))
                {
                    tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
                }
            }

            Task.WhenAll(tasks);
        }
        private void SendToGroups(NCacheInvocationMessageWithGroups invocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(invocationMessage.Target, invocationMessage.Arguments));

            foreach (var group in invocationMessage.GroupNames)
            {
                if (_groups.ContainsKey(group)) //Check if group exists 
                {
                    var connIdList = _groups[group]; // Get connectionIds against the group

                    var tasks = new List<Task>(connIdList.Count);

                    foreach (var connectionId in connIdList) //Iterate through all connectionIds
                    {
                        var connection = _connections[connectionId]; //Fetching the HubConnectionContext against the connectionId

                        if (connection != null)
                        {
                            tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
                        }
                    }
                    Task.WhenAll(tasks);
                }
            }
        }
        private void SendToGroupExcept(NCacheInvocationMessageWithGroupAndConnections invocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(invocationMessage.Target, invocationMessage.Arguments));

            if (_groups.ContainsKey(invocationMessage.GroupName)) //Check if group exists 
            {
                var connIdList = _groups[invocationMessage.GroupName]; //Fetch connection list against it

                var tasks = new List<Task>(connIdList.Count);

                foreach (var connectionId in connIdList)
                {
                    var connection = _connections[connectionId]; //Fetching the HubConnectionContext against the connectionId

                    if (connection != null)
                    {
                        if (!invocationMessage.ExcludedConnectionIds.Contains(connection.ConnectionId)) //For excluding a connection from exclusion list
                        {
                            tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
                        }
                    }
                }
                Task.WhenAll(tasks);
            }
        }
        private void SendToUserIds(NCacheInvocationMessageWithUserIds invocationMessage)
        {
            var serializedHubMessage = new SerializedHubMessage(new InvocationMessage(invocationMessage.Target, invocationMessage.Arguments));

            foreach (var userId in invocationMessage.UserIds)
            {
                if (_userIds.ContainsKey(userId)) //Checking if userId exists locally
                {
                    var conIdList = _userIds[userId]; //Fetching the connectionIds against the userId

                    var tasks = new List<Task>(conIdList.Count);

                    foreach (var con in conIdList)
                    {
                        var connection = _connections[con]; //Fetching the HubConnectionContext against the connectionId

                        if (connection != null)
                        {
                            tasks.Add(connection.WriteAsync(serializedHubMessage.Message).AsTask());
                        }
                    }
                    Task.WhenAll(tasks);
                }
                return;
            }
        }

        #endregion

        #region CallBacks
        private void messageReceivedCallback(object sender, MessageEventArgs args)
        {
            NCacheLog.MessageReceivedFromCache(_logger, EventKey);
            try
            {
                var invocationMessage = (NCacheInvocationMessageWrapper)args.Message.Payload;

                switch (invocationMessage.InvocationMessageType)
                {
                    case InvocationMessageType.NCacheInvocationMessage:
                        SendToAllConnections(invocationMessage.NCacheInvocationMessage as NCacheInvocationMessage);
                        break;

                    case InvocationMessageType.NCacheInvocationMessageWithConnections:
                        var message = invocationMessage.NCacheInvocationMessage as NCacheInvocationMessageWithConnections;

                        if (message.Exclude)
                            SendToConnectionsExcept(message);
                        else
                            SendToSpecificConnections(message);

                        break;

                    case InvocationMessageType.NCacheInvocationMessageWithGroupAndConnections:
                        SendToGroupExcept(invocationMessage.NCacheInvocationMessage as NCacheInvocationMessageWithGroupAndConnections);
                        break;

                    case InvocationMessageType.NCacheInvocationMessageWithGroups:
                        SendToGroups(invocationMessage.NCacheInvocationMessage as NCacheInvocationMessageWithGroups);
                        break;

                    case InvocationMessageType.NCacheInvocationMessageWithUserIds:
                        SendToUserIds(invocationMessage.NCacheInvocationMessage as NCacheInvocationMessageWithUserIds);
                        break;
                }
            }
            catch(Exception ex)
            {
                NCacheLog.FailedWritingMessage(_logger, ex);
            }
        }

        #endregion
        public void Dispose()
        {
            _connectionLock.Dispose();
            _topic.Dispose();
            _cache.Dispose();
        }
    }
}
