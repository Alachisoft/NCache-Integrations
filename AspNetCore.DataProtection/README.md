# NCache as Key Storage Provider for ASP.NET Core Data Protection

The `NCache.OSS.AspNetCore.DataProtection` NuGet package enables ASP.NET Core applications to store Data Protection keys in NCache. The integration extends ASP.NET Core Data Protection through the `PersistKeysToNCache` method. This allows multiple application instances to access the same cryptographic keys from a distributed NCache cluster.

## Package Versions

| **Package**                            | **Version**            |
| -------------------------------------- | ---------------------- |
| `NCache.OSS.AspNetCore.DataProtection` | Current NCache version |
| `Alachisoft.NCache.Opensource.SDK`     | >= 5.3.6.2             |
| `Microsoft.AspNetCore.DataProtection`  | 10.0.0                 |


## Overview

ASP.NET Core Data Protection provides cryptographic APIs for protecting application data. It manages the creation, storage, rotation, and retrieval of the cryptographic keys used to protect and unprotect data.

By default, ASP.NET Core determines where the Data Protection key ring should be stored. Depending on the hosting environment, keys may be stored in the local file system or another default location.

In a web farm, containerized environment, or load-balanced deployment, each application instance must have access to the same key ring. If instances use separate keys, data protected by one instance may not be decrypted by another instance.

NCache acts as a centralized and distributed key storage provider for ASP.NET Core Data Protection. Each application instance connects to the same NCache cache and retrieves the cryptographic keys through the `PersistKeysToNCache` extension method.

This allows protected data, such as authentication cookies and antiforgery tokens, to remain accessible across all participating application instances.

## Key Features

- **Distributed Key Storage:** Stores ASP.NET Core Data Protection keys in a distributed NCache cluster.
- **Web Farm Support:** Allows multiple application servers to access the same Data Protection key ring.
- **Container Support:** Shares cryptographic keys across containers and Kubernetes pods.
- **Centralized Key Management:** Maintains Data Protection keys in one shared cache instead of separate local file systems.
- **Cross-Instance Data Protection:** Allows data protected by one application instance to be unprotected by another instance.
- **Automatic Key Rotation Support:** Works with the key creation and rotation behavior provided by ASP.NET Core Data Protection.
- **Standard ASP.NET Core Integration:** Extends the standard `AddDataProtection` registration.
- **Simple Registration:** Configures NCache through the `PersistKeysToNCache` extension method.
- **Tag-Based Key Grouping:** Uses a common cache tag to group and retrieve the Data Protection keys.
- **Configurable Cache Connections:** Supports cache connectivity through the NCache client configuration files.

## What Is Installed

Installing `NCache.OSS.AspNetCore.DataProtection` adds the assemblies and dependencies required to store ASP.NET Core Data Protection keys in NCache.

The package includes:

- The `PersistKeysToNCache` extension method
- The NCache Data Protection key repository
- The NCache .NET client libraries
- The assemblies required to communicate with an NCache cluster
- The configuration files required for cache connectivity

The following configuration files are copied to the application's output directory:

| **File**        |**Description**|
| --------------- | ---------------------- |
| *client.ncconf* | Contains cache server and client connectivity information.                                    |
| *config.ncconf* | Contains configuration information for local InProc caches and client caches when applicable. |

NCache must be registered separately as the Data Protection key storage provider in the application's service configuration.

## Prerequisites

Before using this package, ensure that you have:

1. **ASP.NET Core Application**: An ASP.NET Core application using the Data Protection services.
2. **Supported .NET Version**: The application must target a .NET version supported by the installed NCache release.
3. **NCache Installation**: NCache must be installed on the cache-server machines.
4. **Running Cache**: A cache, such as `demoCache`, must already be created and running.
5. **Cache Connectivity**: Every application instance must be able to communicate with the NCache servers.
6. **Consistent Configuration**: Every instance of the same application must use the same cache name and cache tag.
7. **Required Namespace**: Include the following namespace in the application:

```csharp
using Alachisoft.NCache.AspNetCore.DataProtection;
```

## Installation

Install the Open Source ASP.NET Core Data Protection integration through the NuGet Package Manager Console:

```powershell
Install-Package NCache.OSS.AspNetCore.DataProtection
```

To install a specific version, use:

```powershell
Install-Package NCache.OSS.AspNetCore.DataProtection -Version x.x.x
```

