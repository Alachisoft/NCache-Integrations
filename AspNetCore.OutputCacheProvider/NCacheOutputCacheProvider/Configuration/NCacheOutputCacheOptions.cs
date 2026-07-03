using Alachisoft.NCache.Client;
using System.Net;

namespace NCache.OSS.AspNetCore.OutputCaching;

/// <summary>
/// Options for configuring and connecting to the NCache.
/// </summary>
public class NCacheOutputCacheOptions
{
    /// <summary>
    /// The name of the cache you want to connect with. This will used for the initialization of the cache.
    /// Output of the API will be cached in the provided cache. Make sure cache is running.
    /// </summary>
    public string? CacheName { get; set; }

    /// <summary>
    /// Gets/Sets list of servers provided by user
    /// </summary>
    public IList<ServerConfig> ServerList { get; set; } = new List<ServerConfig>();

    /// <summary>
    /// This property set to true will create logs of NCache provider. This logger will be initialized at the initialization of cache.
    /// By default logging is disabled.
    /// </summary>
    public bool EnabledLogs { get; set; } = false;

    /// <summary>
    /// Should NCache detailed logs be enabled
    /// </summary>
    public bool? EnableDetailLogs { get; set; } = false;

    /// <summary>
    /// Represents the configuration settings required to connect to a server, including its IP address and port number.
    /// </summary>
    public record ServerConfig
    {
        /// <summary>
        /// Gets or sets the IP address associated with this instance.
        /// </summary>
        public string Ip { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the network port number used for connections.
        /// </summary>
        public int Port { get; set; } = 9800;

        /// <summary>
        /// Creates a new instance of the ServerInfo class using the current IP address and port.
        /// </summary>
        /// <returns>A ServerInfo object initialized with the current IP address and port.</returns>
        internal ServerInfo ToServerInfo() => new ServerInfo(Ip, Port);

        /// <summary>
        /// Determines whether the current IP address and port configuration is valid.
        /// </summary>
        /// <param name="err">When this method returns, contains an error message describing the validation failure if the configuration
        /// is invalid; otherwise, an empty string.</param>
        /// <returns>true if both the IP address and port are valid; otherwise, false.</returns>
        internal bool isValid(out string err)
        {
            err = string.Empty;
            if (!IPAddress.TryParse(Ip, out var ipAddress))
            {
                err = $"IP address '{Ip}' is invalid.";
                return false;
            }
            if (Port <= 0 || Port > 65535)
            {
                err = $"Port number '{Port}' must be between 1 and 65535.";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Determines whether the current configuration is valid.
    /// </summary>
    /// <remarks>Validation fails if the local cache name, distributed cache name, or server list is
    /// missing or invalid. The method checks each server configuration for validity and returns the first
    /// encountered error.</remarks>
    /// <param name="err">When this method returns, contains an error message describing the first validation failure; otherwise, an
    /// empty string if the configuration is valid.</param>
    /// <returns>true if the configuration is valid; otherwise, false.</returns>
    internal bool isValid(out string err)
    {
        // Initialize the error message to an empty string
        err = string.Empty;

        // Validate that the CacheName property is not null or empty
        if (string.IsNullOrEmpty(CacheName))
        {
            err = "CacheName is required.";
            return false;
        }

        // Validate each server configuration in the ServerList
        if (ServerList != null && ServerList.Any())
        {
            foreach (var server in ServerList)
            {
                if (!server.isValid(out err))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Initializes and returns a new instance of the cache connection options based on the current configuration.
    /// </summary>
    /// <remarks>This method aggregates various configuration properties into a single options object
    /// for establishing a cache connection. If an error occurs during initialization, the method returns <see
    /// langword="null"/>.</remarks>
    /// <returns>A <see cref="CacheConnectionOptions"/> instance initialized with the current settings; or <see
    /// langword="null"/> if initialization fails.</returns>
    internal CacheConnectionOptions? getCacheConnectionOptions()
    {
        // Create a new instance of CacheConnectionOptions
        CacheConnectionOptions? cacheInitParams = null;
        try
        {
            // Initialize the cache connection options with the default configuration settings
            cacheInitParams = new CacheConnectionOptions();

            // Check if the ServerList property is not null or empty
            if (ServerList != null && ServerList.Any())
                // Convert each ServerConfig in the ServerList to a ServerInfo object and assign the resulting list to the ServerList property of cacheInitParams
                cacheInitParams.ServerList = ServerList.Select(s => s.ToServerInfo()).ToList();
        }
        catch (Exception ex)
        {
            // In case of any exception during initialization set cacheInitParams to null
            cacheInitParams = null;
        }

        // Return the initialized cache connection options, or null if initialization failed
        return cacheInitParams;
    }
}

