using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class FixedWindowLeaseContext
    {
        public long Count
        {
            get;
            set;
        }

        public long Limit
        {
            get;
            set;
        }

        public TimeSpan Window
        {
            get;
            set;
        }

        public TimeSpan? RetryAfter
        {
            get;
            set;
        }

        public long? ExpiresAt
        {
            get;
            set;
        }
    }
}
