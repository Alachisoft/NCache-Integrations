using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NCache.OSS.AspNetCore.Authentication.TicketStore;
using System.Security.Claims;

namespace NCache.TicketStore.Tests;

public class TicketStoreTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNCacheTicketStore(options => options.CacheName = "defaultCache");
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((cookie, store) => cookie.SessionStore = store);
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapGet("/", (ClaimsPrincipal user) =>
            user.Identity?.IsAuthenticated == true ? $"Hello {user.Identity.Name}" : "Not authenticated");

        _app.MapGet("/login", async (HttpContext context) =>
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "test-user") },
                CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok("Signed in");
        });

        _app.MapGet("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok("Signed out");
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<string> LoginAsync()
    {
        var response = await _client.GetAsync("/login");
        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private async Task<string> GetAsync(string path, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie != null) request.Headers.Add("Cookie", cookie);

        var response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Anonymous_IsNotAuthenticated()
    {
        Assert.Equal("Not authenticated", await GetAsync("/", null));
    }

    [Fact]
    public async Task Login_TicketIsStoredInNCache_AndAuthenticatesLaterRequests()
    {
        var cookie = await LoginAsync();

        Assert.Equal("Hello test-user", await GetAsync("/", cookie));
    }

    [Fact]
    public async Task Logout_RemovesTicket()
    {
        var cookie = await LoginAsync();

        await GetAsync("/logout", cookie);

        Assert.Equal("Not authenticated", await GetAsync("/", cookie));
    }
}
