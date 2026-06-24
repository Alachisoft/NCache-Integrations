using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Alachisoft.NCache.Runtime.Caching;
using Alachisoft.NCache.Client;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public interface ICacheProvider : IDisposable
    {
        Task ConnectAsync(string cacheName, TraceSource trace);
        
        void Close();

        Task SubscribeAsync(string _eventKey, Action<int, NCacheMessage> OnMessage);

        Task PublishAsync(string key, byte[] messageArguments);

        ulong GetUniqueID();        

        event Action<Exception> CacheStopped;

    }
}
