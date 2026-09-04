using Hangfire;
using Microsoft.Extensions.Configuration;
using System;
using System.Configuration;

namespace NCache.OSS.Hangfire
{
    /// <summary>
    /// Bootstrapper extensions for registering NCache job storage with Hangfire.
    /// </summary>
    public static class NCacheBootstrapperConfigurationExtensions
    {
        // ── Existing overloads ──

        /// <summary>
        /// Configures Hangfire to use NCache with default options.
        /// </summary>
        public static IGlobalConfiguration<NCacheStorage> UseNCacheStorage(
            this IGlobalConfiguration configuration,
            string cacheName)
        {
            return UseNCacheStorage(configuration, cacheName, new NCacheStorageOptions());
        }

        /// <summary>
        /// Configures Hangfire to use NCache with the specified options.
        /// </summary>
        public static IGlobalConfiguration<NCacheStorage> UseNCacheStorage(
            this IGlobalConfiguration configuration,
            string cacheName,
            NCacheStorageOptions options)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(cacheName))
                throw new ArgumentNullException(nameof(cacheName));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var storage = new NCacheStorage(cacheName, options);
            return configuration.UseStorage(storage);
        }

        // ── New: appsettings.json overloads ──

        /// <summary>
        /// Configures Hangfire to use NCache from an <c>IConfigurationSection</c>
        /// (e.g. <c>builder.Configuration.GetSection("Hangfire:NCache")</c>).
        /// </summary>
        public static IGlobalConfiguration<NCacheStorage> UseNCacheStorage(
            this IGlobalConfiguration configuration,
            IConfigurationSection section)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            var options = new NCacheStorageOptions();
            section.Bind(options);

            if (string.IsNullOrEmpty(options.CacheName))
                throw new InvalidOperationException("NCache 'CacheName' is required in the configuration section.");

            var storage = new NCacheStorage(options.CacheName, options);
            return configuration.UseStorage(storage);
        }

        /// <summary>
        /// Configures Hangfire to use NCache from <c>IConfiguration</c> using the specified section key.
        /// Defaults to <c>"Hangfire:NCache"</c>.
        /// </summary>
        public static IGlobalConfiguration<NCacheStorage> UseNCacheStorage(
            this IGlobalConfiguration configuration,
            IConfiguration configurationRoot,
            string sectionKey = "Hangfire:NCache")
        {
            if (configurationRoot == null)
                throw new ArgumentNullException(nameof(configurationRoot));

            var section = configurationRoot.GetSection(sectionKey);
            return configuration.UseNCacheStorage(section);
        }
    }
}