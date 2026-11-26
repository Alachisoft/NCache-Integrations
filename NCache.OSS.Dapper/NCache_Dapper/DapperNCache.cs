using System;

namespace NCache.OSS.Dapper
{
    public static class DapperNCache
    {
        /// <summary>
        /// The globally registered cache provider.
        /// </summary>
        public static INCacheCacheProvider? CacheProvider { get; set; }

        public static TimeSpan? DefaultExpiration { get; set; }

        public static bool DefaultIsSliding { get; set; } = false;

        /// <summary>
        /// Optional hook invoked whenever a cache operation (Get/Set/Remove) throws while
        /// <see cref="CacheOptions.ErrorEnabled"/> is false, i.e. whenever a cache failure is
        /// being swallowed so the call can fail open to the database. Mirrors NCache's own
        /// behavior of writing these failures to the cache logs even when errorEnabled is off.
        /// If left unset, failures are written to <see cref="System.Diagnostics.Trace"/> instead.
        /// This handler is never allowed to throw: any exception it raises is swallowed so a
        /// broken logging hook can't take down the calling query.
        /// </summary>
        public static Action<Exception, string>? OnCacheError { get; set; }
    }
}
