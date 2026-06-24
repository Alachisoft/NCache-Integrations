using System;
using System.Collections.Generic;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{
    [Serializable]
    internal class NCacheInvocationMessageWithGroupAndConnections : NCacheInvocationMessage
    {
        private readonly string _groupName;
        public string GroupName { get { return _groupName; } }
        public IReadOnlyList<string> ExcludedConnectionIds { get; }
        public NCacheInvocationMessageWithGroupAndConnections(string target, object[] arguments, string groupName, IReadOnlyList<string> excludedConnectionIds) : base(target, arguments)
        {
            _groupName = groupName;
            ExcludedConnectionIds = excludedConnectionIds;
        }
    }
}
