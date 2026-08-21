using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Primitives
{
    [Serializable]
    internal class NCacheSemaphorePrimitive
    {
        public int Capacity { get; set; }

        public List<DistributedLockAcquisition> DistributedLockAcquisitions { get; set; }

        // Required by many serializers
        public NCacheSemaphorePrimitive()
        {
            DistributedLockAcquisitions = new List<DistributedLockAcquisition>();
        }

        public NCacheSemaphorePrimitive(int capacity)
        {
            Capacity = capacity;
            DistributedLockAcquisitions = new List<DistributedLockAcquisition>();
        }

        public List<DistributedLockAcquisition> GetAcquisitions()
        {
            return DistributedLockAcquisitions;
        }

        public bool TryAcquire(DistributedLockAcquisition lockAcquisition)
        {
            if (DistributedLockAcquisitions.Count < Capacity)
            {
                DistributedLockAcquisitions.Add(lockAcquisition);
                return true;
            }

            return false;
        }

        public bool Release(DistributedLockAcquisition lockAcquisition)
        {
            int removed = DistributedLockAcquisitions.RemoveAll(a => a.IsEqual(lockAcquisition));
            return removed > 0;
        }

        public bool RemoveAcquisition(string acquisitionName)
        {
            int removed = DistributedLockAcquisitions.RemoveAll(a => a.IsEqual(acquisitionName));
            return removed > 0;
        }
    }
}
