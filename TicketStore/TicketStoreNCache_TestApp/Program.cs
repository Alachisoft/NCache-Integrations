using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using NCache.OSS.AspNetCore.Authentication.TicketStore;


var builder = WebApplication.CreateBuilder(args);

//builder.Services.AddNCacheTicketStore(options =>
//{
//    options.CacheName = "demoCache";
//});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddNCacheTicketStore(
    builder.Configuration.GetSection("NCacheTicketStore"));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookieOptions =>
    {
        cookieOptions.SessionStore =
            builder.Services
                   .BuildServiceProvider()
                   .GetRequiredService<ITicketStore>();
        cookieOptions.ExpireTimeSpan = TimeSpan.FromSeconds(15);
        cookieOptions.SlidingExpiration = false;
    });


builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (ClaimsPrincipal user) =>
{
    return user.Identity?.IsAuthenticated == true
        ? $"Hello {user.Identity.Name}"
        : "Not authenticated";
});

app.MapGet("/login", async (HttpContext context) =>
{
    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.Name, "test-user")
    };

    var identity = new ClaimsIdentity(
        claims,
        CookieAuthenticationDefaults.AuthenticationScheme);

    var principal = new ClaimsPrincipal(identity);

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal);

    return Results.Ok("Signed in");
});

app.MapGet("/ping", (ClaimsPrincipal user) =>
{
    return Results.Ok(DateTime.UtcNow);
});

// Use login API, then constantly use ping API over and over until renew async is triggered (will trigger when sliding expiration is enabled in cookie middleware, observe the item does not expire even though expiration time will be passed)

app.Run();