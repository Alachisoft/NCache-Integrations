using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace NCache.OSS.CacheManager.Core
{
    /// <summary>
    /// Extension methods for registering a named <see cref="NCacheOptions"/> (for use by
    /// <c>WithNCacheCacheHandle</c>/<c>WithNCacheBackplane</c>) via <see cref="IServiceCollection"/>,
    /// Registration immediately populates the process-wide registry; it is not scoped to a service provider.
    /// </summary>
    public static class NCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Registers an <see cref="NCacheOptions"/> under <paramref name="configurationKey"/> using a configuration delegate.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="services"/> or <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null/whitespace.</exception>
        /// <exception cref="InvalidOperationException">If required options are missing or invalid.</exception>
        public static IServiceCollection AddNCacheConfiguration(
            this IServiceCollection services,
            string configurationKey,
            Action<NCacheOptions> configure)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            var options = new NCacheOptions();
            configure(options);

            NCacheConfigurations.AddConfiguration(configurationKey, options); // validates internally

            return services;
        }

        /// <summary>
        /// Registers an <see cref="NCacheOptions"/> under <paramref name="configurationKey"/> by binding it from <paramref name="configSection"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="services"/> or <paramref name="configSection"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null/whitespace.</exception>
        /// <exception cref="InvalidOperationException">If <see cref="NCacheOptions"/> cannot be bound from configuration.</exception>
        public static IServiceCollection AddNCacheConfiguration(
            this IServiceCollection services,
            string configurationKey,
            IConfigurationSection configSection)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (configSection == null)
                throw new ArgumentNullException(nameof(configSection));

            var options = configSection.Get<NCacheOptions>()
                ?? throw new InvalidOperationException("Failed to bind NCacheOptions from configuration.");

            NCacheConfigurations.AddConfiguration(configurationKey, options);
            return services;
        }
    }
}
