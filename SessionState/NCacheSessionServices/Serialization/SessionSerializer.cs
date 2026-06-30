using System.Collections.Generic;
using System.IO;
using System.Text;
using Alachisoft.NCache.Common.Util;
using Alachisoft.NCache.IO;
using Alachisoft.NCache.Web.SessionStoreProvider;
using Newtonsoft.Json;

namespace Alachisoft.NCache.Web.SessionState.Serialization
{
    public static class SessionSerializer
    {
        public static byte[] Serialize(NCacheSessionData sessionData, bool isCompact = false, bool isJson = false)
        {
            if (!isJson)
            {
                using (var stream = new MemoryStream())
                {
                    using (var writer = new CompactBinaryWriter(stream))
                    {
                        SerializationUtility.SerializeDictionary(sessionData.Items, writer);
                    }
                    return stream.GetBuffer();
                }
            }
            else
            {
                var serializableObject = Newtonsoft.Json.JsonConvert.SerializeObject(sessionData.Items, JsonSessionSerializationSettings.Instance);
                return Encoding.UTF8.GetBytes(serializableObject);
            }
        }

        public static NCacheSessionData Deserialize(byte[] data, bool isCompact = false, bool isJson = false)
        {
            NCacheSessionData sessionData = new NCacheSessionData();

            if (!isJson)
            {
                using (var stream = new MemoryStream(data))
                {
                    using (var reader = new CompactBinaryReader(stream))
                    {
                        sessionData.Items = SerializationUtility.DeserializeDictionary<string, byte[]>(reader);
                    }
                }
            }
            else
            {
                sessionData.Items = JsonConvert.DeserializeObject<IDictionary<string, byte[]>>(Encoding.UTF8.GetString(data), JsonSessionSerializationSettings.Instance);
            }

            return sessionData;
        }

    }
}
