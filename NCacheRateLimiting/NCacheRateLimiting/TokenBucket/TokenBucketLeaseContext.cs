using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class TokenBucketLeaseContext
    {
        public long Count { get; set; }
        public long Limit { get; set; }
        public int RetryAfter { get; set; }
        public bool Allowed { get; set; }
    }
}
