using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace NCache.OSS.RateLimiting
{
    public static class RateLimitingExtensions
    {
        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheConcurrencyLimiter(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
        string policyName,
        Action<ConcurrencyRateLimiterOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configureOptions);

            var partitionKey = new PolicyNameKey
            {
                PolicyName = policyName
            };

            var limiterOptions =
                new ConcurrencyRateLimiterOptions();

            configureOptions(limiterOptions);

            return options.AddPolicy(policyName, context =>
            {
                return RateLimitPartition
                    .GetConcurrencyRateLimiter(
                        partitionKey,
                        _ => limiterOptions);
            });
        }

        /// <summary>
        /// Registers an NCache concurrency limiter policy using configuration binding.
        /// </summary>
        /// <param name="options">The rate limiter options to register the policy against.</param>
        /// <param name="policyName">The name of the policy being registered.</param>
        /// <param name="configSection">The configuration section used to bind <see cref="ConcurrencyRateLimiterOptions"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/>, <paramref name="policyName"/>, or <paramref name="configSection"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="ConcurrencyRateLimiterOptions"/> cannot be bound from configuration.</exception>
        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheConcurrencyLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            IConfigurationSection configSection)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configSection);

            var boundOptions = configSection.Get<ConcurrencyRateLimiterOptions>();

            if (boundOptions == null)
                throw new InvalidOperationException("Failed to bind ConcurrencyRateLimiterOptions from configuration.");

            return options.AddNCacheConcurrencyLimiter(policyName, limiter =>
            {
                limiter.CacheName = boundOptions.CacheName;
                limiter.Port = boundOptions.Port;

                foreach (var server in boundOptions.ServerList)
                {
                    limiter.ServerList.Add(new RateLimiterOptions.ServerConfig
                    {
                        Ip = server.Ip,
                        Port = server.Port
                    });
                }

                limiter.PermitLimit = boundOptions.PermitLimit;
                limiter.QueueLimit = boundOptions.QueueLimit;
                limiter.TryDequeuePeriod = boundOptions.TryDequeuePeriod;
                limiter.ExpectedRequestTimeout = boundOptions.ExpectedRequestTimeout;
                limiter.LockTimeout = boundOptions.LockTimeout;
            });
        }

        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheFixedWindowLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            Action<FixedWindowLimiterOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configureOptions);

            var partitionKey = new PolicyNameKey
            {
                PolicyName = policyName
            };

            var limiterOptions = new FixedWindowLimiterOptions();

            configureOptions(limiterOptions);

            return options.AddPolicy(policyName, context =>
            {
                return RateLimitPartition
                    .GetFixedWindowRateLimiter(
                        partitionKey,
                        _ => limiterOptions);
            });
        }

        /// <summary>
        /// Registers an NCache fixed window limiter policy using configuration binding.
        /// </summary>
        /// <param name="options">The rate limiter options to register the policy against.</param>
        /// <param name="policyName">The name of the policy being registered.</param>
        /// <param name="configSection">The configuration section used to bind <see cref="FixedWindowLimiterOptions"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/>, <paramref name="policyName"/>, or <paramref name="configSection"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="FixedWindowLimiterOptions"/> cannot be bound from configuration.</exception>
        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheFixedWindowLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            IConfigurationSection configSection)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configSection);

            var boundOptions = configSection.Get<FixedWindowLimiterOptions>();

            if (boundOptions == null)
                throw new InvalidOperationException("Failed to bind FixedWindowLimiterOptions from configuration.");

            return options.AddNCacheFixedWindowLimiter(policyName, limiter =>
            {
                limiter.CacheName = boundOptions.CacheName;
                limiter.Port = boundOptions.Port;

                foreach (var server in boundOptions.ServerList)
                {
                    limiter.ServerList.Add(new RateLimiterOptions.ServerConfig
                    {
                        Ip = server.Ip,
                        Port = server.Port
                    });
                }

                limiter.Window = boundOptions.Window;
                limiter.PermitLimit = boundOptions.PermitLimit;
                limiter.LockTimeout = boundOptions.LockTimeout;
            });
        }

        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheTokenBucketLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            Action<TokenBucketLimiterOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configureOptions);

            var partitionKey = new PolicyNameKey
            {
                PolicyName = policyName
            };

            var limiterOptions = new TokenBucketLimiterOptions();

            configureOptions(limiterOptions);

            return options.AddPolicy(policyName, context =>
            {
                return RateLimitPartition
                    .GetTokenBucketRateLimiter(
                        partitionKey,
                        _ => limiterOptions);
            });
        }

        /// <summary>
        /// Registers an NCache token bucket limiter policy using configuration binding.
        /// </summary>
        /// <param name="options">The rate limiter options to register the policy against.</param>
        /// <param name="policyName">The name of the policy being registered.</param>
        /// <param name="configSection">The configuration section used to bind <see cref="TokenBucketLimiterOptions"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/>, <paramref name="policyName"/>, or <paramref name="configSection"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="TokenBucketLimiterOptions"/> cannot be bound from configuration.</exception>
        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheTokenBucketLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            IConfigurationSection configSection)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configSection);

            var boundOptions = configSection.Get<TokenBucketLimiterOptions>();

            if (boundOptions == null)
                throw new InvalidOperationException("Failed to bind TokenBucketLimiterOptions from configuration.");

            return options.AddNCacheTokenBucketLimiter(policyName, limiter =>
            {
                limiter.CacheName = boundOptions.CacheName;
                limiter.Port = boundOptions.Port;

                foreach (var server in boundOptions.ServerList)
                {
                    limiter.ServerList.Add(new RateLimiterOptions.ServerConfig
                    {
                        Ip = server.Ip,
                        Port = server.Port
                    });
                }

                limiter.TokenLimit = boundOptions.TokenLimit;
                limiter.TokensPerPeriod = boundOptions.TokensPerPeriod;
                limiter.ReplenishmentPeriod = boundOptions.ReplenishmentPeriod;
                limiter.LockTimeout = boundOptions.LockTimeout;
            });
        }

        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheSlidingWindowLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            Action<SlidingWindowLimiterOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configureOptions);

            var partitionKey = new PolicyNameKey { PolicyName = policyName };
            var limiterOptions = new SlidingWindowLimiterOptions();
            configureOptions(limiterOptions);

            return options.AddPolicy(policyName, context =>
            {
                return RateLimitPartition.GetSlidingWindowRateLimiter(partitionKey, _ => limiterOptions);
            });
        }

        /// <summary>
        /// Registers an NCache sliding window limiter policy using configuration binding.
        /// </summary>
        /// <param name="options">The rate limiter options to register the policy against.</param>
        /// <param name="policyName">The name of the policy being registered.</param>
        /// <param name="configSection">The configuration section used to bind <see cref="SlidingWindowLimiterOptions"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/>, <paramref name="policyName"/>, or <paramref name="configSection"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="SlidingWindowLimiterOptions"/> cannot be bound from configuration.</exception>
        public static Microsoft.AspNetCore.RateLimiting.RateLimiterOptions AddNCacheSlidingWindowLimiter(
            this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options,
            string policyName,
            IConfigurationSection configSection)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(policyName);
            ArgumentNullException.ThrowIfNull(configSection);

            var boundOptions = configSection.Get<SlidingWindowLimiterOptions>();

            if (boundOptions == null)
                throw new InvalidOperationException("Failed to bind SlidingWindowLimiterOptions from configuration.");

            return options.AddNCacheSlidingWindowLimiter(policyName, limiter =>
            {
                limiter.CacheName = boundOptions.CacheName;
                limiter.Port = boundOptions.Port;

                foreach (var server in boundOptions.ServerList)
                {
                    limiter.ServerList.Add(new RateLimiterOptions.ServerConfig
                    {
                        Ip = server.Ip,
                        Port = server.Port
                    });
                }

                limiter.PermitLimit = boundOptions.PermitLimit;
                limiter.Window = boundOptions.Window;
                limiter.LockTimeout = boundOptions.LockTimeout;
            });
        }

        private sealed class PolicyNameKey
        {
            public string PolicyName { get; set; } = string.Empty;

            public override string ToString()
            {
                return PolicyName;
            }
        }
    }
}