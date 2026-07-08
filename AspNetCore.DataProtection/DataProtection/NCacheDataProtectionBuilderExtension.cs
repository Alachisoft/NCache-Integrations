using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Alachisoft.NCache.AspNetCore.DataProtection
{
    /// <summary>
    /// This class acts as the registration point for the clients to configure NCache as a key storage provider for Data Protection.
    /// </summary>
    public static class NCacheDataProtectionBuilderExtension
    {
        /// <summary>
        /// This method initializes NCacheXmlRepository to store Data Protection encryption keys.
        /// </summary> 
        /// <param name="builder">The Microsoft.AspNetCore.DataProtection.IDataProtectionBuilder.</param>
        /// <param name="cacheName">The name of the cache for storing encryption keys.</param>
        /// <param name="cacheTag">The name of the tag against which the keys are to be stored.</param>
        /// <returns>A reference to the Microsoft.AspNetCore.DataProtection.IDataProtectionBuilder after this operation has completed.</returns>
        public static IDataProtectionBuilder PersistKeysToNCache(this IDataProtectionBuilder builder, string cacheName, string cacheTag)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            if (string.IsNullOrEmpty(cacheName))
                throw new ArgumentNullException(nameof(cacheName));

            if (string.IsNullOrEmpty(cacheTag))
                throw new ArgumentNullException(nameof(cacheTag));

            return PersistKeysToNCacheInternal(builder, cacheName, cacheTag);
        }

        private static IDataProtectionBuilder PersistKeysToNCacheInternal(IDataProtectionBuilder builder, string cacheName, string cacheTag)
        {
            builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            {
                var logger = services.GetService<ILogger<NCacheXmlRepository>>();

                return new ConfigureOptions<KeyManagementOptions>(options =>
                {
                    options.XmlRepository = new NCacheXmlRepository(cacheName, cacheTag, logger);
                });
            });

            return builder;
        }
    }
}
