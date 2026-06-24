using Alachisoft.NCache.Client;
using System;
using System.Collections.Generic;
using System.Text;

namespace Alachisoft.NCache.AspNetCore.SignalR
{
    public class SignalRConnectionOptions : ICloneable
    {
        internal Client.CacheConnectionOptions _cacheConnectionOption = null;
        //Dirty flags are set without even checking null value of default Read and write thru providers, so we need to only call setter if they are not null
        private string _clientBindIp;

        private bool? _loadBalance;
        private bool? _enableClientLogs;
        private int? _connectionRetries;

        internal int? _clientRequestTimeout;
        internal int? _retryInterval;
        internal int? _connectionTimeout;
        internal int? _retryConnectionDelay;
        private LogLevel? _logLevel;
        private SignalRServerInfo[]? _serverList;

        public SignalRConnectionOptions()
        {
            _cacheConnectionOption = new Client.CacheConnectionOptions();
        }

        internal SignalRConnectionOptions(Client.CacheConnectionOptions cacheConnectionOptions)
        {
            _cacheConnectionOption = cacheConnectionOptions;
        }

        /// <summary>
        /// Gets/Sets List of servers provided by the user
        /// </summary>
        public SignalRServerInfo[] ServerList
        {
            get { return _serverList; }
            set
            {
                if (value == null)
                    return;
                _serverList = value;
                _cacheConnectionOption.ServerList = GetServerInfoList(value);
            }
        }


        /// <summary>
        /// Gets/Sets the IP for the client to be binded with
        /// </summary>
        public string ClientBindIP
        {
            get { return _clientBindIp; }
            set
            {
                if (value != null)
                {
                    _clientBindIp = value;
                    _cacheConnectionOption.ClientBindIP = value;
                }
            }
        }

        /// <summary>
        /// When this flag is set, client tries to connect to the optimum server in terms of number of connected clients.
        /// <para>
        /// This way almost equal number of clients are connected to every node in the clustered cache and no single node 
        /// is overburdened.
        /// </para>
        /// </summary>
        public bool? LoadBalance
        {
            get { return _loadBalance; }
            set
            {
                if (value.HasValue)
                {
                    _loadBalance = value;
                    _cacheConnectionOption.LoadBalance = value;
                }
            }
        }

        /// <summary>
        /// Clients operation timeout specified in seconds.
        /// Clients wait for the response from the server for this time. 
        /// If the response is not received within this time, the operation is not successful.
        /// <para>
        /// Based on the network conditions, OperationTimeout value can be adjusted. 
        /// The default value is 90 seconds.
        /// </para>
        /// </summary>
        public int? ClientRequestTimeOut
        {
            get { return _clientRequestTimeout; }
            set
            {
                if (value.HasValue)
                {
                    _clientRequestTimeout = value;
                    _cacheConnectionOption.ClientRequestTimeOut = TimeSpan.FromSeconds((int)value);
                }
            }
        }

        /// <summary>
        /// Client's connection timeout specified in seconds.
        /// </summary>
        public int? ConnectionTimeout
        {
            get { return _connectionTimeout; }
            set
            {
                if (value.HasValue)
                {
                    _connectionTimeout = value;
                    _cacheConnectionOption.ConnectionTimeout = TimeSpan.FromSeconds((int)value);
                }
            }
        }

        /// <summary>
        /// Number of tries to re-establish a broken connection between client and server.
        /// </summary>
        public int? ConnectionRetries
        {
            get { return _connectionRetries; }
            set
            {
                if (value.HasValue)
                {
                    _connectionRetries = value;
                    _cacheConnectionOption.ConnectionRetries = value;
                }
            }
        }

        /// <summary>
        /// Time in seconds to wait between two connection retries.
        /// </summary>
        public int? RetryInterval
        {
            get { return _retryInterval; }
            set
            {
                if (value.HasValue)
                {
                    _retryInterval = value;
                    _cacheConnectionOption.RetryInterval = TimeSpan.FromSeconds((int)value);
                }
            }
        }

        /// <summary>
        /// The time after which client will try to reconnect to the server.
        /// </summary>
        public int? RetryConnectionDelay
        {
            get { return _retryConnectionDelay; }
            set
            {
                if (value.HasValue)
                {
                    _retryConnectionDelay = value;
                    _cacheConnectionOption.RetryConnectionDelay = TimeSpan.FromSeconds((int)value);
                }
            }
        }

        /// <summary>
        /// If different client applications are connected to server and because of any issue which results in connection failure with server, after the client again establishes connection “AppName” is used to identify these different client applications.
        /// <para>
        /// Data type is string. Its optional.If value is not set it takes the value of the process id.
        /// </para>
        /// </summary>
        public string AppName
        {
            get { return _cacheConnectionOption.AppName; }
            set
            {
                _cacheConnectionOption.AppName = value;
            }
        }


        /// <summary>
        /// Sets the log level either as Info, Error or Debug
        /// </summary>
        public Client.LogLevel? LogLevel
        {
            get { return _logLevel; }
            set
            {
                if (value.HasValue)
                {
                    _logLevel = value;
                    _cacheConnectionOption.LogLevel = value;
                }
            }
        }

        /// <summary>
        /// Enables client logs.
        /// </summary>
        public bool? EnableClientLogs
        {
            get { return _enableClientLogs; }
            set
            {
                if (value.HasValue)
                {
                    _enableClientLogs = value;
                    _cacheConnectionOption.EnableClientLogs = value;
                }
            }
        }

        public object Clone()
        {
            SignalRConnectionOptions _cloneParam = new SignalRConnectionOptions(_cacheConnectionOption.Clone() as Client.CacheConnectionOptions);
            return _cloneParam;
        }
        private IList<ServerInfo> GetServerInfoList(SignalRServerInfo[] list)
        {
            IList<ServerInfo> connectionOptionsList = new List<ServerInfo>();
            foreach (SignalRServerInfo temp in list)
            {
                connectionOptionsList.Add(temp.ServerInfo);
            }
            return connectionOptionsList;
        }


        internal Client.CacheConnectionOptions CacheConnectionOptions
        {
            get
            {
                return _cacheConnectionOption;
            }
        }
    }
}

