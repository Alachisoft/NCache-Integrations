using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching; 
using System;

namespace Alachisoft.NCache.EntityFrameworkCore.NCache
{
    internal class CachingOptionsUtil
    {
        internal static void CopyMetadata(ref CacheItem cacheItem, CachingOptions options)
        {
            Logger.Log(
                "Copying options '" + options.ToLog() + "' into cache item metadata.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );

            Expiration expiration;
            // Set Expiration
            if (options.ExpirationType == ExpirationType.Absolute)
            {
                expiration = new Expiration(Runtime.Caching.ExpirationType.Absolute, options.AbsoluteExpirationTime.Subtract(DateTime.Now));
            }
            else if (options.ExpirationType == ExpirationType.Sliding)
            {
                expiration = new Expiration(Runtime.Caching.ExpirationType.Sliding, options.SlidingExpirationTime);
            }
            else
            {
                expiration = new Expiration(Runtime.Caching.ExpirationType.None);
            }
            cacheItem.Expiration = expiration;
            // Set Priority
            cacheItem.Priority = options.Priority;
               
        }

        internal static CachingOptions ExtractKeyListOptions(CachingOptions options)
        {
            CachingOptions nOptions = (CachingOptions)options.Clone();
            nOptions.QueryIdentifier = null;
            nOptions.RemoveResync();
            return nOptions;
        }
    }
}