using Alachisoft.NCache.Client;
using Dapper;
using NCache.DistributedLock.Locks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace NCache.OSS.Dapper
{
    public static class DapperNCacheExtensions
    {
        private static readonly ConcurrentDictionary<string, NCacheDistributedSemaphore> _keyLocks = new();

        private static NCacheDistributedSemaphore GetKeyLock(string key, ICache cache, TimeSpan expiration) =>
               _keyLocks.GetOrAdd(key, k => new NCacheDistributedSemaphore(k, 1, cache, expiration));

        private static INCacheCacheProvider GetProvider(INCacheCacheProvider? customProvider)
        {
            var provider = customProvider ?? DapperNCache.CacheProvider;
            if (provider == null)
            {
                throw new InvalidOperationException("NCache Cache Provider is not configured. Set DapperNCache.CacheProvider or pass a custom provider instance.");
            }
            return provider;
        }

        private static void ReportCacheError(Exception ex, string context)
        {
            var handler = DapperNCache.OnCacheError;
            if (handler != null)
            {
                try
                {
                    handler(ex, context);
                }
                catch
                {
                    // A broken logging hook must never take down the calling query.
                }
            }
            else
            {
                System.Diagnostics.Trace.TraceWarning($"[NCache.OSS.Dapper] {context}: {ex}");
            }
        }

        /// <summary>
        /// Reads from the cache. On failure: if errorEnabled, rethrows; otherwise logs and
        /// returns default (treated by the caller as a cache miss, falling back to the database).
        /// </summary>
        private static T? TryGetFromCache<T>(INCacheCacheProvider provider, string key, bool errorEnabled)
        {
            try
            {
                return provider.Get<T>(key);
            }
            catch (Exception ex)
            {
                ReportCacheError(ex, $"Get failed for key '{key}'");
                if (errorEnabled) throw;
                return default;
            }
        }

        /// <summary>
        /// Writes to the cache. On failure: if errorEnabled, rethrows; otherwise logs and swallows
        /// -- the database result the caller already has is still returned, it just won't be cached.
        /// </summary>
        private static void TrySetCache<T>(INCacheCacheProvider provider, string key, T value, CacheOptions? options, bool errorEnabled)
        {
            try
            {
                provider.Set(key, value, options);          
            }
            catch (Exception ex)
            {
                ReportCacheError(ex, $"Set failed for key '{key}'");
                if (errorEnabled) throw;               
            }
        }

        /// <summary>
        /// Removes an entry from the cache (used for ForceUpdate). On failure: if errorEnabled,
        /// rethrows; otherwise logs and swallows -- the stale entry may briefly remain, but the
        /// query below still runs against the database and re-populates the cache regardless.
        /// </summary>
        private static void TryRemoveFromCache(INCacheCacheProvider provider, string key, bool errorEnabled)
        {
            try
            {
                provider.Remove(key);
            }
            catch (Exception ex)
            {
                ReportCacheError(ex, $"Remove failed for key '{key}'");
                if (errorEnabled) throw;
            }
        }

        private static CacheOptions ResolveWriteOptions(CacheOptions? options)
        {
            if (options?.Expiration.HasValue == true)
                return options;

            return new CacheOptions
            {
                Expiration = DapperNCache.DefaultExpiration,
                IsSliding = DapperNCache.DefaultIsSliding,
                ForceUpdate = options?.ForceUpdate ?? false,
                ErrorEnabled = options?.ErrorEnabled ?? false       
            };
        }

        // ==========================================
        // Synchronous Query Extension
        // ==========================================

        /// <summary>
        /// Executes a SQL query with cache-aside behavior using NCache. Returns cached result if available;
        /// otherwise queries the database and caches the result.
        /// </summary>
        public static IEnumerable<T> QueryCached<T>(
        this IDbConnection cnn,
        string sql,
        object? param = null,
        CacheOptions? options = null,
        INCacheCacheProvider? cacheProvider = null,
        string? customCacheKey = null)
        {
            var provider = GetProvider(cacheProvider);
            var key = customCacheKey ?? CacheKeyGenerator.GenerateCacheKey(cnn.Database, sql, param, typeof(T));
            var errorEnabled = options?.ErrorEnabled ?? false;

            if (options?.ForceUpdate == true)
            {
                TryRemoveFromCache(provider, key, errorEnabled);
            }
            else
            {
                var cached = TryGetFromCache<List<T>>(provider, key, errorEnabled);
                if (cached != null)
                    return cached;
            }
            
            var lockTimeout = options?.LockTimeout ?? TimeSpan.FromSeconds(30);
            var lockExpiration = options?.LockExpiration ?? TimeSpan.FromSeconds(30);
            var semaphore = GetKeyLock(key, provider.Cache, lockExpiration);

            using var handle = semaphore.Acquire(lockTimeout);

            if (options?.ForceUpdate != true)
            {
                var cached = TryGetFromCache<List<T>>(provider, key, errorEnabled);
                if (cached != null)
                    return cached;
            }

            var results = cnn.Query<T>(sql, param).ToList();
            TrySetCache(provider, key, results, ResolveWriteOptions(options), errorEnabled);
            return results;
        }
    }
}
