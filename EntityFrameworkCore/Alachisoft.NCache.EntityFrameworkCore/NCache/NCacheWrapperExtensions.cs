using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Alachisoft.NCache.EntityFrameworkCore.NCache;
using Alachisoft.NCache.Client;

namespace Alachisoft.NCache.EntityFrameworkCore
{
    internal static class NCacheWrapperExtensions
    {
        internal static List<TItem> Set<TItem>(this NCacheWrapper cache, object key, Dictionary<string, TItem> value, CachingOptions options, StoreAs storingAs, bool throwError = false)
        {
            Logger.Log(
                "About to set values with options " + options.ToLog() + ", and StoringAs '" + storingAs + "'.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );

            // Add entities if stroing as SeparateEntities
            if (storingAs == StoreAs.SeparateEntities)
            {
                Logger.Log("Values are about to be set as separate entities.", Microsoft.Extensions.Logging.LogLevel.Trace);
                cache.Set(value.Keys.ToArray(), value.Values.ToArray(), options, storingAs, throwError);
                cache.Set(key, value.Keys.ToArray(), CachingOptionsUtil.ExtractKeyListOptions(options), storingAs, throwError);
            }
            // from here onwards is the enumerator logic and now it is being done in "else" after we have moved to tags based result set regeneration
            else
            {
                Logger.Log("Values are about to be set as collection.", Microsoft.Extensions.Logging.LogLevel.Trace);

                // Add query enumerator
                CacheEntry<IList<TItem>> entry = cache.CreateEntry<IList<TItem>>(key);

                // Setting options
                if (options != null)
                {
                    entry.SetOptions(options);
                }

                // Setting Value
                if (storingAs == StoreAs.Collection)
                {
                    entry.Value = value.Values.ToList();
                }

                // Mind that this is not the user specified option but the end storing methodology
                entry.StoredAs = storingAs; 

                cache.Set(key, entry, options, storingAs, throwError);
            }
            return value.Values.ToList();
        }

        internal static TItem Set<TItem>(this NCacheWrapper cache, object key, TItem value, CachingOptions options, StoreAs storingAs, bool throwError=false)
        {
            Logger.Log(
                "Setting item '" + value + "' against key '" + key + "'.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            Alachisoft.NCache.Client.CacheItem cacheItem = new Alachisoft.NCache.Client.CacheItem(value);
            CachingOptionsUtil.CopyMetadata( ref cacheItem, options);
            cache.Insert(key, cacheItem, throwError);
            return value;
        }

        internal static TItem[] Set<TItem>(this NCacheWrapper cache, object[] keys, TItem[] values, CachingOptions options,  StoreAs storingAs, bool throwError=false)
        {
            Logger.Log(
                "Setting items in bulk against respective keys.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );

            // CacheItem[] cacheItems = new CacheItem[values.Count()];
            int itemSize = NCacheConfiguration.BulkInsertChunkSize;
            int totalItems = values.Count();
            int totalChunks = (totalItems + itemSize - 1) / itemSize;

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                int startIndex = chunkIndex * itemSize;
                int lastIndex = Math.Min(startIndex + itemSize, totalItems);
                int chunkLength = lastIndex - startIndex;
                CacheItem[] cacheItems = new CacheItem[chunkLength];
                object[] Itemskey = new object[chunkLength];

                for (int i = 0; i < chunkLength; i++)
                {
                    cacheItems[i] = new CacheItem(values[startIndex + i]);
                    CachingOptionsUtil.CopyMetadata(ref cacheItems[i], options);
                    Itemskey[i] = keys[startIndex + i];
                }

                if (keys.Length > 0)
                {
                    cache.InsertBulk(keys, cacheItems, throwError);
                }
            }
            return values;
        }

        internal static TItem Set<TItem>(this NCacheWrapper cache, object key, TItem value, CachingOptions options, bool throwError=false)
        {
            Logger.Log(
                "Setting item '" + value + "' against key '" + key + "' with no DbDependency.",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            CacheItem cacheItem = new CacheItem(value);
            CachingOptionsUtil.CopyMetadata(ref cacheItem, options);
            cache.Insert(key, cacheItem, throwError);
            return value;
        } 

        internal static TItem SetAsCacheEntry<TItem>(this NCacheWrapper cache, object key, TItem value, CachingOptions options, bool throwError = false)
        {
            Logger.Log(
                "Setting CacheEntry '" + value + "' against key '" + key + "'",
                Microsoft.Extensions.Logging.LogLevel.Trace
            );
            CacheEntry<TItem> entry = cache.CreateEntry<TItem>(key);

            if (options != null)
            {
                entry.SetOptions(options);
            } 
            entry.Value = value;

            cache.Set(key, entry, options, options.StoreAs, throwError);
            return value;
        }
    }
}
