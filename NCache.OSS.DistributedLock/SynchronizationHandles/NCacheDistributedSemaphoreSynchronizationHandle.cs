using Alachisoft.NCache.Client;
using NCache.DistributedLock.Locks;
using NCache.DistributedLock.Primitives;
using Medallion.Threading;
using Alachisoft.NCache.Client.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NCache.DistributedLock.Common;

namespace NCache.DistributedLock.SynchronizationHandles
{
    public class NCacheDistributedSemaphoreSynchronizationHandle : IDistributedSynchronizationHandle
    {
        private readonly LockLossMonitor _lossMonitor;

        public CancellationToken HandleLostToken => _lossMonitor.Token;

        private string _lockKey;
        DistributedLockAcquisition _lockAcquisition;
        ICache _cache;
        private TimeSpan _expirationTime;
        public NCacheDistributedSemaphoreSynchronizationHandle(string lockKey, ICache cache, DistributedLockAcquisition lockAcquisition, TimeSpan expirationTime)
        {
            _lockKey = lockKey;
            _lockAcquisition = lockAcquisition;
            _cache = cache;
            _expirationTime = expirationTime;

            _lossMonitor = new LockLossMonitor(expirationTime, IsStillHeld);
        }

        private bool IsStillHeld()
        {
            // Each semaphore slot has its own individual acquisition record (written alongside
            // the shared holder list at acquire time); its presence is a cheap way to confirm
            // this slot is still held without taking the shared list's key-lock.
            return _cache.Get<DistributedLockAcquisition>(_lockAcquisition.GetKey()) != null;
        }

        public void Dispose()
        {
            _lossMonitor.Dispose();

            while (true)
            {
                var isLocked = _cache.LockKey(_lockKey, out var lockToken, _expirationTime);

                if (!isLocked)
                {
                    continue;
                }

                NCacheSemaphorePrimitive semaphorePrimitive = _cache.Get<NCacheSemaphorePrimitive>(_lockKey);

                if(semaphorePrimitive == null)
                {
                    _cache.UnlockKey(_lockKey, lockToken);
                    throw new InvalidOperationException("Semaphore primitive not found in cache.");
                }

                var isRemoved = semaphorePrimitive.Release(_lockAcquisition);

                if(!isRemoved)
                {
                    _cache.UnlockKey(_lockKey, lockToken);
                    break;
                }

                _cache.Insert(_lockKey, semaphorePrimitive);

                _cache.Remove(_lockAcquisition.GetKey());

                _cache.UnlockKey(_lockKey, lockToken);

                break;
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.Run(() => Dispose()));
        }
    }
}
