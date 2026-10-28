using Alachisoft.NCache.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Messaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NCache.SignalR.Tests
{
    public class NCacheProviderTests
    {
        private const string CacheName = "defaultCache";

        private static NCacheMessage NewMessage(ulong id, string key, string payload)
        {
            var message = new Message("test-source", key, new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)));
            return new NCacheMessage(id, new ScaleoutMessage(new List<Message> { message }));
        }

        private static async Task<NCacheProvider> ConnectAsync()
        {
            var provider = new NCacheProvider();
            await provider.ConnectAsync(CacheName, new TraceSource("ncache-signalr-tests"));
            return provider;
        }

        [Fact]
        public void NCacheMessage_RoundTripsThroughBytes()
        {
            var original = NewMessage(42, "hub.key", "hello");

            var copy = NCacheMessage.FromBytes(NCacheMessage.ToBytes(original), new TraceSource("t"));

            Assert.Equal(42UL, copy.Id);
            Assert.Equal("hub.key", copy.ScaleoutMessage.Messages[0].Key);
        }

        [Fact]
        public async Task PublishedMessage_IsReceivedBySubscriber()
        {
            var topic = "signalr-" + Guid.NewGuid().ToString("N");
            using (var server = await ConnectAsync())
            {
                var received = new TaskCompletionSource<NCacheMessage>();
                await server.SubscribeAsync(topic, (stream, msg) => received.TrySetResult(msg));

                await server.PublishAsync(topic, NCacheMessage.ToBytes(NewMessage(7, "hub.key", "hello")));

                var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.Same(received.Task, winner);
                Assert.Equal(7UL, received.Task.Result.Id);
            }
        }

        [Fact]
        public async Task PublishFromOneServer_ReachesAnotherServer()
        {
            var topic = "signalr-" + Guid.NewGuid().ToString("N");
            using (var serverA = await ConnectAsync())
            using (var serverB = await ConnectAsync())
            {
                var received = new TaskCompletionSource<NCacheMessage>();
                await serverB.SubscribeAsync(topic, (stream, msg) => received.TrySetResult(msg));
                await serverA.SubscribeAsync(topic, (stream, msg) => { });

                await serverA.PublishAsync(topic, NCacheMessage.ToBytes(NewMessage(9, "hub.key", "from A")));

                var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.Same(received.Task, winner);
                Assert.Equal(9UL, received.Task.Result.Id);
            }
        }
    }
}
