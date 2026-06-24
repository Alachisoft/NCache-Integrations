using System;
using System.Collections.Generic;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{ 
    [Serializable]
    internal class NCacheInvocationMessageWithGroups : NCacheInvocationMessage
    {
        public IReadOnlyList<string> GroupNames { get; }
        public NCacheInvocationMessageWithGroups(string target, object[] arguments, IReadOnlyList<string> groupNames) : base(target, arguments)
        {
            GroupNames = groupNames;
        }
    }
}
