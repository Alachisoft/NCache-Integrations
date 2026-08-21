using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal class TokenBucketResponse
    {
        internal bool Allowed { get; set; }
        internal long Count { get; set; }
        internal int RetryAfter { get; set; }
    }
}
