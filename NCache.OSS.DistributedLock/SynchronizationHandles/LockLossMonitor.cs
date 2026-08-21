using System;
using System.Threading;

namespace NCache.DistributedLock.SynchronizationHandles
{
    /// <summary>
    /// Watches an acquired lock / semaphore-slot / reader-slot / writer-slot in the background and
    /// cancels its token the moment the supplied ownership check reports the acquisition is no
    /// longer held (e.g. it expired in NCache, was overwritten by another holder, or the ownership
    /// check itself failed, e.g. due to a connectivity problem).
    ///
    /// This is a polling implementation: it re-checks ownership on a fixed interval rather than
    /// reacting to NCache change notifications, so detection latency is bounded by that interval,
    /// not instantaneous. A future version could replace this with NCache's own key-change
    /// notification API for near-instant detection instead of polling.
    /// </summary>
    internal sealed class LockLossMonitor : IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Timer _timer;
        private readonly Func<bool> _isStillHeld;
        private int _stopped;

        public CancellationToken Token => _cts.Token;

        public LockLossMonitor(TimeSpan expirationTime, Func<bool> isStillHeld)
        {
            _isStillHeld = isStillHeld;

            // Poll at roughly a third of the configured expiration window, clamped to a sane
            // range, so we get at least a couple of checks in before the record would expire
            // on its own, without polling absurdly fast for a very short expiration.
            var intervalMs = expirationTime.TotalMilliseconds / 3;
            intervalMs = Math.Max(500, Math.Min(5000, intervalMs));
            var interval = TimeSpan.FromMilliseconds(intervalMs);

            _timer = new Timer(_ => CheckOnce(), null, interval, interval);
        }

        private void CheckOnce()
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            bool stillHeld;
            try
            {
                stillHeld = _isStillHeld();
            }
            catch
            {
                // If we can't confirm ownership (e.g. a cache connectivity issue), the safest
                // assumption is that the lock can no longer be reliably considered held.
                stillHeld = false;
            }

            if (!stillHeld)
            {
                MarkLost();
            }
        }

        private void MarkLost()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _timer.Dispose();

                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// Stops monitoring. Called when the handle is disposed normally (i.e. the lock was
        /// released on purpose, not lost) so the background timer doesn't keep running.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _timer.Dispose();
            }

            _cts.Dispose();
        }
    }
}
