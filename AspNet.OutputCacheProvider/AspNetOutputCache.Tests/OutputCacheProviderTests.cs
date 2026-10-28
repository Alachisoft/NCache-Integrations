using Alachisoft.NCache.OutputCacheProvider;
using System;
using System.Collections.Specialized;
using System.Threading;
using Xunit;

namespace NCache.AspNetOutputCache.Tests
{
    public class OutputCacheProviderTests
    {
        private static NOutputCacheProvider CreateProvider()
        {
            var provider = new NOutputCacheProvider();
            provider.Initialize("NCacheOutputCache", new NameValueCollection { { "cacheName", "defaultCache" } });
            return provider;
        }

        private static string Key() => "oc-" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Add_StoresEntry_AndGetReturnsIt()
        {
            var provider = CreateProvider();
            var key = Key();

            provider.Add(key, "page content", DateTime.UtcNow.AddMinutes(1));

            Assert.Equal("page content", provider.Get(key));
        }

        [Fact]
        public void Add_ExistingKey_ReturnsExistingEntry()
        {
            var provider = CreateProvider();
            var key = Key();
            provider.Add(key, "first", DateTime.UtcNow.AddMinutes(1));

            var result = provider.Add(key, "second", DateTime.UtcNow.AddMinutes(1));

            Assert.Equal("first", result);
            Assert.Equal("first", provider.Get(key));
        }

        [Fact]
        public void Set_OverwritesEntry()
        {
            var provider = CreateProvider();
            var key = Key();
            provider.Set(key, "old", DateTime.UtcNow.AddMinutes(1));

            provider.Set(key, "new", DateTime.UtcNow.AddMinutes(1));

            Assert.Equal("new", provider.Get(key));
        }

        [Fact]
        public void Remove_DeletesEntry()
        {
            var provider = CreateProvider();
            var key = Key();
            provider.Set(key, "value", DateTime.UtcNow.AddMinutes(1));

            provider.Remove(key);

            Assert.Null(provider.Get(key));
        }
    }
}
