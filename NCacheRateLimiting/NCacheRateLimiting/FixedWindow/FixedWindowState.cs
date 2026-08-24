using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    [Serializable]
    public sealed class FixedWindowState
    {
        public long Count { get; set; }

        public DateTime WindowExpiresUtc { get; set; }
    }
}
