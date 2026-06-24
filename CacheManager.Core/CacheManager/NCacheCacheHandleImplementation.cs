using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Runtime.Exceptions;
using CacheManager.Core;
using CacheManager.Core.Internal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NCache.OSS.CacheManager.Core
{
    public class NCacheCacheHandle<T> : BaseCacheHandle<T>
    {
        private Alachisoft.NCache.Client.ICache _cache;
        private CacheHandleConfiguration _configuration;
        private ILogger _logger;
        private NCacheOptions _options;
        private string _regionPrefix = "NCacheCMRegion:";

        public override int Count
        {
            get
            {
                try
                {
                    return checked((int)_cache.Count);
                }
                catch (OverflowException)
                {
                    return int.MaxValue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Count operation");
                    throw;
                }
            }
        }

        protected override ILogger Logger => _logger;

        public NCacheCacheHandle(ICacheManagerConfiguration managerConfiguration, CacheHandleConfiguration configuration, ILoggerFactory loggerFactory, NCacheOptions options) : base(managerConfiguration, configuration)
        {
            _configuration = configuration;
            _options = options;

            if (managerConfiguration == null)
                throw new ArgumentNullException(nameof(managerConfiguration));
            if (_configuration == null)
                throw new ArgumentNullException(nameof(_configuration));
            if (loggerFactory == null)
                throw new ArgumentNullException(nameof(loggerFactory));
            if (_options == null)
                throw new ArgumentNullException(nameof(_options));

            _logger = loggerFactory.CreateLogger(this.GetType());

            try
            {
                _cache = Alachisoft.NCache.Client.CacheManager.GetCache(_options.CacheName, _options.GetCacheConnectionOptions());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during NCacheCacheHandle Initialization");
                throw;
            }

            _logger.LogInformation($"NCache handle initialized. CacheName={_options.CacheName}");
        }

        public override void Clear()
        {
            try
            {
                _logger.LogInformation("CLEAR cache triggered");

                _cache.Clear();

                _logger.LogInformation("CLEAR cache completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Clear operation");
                throw;
            }
        }

        public override void ClearRegion(string region)
        {
            if (string.IsNullOrEmpty(region)) throw new ArgumentNullException(nameof(region));

            try
            {
                var keys = GetRegionKeys(region);

                foreach (var key in keys)
                {
                    _cache.Remove(key);
                }

                _cache.Remove(GetRegionKey(region));

                _logger.LogInformation($"Region keys removed, region = {region}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during ClearRegion operation");
                throw;
            }
        }

        public override bool Exists(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            try
            {
                bool keyExists = _cache.Contains(key);
                if (!keyExists)
                    _logger.LogInformation($"EXISTS miss key={key}");

                return keyExists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Exists(key) operation");
                throw;
            }
        }

        public override bool Exists(string key, string region)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrEmpty(region)) throw new ArgumentNullException(nameof(region));

            try
            {
                if (!_cache.Contains(key))
                    return false;

                var regionKeys = GetRegionKeys(region);

                bool keyExists = regionKeys.Contains(key);

                if (!keyExists)
                    _logger.LogInformation($"EXISTS(region) miss key={key} region={region}");

                return keyExists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Exists(key, region) operation");
                throw;
            }
        }

        protected override bool AddInternalPrepared(CacheItem<T> item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            try
            {
               var ncacheItem = CreateCacheItem(item);

                _cache.Add(item.Key, ncacheItem);

                if (!string.IsNullOrEmpty(item.Region))
                {
                    AddToRegion(item.Region, item.Key);
                }

                _logger.LogInformation($"ADD key={item.Key} region={item.Region}");

                return true;
            }
            catch (OperationFailedException ex)
            {
                // TODO: Write more of the error message that's suppose to show up here, I can't remember it rn
                if (ex.Message.Contains("already exists"))
                    return false;
                _logger.LogError(ex, "Error during AddInternalPrepared(item) operation");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AddInternalPrepared(item) operation");
                throw;
            }
        }

        protected override CacheItem<T> GetCacheItemInternal(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            try
            {
                var value = _cache.GetCacheItem(key);

                if (value == null)
                    return null;

                return new CacheItem<T>(
                    key,
                    value.GetValue<T>(),
                    ToCacheManagerExpiration(value.Expiration.Type),
                    value.Expiration.ExpireAfter);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during GetCacheItemInternal(key) operation");
                throw;
            }
        }

        protected override CacheItem<T> GetCacheItemInternal(string key, string region)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrEmpty(region)) throw new ArgumentNullException(nameof(region));
            var value = _cache.GetCacheItem(key);

            if (value == null)
                return null;

            return new CacheItem<T>(
                    key,
                    region,
                    value.GetValue<T>(),
                    ToCacheManagerExpiration(value.Expiration.Type),
                    value.Expiration.ExpireAfter);
        }

        protected override bool RemoveInternal(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            try
            {
                if (!_cache.Contains(key))
                    return false;

                _cache.Remove(key);

                _logger.LogInformation($"REMOVE key={key}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RemoveInternal(key) operation");
                throw;
            }
        }

        protected override bool RemoveInternal(string key, string region)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrEmpty(region)) throw new ArgumentNullException(nameof(region));

            try
            {
                if (!_cache.Contains(key))
                    return false;

                _cache.Remove(key);

                if (!string.IsNullOrEmpty(region))
                {
                    RemoveFromRegion(region, key);
                }

                _logger.LogInformation($"REMOVE key={key} region={region}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RemoveInternal(key, region) operation");
                throw;
            }
        }

        protected override void Dispose(bool disposeManaged)
        {
            try
            {
                if (disposeManaged)
                {
                    _logger.LogInformation($"Disposing NCache handle CacheName={_options.CacheName}");

                    _cache?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Dispose(disposeManaged) operation");
                throw;
            }
            
        }
     
        protected override void PutInternalPrepared(CacheItem<T> item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            try
            {
                var cacheItem = CreateCacheItem(item);

                _cache.Insert(item.Key, cacheItem);

                if (!string.IsNullOrEmpty(item.Region))
                {
                    AddToRegion(item.Region, item.Key);
                }

                _logger.LogInformation($"PUT key={item.Key} region={item.Region}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during PutInternalPrepared(item) operation");
                throw;
            }
        }

        #region Private Methods

        private ExpirationType ToNCacheExpiration(ExpirationMode source)
        {
            switch (source)
            {
                case ExpirationMode.None:
                    return ExpirationType.None;

                case ExpirationMode.Default:

                    if (_configuration.ExpirationMode ==
                        ExpirationMode.Default)
                    {
                        return ExpirationType.None;
                    }

                    return ToNCacheExpiration(
                        _configuration.ExpirationMode);

                case ExpirationMode.Absolute:
                    return ExpirationType.Absolute;

                case ExpirationMode.Sliding:
                    return ExpirationType.Sliding;

                default:
                    throw new NotSupportedException(
                        $"Unsupported expiration mode: {source}");
            }
        }

        private ExpirationMode ToCacheManagerExpiration(ExpirationType source)
        {
            switch (source)
            {
                case ExpirationType.None:
                    return ExpirationMode.None;

                case ExpirationType.Absolute:
                    return ExpirationMode.Absolute;

                case ExpirationType.Sliding:
                    return ExpirationMode.Sliding;

                default:
                    throw new NotSupportedException(
                        $"Unsupported expiration type: {source}");
            }
        }

        private Alachisoft.NCache.Client.CacheItem CreateCacheItem(CacheItem<T> item)
        {
            Alachisoft.NCache.Client.CacheItem cacheItem = new Alachisoft.NCache.Client.CacheItem(item.Value);

            var expirationType = ToNCacheExpiration(item.ExpirationMode);

            if (expirationType != ExpirationType.None)
            {
                cacheItem.Expiration = new Expiration(expirationType, item.ExpirationTimeout);
            }
            else
            {
                cacheItem.Expiration = new Expiration(expirationType);
            }

            return cacheItem;
        }

        private string GetRegionKey(string region)
        {
            return $"{_regionPrefix}{region}";
        }

        private void AddToRegion(string region, string key)
        {
            var regionKey = GetRegionKey(region);

            var list = _cache.Get<HashSet<string>>(regionKey);

            if (list == null)
                list = new HashSet<string>();

            list.Add(key);

            _cache.Insert(regionKey, list);
        }

        private void RemoveFromRegion(string region, string key)
        {
            var regionKey = GetRegionKey(region);

            var list = _cache.Get<HashSet<string>>(regionKey);

            if (list == null)
                return;

            list.Remove(key);

            _cache.Insert(regionKey, list);
        }

        private HashSet<string> GetRegionKeys(string region)
        {
            var regionKey = GetRegionKey(region);

            return _cache.Get<HashSet<string>>(regionKey) ?? new HashSet<string>();
        }

        #endregion
    }
}
