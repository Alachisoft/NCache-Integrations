# NCache ASP.NET Core Session Provider

The `AspNetCore.Session.NCache.Opensource` NuGet package enables ASP.NET Core applications to store session state in a distributed NCache cluster instead of keeping sessions in the application process.

The integration provides the `AddNCacheSession` and `UseNCacheSession` extension methods for configuring NCache as the ASP.NET Core Session provider. This allows session data to remain available across application instances in a load-balanced web farm while providing configurable session locking, retries, logging, and session options.

## Package Versions

| **Package**                                             | **Version** |
| ------------------------------------------------------- | ----------- |
| `AspNetCore.Session.NCache.Opensource`                  | 5.3.6.1     |
| `Alachisoft.NCache.Opensource.SDK`                      | >= 5.3.6.2  |
| `Microsoft.AspNetCore.Http.Abstractions`                | >= 2.1.0    |
| `Microsoft.Extensions.Caching.Abstractions`             | >= 2.1.0    |
| `Microsoft.Extensions.Configuration`                    | >= 2.1.0    |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | >= 2.1.0    |
| `Microsoft.Extensions.Options.ConfigurationExtensions`  | >= 2.1.0    |
| `System.Configuration.ConfigurationManager`             | >= 9.0.0    |

## Overview

ASP.NET Core applications typically maintain session information associated with individual users. When session data is stored within an application process, it can be lost if the process restarts or the application server becomes unavailable.

In a web farm, users can also be routed to different application servers. Keeping session state in a distributed cache allows every application instance to access the same session independently of the server handling the request.

NCache provides distributed ASP.NET Core Session storage by moving session data outside the application process and into an NCache cluster. This allows session state to survive application process recycles and remain accessible across multiple application servers.

NCache supports ASP.NET Core sessions through two approaches:

- **NCache Session Management Service:** Uses `AddNCacheSession` and `UseNCacheSession` to provide NCache-specific session management capabilities such as exclusive session locking and locking retries.
- **ASP.NET Core Sessions with NCache Distributed Caching:** Uses NCache through the standard `IDistributedCache` abstraction for distributed data and session storage.

## Key Features

- **Distributed Session Storage:** Stores ASP.NET Core session data in a distributed NCache cluster.
- **Web Farm Support:** Allows application instances on different servers to access the same session data.
- **Session Persistence:** Keeps session data outside the ASP.NET Core application process so it can survive process restarts.
- **Exclusive Session Locking:** Supports optional exclusive locking when concurrent requests access the same session.
- **Locking Retries:** Supports configurable retries when another request already holds a session lock.
- **High Availability:** Uses NCache clustering to keep session state available across cache servers.
- **Horizontal Scalability:** Allows cache servers to be added as session workload increases.
- **Configurable Session Options:** Supports ASP.NET Core session cookie and idle-timeout settings.
- **Configurable Error Handling:** Controls whether NCache exceptions are propagated to the application.
- **Operation Retries:** Supports retrying cache operations when connectivity is interrupted.
- **Session Application ID:** Keeps session identifiers unique when multiple applications use the same cache.
- **Read-Only Sessions:** Supports read-only session access that does not acquire an exclusive session lock or commit changes.
- **Provider Logging:** Supports standard and detailed NCache session logs.

## What Is Installed

Installing `AspNetCore.Session.NCache.Opensource` adds the components required to use NCache for ASP.NET Core Session storage.

The package provides:
- The `AddNCacheSession` extension method
- The `UseNCacheSession` middleware extension
- NCache ASP.NET Core Session storage services
- Session configuration support
- Session locking and locking retry support
- NCache client libraries required to communicate with the cache cluster

The session provider must be registered in the application's service collection and added to the ASP.NET Core request pipeline.

## Prerequisites

Before using this package, ensure that you have:

1. **ASP.NET Core Application:** An ASP.NET Core application that requires distributed session storage.
2. **Supported .NET Version:** For .NET 6.0 and later, configure the provider through *Program.cs*.
3. **NCache Installation:** NCache must be installed on the cache-server machines.
4. **Running Cache:** A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity:** Every application instance must be able to communicate with the NCache servers.
6. **Serializable Data:** Data stored in session must be serializable.
7. **Required Namespace:** Include the following namespace in *Program.cs*:

```csharp
using Alachisoft.NCache.Web.SessionState;
```

## Installation

Install the Open Source ASP.NET Core Session provider through the NuGet Package Manager Console:

```powershell
Install-Package AspNetCore.Session.NCache.Opensource
```

You can also install the package through the .NET CLI:

```bash
dotnet add package AspNetCore.Session.NCache.Opensource
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `AspNetCore.Session.NCache.Opensource`.
4. Select the package.
5. Select **Install**.

## Configure the Session Provider through Program.cs

Use `AddNCacheSession` to register NCache Session Management directly in *Program.cs*:

```csharp
using Alachisoft.NCache.Web.SessionState;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddNCacheSession(options =>
{
    options.CacheName = "demoCache";
    options.EnableLogs = true;
    options.SessionAppId = "demoApp";

    options.SessionOptions.IdleTimeout = 5;
    options.SessionOptions.CookieName = "AspNetCore.Session";
});

