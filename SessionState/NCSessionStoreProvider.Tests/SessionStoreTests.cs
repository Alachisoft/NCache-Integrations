using Alachisoft.NCache.Web.SessionState;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.Web.SessionState;
using Xunit;

namespace NCache.SessionStoreProvider.Tests
{
    // Minimal stand-in for the ASP.NET request context the provider normally receives.
    internal class FakeContext : IAspEnvironmentContext
    {
        private readonly Hashtable _items = new Hashtable();
        private string _locationCookie;

        public object Unwrap() => null;
        public object GetItem(object key) => _items[key];
        public void StoreItem(object key, object value) => _items[key] = value;
        public bool ContainsItem(object key) => _items.ContainsKey(key);
        public void RemoveItem(object key) => _items.Remove(key);
        public object this[object key] { get => _items[key]; set => _items[key] = value; }
        public string GetLocationCookie() => _locationCookie;
        public void SetLocationCookie(string cookieValue) => _locationCookie = cookieValue;
        public void RemoveLocationCookie() => _locationCookie = null;
        public void FinalizeContext() { }
    }

    public class SessionStoreTests
    {
        private const int TimeoutMinutes = 20;

        private static SessionStore CreateStore()
        {
            var store = new SessionStore();
            store.Initialize("NCacheSession", new NameValueCollection
            {
                { "cacheName", "defaultCache" },
                { "sessionAppId", "session-store-tests" },
                { "enableSessionLocking", "false" }
            });
            return store;
        }

        private static string NewSessionId() => Guid.NewGuid().ToString("N");

        private static void Save(SessionStore store, FakeContext context, string id, string value)
        {
            var data = new SessionStateStoreData(new SessionStateItemCollection(), null, TimeoutMinutes);
            data.Items["name"] = value;
            store.SetAndReleaseItemExclusive(context, id, data, null, true, TimeoutMinutes);
        }

        private static SessionStateStoreData Load(SessionStore store, FakeContext context, string id)
        {
            return (SessionStateStoreData)store.GetItem(context, id, out _, out _, out _, out _);
        }

        [Fact]
        public void SavedSession_CanBeReadBack()
        {
            var store = CreateStore();
            var context = new FakeContext();
            var id = NewSessionId();

            Save(store, context, id, "alice");

            Assert.Equal("alice", Load(store, context, id).Items["name"]);
        }

        [Fact]
        public void SavingAgain_OverwritesSessionValues()
        {
            var store = CreateStore();
            var context = new FakeContext();
            var id = NewSessionId();
            Save(store, context, id, "first");

            Save(store, context, id, "second");

            Assert.Equal("second", Load(store, context, id).Items["name"]);
        }

        [Fact]
        public void UnknownSession_ReturnsNothing()
        {
            var store = CreateStore();

            Assert.Null(Load(store, new FakeContext(), NewSessionId()));
        }

        [Fact]
        public void RemovedSession_IsGone()
        {
            var store = CreateStore();
            var context = new FakeContext();
            var id = NewSessionId();
            Save(store, context, id, "alice");

            store.RemoveItem(context, id, null);

            Assert.Null(Load(store, context, id));
        }
    }
}
