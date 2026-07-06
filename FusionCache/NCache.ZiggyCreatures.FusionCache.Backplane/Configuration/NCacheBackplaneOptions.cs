using Alachisoft.NCache.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NCache.ZiggyCreatures.FusionCache.Backplane.Configuration
{
    public class NCacheBackplaneOptions
    {
        public IList<FusionCacheServerInfo> ServerList { get; set; }
        public string? ClientBindIP { get; set; }

        public bool? LoadBalance { get; set; }

        public TimeSpan? ClientRequestTimeOut { get; set; }

        public TimeSpan? ConnectionTimeout { get; set; }

        public int? ConnectionRetries { get; set; }

        public TimeSpan? RetryInterval { get; set; }

        public TimeSpan? RetryConnectionDelay { get; set; }

        public string? AppName { get; set; }

        public bool? EnableClientLogs { get; set; }

        public LogLevel? LogLevel { get; set; }

        internal CacheConnectionOptions GetCacheConnectionOptions()
        {
            var options = new CacheConnectionOptions();

            if (AppName is not null)
                options.AppName = AppName;

            if (ClientBindIP is not null)
                options.ClientBindIP = ClientBindIP;

            if (LoadBalance.HasValue)
                options.LoadBalance = LoadBalance.Value;

            if (ClientRequestTimeOut.HasValue)
                options.ClientRequestTimeOut = ClientRequestTimeOut.Value;

            if (ConnectionTimeout.HasValue)
                options.ConnectionTimeout = ConnectionTimeout.Value;

            if (ConnectionRetries.HasValue)
                options.ConnectionRetries = ConnectionRetries.Value;

            if (RetryInterval.HasValue)
                options.RetryInterval = RetryInterval.Value;

            if (RetryConnectionDelay.HasValue)
                options.RetryConnectionDelay = RetryConnectionDelay.Value;

            if (EnableClientLogs.HasValue)
                options.EnableClientLogs = EnableClientLogs.Value;

            if (LogLevel.HasValue)
                options.LogLevel = LogLevel.Value;

            if (ServerList?.Count > 0)
            {
                options.ServerList = ServerList
                    .Select(x => x.ServerInfo)
                    .ToList();
            }

            return options;
        }
    }
}
