
using System;

namespace Alachisoft.NCache.AspNetCore.SignalR
{   
    /// <summary>
    /// This class provides configuration options for NCache.
    /// </summary>
    public sealed class NCacheConfiguration
    { 
        /// <summary>
        /// Name of the cache in NCache which will store the respective item for the client to trace updates via itemVersion.
        /// </summary>
        /// <returns>The name of the cache in NCache which will store the respective item for the client.</returns>
        public string CacheName { get; set; }

        /// <summary>
        /// Contains information used to uniquely identify an application.
        /// </summary>
        /// <returns>Information used to uniquely identify an application.</returns>
        [Obsolete("This property is deprecated. Please use the 'EventKey' property instead.", false)]

        public string ApplicationID { get; set; }
        /// <summary>
        /// Unique, user specified key attribute for the item added to NCache on client registration.
        /// </summary>
        /// <returns>Event key of the item Added.</returns>

        public string EventKey { get; set; }

        /// <summary>
        /// This class contains the client connection properties e.g. client-request-timeout, connection-retries, retry-interval. 
        /// </summary>
        /// <returns>The client connection properties.</returns>
        public SignalRConnectionOptions ConnectionOptions { get; set; }
    }
}
