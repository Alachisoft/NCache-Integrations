using Alachisoft.NCache.AspNetCore.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace NCache.AspNetCoreSignalR.Tests;

public class TestHub : Hub
{
    public Task Broadcast(string message) => Clients.All.SendAsync("Receive", message);
}

public class SignalRBackplaneTests : IAsyncLifetime
{
    private readonly string _eventKey = $"signalr-{Guid.NewGuid():N}";
    private WebApplication _serverA = null!;
    private WebApplication _serverB = null!;
    private readonly List<HubConnection> _connections = new();

    public async Task InitializeAsync()
    {
        _serverA = await StartServerAsync();
        _serverB = await StartServerAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();

        await _serverA.DisposeAsync();
        await _serverB.DisposeAsync();
    }

    // Both servers share one NCache event key, so they act as a scaled-out pair.
    private async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR().AddNCache("defaultCache", _eventKey);

        var app = builder.Build();
        app.MapHub<TestHub>("/hub");

        await app.StartAsync();
        return app;
    }

    private async Task<(HubConnection Connection, TaskCompletionSource<string> Received)> ConnectAsync(WebApplication server)
    {
        var received = new TaskCompletionSource<string>();
        var testServer = server.GetTestServer();

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(testServer.BaseAddress, "/hub"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => testServer.CreateHandler();
            })
            .Build();

        connection.On<string>("Receive", message => received.TrySetResult(message));
        await connection.StartAsync();

        _connections.Add(connection);
        return (connection, received);
    }

    private static async Task<string> WaitAsync(Task<string> received)
    {
        var winner = await Task.WhenAny(received, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received, winner);
        return await received;
    }

    [Fact]
    public async Task Broadcast_ReachesClientsOnTheSameServer()
    {
        var (client, received) = await ConnectAsync(_serverA);

        await client.InvokeAsync("Broadcast", "hello");

        Assert.Equal("hello", await WaitAsync(received.Task));
    }

    [Fact]
    public async Task Broadcast_FromOneServer_ReachesClientsOnAnotherServer()
    {
        var (_, received) = await ConnectAsync(_serverA);
        var (senderOnB, _) = await ConnectAsync(_serverB);

        await senderOnB.InvokeAsync("Broadcast", "cross-server");

        Assert.Equal("cross-server", await WaitAsync(received.Task));
    }
}
