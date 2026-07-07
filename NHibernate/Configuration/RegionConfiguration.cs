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
using Alachisoft.NCache.Runtime;
using Alachisoft.NCache.Common.Configuration;
using Alachisoft.NCache.Common.Enum;
using Alachisoft.NCache.Common;

namespace Alachisoft.NCache.Integrations.NHibernate.Cache.Configuration
{
    class RegionConfiguration
    {
        private string _regionName;
        private string _cacheName;
        private string _priority ="Default";
       
        private string _expirationType="none";
        private int _expirationPeriod = 0;
        private CacheItemPriority _cItemPriotity;

        [ConfigurationAttribute("name")]
        public string RegionName
        {
            get { return _regionName; }
            set { _regionName = value; }
        }

        [ConfigurationAttribute("cache-name")]      
        public string CacheName
        {
            get { return _cacheName; }
            set { _cacheName = value; }
        }

        [ConfigurationAttribute("priority")]
        public string Priority
        {
            get { return _priority; }
            set { _priority = value; }
        }


        [ConfigurationAttribute("expiration-type")]
        public string ExpirationType
        {
            get { return _expirationType; }
            set { _expirationType = value; }
        }

        [ConfigurationAttribute("expiration-period")]
        public int ExpirationPeriod
        {
            get { return _expirationPeriod; }
            set { _expirationPeriod = value; }
        }

        public CacheItemPriority CacheItemPriority
        {
            get { return _cItemPriotity; }
            set { _cItemPriotity = value; }
        }

    }
}
