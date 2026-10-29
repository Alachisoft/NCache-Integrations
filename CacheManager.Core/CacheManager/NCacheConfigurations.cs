using System;
using System.Collections.Generic;

namespace NCache.OSS.CacheManager.Core
{
    /// <summary>
    /// Manages named <see cref="NCacheOptions"/> instances for cache handles and backplanes.
    /// <para>
    /// Configurations are registered through <c>WithNCacheConfiguration</c>
    /// (or the overload that accepts an <see cref="NCacheOptions"/> instance directly), and are then looked
    /// up by the matching configuration key when a cache handle or backplane is added to the configuration.
    /// The registry is process-wide. Replacing a key affects subsequent lookups; builders that
    /// already captured an options instance continue using that instance. Register options during
    /// startup and do not mutate them while cache managers are in use.
    /// </para>
    /// </summary>
    public static class NCacheConfigurations
    {
        private static readonly Dictionary<string, NCacheOptions> Configurations = new Dictionary<string, NCacheOptions>();
        private static readonly object ConfigLock = new object();

        /// <summary>
        /// Adds (or replaces) the <see cref="NCacheOptions"/> registered under <paramref name="configurationKey"/>.
        /// </summary>
        /// <param name="configurationKey">
        /// The configuration key which can be used to reference this configuration by an NCache cache handle or backplane.
        /// </param>
        /// <param name="options">The NCache options instance.</param>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">If <paramref name="options"/> is null.</exception>
        public static void AddConfiguration(string configurationKey, NCacheOptions options)
        {
            if (string.IsNullOrWhiteSpace(configurationKey))
            {
                throw new ArgumentException("Configuration key must not be null or empty.", nameof(configurationKey));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!options.isValid(out var validationError))
            {
                throw new InvalidOperationException($"Invalid NCache configuration for key '{configurationKey}': {validationError}");
            }

            lock (ConfigLock)
            {
                Configurations[configurationKey] = options;
            }
        }

        /// <summary>
        /// Gets the <see cref="NCacheOptions"/> registered under <paramref name="configurationKey"/>.
        /// </summary>
        /// <param name="configurationKey">The configuration key.</param>
        /// <returns>The matching <see cref="NCacheOptions"/>.</returns>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        /// <exception cref="InvalidOperationException">If no configuration was added for the key.</exception>
        public static NCacheOptions GetConfiguration(string configurationKey)
        {
            if (string.IsNullOrWhiteSpace(configurationKey))
            {
                throw new ArgumentException("Configuration key must not be null or empty.", nameof(configurationKey));
            }

            lock (ConfigLock)
            {
                if (!Configurations.TryGetValue(configurationKey, out var options))
                {
                    throw new InvalidOperationException($"No NCache configuration added for configuration key '{configurationKey}'. Call WithNCacheConfiguration first.");
                }

                return options;
            }
        }
    }
}
