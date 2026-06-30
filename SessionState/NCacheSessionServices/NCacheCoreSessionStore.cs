using Alachisoft.NCache.Web.SessionState.Serialization;

namespace Alachisoft.NCache.Web.SessionState
{
    internal class NCacheCoreSessionStore : SessionStoreBase
    {
        protected override object DeserializeSession(byte[] buffer, int timeout, bool isCompact = false, bool isJson = false)
        {
            return SessionSerializer.Deserialize(buffer, isCompact, isJson);
        }

        protected override byte[] SerializeSession(object sessionData, bool isCompact = false, bool isJson = false)
        {
            NCacheSessionData session = sessionData as NCacheSessionData;
            return SessionSerializer.Serialize(session, isCompact, isJson);
        }

        public override object CreateNewStoreData(IAspEnvironmentContext context, int timeOut)
        {
            return new NCacheSessionData();
        }

        protected override object CreateEmptySession(IAspEnvironmentContext context, int sessionTimeout)
        {
            var session = new NCacheSessionData();
            session.Items.Add(NCacheStatics.EmptySessionFlag, null);
            return session;
        }
    }
}
