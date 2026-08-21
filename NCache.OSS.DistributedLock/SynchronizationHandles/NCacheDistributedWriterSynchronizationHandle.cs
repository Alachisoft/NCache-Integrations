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
    internal class NCacheDistributedWriterSynchronizationHandle : IDistributedSynchronizationHandle
    {
        private readonly LockLossMonitor _lossMonitor;

        public CancellationToken HandleLostToken => _lossMonitor.Token;

        private string _writerKey;
        DistributedLockAcquisition _lockAcquisition;
        ICache _cache;
        public NCacheDistributedWriterSynchronizationHandle(string writerKey, ICache cache, DistributedLockAcquisition lockAcquisition, TimeSpan expirationTime)
        {
            _writerKey = writerKey;
            _lockAcquisition = lockAcquisition;
            _cache = cache;

            _lossMonitor = new LockLossMonitor(expirationTime, IsStillHeld);
        }

        private bool IsStillHeld()
        {
            // The writer slot has no individual per-acquisition record; ownership is confirmed
            // by checking that the shared writer-slot record still names this acquisition.
            var writerPrimitive = _cache.Get<NCacheWriterPrimitive>(_writerKey);
            return writerPrimitive != null && writerPrimitive.GetWriter().IsEqual(_lockAcquisition);
        }

        public void Dispose()
        {
            _lossMonitor.Dispose();

            var storedLockAcquisition = _cache.Get<NCacheWriterPrimitive>(_writerKey);

            if (storedLockAcquisition != null && storedLockAcquisition.GetWriter().IsEqual(_lockAcquisition))
            {
                _cache.Remove(_writerKey);
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.Run(() => Dispose()));
        }
    }
}
