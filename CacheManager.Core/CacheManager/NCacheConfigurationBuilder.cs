using System;
using System.Collections.Generic;

namespace NCache.OSS.CacheManager.Core
{
    /// <summary>
    /// <see cref="NCacheConfigurationBuilder"/> helps creating an <see cref="NCacheOptions"/> instance via
    /// fluent code, the same way <c>RedisConfigurationBuilder</c> does for the Redis integration.
    /// </summary>
    public class NCacheConfigurationBuilder
    {
        private readonly IList<NCacheOptions.ServerConfig> _servers = new List<NCacheOptions.ServerConfig>();
        private string _cacheName;
        private const int DefaultPort = 9800;

        /// <summary>
        /// Initializes a new instance of the <see cref="NCacheConfigurationBuilder"/> class.
        /// </summary>
        /// <param name="configurationKey">The configuration key.</param>
        /// <exception cref="ArgumentException">If <paramref name="configurationKey"/> is null or whitespace.</exception>
        public NCacheConfigurationBuilder(string configurationKey)
        {
            if (string.IsNullOrWhiteSpace(configurationKey))
            {
                throw new ArgumentException("Configuration key must not be null or empty.", nameof(configurationKey));
            }

        }

        /// <summary>
        /// Sets the name of the NCache cache instance. The cache must already exist on the server.
        /// </summary>
        /// <param name="cacheName">The cache name.</param>
        /// <returns>The builder.</returns>
        public NCacheConfigurationBuilder WithCacheName(string cacheName)
        {
            _cacheName = cacheName;
            return this;
        }

        /// <summary>
        /// Adds an NCache server node to the connection configuration, using the default port (<c>9800</c>).
        /// <para>Call this multiple times to add multiple server nodes.</para>
        /// <para>To use a non-default port, use <see cref="WithEndpoint(string, int)"/> instead.</para>
        /// </summary>
        /// <param name="host">The IP address of the NCache server.</param>
        /// <returns>The builder.</returns>
        public NCacheConfigurationBuilder WithEndpoint(string host) => WithEndpoint(host, DefaultPort);

        /// <summary>
        /// Adds an NCache server node to the connection configuration.
        /// <para>Call this multiple times to add multiple server nodes.</para>
        /// </summary>
        /// <param name="host">The IP address of the NCache server.</param>
        /// <param name="port">The port of the NCache server.</param>
        /// <returns>The builder.</returns>
        public NCacheConfigurationBuilder WithEndpoint(string host, int port)
        {
            _servers.Add(new NCacheOptions.ServerConfig { Ip = host, Port = port });
            return this;
        }

        /// <summary>
        /// Creates the <see cref="NCacheOptions"/> out of the currently specified properties.
        /// </summary>
        /// <returns>The <see cref="NCacheOptions"/>.</returns>
        internal NCacheOptions Build() =>
            new NCacheOptions
            {
                CacheName = _cacheName,
                ServerList = new List<NCacheOptions.ServerConfig>(_servers)
            };
    }
}
