using System;

namespace NCache.OSS.Dapper
{
    public class CacheOptions
    {
        /// <summary>
        /// The expiration time span for the cached item.
        /// </summary>
        public TimeSpan? Expiration { get; set; }

        /// <summary>
        /// True if the expiration is sliding (resets TTL on cache hits); false for absolute expiration.
        /// </summary>
        public bool IsSliding { get; set; } = false;

        /// <summary>
        /// Force update the cache by ignoring any existing cache entries, executing the query against the database, and updating the cache.
        /// </summary>
        public bool ForceUpdate { get; set; } = false;

        /// <para>
        /// Set this to <c>true</c> if caching and consistency are a priority and you'd rather the
        /// call fail loudly than silently degrade to hitting the database every time. Leave it
        /// <c>false</c> if keeping the application running matters more than surfacing cache
        /// outages.
        /// </para>
        /// </summary>
        public bool ErrorEnabled { get; set; } = false;

        /// <summary>
        /// How long an acquired stampede lock is considered valid before it expires
        /// </summary>
        public TimeSpan? LockExpiration { get; set; }

        /// <summary>
        /// How long a caller will wait attempting to acquire the stampede lock
        /// before giving up. 
        /// </summary>
        public TimeSpan? LockTimeout { get; set; }  


    }
}
