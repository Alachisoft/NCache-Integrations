using Alachisoft.NCache.Client;
using System;
using System.Collections.Generic;
using System.Text;

namespace NCache.ZiggyCreatures.FusionCache.Backplane.Configuration
{

    public class FusionCacheServerInfo
    {

        private int? _port;
        private string _name;

        /// <summary>
        /// Initializes the instance of <see cref="Alachisoft.NCache.AspNetCore.SignalR.SignalRServerInfo"/> for this application.
        /// </summary>
        public FusionCacheServerInfo()
        { }

        public FusionCacheServerInfo(string name, int? port = null)
        {
            _name = name;
            _port = port;
        }

        internal ServerInfo ServerInfo
        {
            get
            {
                if (_name != null && _port != null)
                    return new ServerInfo(_name, (int)_port);
                else if (_name != null && _port == null)
                    return new ServerInfo(_name); // Fixing ambigious call error
                return null;
            }
            set
            {
                _port = value.Port;
                _name = value.Name;
            }
        }

        /// <summary>
        /// Get/Set the Port 
        /// </summary>
        public int? Port
        {
            get { return _port; }
            set
            {
                if (value.HasValue)
                    _port = value;
            }
        }
        /// <summary>
        /// Get the Name/IP of the server
        /// </summary>
        public string Name
        {
            get { return _name; }
            set
            {
                _name = value;
            }
        }
    }
}
