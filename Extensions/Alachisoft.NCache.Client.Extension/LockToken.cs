using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alachisoft.NCache.Client.Extension
{
    [Serializable]
    public class LockToken
    {
        public Guid Id { get; set; }

        public LockToken()
        {
            Id = Guid.NewGuid();
        }

        public bool IsEqual(LockToken compLockToken)
        {
            return this.Id.Equals(compLockToken.Id);
        }
    }
}
