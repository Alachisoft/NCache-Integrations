using Alachisoft.NCache.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

namespace NCache.DataProtection.Tests;

public class DataProtectionTests
{
    private const string CacheName = "defaultCache";

    private static IDataProtectionProvider CreateProvider(string keyName)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("NCacheDataProtectionTests")
            .PersistKeysToNCache(CacheName, keyName);

        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static string NewKeyName() => $"dp-keys-{Guid.NewGuid():N}";

    [Fact]
    public void Protect_Then_Unprotect_RoundTrips()
    {
        var protector = CreateProvider(NewKeyName()).CreateProtector("tests.v1");

        var payload = protector.Protect("hello ncache");

        Assert.NotEqual("hello ncache", payload);
        Assert.Equal("hello ncache", protector.Unprotect(payload));
    }

    [Fact]
    public void SecondInstance_SharingKeyRing_CanUnprotect()
    {
        var keyName = NewKeyName();
        var payload = CreateProvider(keyName).CreateProtector("tests.v1").Protect("shared secret");

        // A separate provider stands in for another server reading the same key ring from NCache.
        var other = CreateProvider(keyName).CreateProtector("tests.v1");

        Assert.Equal("shared secret", other.Unprotect(payload));
    }

    [Fact]
    public void DifferentPurpose_CannotUnprotect()
    {
        var provider = CreateProvider(NewKeyName());
        var payload = provider.CreateProtector("purpose.a").Protect("secret");

        Assert.Throws<CryptographicException>(() => provider.CreateProtector("purpose.b").Unprotect(payload));
    }
}
