using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.DistributedLock.Common
{
    internal static class LockExpiration
    {
        public static TimeSpan Default_Expiration = TimeSpan.FromSeconds(15);
    }
}
