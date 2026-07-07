// ===============================================================================
// Alachisoft (R) NCache Integrations
// NCache Provider for NHibernate
// ===============================================================================
// Copyright © Alachisoft.  All rights reserved.
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY
// OF ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT
// LIMITED TO THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS FOR A PARTICULAR PURPOSE.
// ===============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NHibernate;
using NHibernate.Cache;
using Alachisoft.NCache.Integrations.NHibernate.Cache.Configuration;

namespace Alachisoft.NCache.Integrations.NHibernate.Cache
{

    class NCacheProvider : ICacheProvider
    {
        private static readonly INHibernateLogger _logger = NHibernateLogger.For(typeof(Alachisoft.NCache.Integrations.NHibernate.Cache.NCacheProvider));

        #region ICacheProvider Members

        public ICache BuildCache(string regionName, IDictionary<string, string> properties)
        {
            if (_logger.IsDebugEnabled())
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kvp in properties)
                {
                    sb.Append("name=");
                    sb.Append(kvp.Key.ToString());
                    sb.Append("&value=");
                    sb.Append(kvp.Value.ToString());
                    sb.Append(";");
                }
                _logger.Debug("building cache with region: " + regionName + ", properties: " + sb.ToString());
            }

            return new NCache(regionName, properties);
        }

        public long NextTimestamp()
        {
            return Timestamper.Next();
        }

        public void Start(IDictionary<string, string> properties)
        {
            //do nothing
        }

        public void Stop()
        {
            //do nothing
        }

        #endregion
    }
}
