using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    public sealed class
    FixedWindowLimiterOptions
    : RateLimiterOptions
    {
        public TimeSpan Window
        {
            get;
            set;
        } = TimeSpan.Zero;

        public int PermitLimit
        {
            get;
            set;
        }

        public TimeSpan LockTimeout
        {
            get;
            set;
        } = TimeSpan.FromSeconds(10);
    }
}
