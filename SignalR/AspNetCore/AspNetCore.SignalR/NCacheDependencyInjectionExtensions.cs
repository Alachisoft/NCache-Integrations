using System;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Alachisoft.NCache.AspNetCore.SignalR
{
    public static class NCacheDependencyInjectionExtensions
    {
        /// <summary>
        /// Adds scale-out to a <see cref="ISignalRServerBuilder"/>, using NCache server.
        /// </summary>
        /// <param name="signalRBuilder">The <see cref="ISignalRServerBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="ISignalRServerBuilder"/> for chaining.</returns>
        public static ISignalRServerBuilder AddNCache(this ISignalRServerBuilder signalRBuilder, string cacheName, string eventKey)
        {            
            return AddNCache(signalRBuilder, o =>
            {
                o.CacheName = cacheName;
                o.EventKey = eventKey;
            });
        }

        /// <summary>
        /// Adds scale-out to a <see cref="ISignalRServerBuilder"/>, using NCache server.
        /// </summary>
        /// <param name="signalrBuilder">The <see cref="ISignalRServerBuilder"/>.</param>
        /// <param name="configure">A callback to configure NCache options.</param>
        /// <returns>The same instance of the <see cref="ISignalRServerBuilder"/> for chaining.</returns>
        public static ISignalRServerBuilder AddNCache(this ISignalRServerBuilder signalrBuilder, Action<NCacheConfiguration> configure)
        {
            signalrBuilder.Services.Configure(configure);
            signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(NCacheHubLifetimeManager<>));
            return signalrBuilder;
        }
    }
}
