using Medallion.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Locks
{
    public abstract class NCacheDistributedLockCommon
    {
        protected NCacheDistributedLockCommon() { }

        protected IDistributedSynchronizationHandle AcquireSetup(Func<IDistributedSynchronizationHandle?> AcquireStrategy, TimeSpan? timeSpan = null, CancellationToken cts = default)
        {
            var now = DateTime.Now;
            DateTime? expiryTime = timeSpan != null ? now.Add(timeSpan.Value) : null;

            while(expiryTime == null || expiryTime.Value > DateTime.Now)
            {

                cts.ThrowIfCancellationRequested();

                IDistributedSynchronizationHandle? lockHandle = AcquireStrategy();
                
                if (lockHandle != null)
                {
                    return lockHandle;
                }

                cts.ThrowIfCancellationRequested();
                // wait insteading of flooding cache with requests.
                Thread.Sleep(200);
            }

            throw new TimeoutException();
        }

        /// <summary>
        /// Attempts the given acquisition strategy, retrying until it succeeds or the given timeout elapses.
        /// Returns null instead of throwing if the lock could not be acquired in time.
        /// This is the single shared implementation used by both the "Acquire" and "TryAcquire" style APIs -
        /// AcquireSetup simply turns a null result from this method into a TimeoutException.
        /// </summary>
        protected IDistributedSynchronizationHandle? TryAcquireSetup(Func<IDistributedSynchronizationHandle?> AcquireStrategy, TimeSpan? timeSpan = null, CancellationToken cts = default)
        {
            var now = DateTime.Now;
            DateTime? expiryTime = timeSpan != null ? now.Add(timeSpan.Value) : null;

            do
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }

                IDistributedSynchronizationHandle? lockHandle = AcquireStrategy();

                if (lockHandle != null)
                {
                    return lockHandle;
                }

                if (expiryTime != null && DateTime.Now >= expiryTime.Value)
                {
                    break;
                }


                if (cts.IsCancellationRequested)
                {
                    break;
                }

                //wait insteading of flooding cache with requests.
                Thread.Sleep(200);
            } while (expiryTime == null || expiryTime.Value > DateTime.Now);

            return null;
        }
    }
}
