using Alachisoft.NCache.Client;
using Alachisoft.NCache.Runtime.Caching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace NCache.OSS.AspNetCore.DataProtection
{
    public static class DPXElementConverter
    {
        public static CacheItem ConvertToCacheItem(XElement element)
        {

            string encodedElement = Convert.ToBase64String(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));

            if (element.Value.IndexOf("revocation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new CacheItem(encodedElement);
            }
            string[] parts = element.Value.Split('Z');

            DateTime expirationTime = DateTime.Parse(parts[2]+'Z');
            DateTime timeNow = DateTime.UtcNow;
            TimeSpan expirationDuration = expirationTime - timeNow;

            var item = new CacheItem(encodedElement);
            item.Expiration = new Expiration(ExpirationType.Absolute, expirationDuration);

            return item;
        }
    }
}
