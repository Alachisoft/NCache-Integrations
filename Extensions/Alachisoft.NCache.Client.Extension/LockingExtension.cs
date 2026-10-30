using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Exceptions;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Alachisoft.NCache.Client.Extension
{
    public static class LockingExtension
    {
        public static bool LockKey(this ICache cache, string key, out LockToken? lockToken, TimeSpan? expirationTime = null)
        {
            if (String.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("Key cannot be null");
            }

            var lockKey = Constants.LOCK_KEY_PREFIX + key;
            lockToken = null;

            if (!cache.Contains(key))
                return false;

            if (cache.Contains(lockKey))
                return false;

            try
            {
                var tempLockToken = new LockToken();

                CacheItem item = new CacheItem(tempLockToken);

                if (expirationTime != null)
                    item.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(ExpirationType.Absolute, expirationTime.Value);

                // Adds a lock object against the lock-key in cache
                cache.Add(lockKey, item);

                // Three Cases are Possible Here
                // 1. Lock Handle is same as lock handle added by this process
                // 2. Lock Handle is different but cache didn't throw exception [Exception not thrown case]
                // 3. Lock Handle is not even added yet [probably it will be from an other proccess not this one] [Exception not thrown case]
                List<string> keys = new List<string>();
                keys.Add(key); // Getting key to confirm that key exists against which lock is being taken
                keys.Add(lockKey); // Getting lock to confirm that lockhandle stored is same as lock handle added by this process.
                var keysStored = cache.GetBulk<object>(keys);

                LockToken storedLock = null;

                // Incase lockhandle doesn't exist or it's not equal to lock handle add by this process [ which means it followed 2 or 3 case mentioned above]
                if (!keysStored.ContainsKey(lockKey) || !tempLockToken.IsEqual((LockToken)keysStored[lockKey]))
                {
                    return false;
                }

                // Incase lockkey is created against a key which doesn't exist, still null will be returned.
                if (!keysStored.ContainsKey(key))
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

        public static bool IsCorrectLockToken(this ICache cache, string key, LockToken token)
        {
            var lockKey = Constants.LOCK_KEY_PREFIX + key;
            var tempLockToken = cache.Get<LockToken>(lockKey);
            if (tempLockToken != null && tempLockToken.IsEqual(token))
            {
                return true;
            }

            return false;

        }

    }
}
