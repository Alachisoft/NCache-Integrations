using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Primitives
{

    [Serializable]
    public class NCacheReadersPrimitive
    {
        public List<DistributedLockAcquisition> Readers { get; set;  }

        public NCacheReadersPrimitive()
        {
            Readers = new List<DistributedLockAcquisition>();
        }

        public List<DistributedLockAcquisition> GetReaders()
        {
            return Readers;
        }

        public void AddReader(DistributedLockAcquisition lockAcquisition)
        {
            Readers.Add(lockAcquisition);
        }

        public bool RemoveReader(DistributedLockAcquisition lockAcquisition)
        {
            int removed = Readers.RemoveAll(a => a.IsEqual(lockAcquisition));
            return removed > 0;
        }

        public DistributedLockAcquisition GetReader(DistributedLockAcquisition lockAcquisition)
        {
            return Readers.FirstOrDefault(a => a.IsEqual(lockAcquisition));
        }

        public DistributedLockAcquisition GetWriter()
        {
            return Readers.FirstOrDefault(a => a.IsUpgraded);
        }
    }

    public enum WriterStatus
    {
        Waiting,
        Writing
    }

    [Serializable]
    public class NCacheWriterPrimitive
    {
        public DistributedLockAcquisition Writer { get; set; }
        public WriterStatus Status { set; get; }

        public NCacheWriterPrimitive(DistributedLockAcquisition lockAcquisition, WriterStatus status)
        {
            Writer = lockAcquisition;
            Status = status;
        }

        public DistributedLockAcquisition GetWriter()
        {
            return Writer;
        }

        public WriterStatus GetStatus()
        {
            return Status;
        }
    }
}
