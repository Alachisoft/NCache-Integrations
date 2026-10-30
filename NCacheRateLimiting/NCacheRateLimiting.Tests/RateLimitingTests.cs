using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NCache.OSS.RateLimiting;
using System.Net;

namespace NCache.RateLimiting.Tests;

public class RateLimitingTests
{
    private const int PermitLimit = 3;

    // The policy name is part of the cache key, so a unique one keeps runs from sharing a window.
    private static async Task<WebApplication> StartAsync()
    {
        var policy = $"orders-{Guid.NewGuid():N}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddNCacheFixedWindowLimiter(policy, opt =>
            {
                opt.PermitLimit = PermitLimit;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.CacheName = "defaultCache";
            });
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/order", () => Results.Ok()).RequireRateLimiting(policy);

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task FixedWindow_AllowsRequestsUpToTheLimit()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        for (var i = 0; i < PermitLimit; i++)
        {
            var response = await client.PostAsync("/order", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task FixedWindow_RejectsRequestsOverTheLimit()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        for (var i = 0; i < PermitLimit; i++)
            await client.PostAsync("/order", null);

        var rejected = await client.PostAsync("/order", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }
}
