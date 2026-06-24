using CacheManager.Core.Internal;
using System;
using System.Collections.Generic;
using System.Text;

namespace NCache.OSS.CacheManager.Core
{
    [Serializable]
    public class NCacheBackplaneEvent
    {
        public string Region { get; set; }

        public CacheItemChangedEventAction ChangeAction {get; set;}

        public String Key { get; set; }

        public byte[] OwnerIdentity { get; set; }

        public BackplaneAction Action { get; set; }

        public static NCacheBackplaneEvent From(BackplaneMessage message)
        {
            if (message == null) return null;

            return new NCacheBackplaneEvent
            {
                Key = message.Key,
                Region = message.Region,
                ChangeAction = message.ChangeAction,
                Action = message.Action,
                OwnerIdentity = message.OwnerIdentity
            };
        }

        public BackplaneMessage ToBackplaneMessage()
        {
            switch (Action)
            {
                case BackplaneAction.Changed:
                    if (string.IsNullOrEmpty(Region))
                        return BackplaneMessage.ForChanged(OwnerIdentity, Key, ChangeAction);
                    else
                        return BackplaneMessage.ForChanged(OwnerIdentity, Key, Region, ChangeAction);

                case BackplaneAction.Clear:
                    return BackplaneMessage.ForClear(OwnerIdentity);

                case BackplaneAction.ClearRegion:
                    return BackplaneMessage.ForClearRegion(OwnerIdentity, Region);

                case BackplaneAction.Removed:
                    if (string.IsNullOrEmpty(Region))
                        return BackplaneMessage.ForRemoved(OwnerIdentity, Key);
                    else
                        return BackplaneMessage.ForRemoved(OwnerIdentity, Key, Region);

                default:
                    throw new NotSupportedException($"Unsupported action: {Action}");
            }
        }
    }
}
