// Description: Entity Framework Bulk Operations & Utilities (EF Bulk SaveChanges, Insert, Update, Delete, Merge | LINQ Query Cache, Deferred, Filter, IncludeFilter, IncludeOptimize | Audit)
// Website & Documentation: https://github.com/zzzprojects/Entity-Framework-Plus
// Forum & Issues: https://github.com/zzzprojects/EntityFramework-Plus/issues
// License: https://github.com/zzzprojects/EntityFramework-Plus/blob/master/LICENSE
// More projects: http://www.zzzprojects.com/
// Copyright © ZZZ Projects Inc. 2014 - 2016. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Security.Principal;
using Alachisoft.NCache.EntityFrameworkCore.NCache;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Caching.Memory;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    /// <summary>Manage EF+ Query Cache Configuration.</summary>
    internal static class QueryCacheManager
    {
        private static NCacheWrapper _nCacheWrapper; 
        internal static Microsoft.Extensions.Logging.ILogger[] Loggers { get; set; }

        /// <summary>Static constructor.</summary>
        static QueryCacheManager()
        {
            DefaultNCacheEntryOptions = new CachingOptions();
            CachePrefix = "NCache;";
            CacheTags = new ConcurrentDictionary<string, List<string>>();
            IncludeConnectionInCacheKey = false;
            IncludeUserNameAndDatabase = true;
        }

        /// <summary>Gets or sets the cache to use for the QueryCacheExtensions extension methods.</summary>
        /// <value>The cache to use for the QueryCacheExtensions extension methods.</value>
        internal static NCacheWrapper Cache
        {
            get
            {
                if (_nCacheWrapper == default(NCacheWrapper))
                {
                    _nCacheWrapper = new NCacheWrapper(new MemoryCacheOptions());
                }
                return _nCacheWrapper;
            }
        }

        /// <summary>The default memory cache entry options.</summary>
        private static CachingOptions _defaultNCacheEntryOptions;

        /// <summary>The memory cache entry options factory.</summary>
        private static Func<CachingOptions> _NCacheEntryOptionsFactory;

        /// <summary>Gets or sets the default memory cache entry options to use when no policy is specified.</summary>
        /// <value>The default memory cache entry options to use when no policy is specified.</value>
        internal static CachingOptions DefaultNCacheEntryOptions
        {
            get
            {
                if (_defaultNCacheEntryOptions == null && NCacheEntryOptionsFactory != null)
                {
                    return NCacheEntryOptionsFactory();
                }

                return _defaultNCacheEntryOptions;
            }
            set
            {
                _defaultNCacheEntryOptions = value;
                _NCacheEntryOptionsFactory = null;
            }
        }

        /// <summary>Gets or sets the memory cache entry options factory.</summary>
        /// <value>The memory cache entry options factory.</value>
        internal static Func<CachingOptions> NCacheEntryOptionsFactory
        {
            get { return _NCacheEntryOptionsFactory; }
            set
            {
                _NCacheEntryOptionsFactory = value;
                _defaultNCacheEntryOptions = null;
            }
        }

        /// <summary>Gets or sets the cache prefix to use to create the cache key.</summary>
        /// <value>The cache prefix to use to create the cache key.</value>
        internal static string CachePrefix { get; set; }

        /// <summary>Gets or sets the cache key factory.</summary>
        /// <value>The cache key factory.</value>
        internal static Func<IQueryable, string[], string> CacheKeyFactory { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the connection in cache key should be included.
        /// </summary>
        /// <value>true if include connection in cache key, false if not.</value>
        internal static bool IncludeConnectionInCacheKey { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the UserName and Database in cache key should be included.
        ///     NOTE: It is ignored if IncludeConnectionInCacheKey is true
        /// </summary>
        /// <value>true if include UserName nad Databse in cache key, false if not.</value>
        internal static bool IncludeUserNameAndDatabase { get; set; }

        /// <summary>Gets the dictionary cache tags used to store tags and corresponding cached keys.</summary>
        /// <value>The cache tags used to store tags and corresponding cached keys.</value>
        internal static ConcurrentDictionary<string, List<string>> CacheTags { get; }

        /// <summary>
        ///     Gets or sets a value indicating whether this object use first tag as cache key.
        /// </summary>
        /// <value>true if use first tag as cache key, false if not.</value>
        internal static bool UseFirstTagAsCacheKey { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether this object use tag as cache key.
        /// </summary>
        /// <value>true if use tag as cache key, false if not.</value>
        internal static bool UseTagsAsCacheKey { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this object is command information optional for cache
        /// key.
        /// </summary>
        /// <value>
        /// True if this object is command information optional for cache key, false if not.
        /// </value>
        internal static bool IsCommandInfoOptionalForCacheKey { get; set; }

        /// <summary>Adds cache tags corresponding to a cached key in the CacheTags dictionary.</summary>
        /// <param name="cacheKey">The cache key.</param>
        /// <param name="tags">A variable-length parameters list containing tags corresponding to the <paramref name="cacheKey" />.</param>
        internal static void AddCacheTag(string cacheKey, params string[] tags)
        {
            foreach (var tag in tags)
            {
                CacheTags.AddOrUpdate(CachePrefix + tag, x => new List<string> { cacheKey }, (x, list) =>
                  {
                      if (!list.Contains(cacheKey))
                      {
                          list.Add(cacheKey);
                      }

                      return list;
                  });
            }
        }

        /// <summary>Gets cached keys used to cache or retrieve a query from the QueryCacheManager.</summary>
        /// <param name="query">The query to cache or retrieve from the QueryCacheManager.</param>
        /// <param name="tags">A variable-length parameters list containing tags to create the cache key.</param>
        /// <returns>The cache key used to cache or retrieve a query from the QueryCacheManager.</returns>
        internal static string GetQueryCacheKey(IQueryable query,string tag)
        {
            if(tag!=null)
            {
                UseFirstTagAsCacheKey = true;
            }
            if (CacheKeyFactory != null)
            {
                var cacheKey = CacheKeyFactory(query, new string[] { tag });

                if (!string.IsNullOrEmpty(cacheKey))
                {
                    return cacheKey;
                }
            }

            var sb = new StringBuilder();
            RelationalQueryContext queryContext = null;

            query.CreateCommand(out queryContext);

            sb.Append(CachePrefix);

            if (IncludeConnectionInCacheKey)
            {
                sb.Append(GetConnectionStringForCacheKey(queryContext));
            }

            if (!IncludeConnectionInCacheKey && IncludeUserNameAndDatabase)
            {
                sb.Append(GetUserNameAndDatabaseForCacheKey(queryContext));
                sb.Append(';');
            }

            if (UseFirstTagAsCacheKey)
            {
                if (string.IsNullOrEmpty(tag))
                {
                    throw new Exception(ExceptionMessage.QueryCache_FirstTagNullOrEmpty);
                }
                UseFirstTagAsCacheKey = false;
                sb.Append("$QId$"+tag);
                return sb.ToString();
            }

            sb.Append(query.ElementType.FullName);

            sb.Append(ExtensionMethods.ToStringWithoutAlias(query.Expression));


            foreach (var parameter in queryContext.ParameterValues)
            {
                sb.Append(parameter.Key);
                sb.Append(",");
                sb.Append(parameter.Value);
                sb.Append(";");
            }

            return sb.ToString();
        }



        internal static string GetConnectionStringForCacheKey(IQueryable query)
        {
            RelationalQueryContext queryContext;
            query.CreateCommand(out queryContext);
            return GetConnectionStringForCacheKey(queryContext);
        }

        internal static string GetConnectionStringForCacheKey(RelationalQueryContext queryContext)
        {
            var connection = queryContext.Connection.DbConnection;

            string connectionStringWithoutPassword = "";
            // Remove the password from the connection string
            {
                if (connection.ConnectionString != null)
                {
                    var list = new List<string>();

                    var keyValues = connection.ConnectionString.Split(';');

                    foreach (var keyValue in keyValues)
                    {
                        if (!string.IsNullOrEmpty(keyValue))
                        {
                            var key = keyValue.Split('=')[0].Trim().ToLowerInvariant();

                            if (key != "password" && key != "pwd")
                            {
                                list.Add(keyValue);
                            }
                        }
                    }

                    connectionStringWithoutPassword = string.Join(",", list);
                }
            }

            // FORCE database name in case "ChangeDatabase()" method is used
            var connectionString = string.Concat(connection.DataSource ?? "",
                Environment.NewLine,
                connection.Database ?? "",
                Environment.NewLine,
                connectionStringWithoutPassword ?? "");
            return connectionString;
        }

        internal static string GetUserNameAndDatabaseForCacheKey(RelationalQueryContext queryContext)
        {
            var connection = queryContext.Connection.DbConnection;

            string connectionStringWithoutPassword = "";
            // Remove the password from the connection string
            {
                if (connection.ConnectionString != null)
                {
                    var list = new List<string>();

                    var keyValues = connection.ConnectionString.Split(';');

                    foreach (var keyValue in keyValues)
                    {
                        if (!string.IsNullOrEmpty(keyValue))
                        {
                            var key = keyValue.Split('=')[0].Trim().ToLowerInvariant();

                            if (key == "database" || key == "initial catalog")
                            {
                                list.Add(keyValue);
                            }
                            else if (key == "user id")
                            {
                                list.Add(keyValue);
                            }
                            else if (key == "integrated security")
                            {
                                var value = keyValue.Split('=')[1].Trim().ToLowerInvariant();
                                if (value == "true")
                                {
                                    list.Add("Integrated Security = " + (Environment.UserDomainName + @"\" + Environment.UserName));
                                }
                            }
                        }
                    }

                    connectionStringWithoutPassword = string.Join(",", list);
                }
            }

            return connectionStringWithoutPassword;
        }


        /// <summary>Gets cached keys used to cache or retrieve a query from the QueryCacheManager.</summary>
        /// <typeparam name="T">Generic type parameter.</typeparam>
        /// <param name="query">The query to cache or retrieve from the QueryCacheManager.</param>
        /// <param name="tags">A variable-length parameters list containing tags to create the cache key.</param>
        /// <returns>The cache key used to cache or retrieve a query from the QueryCacheManager.</returns>
        internal static string GetCacheKey<T>(QueryDeferred<T> query, string tag)
        {
            return GetQueryCacheKey(query.Query, tag);
        }
    }
}