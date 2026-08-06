# NCache Integrations for .NET

NCache Integrations provides open-source integrations that enable .NET and ASP.NET applications to use [NCache](https://www.alachisoft.com/ncache/) for distributed caching, application-state management, output caching, real-time messaging, and other scalability requirements.

These integrations help applications share cached data and application state across multiple servers in web farms, containerized environments, and cloud deployments.

## Key Capabilities

The integrations in this repository provide support for:

- Distributed [ASP.NET](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet.html) and [ASP.NET Core](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-core.html) application state
- ASP.NET and ASP.NET Core [output caching](https://www.alachisoft.com/resources/docs/ncache/prog-guide/output-cache.html)
- [Session State](https://www.alachisoft.com/resources/docs/ncache/prog-guide/session-storage-aspnet-core.html) storage
- ASP.NET Core authentication [ticket](https://www.alachisoft.com/resources/docs/ncache/prog-guide/iticket-store-overview.html) storage
- ASP.NET Core [Data Protection](https://www.alachisoft.com/resources/docs/ncache/prog-guide/data-protection-providers-aspnet-core-overview.html) key sharing
- [ASP.NET](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-signalr.html) and [ASP.NET Core](https://www.alachisoft.com/resources/docs/ncache/prog-guide/asp-net-core-signalr.html) SignalR scale-out
- [Entity Framework Core](https://www.alachisoft.com/resources/docs/ncache/prog-guide/entity-framework-core-caching.html) query caching
- [NHibernate](https://www.alachisoft.com/resources/docs/ncache/prog-guide/nhibernate.html) second-level caching
- [`IDistributedCache`](https://www.alachisoft.com/resources/docs/ncache/prog-guide/aspnet-core-idistributedcache.html) integration
- [CacheManager.Core](https://www.alachisoft.com/resources/docs/ncache/prog-guide/cache-manager.html) integration
- [FusionCache](https://www.alachisoft.com/resources/docs/ncache/prog-guide/fusioncache.html) backplane synchronization
- Multi-server, containerized, and cloud deployments

## Getting Started

Before using an integration, install NCache Open Source and create a running cache. You can install NCache directly on Windows or Linux, or run it inside a Docker container.

### Prerequisites

Make sure that you have:

- A supported .NET SDK or .NET Framework version for the selected integration
- NCache installed and running
- A configured and running NCache cache
- Network connectivity between the application and the NCache servers
- Docker installed when using the Docker deployment option

## Use an Integration

Select the integration required by your application and install its NuGet package.

For example:

```bash
dotnet add package NCache.Microsoft.Extensions.Caching.Opensource
```

Restore and build the application:

```bash
dotnet restore
dotnet build
```

Configure the integration with the name of your running cache and any required cache-server information.Refer to the [documentation](https://www.alachisoft.com/resources/docs/ncache/prog-guide/client-side-aspnet-features.html) provided with the selected integration before running the application.

## Build the Repository

Clone or download this repository, and then restore and build the required integration project:

```bash
dotnet restore
dotnet build
```

Some integrations target ASP.NET or .NET Framework and may need to be built through Visual Studio on Windows.

## Documentation

- [NCache Website](https://www.alachisoft.com/ncache/)
- [NCache Download Center](https://www.alachisoft.com/download-ncache.html)
- [Getting Started Guide](https://www.alachisoft.com/resources/docs/ncache/getting-started/)
- [Getting Started with NCache Open Source](https://www.alachisoft.com/resources/docs/ncache/getting-started/ncache-oss.html)
- [NCache Developer's Guide](https://www.alachisoft.com/resources/docs/ncache/prog-guide/)
- [NCache Administrator's Guide](https://www.alachisoft.com/resources/docs/ncache/admin-guide/)
- [NCache Installation & Deployment Guide](https://www.alachisoft.com/resources/docs/ncache/install-guide/)
- [NCache Docker Guide](https://www.alachisoft.com/resources/docs/ncache/install-guide/getting-started-guide-docker.html)
- [NCache NuGet Packages](https://www.nuget.org/profiles/NCache)

## Technical Support

Alachisoft provides various technical support resources.

- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).

## License

Copyright © 2005-2026 Alachisoft. All rights reserved.
