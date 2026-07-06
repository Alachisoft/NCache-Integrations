# Enabling NCache in FusionCache Tests

Each test file has static boolean flags at the top that switch the backend between
**MemoryCache (default)**, **Redis**, and **NCache**. To run tests against NCache,
flip the relevant flags and set the server IP/cache name.

---

## Files With NCache Flags

| Test File | Flags Available |
|---|---|
| `L1L2Tests.cs` | `UseNCache` |
| `L1BackplaneTests.cs` | `UseNCacheBackplane` |
| `L1L2BackplaneTests.cs` | `UseNCache`, `UseNCacheBackplane` |
| `AutoRecoveryTests.cs` | `UseNCache`, `UseNCacheBackplane` |
| `FusionHybridCacheTests/HybridL1L2Tests.cs` | `UseNCache` |

---

## Step 1 — Set the Flags

Open the relevant `.cs` file and set the flags:

```csharp
// To use NCache as the distributed cache (L2):
private static readonly bool UseNCache = true;

// To use NCache as the backplane:
private static readonly bool UseNCacheBackplane = true;

// Make sure Redis flags are false (they take priority if true):
private static readonly bool UseRedis = false;
private static readonly bool UseRedisBackplane = false;
```

Setting a flag to `false` falls back to the in-memory implementation automatically —
no other code changes needed.

---

## Step 2 — Set the Cache Name and Server IP

In the same file, update these two values to match your NCache server:

```csharp
private static readonly string NCacheCacheName = "demoCache";  // your cache name
```

For the backplane server IP, find the `NCacheBackplaneOptions` block and update it:

```csharp
return new NCacheBackplane(NCacheCacheName, new NCacheBackplaneOptions()
{
    ServerList = new List<FusionCacheServerInfo>
    {
        new FusionCacheServerInfo("YOUR_SERVER_IP")  // <-- change this
    },
    AppName = "TestApp"
});
```

---

## Step 3 — Update client.ncconf

Set your NCache server IP in `client.ncconf` (copied to output directory on build):

```xml
<cache id="demoCache" enable-client-logs="False" log-level="error">
    <server name="YOUR_SERVER_IP"/>
</cache>
```

Make sure `cache id` matches `NCacheCacheName` in the test file.

---

## Prerequisites

- NCache Server running with the target cache in **Started** state.
- The cache name in `client.ncconf` must match the `NCacheCacheName` constant.
- Project references to `NCache.ZiggyCreatures.FusionCache.Backplane` and
  `NCacheDistributedCache.Net` must resolve (update paths in `.csproj` if needed).
