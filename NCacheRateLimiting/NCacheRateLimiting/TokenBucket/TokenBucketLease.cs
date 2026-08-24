using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class TokenBucketLease : RateLimitLease
    {
        private static readonly string[] s_allMetadataNames =
            { "Limit", "Remaining", MetadataName.RetryAfter.Name };
        private readonly TokenBucketLeaseContext? _context;

        public TokenBucketLease(bool isAcquired, TokenBucketLeaseContext? context)
        {
            IsAcquired = isAcquired;
            _context = context;
        }

        public override bool IsAcquired { get; }
        public override IEnumerable<string> MetadataNames => s_allMetadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_context is null)
            {
                metadata = default;
                return false;
            }

            // String matching for 'Limit'
            if (metadataName == "Limit")
            {
                metadata = _context.Limit.ToString();
                return true;
            }

            // String matching for 'Remaining'
            if (metadataName == "Remaining")
            {
                metadata = _context.Count;
                return true;
            }

            if (metadataName == MetadataName.RetryAfter.Name)
            {
                // Note: ASP.NET Core rate limiting middleware expects a TimeSpan object 
                // for the RetryAfter metadata to automatically generate the HTTP header
                metadata = TimeSpan.FromSeconds(_context.RetryAfter);
                return true;
            }

            metadata = default;
            return false;
        }
    }
}