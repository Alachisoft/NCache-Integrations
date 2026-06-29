// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System.Collections;
using System.Collections.Generic;
using System.Linq; 
using System.Data.Common;
using System.Data;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.SqlClient;
using Alachisoft.NCache.EntityFrameworkCore.NCache;
using Alachisoft.NCache.EntityFrameworkCore.NCLinq;
using Microsoft.Extensions.Logging;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using System.Diagnostics;
using System;
using Alachisoft.NCache.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>
    /// A static class that contains extension methods for caching entity framework query result sets.
    /// </summary>
    public static partial class QueryCacheExtensions
    {
        /// <summary>
        /// Checks if the result set is available in cache or not. If it is available it is fetched from the cache and returned
        /// however if it is not available the query is executed on the database and the result set is stored in cache as well
        /// as returned.
        /// </summary>
        /// <typeparam name="T">The generic type of the collection</typeparam>
        /// <param name="query">The query to be executed.</param>
        /// <param name="options">The option that will be used to store the result set.</param>
        /// <returns>Returns the result set of the query from cache if available else from the database and stores it in 
        /// the cache.
        /// </returns>
        public static IEnumerable<T> FromCache<T>(this IQueryable<T> query, CachingOptions options) where T : class
        {
            string str;
            return query.FromCache(out str, options);
        }

        /// <summary>
        /// Checks if the result set is available in cache or not. If it is available it is fetched from the cache and returned
        /// however if it is not available the query is executed on the database and the result set is stored in cache as well
        /// as returned.
        /// </summary>
        /// <typeparam name="T">The generic type of the collection</typeparam>
        /// <param name="query">The query to be executed.</param>
        /// <param name="cacheKey">The key against which the result set will be cached is returned as out parameter.</param>
        /// <param name="options">The option that will be used to store the result set.</param>
        /// <returns>Returns the result set of the query from cache if available else from the database and stores it in 
        /// the cache.
        /// </returns>
        public static IEnumerable<T> FromCache<T>(this IQueryable<T> query, out string cacheKey, CachingOptions options) where T : class
        {
            return FromCacheImplementation(CachingMethod.FromCache, query, out cacheKey, options, NCacheConfiguration.IsErrorEnabled);
        }

        // Main implementation
        private static IEnumerable<T> FromCacheImplementation<T>(CachingMethod cachingMethod, IQueryable<T> query, out string cacheKey, CachingOptions options, bool throwError = false) where T : class
        {
            Logger.Log(
                "Performing " + cachingMethod + " for " + query.ToString() + " with options " + options.ToLog() + ".", Microsoft.Extensions.Logging.LogLevel.Trace
            );

            // Create NCache entry options
            CachingOptions optionsCloned = (CachingOptions)options.Clone();

            cacheKey = null;
            string queryStoreKey = null;

            if (cachingMethod == CachingMethod.FromCache &&
                                 QueryCacheManager.Cache.IsCacheInitialized)
            {
                // Verify if query can be fetched seperately
                string pkCacheKey;
                if (QueryHelper.CanDirectPkFetch(query, optionsCloned, out pkCacheKey))
                {
                    T pkItem;


                    if (QueryCacheManager.Cache.TryGetValue<T>(pkCacheKey, out pkItem, throwError))
                    {
                        List<T> resultSetPk = new List<T>();
                        List<T> resultSetPkTracked = new List<T>();
                        var stateManagerPk = query.GetStateManager();

                        resultSetPk.Add((T)pkItem);

                        foreach (var entity in resultSetPk)
                        {
                            resultSetPkTracked.Add(((StateManager)stateManagerPk).GetRefValue(entity));
                        }
                        return resultSetPkTracked;
                    }


                }
            }

            bool cacheHit = false;
            IDictionary cacheResult = null;

            queryStoreKey = QueryCacheManager.GetQueryCacheKey(query, optionsCloned.QueryIdentifier);
            if (optionsCloned.StoreAs == StoreAs.Collection || optionsCloned.QueryIdentifier == null)
            {
                if (optionsCloned.StoreAs == StoreAs.Collection)
                    cacheKey = queryStoreKey;
                if (optionsCloned.QueryIdentifier == null)
                    optionsCloned.QueryIdentifier = queryStoreKey;
            }

            // Check in cache
            if (cachingMethod == CachingMethod.FromCache &&
                                 QueryCacheManager.Cache.IsCacheInitialized)
            {
                try
                {
                    cacheHit = QueryCacheManager.Cache.GetByKey<T>(queryStoreKey, out cacheResult, optionsCloned.StoreAs, throwError);
                }
                catch (Exception ex)
                {

                }
            }

            // If not found in cache go for db
            if (!cacheHit)
            {
                var enumerableSet = query.AsEnumerable<T>();
                if (cachingMethod == CachingMethod.FromCache &&
                    !QueryCacheManager.Cache.IsCacheInitialized)
                    return enumerableSet; 

                return new NCacheEnumerable<T>(queryStoreKey, query, enumerableSet, optionsCloned, throwError);

            }
            // data is found in cache return result set
            else
            {
                // Assume its a collection
                if (cacheResult.Count == 1)
                {
                    foreach (var item in cacheResult.Values)
                    {
                        CacheEntry<IList<T>> entry = item as CacheEntry<IList<T>>;
                        if (entry != null)
                        {
                            // confirmed stored as collection just return the value after casting
                            IEnumerable<T> resultSetC = (IEnumerable<T>)entry.Value;
                            var resultSetCTracked = new List<T>();
                            var stateManagerC = query.GetStateManager();
                            foreach (var entity in resultSetC)
                            {
                                resultSetCTracked.Add(((StateManager)stateManagerC).GetRefValue(entity));
                            }
                            return resultSetCTracked;
                        }
                        break;
                    }
                }

                var resultSetSE = cacheResult.Values.Cast<T>();
                var resultSetSETracked = new List<T>();
                var stateManagerSE = query.GetStateManager();
                foreach (var entity in resultSetSE)
                {
                    resultSetSETracked.Add(((StateManager)stateManagerSE).GetRefValue(entity));
                }
                return resultSetSETracked;
            }
        }
    }
}
