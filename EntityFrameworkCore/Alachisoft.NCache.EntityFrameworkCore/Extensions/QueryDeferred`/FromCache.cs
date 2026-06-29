// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using Alachisoft.NCache.EntityFrameworkCore.NCache;
using System.Collections.Generic;
using Alachisoft.NCache.EntityFrameworkCore.NCLinq;
using System.Linq;
using Alachisoft.NCache.EntityFrameworkCore.Extensions.QueryDeferred;
using System.Collections;
using Alachisoft.NCache.Runtime.Caching; 
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Data.SqlClient;
using System.Dynamic;
using Alachisoft.NCache.Client;
using System;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>
    /// A static class that contains extension methods for caching entity framework query result sets.
    /// </summary>
    public static partial class QueryCacheExtensions
    {

        /// <summary>
        /// Checks if the result is available in cache or not. If it is available it is fetched from the cache and returned
        /// however if it is not available the query is executed on the database and the result is stored in cache as well
        /// as returned.
        /// </summary>
        /// <typeparam name="T">The generic type of the collection</typeparam>
        /// <param name="query">The query to be executed.</param>
        /// <param name="options">The option that will be used to store the result set.</param>
        /// <returns>Returns the result set of the query from cache if available else from the database and stores it in 
        /// the cache.
        /// </returns>
        public static T FromCache<T>(this QueryDeferred<T> query, CachingOptions options)
        {
            return query.FromCache(out string cacheKey, options);
        }

        /// <summary>
        /// Checks if the result is available in cache or not. If it is available it is fetched from the cache and returned
        /// however if it is not available the query is executed on the database and the result is stored in cache as well
        /// as returned.
        /// </summary>
        /// <typeparam name="T">The generic type of the result</typeparam>
        /// <param name="query">The query to be executed.</param>
        /// <param name="cacheKey">The key against which the result will be cached is returned as out parameter.</param>
        /// <param name="options">The option that will be used to store the result.</param>
        /// <returns>Returns the result of the query from cache if available else from the database and stores it in 
        /// the cache.
        /// </returns>
        public static T FromCache<T>(this QueryDeferred<T> query, out string cacheKey, CachingOptions options)
        {
            return FromCacheImplementation(CachingMethod.FromCache, query, out cacheKey, options, NCacheConfiguration.IsErrorEnabled);
        }

        private static T FromCacheImplementation<T>(CachingMethod cachingMethod, QueryDeferred<T> query, out string cacheKey, CachingOptions options, bool throwError = false)
        {
            Logger.Log(
                "Performing " + cachingMethod + " for " + query.ToString() + " with options " + options.ToLog() + ".",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            options = (CachingOptions)options.Clone();
            // Always store as collection
            options.StoreAs = StoreAs.Collection;

            bool cacheHit = false;
            IDictionary cacheResult = default(Hashtable);
            cacheKey = QueryCacheManager.GetQueryCacheKey(query.Query, options.QueryIdentifier);

            // If user has specified tag, leave it as it is
            // Otherwise overwrite it with 'cacheKey'
            options.QueryIdentifier = options.QueryIdentifier ?? cacheKey;

            /* NOTE: If user stored result with a tag and is trying to query 
                 *       it without the tag, it's a different query so don't 
                 *       worry about that.
                 */

            // Get result into 'cacheResult' hashtable if it exists
            if (cachingMethod == CachingMethod.FromCache &&
                                 QueryCacheManager.Cache.IsCacheInitialized)
            {

                // Get by the tag (more reliable)
                cacheHit = QueryCacheManager.Cache.GetByKey<T>(options.QueryIdentifier, out cacheResult, options.StoreAs, throwError);

            }
            // If result wasn't found OR result was meant to be stored fresh
            if (!cacheHit)
            {
                
                object item = query.Execute();

                try
                {
                    if (cachingMethod == CachingMethod.FromCache &&
                                 QueryCacheManager.Cache.IsCacheInitialized)
                    {
                        QueryCacheManager.Cache.SetAsCacheEntry(cacheKey, item ?? Null.Value, options, throwError);
                    }
                }
                catch (Exception ex)
                {
                    if (throwError)
                        throw;
                }

                return item == null ? default(T) : (T)item;
            }
            // If result was meant to be fetched instead of stored fresh AND it was found (somewhat)
            else
            {
                object returnVal = default(T);

                if (cacheResult != default(Hashtable))
                {
                    returnVal = cacheResult.Values.Cast<CacheEntry<T>>().FirstOrDefault().Value;
                }
                return returnVal != null ? (returnVal is Null ? default(T) : (T)returnVal) : default(T);
            }
        }
    }
}
