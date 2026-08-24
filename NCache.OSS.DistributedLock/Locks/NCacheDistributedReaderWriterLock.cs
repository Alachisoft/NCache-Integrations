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
    public class NCacheDistributedReaderWriterLock : NCacheDistributedLockCommon, IDistributedReaderWriterLock
    {

        public string Name { get; }

        private string _readersKey;
        private string _writerKey;
        private ICache _cache;
        private TimeSpan _expirationTime;

        public NCacheDistributedReaderWriterLock(string name, ICache cache, TimeSpan expirationTime)
        {
            if (cache == null)
            {
                throw new ArgumentException("ICache instance cannot be null");
            }

            Name = name;
            _cache = cache;
            _readersKey = KeyGeneration.GetKey(KeyGeneration.LockType.ReaderLock, name);
            _writerKey = KeyGeneration.GetKey(KeyGeneration.LockType.WriterLock, name);

            try
            {
                _cache.Add(_readersKey, new NCacheReadersPrimitive());
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {
                // It means that Item is already added, so primitive is already available in cache.
            }

            _expirationTime = expirationTime;
        }


        public IDistributedSynchronizationHandle? TryAcquireReadLock(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            return TryAcquireSetup(ReaderAcquireStrategy, timeout, cancellationToken);
        }

        public IDistributedSynchronizationHandle AcquireReadLock(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return AcquireSetup(ReaderAcquireStrategy, timeout, cancellationToken);
        }

        public ValueTask<IDistributedSynchronizationHandle?> TryAcquireReadLockAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle?>(Task.Run(() => TryAcquireReadLock(timeout, cancellationToken), cancellationToken));
        }

        public ValueTask<IDistributedSynchronizationHandle> AcquireReadLockAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle>(Task.Run(() => AcquireReadLock(timeout, cancellationToken), cancellationToken));
        }

        public IDistributedSynchronizationHandle? TryAcquireWriteLock(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            DateTime? deadline = DateTime.UtcNow.Add(timeout);
            return TryAcquireSetup(() => WriterAcquireStrategy(deadline), timeout, cancellationToken);
        }

        public IDistributedSynchronizationHandle AcquireWriteLock(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            DateTime? deadline = null;
            if (timeout != null)
            {
                deadline = DateTime.UtcNow.Add(timeout.Value);
            }
            return AcquireSetup(() => WriterAcquireStrategy(deadline), timeout, cancellationToken);
        }

        public ValueTask<IDistributedSynchronizationHandle?> TryAcquireWriteLockAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle?>(Task.Run(() => TryAcquireWriteLock(timeout, cancellationToken), cancellationToken));
        }

        public ValueTask<IDistributedSynchronizationHandle> AcquireWriteLockAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IDistributedSynchronizationHandle>(Task.Run(() => AcquireWriteLock(timeout, cancellationToken), cancellationToken));
        }


        private IDistributedSynchronizationHandle? ReaderAcquireStrategy()
        {
            try
            {
                //1. Looks for Writer, if there is writer is found, fails.
                var writer = _cache.Get<NCacheWriterPrimitive>(_writerKey);

                if (writer != null)
                {
                    return null;
                }

                //2. Takes Lock on Readers
                var isLocked = _cache.LockKey(_readersKey, out var lockToken, _expirationTime);

                //If lock is not acquired, it means that readers is already locked by some other process.
                if (!isLocked)
                {
                    return null;
                }


                NCacheReadersPrimitive readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);

                if (readersPrimitive == null)
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                    throw new Exception("Readers Primitive cannot be null");
                }

                //Insert reader lock acquisition//
                DistributedLockAcquisition lockHandle = new DistributedLockAcquisition(_expirationTime);
                readersPrimitive.AddReader(lockHandle);

                try
                {

                    var item = new CacheItem(lockHandle);
                    item.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, _expirationTime);
                    _cache.Add(lockHandle.GetKey(), item);

                    _cache.Insert(_readersKey, readersPrimitive);
                }
                catch (Exception ex)
                {
                    throw new Exception("Exception occurred while adding Lock Handle");
                }
                finally
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                }

                var syncHandle = new NCacheDistributedReaderSynchronizationHandle(_readersKey, _cache, lockHandle, _expirationTime);

                return syncHandle;
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {

            }

            return null;
        }
        private IDistributedSynchronizationHandle? WriterAcquireStrategy(DateTime? timeoutDateTime)
        {
            try
            {
                var isLocked = _cache.LockKey(_readersKey, out var lockToken, _expirationTime);

                //If lock is not acquired, it means that readers is already locked by some other process.
                if (!isLocked)
                {
                    return null;
                }

                NCacheReadersPrimitive readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);

                if (readersPrimitive == null)
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                    throw new Exception("Readers Primitive cannot be null");
                }

                NCacheWriterPrimitive writerPrimitive;
                DistributedLockAcquisition lockHandle = new DistributedLockAcquisition(_expirationTime);
                //Here we decide whether writer will wait or not, if there are readers already acquired, then writer will wait for them to release the lock.
                if (readersPrimitive.GetReaders().Count > 0)
                {
                    writerPrimitive = new NCacheWriterPrimitive(lockHandle, WriterStatus.Waiting);
                }
                else
                {
                    writerPrimitive = new NCacheWriterPrimitive(lockHandle, WriterStatus.Writing);
                }

                var writerItem = new CacheItem(writerPrimitive);
                writerItem.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, _expirationTime);


                try
                {
                    _cache.Add(_writerKey, writerItem);
                }
                catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
                {
                    _cache.UnlockKey(_readersKey, lockToken);
                    return null;
                }

                _cache.UnlockKey(_readersKey, lockToken);

                if (writerPrimitive.GetStatus() == WriterStatus.Waiting)
                {
                    bool readersReleased = false;
                    while (timeoutDateTime != null && DateTime.UtcNow < timeoutDateTime)
                    {
                        readersPrimitive = _cache.Get<NCacheReadersPrimitive>(_readersKey);

                        //TODO: Add Code in case one of readers crash//
                        if (readersPrimitive.GetReaders().Count == 0)
                        {
                            readersReleased = true;
                        }
                    }

                    if (readersReleased)
                    {
                        writerPrimitive = new NCacheWriterPrimitive(lockHandle, WriterStatus.Writing);
                        var writerItemUpdated = new CacheItem(writerPrimitive);
                        writerItemUpdated.Expiration = new Alachisoft.NCache.Runtime.Caching.Expiration(Alachisoft.NCache.Runtime.Caching.ExpirationType.Absolute, _expirationTime);
                        _cache.Insert(_writerKey, writerItemUpdated);

                        var syncHandle = new NCacheDistributedWriterSynchronizationHandle(_writerKey, _cache, lockHandle, _expirationTime);
                        return syncHandle;
                    }
                    else
                    {
                        _cache.Remove(_writerKey);
                        return null;
                    }
                }
                else
                {
                    var syncHandle = new NCacheDistributedWriterSynchronizationHandle(_writerKey, _cache, lockHandle, _expirationTime);
                    return syncHandle;
                }
            }
            catch (Alachisoft.NCache.Runtime.Exceptions.OperationFailedException ex)
            {

            }

            return null;
        }
    }
}