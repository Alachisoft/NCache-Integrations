using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using NCache.DistributedLock.Common;
using NCache.DistributedLock.Primitives;
using NCache.DistributedLock.SynchronizationHandles;
using Medallion.Threading;
using NCache.DistributedLock.Locks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Locks
{

    public class NCacheDistributedSemaphore : NCacheDistributedLockCommon, IDistributedSemaphore
    {

        public string Name { get; }

        private int _count;
        public int MaxCount
        {
            get { return _count; }
        }

        private string _lockKey;
        private ICache _cache;
        private TimeSpan _expirationTime;

        public NCacheDistributedSemaphore(string name, int count, ICache cache, TimeSpan expirationTime)
        {
            if (cache == null)
            {
                throw new ArgumentException("ICache instance cannot be null");
            }

            if(count<= 0)
            {
                throw new ArgumentException("Semaphore count cannot be less than 1");
            }

            Name = name;
            _cache = cache;
            _lockKey = KeyGeneration.GetKey(KeyGeneration.LockType.SempahoreLock, name);
            _count = count;

            try
            {
                _cache.Add(_lockKey, new NCacheSemaphorePrimitive(count));
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {
                // It means that Item is already added, so primitive is already available in cache.
            }

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

        protected IDistributedSynchronizationHandle? AcquireStrategy()
        {
            try
            {
                var isLocked = _cache.LockKey(_lockKey, out var lockToken, _expirationTime);

                //If lock is not acquired, it means that semaphore is already locked by some other process.
                if (!isLocked)
                {
                    return null;
                }

                NCacheSemaphorePrimitive semaphorePrimitive = _cache.Get<NCacheSemaphorePrimitive>(_lockKey);

                if (semaphorePrimitive == null)
                {
                    _cache.UnlockKey(_lockKey, lockToken);
                    throw new Exception("Semaphore primitive is not available in cache.");
                }

                DistributedLockAcquisition lockHandle = new DistributedLockAcquisition(_expirationTime);

                var acquired = semaphorePrimitive.TryAcquire(lockHandle);

                if (acquired == false)
                {
                    var lockAcquisitionsToBeRemoved = new List<DistributedLockAcquisition>();

                    //Handling here the case of some processes crash, leaves some dangling semaphores//
                    foreach (var lockAcquisition in semaphorePrimitive.GetAcquisitions())
                    {
                        if (lockAcquisition.IsExpired())
                        {

                            var requiredLock = _cache.Get<DistributedLockAcquisition>(lockAcquisition.GetKey());

                            if (requiredLock == null)
                            {
                                lockAcquisitionsToBeRemoved.Add(lockAcquisition);
                            }
                        }
                    }

                    if (lockAcquisitionsToBeRemoved.Count > 0)
                    {
                        foreach (var lockAcquisition in lockAcquisitionsToBeRemoved)
                        {
                            semaphorePrimitive.Release(lockAcquisition);
                        }
                        lockHandle = new DistributedLockAcquisition(_expirationTime);
                        acquired = semaphorePrimitive.TryAcquire(lockHandle);
                    }
                }

                if (acquired == false)
                {
                    _cache.UnlockKey(_lockKey, lockToken);
                    return null;
                }

                //Update the primitive in cache after acquiring the lock
                _cache.Insert(_lockKey, semaphorePrimitive);

                //Add key against which lock is acquired, so that it can be used to release the lock later.
                var item = new CacheItem(lockHandle);
                item.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, _expirationTime);
                _cache.Add(lockHandle.GetKey(), item);

                var syncHandle = new NCacheDistributedSemaphoreSynchronizationHandle(_lockKey, _cache, lockHandle, _expirationTime);

                _cache.UnlockKey(_lockKey, lockToken);

                return syncHandle;
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {

            }

            return null;
        }
    }
}