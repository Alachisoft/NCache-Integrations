using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class FixedWindowResponse
    {
        public long Count { get; set; }

        public bool Allowed { get; set; }

        public long ExpiresAt { get; set; }

        public TimeSpan RetryAfter { get; set; }
    }
}
