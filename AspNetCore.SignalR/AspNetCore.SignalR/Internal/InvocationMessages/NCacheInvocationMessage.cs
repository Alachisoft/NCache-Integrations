using System;

namespace Alachisoft.NCache.AspNetCore.SignalR.Internal
{
    [Serializable]
    internal class NCacheInvocationMessage
    {
        private readonly string _target;
        private object[] _arguments;
        public string Target
        {
            get { return _target; }
        }
        public object[] Arguments
        {
            get { return _arguments; }
        }
        public NCacheInvocationMessage(string target, object[] arguments)
        {
            _target = target;
            _arguments = arguments;
        }
    }
}