var app = builder.Build();
```

`CacheName` is required and must identify an existing and running NCache cache.

`SessionAppId` should use the same value for every instance of the same application in a web farm.

## Configure the Session Provider through appsettings.json

Session settings can also be maintained outside application code in *appsettings.json*.

Add an `NCacheSettings` section:

```json
{
  "NCacheSettings": {
    "SessionAppId": "demoApp",
    "CacheName": "demoCache",
    "EnableLogs": true,
    "RequestTimeout": 90,
    "WriteExceptionsToEventLog": false,
    "SessionOptions": {
      "CookieName": "AspNetCore.Session",
      "CookieDomain": null,
      "CookiePath": "/",
      "CookieHttpOnly": true,
      "IdleTimeout": 5,
      "CookieSecure": "None"
    }
  }
}
```

Register the configuration section in *Program.cs*:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddNCacheSession(
    builder.Configuration.GetSection("NCacheSettings"));
```

This approach allows session settings to be changed between environments without modifying application code.

## Add NCache Session Middleware

After registering the Session Management Service, add `UseNCacheSession` to the ASP.NET Core request pipeline.

```csharp
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseNCacheSession();

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
```

Place `UseNCacheSession()` before middleware or endpoints that require access to session data.

## Use ASP.NET Core Sessions

After NCache Session Management is registered, continue using ASP.NET Core session APIs through `HttpContext.Session`.

For example:

```csharp
public IActionResult SetSession()
{
    HttpContext.Session.SetString(
        "UserName",
        "John Smith");

    return Ok();
}
```

Retrieve the value:

```csharp
public IActionResult GetSession()
{
    string userName =
        HttpContext.Session.GetString("UserName");

    return Ok(userName);
}
```

## How NCache Session Management Works

When ASP.NET Core Session Management uses NCache:

1. The application registers the provider through `AddNCacheSession`.
2. `UseNCacheSession` adds NCache Session handling to the ASP.NET Core request pipeline.
3. A client request contains the ASP.NET Core session identifier.
4. NCache retrieves the corresponding session data from the distributed cache.
5. If session locking is enabled, NCache acquires an exclusive lock before allowing the request to update the session.
6. The application accesses the session through `HttpContext.Session`.
7. Updated session state is stored back in NCache.
8. Any application instance connected to the same cache can retrieve the session on subsequent requests.
9. Session data remains outside the application process and can survive application server or process restarts.

This allows ASP.NET Core applications in a web farm to share session state without relying on local in-process storage.

## Run the Sample

Open Source ASP.NET Core Session samples are available in the NCache Samples repository:

- [ASP.NET Core Session Sharing Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/SessionSharing/oss)
- [ASP.NET Framework Session Sharing Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet-framework/SessionSharing/oss)

### Using Visual Studio

1. Clone or download the NCache Samples repository.
2. Open the ASP.NET Core Session sample in Visual Studio.
3. Restore the NuGet packages.
4. Make sure that NCache is running.
5. Create and start the cache configured by the sample.
6. Verify that the application can connect to the NCache cluster.
7. Build the application.
8. Run the sample.
9. Create or update session data.
10. Access the application through multiple instances to verify that the session remains available.

### Using the Command Line

Restore the application dependencies:

```bash
dotnet restore
```

Build the application:

```bash
dotnet build
```

Run the application:

```bash
dotnet run
```

Use multiple application instances when testing distributed session behavior.

## Validation

Before running the application, verify the following:

- `AspNetCore.Session.NCache.Opensource` is installed.
- The configured NCache cache exists.
- The cache is running.
- Every application instance can connect to the NCache cluster.
- `Alachisoft.NCache.Web.SessionState` is included.
- `AddNCacheSession` is registered in the service collection.
- `CacheName` specifies an existing and running cache.
- `UseNCacheSession` is added to the request pipeline.
- `UseNCacheSession` appears before middleware or endpoints that access session data.
- Every instance of the same application uses the same `SessionAppId` when one is configured.
- Session data stored by the application is serializable.
- `EnableSessionLocking` and its retry settings are configured according to the application's concurrency requirements.
- `RequestTimeout` is appropriate for the expected request duration.

If `CacheName` is missing, the provider throws a configuration exception.

If the configured cache does not exist, is not running, or cannot be reached, NCache cannot retrieve or persist ASP.NET Core session data.

## Best Practices

- Use the same NCache cache across application instances that need to share session state.
- Use the same `SessionAppId` across all instances of the same application.
- Use different `SessionAppId` values when unrelated applications share the same cache.
- Enable exclusive session locking only when concurrent requests can modify the same session and require serialization.
- Configure `SessionLockingRetry` according to the application's expected level of concurrent access.
- Configure `RequestTimeout` so abandoned locks can be released without prematurely unlocking valid long-running requests.
- Use read-only sessions for requests that only need to inspect session data.
- Keep NCache Session middleware before components that access `HttpContext.Session`.
- Keep configuration in *appsettings.json* when settings vary between deployment environments.
- Enable detailed logs only while troubleshooting.
- Avoid enabling `WriteExceptionsToEventLog` in production environments.
- Configure operation retries according to the application's connectivity requirements.
- Use separate cache configurations for development, testing, and production environments.
- Monitor the NCache cluster to ensure sufficient capacity for the expected session workload.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET Core Session Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-aspnet-core-session-provider.html)
- [NCache IDistributedCache Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnetcore-sessions-ncache-idistributedcache-provider.html?tabs=net)
- [AspNetCore.Session.NCache.Opensource](https://www.nuget.org/packages/AspNetCore.Session.NCache.Opensource)
- [ASP.NET Core Session Sharing Sample](https://github.com/Alachisoft/NCache-Samples/tree/master/dotnet/SessionSharing/oss)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright © 2026 Alachisoft. All rights reserved.
