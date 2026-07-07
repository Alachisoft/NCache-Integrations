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
using Alachisoft.NCache.Common.Configuration;

namespace Alachisoft.NCache.Integrations.NHibernate.Cache.Configuration
{
    class CacheRegions
    {
        private RegionConfiguration[] _regions;

        [ConfigurationSection("region")]
        public RegionConfiguration[] Regions
        {
            get { return _regions; }
            set { _regions = value; }
        }
    }
}
