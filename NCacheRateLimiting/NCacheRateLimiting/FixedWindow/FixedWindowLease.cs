using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace NCache.OSS.RateLimiting
{
    internal sealed class FixedWindowLease : RateLimitLease
    {
        private static readonly string[] s_allMetadataNames =
        {
                "Limit",
                "Remaining",
                MetadataName.RetryAfter.Name,
                "Reset"
            };

        private readonly FixedWindowLeaseContext? _context;

        public FixedWindowLease(bool isAcquired, FixedWindowLeaseContext? context)
        {
            IsAcquired = isAcquired;
            _context = context;
        }

        public override bool IsAcquired
        {
            get;
        }

        public override IEnumerable<string> MetadataNames => s_allMetadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_context is null)
            {
                metadata = null;
                return false;
            }

            if (metadataName == "Limit")
            {
                metadata = _context.Limit.ToString();

                return true;
            }

            if (metadataName == "Remaining")
            {
                metadata = Math.Max(_context.Limit - _context.Count, 0);

                return true;
            }

            if (metadataName == MetadataName.RetryAfter.Name && _context.RetryAfter is not null)
            {
                metadata = _context.RetryAfter.Value;
                return true;
            }

            if (metadataName == "Reset" && _context.ExpiresAt is not null)
            {
                metadata = _context.ExpiresAt.Value;
                return true;
            }

            metadata = null;

            return false;
        }
    }
}