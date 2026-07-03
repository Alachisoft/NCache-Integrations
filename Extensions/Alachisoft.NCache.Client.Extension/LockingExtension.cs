using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Exceptions;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Diagnostics;

namespace Alachisoft.NCache.Client.Extension
{
    public static class LockingExtension
    {
        public static bool LockKey(this ICache cache, string key, out LockToken? lockToken, TimeSpan? expirationTime = null)
        {
            if(String.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("Key cannot be null");
            }

            var lockKey = Constants.LOCK_KEY_PREFIX + key;
            lockToken = null;

            try
            {
                var tempLockToken = new LockToken();

                CacheItem item = new CacheItem(tempLockToken);

                if (expirationTime != null)
                    item.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(ExpirationType.Absolute, expirationTime.Value);
                
                cache.Add(lockKey, item);

                bool exists = cache.Contains(key);

                if (!exists)
                {
                    cache.Remove(lockKey);
                    return false;
                }

                lockToken = tempLockToken;
                return true;
            }
            catch (OperationFailedException ex)
            {
                if (ex.Message.ToLower().Contains(Constants.KEY_ALREADY_EXISTS_EXCEPTION))
                {
                    lockToken = cache.Get<LockToken>(lockKey);
                    return false;
                }
                else
                {
                    throw;
                }
            }
        }
        public static void UnlockKey(this ICache cache, string key, LockToken lockHandle)
        {
            var lockKey = Constants.LOCK_KEY_PREFIX + key;

            if (lockHandle == null)
            {
                cache.Remove(lockKey);
            }

            else
            {
                var tempLockToken = cache.Get<LockToken>(lockKey);

                if (tempLockToken != null && tempLockToken.IsEqual(lockHandle))
                {
                    cache.Remove(lockKey);
                }

            }

        }
    }
}
