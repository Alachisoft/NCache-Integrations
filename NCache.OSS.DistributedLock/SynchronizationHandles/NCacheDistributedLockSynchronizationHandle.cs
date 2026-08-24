using Alachisoft.NCache.Client;
using NCache.DistributedLock.Primitives;
using Medallion.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.SynchronizationHandles
{
    public class NCacheDistributedLockSynchronizationHandle : IDistributedSynchronizationHandle
    {
        private readonly LockLossMonitor _lossMonitor;

        public CancellationToken HandleLostToken => _lossMonitor.Token;

        private string _lockKey;
        DistributedLockAcquisition _lockAcquisition;
        ICache _cache;
        public NCacheDistributedLockSynchronizationHandle(string lockKey, ICache cache, DistributedLockAcquisition lockAcquisition, TimeSpan expirationTime)
        {
            _lockKey = lockKey;
            _lockAcquisition = lockAcquisition;
            _cache = cache;

            _lossMonitor = new LockLossMonitor(expirationTime, IsStillHeld);
        }

        private bool IsStillHeld()
        {
            var storedLockAcquisition = _cache.Get<DistributedLockAcquisition>(_lockKey);
            return storedLockAcquisition != null && storedLockAcquisition.IsEqual(_lockAcquisition);
        }

        public void Dispose()
        {
            _lossMonitor.Dispose();

            var storedLockAcquisition = _cache.Get<DistributedLockAcquisition>(_lockKey);

            if (storedLockAcquisition != null && storedLockAcquisition.IsEqual(_lockAcquisition))
            {
                _cache.Remove(_lockKey);
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.Run(() => Dispose()));
        }
    }
}
