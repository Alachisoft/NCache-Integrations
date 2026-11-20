using System;
using System.Reflection;
using CacheManager.Core;

namespace NCache.OSS.CacheManager.Core
{

    [Serializable]
    internal sealed class NCacheCacheItem
    {
        public NCacheCacheItem()
        {
        }

        public DateTime CreatedUtc { get; set; }
        public ExpirationMode ExpirationMode { get; set; }
        public TimeSpan ExpirationTimeout { get; set; }
        public string Key { get; set; }
        public DateTime LastAccessedUtc { get; set; }
        public string Region { get; set; }
        public bool UsesExpirationDefaults { get; set; }

    
        public object Value { get; set; }

        public static NCacheCacheItem From<T>(CacheItem<T> item)
        {
            return new NCacheCacheItem
            {
                Key = item.Key,
                Region = item.Region,
                CreatedUtc = item.CreatedUtc,
                LastAccessedUtc = item.LastAccessedUtc,
                ExpirationMode = item.ExpirationMode,
                ExpirationTimeout = item.ExpirationTimeout,
                UsesExpirationDefaults = item.UsesExpirationDefaults,
                Value = item.Value
            };
        }

        public CacheItem<T> Restore<T>()
        {
            // Public constructors and SerializerCacheItem.ToCacheItem reset the
            // resolved expiration when UsesExpirationDefaults is true. Restore
            // through CacheManager 3's full constructor to retain both exactly.
            return (CacheItem<T>)CacheItemConstructor<T>.Instance.Invoke(new object[]
            {
                Key, Region, Value, ExpirationMode, ExpirationTimeout,
                CreatedUtc, LastAccessedUtc, UsesExpirationDefaults
            });
        }

        private static class CacheItemConstructor<T>
        {
            internal static readonly ConstructorInfo Instance = typeof(CacheItem<T>).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[]
                {
                    typeof(string), typeof(string), typeof(T), typeof(ExpirationMode?),
                    typeof(TimeSpan?), typeof(DateTime?), typeof(DateTime?), typeof(bool)
                }, null) ?? throw new NotSupportedException("Unsupported CacheItem constructor.");
        }
    }
}
