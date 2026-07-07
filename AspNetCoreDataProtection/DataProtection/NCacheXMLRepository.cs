using System;
using System.Text;
using System.Linq;
using System.Xml.Linq;
using Alachisoft.NCache.Client;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Runtime.Exceptions;
using NCache.OSS.AspNetCore.DataProtection;

namespace Alachisoft.NCache.AspNetCore.DataProtection
{
    public class NCacheXmlRepository : IXmlRepository, IDisposable
    {
        private string _tag;
        private ICache _cache;
        private ILogger _logger;
        private string _cacheName;

        public NCacheXmlRepository(string cacheName, string cacheTag, ILogger<NCacheXmlRepository> logger)
        {
            if (string.IsNullOrEmpty(cacheName))
                throw new ArgumentNullException(nameof(cacheName));

            if (string.IsNullOrEmpty(cacheTag))
                throw new ArgumentNullException(nameof(cacheTag));

            _tag = cacheTag;
            _logger = logger;

            try
            {
                _cacheName = cacheName;
                _cache = CacheManager.GetCache(cacheName);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{_cacheName} : An error occured while connecting to cache \n {ex}");

                throw;
            }

            _logger?.LogInformation($"{_cacheName} : NCacheXmlRepository has been initialized successfully");
        }

        public void StoreElement(XElement element, string friendlyName)
        {

            var item = DPXElementConverter.ConvertToCacheItem(element);

            try
            {
                _cache?.AddUsingTag(friendlyName, item, _tag);
            }
            catch (OperationFailedException ex)
            {
                if (ex.ErrorCode == NCacheErrorCodes.KEY_ALREADY_EXISTS)
                    _logger?.LogError($"{_cacheName} : An encryption Key with Key ID : {friendlyName} already exists in cache. \n {ex}");
                else
                    _logger?.LogError($"{_cacheName} : An error occured while adding the new key to cache \n {ex}");

                throw;
            }
        }

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            try
            {
                IDictionary<string, string> retrievedStrings = _cache?.GetUsingTag<string>(_tag);

                if (retrievedStrings.Count > 0)
                {
                    IList<XElement> retrievedElements = new List<XElement>();

                    foreach (KeyValuePair<string, string> element in retrievedStrings)
                    {
                        retrievedElements.Add(XElement.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(element.Value))));
                    }

                    return retrievedElements.ToList().AsReadOnly();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{_cacheName} : An error occured while fetching keys from cache \n {ex}");

                throw;
            }

            return new List<XElement>();
        }

        public ICache Cache
        {
            get { return _cache; }
        }

        ~NCacheXmlRepository()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_cache != null)
            {
                _cache.Dispose();
                _cache = null;
            }

            _logger?.LogInformation($"{_cacheName} : NCacheXmlRepository has been disposed successfully");
        }
    }
}
