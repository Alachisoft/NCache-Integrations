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
    [ConfigurationRoot("application-config")]
    class ApplicationConfiguration
    {
        private string _applicationID;
        private bool _cacheExceptionEnabled = true;
        private string _defaultRegion;
        private bool _keyCaseSensitivity = false;
        private CacheRegions _cacheRegions;

        [ConfigurationAttribute("application-id")]
        public string ApplicationID
        {
            get { return _applicationID; }
            set { _applicationID = value; }
        }

        [ConfigurationAttribute("enable-cache-exception")]
        public bool CacheExceptionEnabled
        {
            get { return _cacheExceptionEnabled; }
            set { _cacheExceptionEnabled = value; }
        }

        [ConfigurationAttribute("default-region-name")]
        public string DefaultRegion
        {
            get { return _defaultRegion; }
            set { _defaultRegion = value; }
        }

        [ConfigurationAttribute("key-case-sensitivity")]
        public bool KeyCaseSensitivity
        {
            get { return _keyCaseSensitivity; }
            set { _keyCaseSensitivity = value; }
        }

        [ConfigurationSection("cache-regions")]
        public CacheRegions CacheRegions
        {
            get { return _cacheRegions; }
            set { _cacheRegions = value; }
        }

    }
}
