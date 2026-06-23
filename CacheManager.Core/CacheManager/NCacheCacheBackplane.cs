using Alachisoft.NCache.Runtime.Caching;
using CacheManager;
using CacheManager.Core;
using CacheManager.Core.Internal;
using System;
using System.Configuration;
using System.Drawing;
using System.Linq;
using System.Text;

namespace NCache.OSS.CacheManager.Core
{
    public sealed class NCacheCacheBackplane : CacheBackplane
    {
        private Alachisoft.NCache.Client.ICache _cache;
        private NCacheOptions _options;
        private ITopic _topic;
        private byte[] _identifier;
        ITopicSubscription _subscription;

        public NCacheCacheBackplane(ICacheManagerConfiguration managerConfiguration, NCacheOptions options) : base(managerConfiguration)
        {
            _options = options;

            if (managerConfiguration == null) throw new ArgumentNullException(nameof(managerConfiguration));
            if (options == null) throw new ArgumentNullException(nameof(options));

            try
            {
                _cache = Alachisoft.NCache.Client.CacheManager.GetCache(_options.CacheName, _options.GetCacheConnectionOptions());
                _topic = _cache.MessagingService.GetTopic(managerConfiguration.BackplaneChannelName ?? "NCacheBackplaneEvent");
                if (_topic == null)
                    _topic = _cache.MessagingService.CreateTopic(managerConfiguration.BackplaneChannelName ?? "NCacheBackplaneEvent");
                _subscription = _topic.CreateSubscription(OnMessageReceived);                
            }
            catch (Exception ex)
            {
                throw;
            }

            if (_topic == null)
                throw new InvalidOperationException("Backplane topic is not initialized.");

            _identifier = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        }

        public override void NotifyChange(string key, CacheItemChangedEventAction action)
        {
            BackplaneMessage message = BackplaneMessage.ForChanged(_identifier, key, action);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);
            
            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

     
        public override void NotifyChange(string key, string region, CacheItemChangedEventAction action)
        {
            BackplaneMessage message = BackplaneMessage.ForChanged(_identifier, key, region, action);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);

            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

        public override void NotifyClear()
        {

            BackplaneMessage message = BackplaneMessage.ForClear(_identifier);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);

            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

        public override void NotifyClearRegion(string region)
        {
            BackplaneMessage message = BackplaneMessage.ForClearRegion(_identifier, region);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);

            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

        public override void NotifyRemove(string key)
        {
            BackplaneMessage message = BackplaneMessage.ForRemoved(_identifier, key);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);

            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

        public override void NotifyRemove(string key, string region)
        {
            BackplaneMessage message = BackplaneMessage.ForRemoved(_identifier, key, region);
            NCacheBackplaneEvent payload = NCacheBackplaneEvent.From(message);

            _topic.Publish(new Message(payload), DeliveryOption.All);
        }

        protected override void Dispose(bool managed)
        {
            if (managed)
            {
                try
                {
                    _subscription.UnSubscribe();
                    _cache.Dispose();
                }
                catch (Exception ex)
                {
                    throw;
                }
            }

            base.Dispose(managed);
        }

        private void OnMessageReceived(object sender, MessageEventArgs e)
        {
            var ncacheMessage = e.Message.Payload as NCacheBackplaneEvent;
            var message = ncacheMessage.ToBackplaneMessage();

            if (message == null)
                return;

            if (_identifier.SequenceEqual(message.OwnerIdentity))
                return;

            switch (message.Action)
            {
                case BackplaneAction.Clear:
                    TriggerCleared();
                    break;

                case BackplaneAction.ClearRegion:
                    TriggerClearedRegion(message.Region);
                    break;

                case BackplaneAction.Removed:
                    if (message.Region == null)
                        TriggerRemoved(message.Key);
                    else
                        TriggerRemoved(message.Key, message.Region);
                    break;

                case BackplaneAction.Changed:
                    if (message.Region == null)
                        TriggerChanged(message.Key, message.ChangeAction);
                    else
                        TriggerChanged(message.Key, message.Region, message.ChangeAction);
                    break;
            }
        }
    }
}
