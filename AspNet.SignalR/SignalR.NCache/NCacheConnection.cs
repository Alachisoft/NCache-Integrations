using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Configuration;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public class NCacheProvider : ICacheProvider
    {
        private ICache _cache = null;
        private TraceSource _trace;
        private Action<int, NCacheMessage> messageHandler = null;
        private String eventKey = null;
        private int counter = 0;
        private ITopic _topic;
        private ITopicSubscription _topicSubscription;

        public async Task ConnectAsync(string cacheName, TraceSource trace)
        {
            try
            {
                CacheConnectionOptions cacheConnectionOptions = WebConfigurationManager.GetSection("ConnectionOptions") as CacheConnectionOptions;
                _cache = CacheManager.GetCache(cacheName, cacheConnectionOptions: cacheConnectionOptions);
            }
            catch (Exception ex)
            {
                if (_cache != null)
                {
                    _cache.Dispose();
                    _cache = null;
                }
                throw new InvalidOperationException("failed to initialize cache");
            }

            _trace = trace;
        }

        public async Task SubscribeAsync(string _eventKey, Action<int, NCacheMessage> OnMessage)
        {
            _trace.TraceInformation("subscribing to key: " + _eventKey);

            if (_cache != null)
            {
                // Gets the topic.
                _topic = _cache.MessagingService.GetTopic(_eventKey);

                // Creates the topic if it doesn't exist.
                if (_topic == null)
                    _topic = _cache.MessagingService.CreateTopic(_eventKey);

                // Subscribes to the topic.
                _topicSubscription = _topic.CreateSubscription(messageReceivedCallback);

                this.eventKey = _eventKey;
                this.messageHandler = OnMessage;
            }
        }

        private void messageReceivedCallback(object sender, MessageEventArgs args)
        {
            //Raise event 
            ITopicSubscription topicSubscription = (ITopicSubscription)sender;
            String eventKey = topicSubscription.Topic.Name as String;
            if (eventKey.Equals(this.eventKey) && this.messageHandler != null)
            {
                messageHandler(0, NCacheMessage.FromBytes(args.Message.Payload as byte[], this._trace));
            }
        }

        public void Close()
        {
            _trace.TraceInformation("Closing " + eventKey);

            if (_cache != null)
            {
                _topicSubscription.UnSubscribe();

                _cache.Dispose();
            }
        }
        public void Dispose()
        {
            if (_cache != null)
            {
                this.eventKey = string.Empty;
                this.messageHandler = null;

                _topicSubscription.UnSubscribe();
                _cache.Dispose();
            }
        }

        public Task PublishAsync(string key, byte[] messageArguments)
        {
            if (_cache == null)
            {
                throw new InvalidOperationException("cache has not been initialized");
            }

            // Create messaging topic.
            _topic = _cache.MessagingService.CreateTopic(key);

            return Task.Run(() => { try { _topic.Publish(new Message(messageArguments), DeliveryOption.All, true); } catch (Exception e) { } });
        }
               
        public event Action<Exception> CacheStopped;        
        
        private void OnCustomEvent(object notifId, object data) 
        {
            String eventKey = notifId as String;
            if(eventKey.Equals(this.eventKey) && this.messageHandler!=null)
            {
                messageHandler(0,NCacheMessage.FromBytes(data as byte[],this._trace));
            }
        }

        public ulong GetUniqueID()
        {
            var cacheItem = new CacheItem(new byte[1]);
            _cache.Insert(eventKey, cacheItem);
            cacheItem = _cache.GetCacheItem(eventKey);
            return (ulong)cacheItem.LastModifiedTime.Ticks;
        }
    }
}
