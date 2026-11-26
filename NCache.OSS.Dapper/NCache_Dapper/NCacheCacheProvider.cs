using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using System;

namespace NCache.OSS.Dapper
{
    public class NCacheCacheProvider : INCacheCacheProvider
    {
        private readonly ICache _cache;

        public ICache Cache => _cache;



        /// <summary>
        /// Creates a new NCacheCacheProvider backed by an already-configured NCache
        /// <see cref="ICache"/> instance. The caller is responsible for building and
        /// configuring that instance however they like -- e.g. via
        /// <c>CacheManager.GetCache(cacheName, connectionOptions)</c>, client config files,
        /// TLS settings, custom retry/timeout policies, etc. This provider only consumes it.
        /// </summary>
        /// <param name="cache">An initialized, connected NCache <see cref="ICache"/> instance.</param>
        public NCacheCacheProvider(ICache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public T? Get<T>(string key)
        {
            return _cache.Get<T>(key);
        }

        public void Set<T>(string key, T value, CacheOptions? options = null)
        {
            if (value == null) return;

            var cacheItem = new CacheItem(value);

            if (options != null && options.Expiration.HasValue)
            {
                var expType = options.IsSliding ? ExpirationType.Sliding : ExpirationType.Absolute;
                cacheItem.Expiration = new Expiration(expType, options.Expiration.Value);
            }

            _cache.Insert(key, cacheItem);
        }

        public void Remove(string key)
        {
            _cache.Remove(key);
        }

        public void Dispose()
        {
            _cache?.Dispose();
        }
    }
}