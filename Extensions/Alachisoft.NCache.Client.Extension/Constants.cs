using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alachisoft.NCache.Client.Extension
{
    internal static class Constants
    {
        public static readonly TimeSpan DEFAULT_LOCK_EXPIRATION = TimeSpan.FromSeconds(15);
        public static readonly string LOCK_KEY_PREFIX = "NCacheWrapperLock:";
        public static readonly string KEY_ALREADY_EXISTS_EXCEPTION = "key already exists";
    }
}
