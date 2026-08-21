using Alachisoft.NCache.Caching;
using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using Medallion.Threading;
using NCache.DistributedLock.Common;
using NCache.DistributedLock.Locks;
using NCache.DistributedLock.Primitives;
using NCache.DistributedLock.SynchronizationHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Locks
{
    public class NCacheDistributedLock : NCacheDistributedLockCommon, IDistributedLock
    {

        public string Name { get; }
        private string _lockKey;
        private ICache _cache;
        private TimeSpan _expirationTime;
        public NCacheDistributedLock(string name, ICache cache, TimeSpan expirationTime)
        {
            if (cache == null)
            {
                throw new ArgumentException("ICache instance cannot be null");
            }

            Name = name;
            _cache = cache;
            _lockKey = KeyGeneration.GetKey(KeyGeneration.LockType.DistributedLock, name);
            _expirationTime = expirationTime;
        }

        public IDistributedSynchronizationHandle Acquire(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return AcquireSetup(AcquireStrategy, timeout, cancellationToken);
        }

        public ValueTask<IDistributedSynchronizationHandle> AcquireAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle>(Task.Run(() => Acquire(timeout, cancellationToken), cancellationToken));
        }

        public IDistributedSynchronizationHandle? TryAcquire(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            return TryAcquireSetup(AcquireStrategy, timeout, cancellationToken);
        }

        public ValueTask<IDistributedSynchronizationHandle?> TryAcquireAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle?>(Task.Run(() => TryAcquire(timeout, cancellationToken), cancellationToken));
        }

        private IDistributedSynchronizationHandle? AcquireStrategy()
        {
            try
            {
                DistributedLockAcquisition lockHandle = new DistributedLockAcquisition(_expirationTime);

                var item = new CacheItem(lockHandle);

                item.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, _expirationTime);

                //Exception from this part will be handled below
                _cache.Add(_lockKey, item);

                var syncHandle = new NCacheDistributedLockSynchronizationHandle(_lockKey, _cache, lockHandle, _expirationTime);

                return syncHandle;
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {

            }

            return null;
        }
    }
}