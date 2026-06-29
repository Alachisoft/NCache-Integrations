// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Alachisoft.NCache.EntityFrameworkCore.NCache;
using Microsoft.Extensions.Caching.Memory;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>
    /// A static class that contains extension methods for caching entity framework query result sets.
    /// </summary>
    public static partial class QueryCacheExtensions
    {
        /* ************************************************************************************************************************ */
        /*                                                                                                                          */
        /*                  OUR IMPLEMENTATIONS FOR [FromCache] ASYNC METHODS                      */
        /*                                                                                                                          */
        /* ************************************************************************************************************************ */
         

        /// <summary>
        /// Asynchronously Checks if the result is available in cache or not. If it is available it is fetched from the cache and returned
        /// however if it is not available the query is executed on the database and the result is stored in cache as well
        /// as returned. The result is encapsulated in a task and returned.
        /// </summary>
        /// <typeparam name="T">The generic type of the result</typeparam>
        /// <param name="query">The query to be executed.</param>
        /// <param name="options">The option that will be used to store the result.</param>
        /// <returns>Returns the result of the query (encapsulated in a task) from cache if available else from the database and stores it in 
        /// the cache.
        /// </returns>
        public static async Task<T> FromCacheAsync<T>(this QueryDeferred<T> query, CachingOptions options/*, CancellationToken cancellationToken = default(CancellationToken)*/)
        {
            Logger.Log(
                "Async operation requested.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            Task<T> task = Task.Factory.StartNew(
                () => FromCache(query, options)/*, cancellationToken*/
            );
            return await task;
        }
    }
}