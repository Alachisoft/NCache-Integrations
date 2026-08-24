using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Primitives
{
    [Serializable]
    public class DistributedLockAcquisition
    {
        public Guid OwnershipId { get; set; }

        public DateTime Expiry { get; set; }

        public bool IsUpgraded { get; set; } = false;

        // Parameterless constructor required by many serializers
        public DistributedLockAcquisition()
        {
        }

        public DistributedLockAcquisition(TimeSpan expiresIn)
        {
            OwnershipId = Guid.NewGuid();
            Expiry = DateTime.UtcNow.Add(expiresIn);
        }

        public string GetKey()
        {
            return OwnershipId.ToString();
        }

        public bool IsEqual(DistributedLockAcquisition other)
        {
            return OwnershipId.Equals(other.OwnershipId);
        }

        public bool IsEqual(string ownershipId)
        {
            return OwnershipId.ToString().Equals(ownershipId);
        }

        public bool IsExpired()
        {
            return DateTime.UtcNow > Expiry;
        }

        public void Upgrade()
        {
            IsUpgraded = true;
        }
    }
}
