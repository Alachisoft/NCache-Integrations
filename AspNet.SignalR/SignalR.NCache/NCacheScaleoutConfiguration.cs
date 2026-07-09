using System;
using System.Globalization;
using Microsoft.AspNet.SignalR.Messaging;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public class NCacheScaleoutConfiguration : ScaleoutConfiguration
    {
        public NCacheScaleoutConfiguration(string cacheName, string eventKey)
        {
            if (cacheName == null)
            {
                throw new ArgumentNullException("connectionString");
            }

            if (eventKey == null)
            {
                throw new ArgumentNullException("eventKey");
            }

            CacheName = cacheName;        
            EventKey = eventKey;
        }

        internal string CacheName { get; private set; }

        public string EventKey { get; private set; }
    }
}
