using Alachisoft.NCache.Client;
using Alachisoft.NCache.Client.Extension;
using NCache.DistributedLock.Common;
using NCache.DistributedLock.Primitives;
using Medallion.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.SynchronizationHandles
{
    //internal class NCacheDistributedUpgradableReaderSynchronizationHandle : IDistributedLockUpgradeableHandle
    //{
    //    public CancellationToken HandleLostToken => throw new NotImplementedException();


    //    private string _readersKey;
    //    DistributedLockAcquisition _lockAcquisition;
    //    ICache _cache;
    //    private bool isUpgradedToWriteLock = false;
    //    public NCacheDistributedUpgradableReaderSynchronizationHandle(string readersKey, ICache cache, DistributedLockAcquisition lockAcquisition)
    //    {
    //        _readersKey = readersKey;
    //        _lockAcquisition = lockAcquisition;
    //        _cache = cache;
    //    }
    //    public void Dispose()
    //    {
    //        while (true)
    //        {
    //            var isLocked = _cache.LockKey(_readersKey, out var lockToken, _expirationTime);

    //            if (!isLocked)
    //            {
    //                continue;
    //            }

    //            NCacheReadersPrimitive readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);

    //            if (readersPrimitive == null)
    //            {
    //                _cache.UnlockKey(_readersKey, lockToken);
    //                throw new InvalidOperationException("readers primitive not found in cache.");
    //            }

    //            var isRemoved = readersPrimitive.RemoveReader(_lockAcquisition);

    //            if (!isRemoved)
    //            {
    //                _cache.UnlockKey(_readersKey, lockToken);
    //                break;
    //            }

    //            _cache.Insert(_readersKey, readersPrimitive);

    //            _cache.Remove(_lockAcquisition.GetKey());

    //            _cache.UnlockKey(_readersKey, lockToken);

    //            break;
    //        }
    //    }

    //    public ValueTask DisposeAsync()
    //    {
    //        return new ValueTask(Task.Run(() => Dispose()));
    //    }

    //    public bool TryUpgradeToWriteLock(TimeSpan timeout = default, CancellationToken cancellationToken = default)
    //    {
    //        throw new NotImplementedException();
    //    }

    //    public ValueTask<bool> TryUpgradeToWriteLockAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
    //    {
    //        throw new NotImplementedException();
    //    }

    //    public void UpgradeToWriteLock(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    //    {
    //        var now = DateTime.Now;
    //        DateTime? expiryTime = timeout != null ? now.Add(timeout.Value) : null;

    //        while (expiryTime == null || expiryTime.Value > DateTime.Now)
    //        {
    //            var timeLeft = expiryTime != null ? expiryTime.Value - DateTime.Now : TimeSpan.MaxValue;
    //            bool isUpgradedToWriteLock = UpgradeStrategy(timeLeft);

    //            if (isUpgradedToWriteLock)
    //            {
    //                return;
    //            }

    //            // wait insteading of flooding cache with requests.
    //            Thread.Sleep(200);
    //        }

    //        throw new TimeoutException();
    //    }

    //    public ValueTask UpgradeToWriteLockAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    //    {
    //        throw new NotImplementedException();
    //    }

    //    private bool UpgradeStrategy(TimeSpan timeLeft)
    //    {
    //        if (isUpgradedToWriteLock)
    //        {
    //            throw new InvalidOperationException("Already upgraded to write lock.");
    //        }

    //        // Try to acquire the lock on the readers primitive
    //        var isLocked = _cache.LockKey(_readersKey, out var lockToken, _expirationTime);
    //        if (!isLocked)
    //        {
    //            return false;
    //        }
    //        NCacheReadersPrimitive readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);
    //        if (readersPrimitive == null)
    //        {
    //            _cache.UnlockKey(_readersKey, lockToken);
    //            throw new InvalidOperationException("readers primitive not found in cache.");
    //        }

    //        //Try to check if there is a writer trying to acquire the lock, if so, we cannot upgrade to write lock.
    //        var writer = readersPrimitive.GetWriter();
    //        if (writer != null)
    //        {
    //            throw new InvalidOperationException("Cannot upgrade to write lock, there is already a upgraded writer.");
    //        }

    //        // Get the Reader
    //        var reader = readersPrimitive.GetReader(_lockAcquisition);
    //        reader.Upgrade();
    //        reader.Expiry = DateTime.Now.Add(timeLeft); // Renew the expiry time for the upgraded lock.

    //        _cache.Insert(_readersKey, readersPrimitive);
    //        // Now wait for other readers to finish, we can do this by checking if the readers list has only one reader (the current one).

    //        while ()

    //        _cache.Insert(_readersKey, readersPrimitive);
    //        _cache.Remove(_lockAcquisition.GetKey());
    //        _cache.UnlockKey(_readersKey, lockToken);
    //        isUpgradedToWriteLock = true;
    //        return true;
    //    }
    //}
    internal class NCacheDistributedReaderSynchronizationHandle : IDistributedSynchronizationHandle
    {
        private readonly LockLossMonitor _lossMonitor;

        public CancellationToken HandleLostToken => _lossMonitor.Token;

        private string _readersKey;
        DistributedLockAcquisition _lockAcquisition;
        ICache _cache;
        private TimeSpan _expirationTime;
        public NCacheDistributedReaderSynchronizationHandle(string readersKey, ICache cache, DistributedLockAcquisition lockAcquisition, TimeSpan expirationTime)
        {
            _readersKey = readersKey;
            _lockAcquisition = lockAcquisition;
            _cache = cache;
            _expirationTime = expirationTime;

            _lossMonitor = new LockLossMonitor(expirationTime, IsStillHeld);
        }

        private bool IsStillHeld()
        {
            // Each reader has its own individual acquisition record (written alongside the
            // shared readers list at acquire time); its presence is a cheap way to confirm this
            // reader slot is still held without taking the readers list's key-lock.
            return _cache.Get<DistributedLockAcquisition>(_lockAcquisition.GetKey()) != null;
        }

        public void Dispose()
        {
            _lossMonitor.Dispose();

            while (true)
            {
                var isLocked = _cache.LockKey(_readersKey, out var lockToken, _expirationTime);

                if (!isLocked)
                {
                    continue;
                }

                NCacheReadersPrimitive readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);

                if (readersPrimitive == null)
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                    throw new InvalidOperationException("readers primitive not found in cache.");
                }

                var isRemoved = readersPrimitive.RemoveReader(_lockAcquisition);

                if (!isRemoved)
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                    break;
                }

                _cache.Insert(_readersKey, readersPrimitive);

                _cache.Remove(_lockAcquisition.GetKey());

                _cache.UnlockKey(_readersKey, lockToken);

                break;
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.Run(() => Dispose()));
        }
    }
}
