using Alachisoft.NCache.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    public class TokenBucketLimiterOptions : RateLimiterOptions
    {
        public int TokenLimit { get; set; }
        public int TokensPerPeriod { get; set; }
        public TimeSpan ReplenishmentPeriod { get; set; }
        public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
