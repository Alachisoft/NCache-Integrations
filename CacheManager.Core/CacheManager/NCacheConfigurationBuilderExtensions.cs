using System;
using NCache.OSS.CacheManager.Core;

namespace CacheManager.Core
{
    /// <summary>
    /// Extensions for the configuration builder specific to the NCache cache handle and backplane.
    /// <para>
    /// These mirror the fluent bootstrapping extensions shipped for the Redis integration
    /// (<c>WithRedisConfiguration</c> / <c>WithRedisBackplane</c> / <c>WithRedisCacheHandle</c>),
    /// so that NCache no longer requires the low-level <c>WithHandle</c>/<c>WithBackplane</c> calls.
    /// </para>
    /// </summary>
    public static class NCacheConfigurationBuilderExtensions
    {
        /// <summary>
        /// Adds an NCache configuration with the given <paramref name="configurationKey"/>.
        /// </summary>
        /// <param name="part">The builder instance.</param>
        /// <param name="configurationKey">
        /// The configuration key which can be used to reference this configuration by an NCache cache handle or backplane.
        /// </param>
        /// <param name="configuration">The configuration action.</param>
        /// <returns>The configuration builder.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="part"/> or <paramref name="configuration"/> are null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        public static ConfigurationBuilderCachePart WithNCacheConfiguration(this ConfigurationBuilderCachePart part, string configurationKey, Action<NCacheConfigurationBuilder> configuration)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var builder = new NCacheConfigurationBuilder(configurationKey);
            configuration(builder);
            NCacheConfigurations.AddConfiguration(configurationKey, builder.Build());
            return part;
        }

        /// <summary>
        /// Adds an existing <see cref="NCacheOptions"/> instance with the given <paramref name="configurationKey"/>.
        /// </summary>
        /// <param name="part">The builder instance.</param>
        /// <param name="configurationKey">
        /// The configuration key which can be used to reference this configuration by an NCache cache handle or backplane.
        /// </param>
        /// <param name="options">The NCache options instance.</param>
        /// <returns>The configuration builder.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="part"/> or <paramref name="options"/> are null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        public static ConfigurationBuilderCachePart WithNCacheConfiguration(this ConfigurationBuilderCachePart part, string configurationKey, NCacheOptions options)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            NCacheConfigurations.AddConfiguration(configurationKey, options);
            return part;
        }

        /// <summary>
        /// Configures an NCache-backed cache backplane for the cache manager.
        /// The <paramref name="configurationKey"/> is used to find a matching NCache configuration
        /// added via <see cref="WithNCacheConfiguration(ConfigurationBuilderCachePart, string, Action{NCacheConfigurationBuilder})"/>.
        /// <para>
        /// If a backplane is defined, at least one cache handle must be marked as backplane source.
        /// The cache manager then will try to synchronize multiple instances of the same configuration.
        /// </para>
        /// </summary>
        /// <param name="part">The builder instance.</param>
        /// <param name="configurationKey">The NCache configuration key to use for the connection.</param>
        /// <returns>The builder instance.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="part"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        public static ConfigurationBuilderCachePart WithNCacheBackplane(this ConfigurationBuilderCachePart part, string configurationKey)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            var options = NCacheConfigurations.GetConfiguration(configurationKey);
            return part.WithBackplane(typeof(NCacheCacheBackplane), configurationKey, options);
        }

        /// <summary>
        /// Configures an NCache-backed cache backplane for the cache manager, using a specific topic name.
        /// The <paramref name="configurationKey"/> is used to find a matching NCache configuration
        /// added via <see cref="WithNCacheConfiguration(ConfigurationBuilderCachePart, string, Action{NCacheConfigurationBuilder})"/>.
        /// <para>
        /// If a backplane is defined, at least one cache handle must be marked as backplane source.
        /// The cache manager then will try to synchronize multiple instances of the same configuration.
        /// </para>
        /// </summary>
        /// <param name="part">The builder instance.</param>
        /// <param name="configurationKey">The NCache configuration key to use for the connection.</param>
        /// <param name="topicName">The NCache Messaging Service topic name the backplane should use.</param>
        /// <returns>The builder instance.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="part"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> or <paramref name="topicName"/> are null or whitespace.</exception>
        public static ConfigurationBuilderCachePart WithNCacheBackplane(this ConfigurationBuilderCachePart part, string configurationKey, string topicName)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            if (string.IsNullOrWhiteSpace(topicName))
            {
                throw new ArgumentException("Topic name must not be null or empty.", nameof(topicName));
            }

            var options = NCacheConfigurations.GetConfiguration(configurationKey);
            return part.WithBackplane(typeof(NCacheCacheBackplane), configurationKey, topicName, options);
        }

        /// <summary>
        /// Adds an <see cref="NCacheCacheHandle{TCacheValue}"/>.
        /// This handle requires an NCache configuration to be defined with the given <paramref name="configurationKey"/>
        /// via <see cref="WithNCacheConfiguration(ConfigurationBuilderCachePart, string, Action{NCacheConfigurationBuilder})"/>.
        /// </summary>
        /// <param name="part">The builder instance.</param>
        /// <param name="configurationKey">The NCache configuration key to use for the connection.</param>
        /// <param name="isBackplaneSource">
        /// Set this to true if this cache handle should be the source of the backplane.
        /// This setting will be ignored if no backplane is configured.
        /// </param>
        /// <returns>The builder instance.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="part"/> is null.</exception>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        public static ConfigurationBuilderCacheHandlePart WithNCacheCacheHandle(this ConfigurationBuilderCachePart part, string configurationKey, bool isBackplaneSource = true)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            var options = NCacheConfigurations.GetConfiguration(configurationKey);
            return part.WithHandle(typeof(NCacheCacheHandle<>), configurationKey, isBackplaneSource, options);
        }
    }
}
