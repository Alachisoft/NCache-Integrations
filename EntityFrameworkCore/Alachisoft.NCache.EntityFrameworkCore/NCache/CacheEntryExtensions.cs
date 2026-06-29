using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Text;

namespace Alachisoft.NCache.EntityFrameworkCore.NCache
{
    public static class CacheEntryExtensions
    {
        /// <summary>
        /// Applies the values of an existing <see cref="CachingOptions"/> to the entry.
        /// </summary>
        /// <param name="entry"></param>
        /// <param name="options"></param>
        internal static CacheEntry<T> SetOptions<T>(this CacheEntry<T> entry, CachingOptions options)
        {
            Logger.Log(
                "Setting options '" + options.ToLog() + "'.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            entry.ExpirationType = options.ExpirationType;
            entry.AbsoluteExpirationTime = options.AbsoluteExpirationTime;
            entry.SlidingExpirationTime = options.SlidingExpirationTime;
            entry.Priority = options.Priority;
            entry.QueryIDentifier = options.QueryIdentifier == null ? default(string) : options.QueryIdentifier.ToString();

            return entry;
        }
    }
}
