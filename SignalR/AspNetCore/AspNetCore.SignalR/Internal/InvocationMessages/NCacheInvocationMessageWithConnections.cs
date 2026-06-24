using System;
using System.Collections.Generic;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{
    [Serializable]
    internal class NCacheInvocationMessageWithConnections : NCacheInvocationMessage
    {
        private readonly bool _exclude;
        public IReadOnlyList<string> ConnectionIds { get; }
        public bool Exclude { get { return _exclude; } }
        public NCacheInvocationMessageWithConnections(string target, object[] arguments, IReadOnlyList<string> connectionIds, bool exclude) : base(target, arguments)
        {
            ConnectionIds = connectionIds;
            _exclude = exclude;
        }
    }
}
