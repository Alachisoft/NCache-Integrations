using Alachisoft.NCache.Web.SessionState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace NCache.SessionServices.Tests;

public class SessionTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddNCacheSession(options => options.CacheName = "defaultCache");

        _app = builder.Build();
        _app.UseNCacheSession();

        _app.MapGet("/set/{value}", (HttpContext context, string value) =>
        {
            context.Session.SetString("name", value);
            return Results.Ok();
        });
        _app.MapGet("/get", (HttpContext context) => context.Session.GetString("name") ?? "<none>");
        _app.MapGet("/clear", (HttpContext context) =>
        {
            context.Session.Clear();
            return Results.Ok();
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<string> StartSessionAsync(string value)
    {
        var response = await _client.GetAsync($"/set/{value}");
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    private async Task<string> SendAsync(string path, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie != null) request.Headers.Add("Cookie", cookie);
        return await (await _client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task SessionValue_IsStoredInNCache_AndReadOnNextRequest()
    {
        var cookie = await StartSessionAsync("alice");

        Assert.Equal("alice", await SendAsync("/get", cookie));
    }

    [Fact]
    public async Task DifferentSessions_AreIsolated()
    {
        var alice = await StartSessionAsync("alice");
        var bob = await StartSessionAsync("bob");

        Assert.Equal("alice", await SendAsync("/get", alice));
        Assert.Equal("bob", await SendAsync("/get", bob));
    }

    [Fact]
    public async Task Request_WithoutCookie_HasEmptySession()
    {
        Assert.Equal("<none>", await SendAsync("/get", null));
    }

    [Fact]
    public async Task ClearedSession_LosesItsValues()
    {
        var cookie = await StartSessionAsync("alice");

        await SendAsync("/clear", cookie);

        Assert.Equal("<none>", await SendAsync("/get", cookie));
    }
}
