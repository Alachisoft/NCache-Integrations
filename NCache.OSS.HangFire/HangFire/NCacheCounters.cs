
namespace NCache.OSS.Hangfire
{
    public static class NCacheCounters
    {
        public static long GetValue(NCacheStorage storage, string name)
        {
            var cache = storage.Cache;
            var counterKey = NCacheKeys.Counter(storage.Prefix, name);
            return cache.Contains(counterKey) ? cache.Get<long>(counterKey) : 0;
        }
    }
}