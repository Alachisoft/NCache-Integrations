using System;
using System.Collections.Generic;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{
    [Serializable]
    internal class NCacheInvocationMessageWithUserIds : NCacheInvocationMessage
    {
        public IReadOnlyList<string> UserIds { get; }
        public NCacheInvocationMessageWithUserIds(string target, object[] arguments, IReadOnlyList<string> userIds) : base(target, arguments)
        {
            UserIds = userIds;
        }
    }
}
