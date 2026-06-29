using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NCache.OSS.AspNetCore.Authentication.TicketStore
{
    /// <summary>
    /// NCache-backed implementation of <see cref="ITicketStore"/> for distributed authentication ticket storage.
    /// </summary>
    public class NCacheTicketStore : ITicketStore, IDisposable
    {
        /// <summary>
        /// ICache instance for interaction with cache.
        /// </summary>
        private readonly ICache _cache;

        /// <summary>
        /// Configuration options used to initialize and connect to the NCache instance.
        /// </summary>
        private readonly NCacheOptions _options;

        /// <summary>
        /// Default failsafe expiration, it is used when the AuthenticationTicket expiration settings is null
        /// </summary>
        private readonly Expiration _defaultExpiration;

        /// <summary>
        /// Key prefix for AuthenticationTicket.
        /// </summary>
        private const string _keyPrefix = "NCacheAuthTicket:";

        /// <summary>
        /// Logger used to record diagnostic and operational information.
        /// </summary>
        private readonly ILogger<NCacheTicketStore> _logger;

        /// <summary>
        /// Default expiration type for NCache.
        /// </summary>
        private static readonly ExpirationType defaultExpirationType = ExpirationType.Absolute;

        /// <summary>
        /// Default expiration time for NCache
        /// </summary>
        private static readonly TimeSpan defaultExpirationTime = TimeSpan.FromDays(14);

        /// <summary>
        /// Initializes a new instance of the <see cref="NCacheTicketStore"/> class.
        /// </summary>
        /// <param name="options">
        /// Configuration options used to initialize and connect to the NCache instance.
        /// </param>
        /// <param name="logger">
        /// Logger used to record diagnostic and operational information.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        public NCacheTicketStore(NCacheOptions options, ILogger<NCacheTicketStore> logger)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _options = options;
            _logger = logger;

            try
            {
                _logger.LogInformation("Initializing NCache Ticket Store");

                _cache = CacheManager.GetCache(_options.CacheName, _options.GetCacheConnectionOptions());

                _logger.LogInformation("NCache Ticket Store initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize NCache Ticket Store");
                throw;
            }

            _defaultExpiration = new Expiration(defaultExpirationType, defaultExpirationTime);
        }

        /// <summary>
        /// Removes the authentication ticket associated with the specified key.
        /// </summary>
        /// <param name="key">
        /// The key identifying the authentication ticket to remove.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous remove operation.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="key"/> is <see langword="null"/>
        /// </exception>
        public Task RemoveAsync(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            string storeKey = MarkKey(key);

            try
            {
                _cache.Remove(storeKey);

                _logger.LogInformation($"RemoveAsync method successfully called for key: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call RemoveAsync method successfully");
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Updates the authentication ticket associated with the specified key and refreshes its expiration in the cache.
        /// </summary>
        /// <param name="key">
        /// The key identifying the authentication ticket to renew.
        /// </param>
        /// <param name="ticket">
        /// The authentication ticket containing the updated authentication state.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous renew operation.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="key"/> is null or empty, or when
        /// <paramref name="ticket"/> is null.
        /// </exception>
        public Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            if (ticket == null) throw new ArgumentNullException(nameof(ticket));

            string storeKey = MarkKey(key);

            try
            {
                var serializedTicket = TicketSerializer.Default.Serialize(ticket);

                CacheItem cacheItem = new CacheItem(serializedTicket);

                cacheItem.Expiration = GetExpiration(ticket);

                _cache.Insert(storeKey, cacheItem);

                _logger.LogInformation($"RenewAsync method successfully called for key: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call RenewAsync method successfully");
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves the authentication ticket associated with the specified key.
        /// </summary>
        /// <param name="key">
        /// The key identifying the authentication ticket to retrieve.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous retrieve operation. The task result contains the <see cref="AuthenticationTicket"/> associated with the specified key, or <see langword="null"/> if no ticket exists for the key.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="key"/> is null or empty.
        /// </exception>
        public Task<AuthenticationTicket> RetrieveAsync(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            string storeKey = MarkKey(key);

            try
            {
                var ticket = _cache.Get<byte[]>(storeKey);

                if (ticket == null)
                    return Task.FromResult<AuthenticationTicket>(null);

                var deserializaedTicket = TicketSerializer.Default.Deserialize(ticket);

                _logger.LogInformation($"RetrieveAsync method successfully called for key: {key}");

                return Task.FromResult(deserializaedTicket);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call RetrieveAsync method successfully");
                throw;
            }
        }

        /// <summary>
        /// Stores an authentication ticket in the cache and returns a unique key that can be used to retrieve it later.
        /// </summary>
        /// <param name="ticket">
        /// The authentication ticket to store.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous store operation. The task result contains the generated key used to reference the stored ticket.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="ticket"/> is <see langword="null"/>.
        /// </exception>
        public Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));

            var key = Guid.NewGuid().ToString("N");
            var storeKey = MarkKey(key);

            try
            {
                var serializedTicket = TicketSerializer.Default.Serialize(ticket);

                var cacheItem = new CacheItem(serializedTicket)
                {
                    Expiration = GetExpiration(ticket)
                };

                _cache.Insert(storeKey, cacheItem);

                _logger.LogInformation($"StoreAsync method successfully called for ticket: {ticket.Principal?.Identity?.Name}");

                return Task.FromResult(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call StoreAsync method successfully");
                throw;
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="NCacheTicketStore"/>.
        /// </summary>
        /// <remarks>
        /// Disposes the underlying NCache cache instance. After calling this method,
        /// the ticket store should no longer be used.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                _cache.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispose NCache Ticket Store");
                throw;
            }
        }

        #region Private methods

        /// <summary>
        /// Determines the expiration policy for the cached authentication ticket.
        /// </summary>
        /// <param name="ticket">
        /// The authentication ticket containing expiration metadata.
        /// </param>
        /// <returns>
        /// An <see cref="Expiration"/> instance derived from the ticket's <see cref="AuthenticationProperties.ExpiresUtc"/>
        /// value when available and valid; otherwise, the default expiration configured for the store.
        /// </returns>
        /// <remarks>
        /// If <see cref="AuthenticationProperties.ExpiresUtc"/> is set, a TTL is calculated relative to <see cref="DateTimeOffset.UtcNow"/>.
        /// If the resulting TTL is positive, it is applied as the expiration duration.
        /// Otherwise, the configured default expiration is used.
        /// </remarks>
        private Expiration GetExpiration(AuthenticationTicket ticket)
        {
            if (ticket.Properties != null && ticket.Properties.ExpiresUtc.HasValue)
            {
                var expiresUtc = ticket.Properties?.ExpiresUtc;

                if (expiresUtc.HasValue)
                {
                    var ttl = expiresUtc.Value - DateTimeOffset.UtcNow;

                    if (ttl > TimeSpan.Zero)
                        return new Expiration(ExpirationType.Absolute, ttl);
                }
            }

            _logger.LogDebug("AuthenticationTicket did not contain ExpiresUtc. Using fallback expiration.");

            return _defaultExpiration;
        }

        /// <summary>
        /// Prefixes a cache key to avoid collisions with other cached entries.
        /// </summary>
        /// <param name="key">
        /// The original cache key.
        /// </param>
        /// <returns>
        /// A namespaced cache key with the configured prefix applied.
        /// </returns>
        private string MarkKey(string key)
        {
            return $"{_keyPrefix}:{key}";
        }

        #endregion
    }
}