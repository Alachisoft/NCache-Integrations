using System;

namespace Alachisoft.NCache.AspNetCore.SignalR
{
    public class SignalRServerInfo
    {

        private int? _port;
        private string _name;

        /// <summary>
        /// Initializes the instance of <see cref="Alachisoft.NCache.AspNetCore.SignalR.SignalRServerInfo"/> for this application.
        /// </summary>
        public SignalRServerInfo()
        { }

        internal Client.ServerInfo ServerInfo
        {
            get
            {
                if (_name != null && _port != null)
                    return new Client.ServerInfo(_name, (int)_port);
                else if (_name != null && _port == null)
                    return new Client.ServerInfo(_name, 9800); // Fixing ambigious call error
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
