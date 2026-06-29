using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace NCache.OSS.AspNetCore.Authentication.TicketStore
{
    /// <summary>
    /// Extension methods for registering NCache ticket store services in DI.
    /// </summary>
    public static class NCacheServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the NCache-based <see cref="ITicketStore"/> using a configuration delegate.
        /// </summary>
        /// <param name="services">
        /// The service collection to register dependencies into.
        /// </param>
        /// <param name="configure">
        /// A delegate used to configure <see cref="NCacheOptions"/>.
        /// </param>
        /// <returns>
        /// The updated <see cref="IServiceCollection"/> instance.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required configuration values are missing or invalid.
        /// </exception>
        public static IServiceCollection AddNCacheTicketStore(
            this IServiceCollection services,
            Action<NCacheOptions> configure)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (configure == null)
                throw new ArgumentNullException(nameof(configure));


            var options = new NCacheOptions();

            configure(options);

            if (string.IsNullOrWhiteSpace(options.CacheName))
                throw new ArgumentException("CacheName is required", nameof(options));

            if (!options.isValid(out var error))
                throw new ArgumentException(error, nameof(options));

            services.AddSingleton<NCacheOptions>(options);

            services.AddSingleton<ITicketStore, NCacheTicketStore>();

            return services;
        }

        /// <summary>
        /// Registers the NCache-based <see cref="ITicketStore"/> using configuration binding.
        /// </summary>
        /// <param name="services">
        /// The service collection to register dependencies into.
        /// </param>
        /// <param name="configSection">
        /// The configuration section used to bind <see cref="NCacheOptions"/>.
        /// </param>
        /// <returns>
        /// The updated <see cref="IServiceCollection"/> instance.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/> or <paramref name="configSection"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="NCacheOptions"/> cannot be bound from configuration.
        /// </exception>
        public static IServiceCollection AddNCacheTicketStore(
            this IServiceCollection services,
            IConfigurationSection configSection)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            if (configSection == null) throw new ArgumentNullException(nameof(configSection));

            var options = configSection.Get<NCacheOptions>();

            if (options == null) throw new InvalidOperationException("Failed to bind NCacheOptions from configuration.");

            return services.AddNCacheTicketStore(opt =>
            {
                opt.CacheName = options.CacheName;
                opt.Port = options.Port;

                foreach (var server in options.ServerList)
                {
                    opt.ServerList.Add(new NCacheOptions.ServerConfig
                    {
                        Ip = server.Ip,
                        Port = server.Port
                    });
                }
            });
        }
    }
}