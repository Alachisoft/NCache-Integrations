using Alachisoft.NCache.Caching;
using Alachisoft.NCache.Client;
using NCache.DistributedLock.Locks;
using Medallion.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NCache.DistributedLock.Locks;
using Alachisoft.NCache.Runtime.Caching;
using NCache.DistributedLock.Common;

namespace NCache.DistributedLock.Providers
{
    public sealed class NCacheDistributedSynchronizationProvider : IDistributedLockProvider, IDistributedSemaphoreProvider, IDistributedReaderWriterLockProvider
    {
        private readonly ICache _cache;
        private TimeSpan _expirationTime;
        public NCacheDistributedSynchronizationProvider( ICache cache, TimeSpan? expirationTime = null)
        {
            if(cache == null)
            {
                throw new ArgumentException("ICache instance cannot be null");
            }

            _cache = cache;

            //If Expiration time doesn't have value, it will be same as Default Expiration Time
            if (! expirationTime.HasValue)
            {
                _expirationTime = LockExpiration.Default_Expiration;
            }
            else
            {
                _expirationTime = expirationTime.Value;
            }
        }
        public IDistributedLock CreateLock(string name)
        {
            var distLock = new NCacheDistributedLock(name, _cache, _expirationTime);

            return distLock;
        }

        public IDistributedReaderWriterLock CreateReaderWriterLock(string name)
        {
            var distDistributedReaderWriterLock = new NCacheDistributedReaderWriterLock(name, _cache, _expirationTime);

            return distDistributedReaderWriterLock;
        }

        public IDistributedSemaphore CreateSemaphore(string name, int maxCount)
        {
            if(maxCount <= 0)
            {
                throw new ArgumentException("Semaphore count cannot be less than 1");
            }

           var disSemaphore = new NCacheDistributedSemaphore(name, maxCount, _cache, _expirationTime);
            return disSemaphore;
        }
    }
}