Replace `x.x.x` with the NCache package version you are using.

You can also install the package through the .NET CLI:

```bash
dotnet add package NCache.OSS.AspNetCore.DataProtection
```

Alternatively, install it through the Visual Studio NuGet Package Manager:

1. Right-click the ASP.NET Core project in **Solution Explorer**.
2. Select **Manage NuGet Packages**.
3. Search for `NCache.OSS.AspNetCore.DataProtection`.
4. Select the package.
5. Select **Install**.

## Configure the Cache Connection

The Data Protection integration uses the NCache client configuration to locate and connect to the cache servers.

Configure the cache in *client.ncconf*:

```xml
<cache id="demoCache"
       enable-client-logs="False"
       log-level="error">
  <server name="20.200.20.11" />
</cache>
```

The `id` attribute must match the cache name passed to `PersistKeysToNCache`.

You can add multiple `<server>` elements when the application can connect to more than one NCache server:

```xml
<cache id="demoCache"
       enable-client-logs="False"
       log-level="error">
  <server name="20.200.20.11" />
  <server name="20.200.20.12" />
</cache>
```

The *config.ncconf* file contains cache configuration for local InProc caches and client caches. Modify it only when the application uses one of these cache types.

## Configure the Data Protection Service

ASP.NET Core Data Protection must be added to the application's service collection through `AddDataProtection`.

For an ASP.NET Core application using `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();

var app = builder.Build();
```

For an application using `Startup.cs`, add Data Protection in `ConfigureServices`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddDataProtection();
}
```

## Register NCache as the Key Storage Provider

Chain `PersistKeysToNCache` onto `AddDataProtection`:

```csharp
using Alachisoft.NCache.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

string cacheName = "demoCache";
string cacheTag = "encryption_keys_tag";

builder.Services.AddDataProtection()
    .PersistKeysToNCache(cacheName, cacheTag);
```

The parameters have the following purposes:

| **Parameter** | **Description**-|
| ------------- | ----------------------- |
| `cacheName`   | Specifies the name of the running NCache cache in which Data Protection keys are stored.                                           |
| `cacheTag`    | Specifies the tag used to group and retrieve Data Protection keys. Every instance of the same application must use the same value. |

Make sure that `cacheName` matches the cache configured in *client.ncconf*.

Use a unique cache tag for each unrelated application that shares the same cache.

## Enable Logging

To use logging with the NCache Data Protection provider, register logging in the service collection:

```csharp
using Alachisoft.NCache.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

string cacheName = "demoCache";
string cacheTag = "encryption_keys_tag";

builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
});

builder.Services.AddDataProtection()
    .PersistKeysToNCache(cacheName, cacheTag);
```

Logging can help identify cache connection, key storage, and key retrieval issues.

## Use Data Protection

After configuring NCache, continue using the standard ASP.NET Core Data Protection APIs.

The following class creates an `IDataProtector` and uses it to protect and unprotect application data:

```csharp
using Microsoft.AspNetCore.DataProtection;

public class CustomDataProtector
{
    private readonly IDataProtector _protector;

    public CustomDataProtector(
        IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(
            "Contoso.MyClass.v1");
    }

    public string Protect(string input)
    {
        return _protector.Protect(input);
    }

    public string Unprotect(string protectedPayload)
    {
        return _protector.Unprotect(protectedPayload);
    }
}
```

Register the class with dependency injection:

```csharp
builder.Services.AddTransient<CustomDataProtector>();
```

Use the class from an endpoint, controller, or another application service:

```csharp
app.MapPost(
    "/protect",
    (string input, CustomDataProtector protector) =>
    {
        return protector.Protect(input);
    });

app.MapPost(
    "/unprotect",
    (string input, CustomDataProtector protector) =>
    {
        return protector.Unprotect(input);
    });
