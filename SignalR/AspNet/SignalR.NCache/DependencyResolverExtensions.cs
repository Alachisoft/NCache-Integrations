using System;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Messaging;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public static class DependencyResolverExtensions
    {
        public static IDependencyResolver UseNCache(this IDependencyResolver resolver, string cacheName, string eventKey)
        {            
            var configuration = new NCacheScaleoutConfiguration(cacheName, eventKey);


            return UseNCache(resolver, configuration);
        }

        public static IDependencyResolver UseNCache(this IDependencyResolver resolver, NCacheScaleoutConfiguration configuration)
        {
            var bus = new Lazy<NCacheMessageBus>(() => new NCacheMessageBus(resolver, configuration, new NCacheProvider()));
            resolver.Register(typeof(IMessageBus), () => bus.Value);

            return resolver;
        }
    }
}
