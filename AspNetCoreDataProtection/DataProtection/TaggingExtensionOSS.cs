using Alachisoft.NCache.Caching;
using Alachisoft.NCache.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Alachisoft.NCache.Client.Extension;
using Alachisoft.NCache.Runtime.Caching;
namespace Alachisoft.NCache.AspNetCore.DataProtection
{
    internal static class TaggingExtensionOSS
    {
        public static void AddUsingTag(this ICache cache, string key, CacheItem item, string tag)
        {
            List<string> tags = cache.Get<List<string>>(tag);

            if(tags == null)
            {
                try
                {
                    CacheItem tagItem = new CacheItem(new List<string>());
                    //Keeping Expiration None cause
                    //If Cache Item Expiration is Absolute and we keep Sliding Expiration for tags it works fine,
                    //But when CacheItem Expiration is Sliding and we can either update tagItem each time we change the object and have a reference of it in cacheItem.
                    //or we can simply keep expirationType.None for tags.
                    tagItem.Expiration = new Expiration(ExpirationType.None);
                    cache.Add(tag, tagItem);
                }
                catch (Exception ex)
                {
                    //No need to cater this, if this happenned it probably means some other process has already made empty list before us.
                }
            }

            while (true)
            {
                bool locked = cache.LockKey(tag, out var lockToken);

                if (!locked)
                {
                    continue;
                }

                tags = cache.Get<List<string>>(tag);

                try
                {
                    if(tags == null)
                    {
                        tags = new List<string>();
                    }

                    tags.Add(key);

                    cache.Insert(key, item);

                    cache.Insert(tag, tags);
                }
                finally
                {
                    cache.UnlockKey(tag, lockToken);
                }
                break;
            }
        }

        public static Dictionary<string, T> GetUsingTag<T>(this ICache cache, string tag)
        {
            var dictResult = new Dictionary<string, T>();
            var keys = cache.Get<List<string>>(tag);

            if (keys == null || keys.Count == 0)
                return dictResult;

            dictResult = (Dictionary<string, T>)cache.GetBulk<T>(keys);

            return dictResult;
        }

        public static void RemoveUsingTag(this ICache cache, string tag)
        {
            LockHandle lockHandle = null;

            while (true)
            {
                try
                {
                    var keys = cache.Get<List<string>>(tag);


                    if (keys == null)
                        return;


                    keys = cache.Get<List<string>>(tag, true, TimeSpan.FromSeconds(10), ref lockHandle);

                    if (keys == null && lockHandle == null)
                        continue;

                    cache.RemoveBulk<object>(keys, out var removedKeys);

                    // Remove tag itself
                    cache.Remove(tag);
                }
                finally
                {
                    if (lockHandle != null)
                        cache.Unlock(tag, lockHandle);
                }
                break;
            }
           
        }
    }
}
