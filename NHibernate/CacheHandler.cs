
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


using Alachisoft.NCache.Client;
using Alachisoft.NCache.Common.FeatureUsageData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace Alachisoft.NCache.Integrations.NHibernate.Cache

{
    class CacheHandler
    {

         private Alachisoft.NCache.Client.ICache _cache;


        private int _refCount = 0;

        public CacheHandler(string cacheName, bool exceptionEnabled)
        {
			  CacheConnectionOptions cacheConnectionOptions = new CacheConnectionOptions
            {
                AppName = FeatureUsageCollector.FeatureTag + FeatureEnum.hibernate
            };

            _cache = Alachisoft.NCache.Client.CacheManager.GetCache(cacheName, cacheConnectionOptions);

            _refCount++;
        }


        public Alachisoft.NCache.Client.ICache Cache

        {
            get { return _cache; }
        }

        public void IncrementRefCount()
        {
            _refCount++;
        }

        public int DecrementRefCount()
        {
            return --_refCount;
        }

        public void DisposeCache()
        {
            _cache.Dispose();
        }
    }
}