```

Data protected by one application instance can be unprotected by another instance when both use the same NCache cache, cache tag, application name, and Data Protection purpose.

## How the Data Protection Provider Works

When ASP.NET Core Data Protection uses NCache:

1. The application registers Data Protection through `AddDataProtection`.
2. `PersistKeysToNCache` registers NCache as the Data Protection key repository.
3. The repository connects to the specified NCache cache.
4. ASP.NET Core requests the existing key ring when the application starts.
5. NCache retrieves the keys associated with the configured cache tag.
6. When ASP.NET Core creates a new key, the key is stored in NCache.
7. Every application instance using the same cache and tag can retrieve the updated key ring.
8. Each instance can protect and unprotect data using the same cryptographic keys.

The provider stores the key information as encoded XML elements in NCache. Keys use the expiration information supplied by the Data Protection framework, while key revocation entries remain available without expiration.

This allows ASP.NET Core to maintain consistent Data Protection behavior across web farms, containers, and other distributed deployments.

## Run the Sample

The sample demonstrates how to store the ASP.NET Core Data Protection key ring in NCache instead of the local file system.

Run the sample on two application instances to verify that data protected by one instance can be unprotected by the other.

### Using Visual Studio

1. Open the sample solution in Visual Studio.
2. Restore the NuGet packages.
3. Make sure that NCache is running.
4. Create and start the cache specified by `cacheName`.
5. Confirm that *client.ncconf* contains the correct cache name and server addresses.
6. Configure separate launch profiles for the two application instances.
7. Start both launch profiles.
8. Protect data through the first application instance.
9. Pass the protected value to the second instance.
10. Verify that the second instance can unprotect the value.

For example, open:

```text
http://localhost:5101/
http://localhost:5102/
```

### Using the Command Line

Restore the project dependencies:

```bash
dotnet restore
```

Run the first application instance:

```bash
dotnet run --launch-profile Server1
```

Open another terminal and run the second application instance:

```bash
dotnet run --launch-profile Server2
```

Open both application URLs:

```text
http://localhost:5101/
http://localhost:5102/
```

Protect a value through the application running on port `5101`, and then unprotect the generated payload through the application running on port `5102`.

Both instances must use the same cache name and cache tag.

## Validation

Before running the application, verify the following:

- The configured NCache cache exists.
- The cache is running.
- Every application instance can connect to the NCache cluster.
- The cache name in *client.ncconf* matches the value passed to `PersistKeysToNCache`.
- At least one valid NCache server is configured in *client.ncconf*.
- Every instance of the same application uses the same cache name.
- Every instance of the same application uses the same cache tag.
- The `Alachisoft.NCache.AspNetCore.DataProtection` namespace is included.
- `AddDataProtection` is registered in the service collection.
- `PersistKeysToNCache` is chained onto the Data Protection registration.
- The application uses a supported ASP.NET Core and .NET version.
- All application instances use a compatible Data Protection configuration.

If the cache does not exist, is not running, or cannot be reached, NCache cannot store or retrieve the Data Protection key ring.

If application instances use different keys, cache tags, application names, or Data Protection purposes, one instance may be unable to unprotect data generated by another.

## Best Practices

- Use the same cache name and cache tag across all instances of the same application.
- Use a unique cache tag for each unrelated application sharing the same cache.
- Keep the NCache cluster accessible to every application instance.
- Configure multiple NCache server addresses for distributed deployments.
- Use the same Data Protection application name across all instances of one application when `SetApplicationName` is configured.
- Use the same Data Protection purpose when protecting and unprotecting related data.
- Configure NCache before the application begins protecting or unprotecting data.
- Enable logging while diagnosing cache connection or key retrieval issues.
- Test key sharing across multiple application instances before deploying to production.
- Avoid clearing the cache that contains active Data Protection keys.
- Use separate caches or cache tags for development, testing, and production environments.
- Monitor the NCache cluster to ensure that the Data Protection keys remain available.
- Handle potential Data Protection and cache connection exceptions according to the application's failure-handling requirements.

## Resources

- [NCache Documentation](https://www.alachisoft.com/resources/docs/)
- [ASP.NET Core Data Protection Provider](https://www.alachisoft.com/resources/docs/ncache/prog-guide/data-protection-providers-aspnet-core.html)
- [ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction)
- [ASP.NET Core Key Storage Providers](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers)
- [NCache Open Source](https://github.com/Alachisoft/NCache)
- [Alachisoft Website](https://www.alachisoft.com/ncache/)

## Technical Support

Alachisoft provides various technical support resources.
- Visit the [Alachisoft Support Center](https://www.alachisoft.com/support.html) to select a support resource appropriate for your issue.
- To request an additional feature or report a documentation discrepancy, contact [support@alachisoft.com](mailto:support@alachisoft.com).


## Copyrights

Copyright © 2005–2026 Alachisoft. All rights reserved.
