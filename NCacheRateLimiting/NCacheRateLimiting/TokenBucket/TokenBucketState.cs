using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    [Serializable]
    public class TokenBucketState
    {
        public double CurrentTokens { get; set; }
        public long LastRefreshedMs { get; set; }
    }
}
