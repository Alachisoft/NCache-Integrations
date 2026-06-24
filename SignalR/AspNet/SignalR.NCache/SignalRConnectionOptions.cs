using Alachisoft.NCache.Client;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public class SignalRConnectionOptions : IConfigurationSectionHandler
    {

        #region IConfigurationSectionHandler
        public object Create(object parent, object configContext, XmlNode section)
        {
            CacheConnectionOptions _connectionOptions = new CacheConnectionOptions();
            foreach (XmlAttribute attr in section.Attributes)
            {
                
                if (attr.Name.Equals(nameof(CacheConnectionOptions.ClientBindIP), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.ClientBindIP = attr.Value.ToString();
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.AppName), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.AppName = attr.Value.ToString();
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.LoadBalance), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.LoadBalance = System.Convert.ToBoolean(attr.Value);
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.ConnectionRetries), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.ConnectionRetries = System.Convert.ToInt32(attr.Value);
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.EnableClientLogs), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.EnableClientLogs = System.Convert.ToBoolean(attr.Value);
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.ClientRequestTimeOut), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.ClientRequestTimeOut = TimeSpan.FromSeconds(double.Parse(attr.Value, CultureInfo.InvariantCulture));
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.ConnectionTimeout), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.ConnectionTimeout = TimeSpan.FromSeconds(double.Parse(attr.Value, CultureInfo.InvariantCulture));
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.RetryInterval), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.RetryInterval = TimeSpan.FromSeconds(double.Parse(attr.Value, CultureInfo.InvariantCulture));
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.RetryConnectionDelay), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.RetryConnectionDelay = TimeSpan.FromSeconds(double.Parse(attr.Value, CultureInfo.InvariantCulture));
                }
                else if (attr.Name.Equals(nameof(CacheConnectionOptions.LogLevel), StringComparison.InvariantCultureIgnoreCase))
                {
                    _connectionOptions.LogLevel = (LogLevel)Enum.Parse(typeof(LogLevel), attr.Value.ToString(), ignoreCase: true);
                }
                else
                {
                    throw new ConfigurationException("Unknown section attribute name:" + attr.Name);
                }

            }
            _connectionOptions.ServerList = new List<ServerInfo>();
            foreach (XmlNode child in section.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                ServerInfo _server = null;
                bool isNameSet = false;
                XmlNode a = child.Attributes.RemoveNamedItem("value");
                int PriorityCounter = 0;
                if (child.Name.Equals("server", StringComparison.InvariantCultureIgnoreCase))
                {
                    foreach (XmlAttribute childAttribute in child.Attributes)
                    {
                        if (childAttribute.Name.Equals(nameof(ServerInfo.Name), StringComparison.InvariantCultureIgnoreCase))
                        {
                            _server = new ServerInfo(childAttribute.Value.ToString());
                            isNameSet = true;
                        }
                        else if (childAttribute.Name.Equals(nameof(ServerInfo.Port), StringComparison.InvariantCultureIgnoreCase))
                        {
                            _server.Port = System.Convert.ToInt32(childAttribute.Value);
                        }
                        else
                        {
                            throw new ConfigurationException("Unknown child node attribute name:" + childAttribute.Name);
                        }
                    }
                    if (!isNameSet)
                    {
                        throw new ConfigurationException("Missing required attribute name:" + nameof(ServerInfo.Name));
                    }
                    if (!_connectionOptions.ServerList.Contains(_server))
                        _connectionOptions.ServerList.Add(_server);
                }
                else
                {
                    throw new ConfigurationException(
                        "Unknown child node element name:" + child.Name);
                }
            }

            return _connectionOptions;
        }
        #endregion
    }
}
