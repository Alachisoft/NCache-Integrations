using Alachisoft.NCache.Client;
using System;

namespace NCache.OSS.Dapper
{
    public interface INCacheCacheProvider : IDisposable
    {
        ICache Cache { get; }
        T? Get<T>(string key);
        void Set<T>(string key, T value, CacheOptions? options = null);
        void Remove(string key);
    }
}