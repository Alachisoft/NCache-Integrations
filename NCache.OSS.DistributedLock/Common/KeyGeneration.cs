using NCache.DistributedLock.Locks;
using Medallion.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Common
{
    internal static class KeyGeneration
    {

        private static readonly Dictionary<LockType, string> PrefixMap = new()
        {
            { LockType.DistributedLock, "ncache#distLock#" },
            { LockType.SempahoreLock, "ncache#distSemaphore#" },
            { LockType.ReaderLock, "ncache#distReaderLock#" },
            { LockType.WriterLock, "ncache#distWriterLock#" }
        };

        internal enum LockType
        {
            DistributedLock,
            SempahoreLock,
            ReaderLock,
            WriterLock
        }

        internal static string GetKey(LockType lockType, string lockName)
        {
            
            if (string.IsNullOrWhiteSpace(lockName))
                throw new ArgumentException("Lock name cannot be null or empty.", nameof(lockName));

            if (!PrefixMap.TryGetValue(lockType, out var prefix))
                throw new ArgumentOutOfRangeException(lockType.ToString(), "Invalid lock type.");

            return $"{prefix}{lockName}";
        }
    }
}
