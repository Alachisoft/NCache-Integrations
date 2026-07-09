using System;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{
    [Serializable]
    internal class NCacheInvocationMessageWrapper
    {
        private readonly InvocationMessageType _invocationMessageType;
        public NCacheInvocationMessage NCacheInvocationMessage { get; }
        public InvocationMessageType InvocationMessageType { get { return _invocationMessageType; } }
        public NCacheInvocationMessageWrapper(NCacheInvocationMessage nCacheInvocationMessage, InvocationMessageType invocationMessageType)
        {
            NCacheInvocationMessage = nCacheInvocationMessage;
            _invocationMessageType = invocationMessageType;
        }
    }
    internal enum InvocationMessageType
    {
        NCacheInvocationMessage,
        NCacheInvocationMessageWithConnections,
        NCacheInvocationMessageWithGroups,
        NCacheInvocationMessageWithGroupAndConnections,
        NCacheInvocationMessageWithUserIds
    }
}
