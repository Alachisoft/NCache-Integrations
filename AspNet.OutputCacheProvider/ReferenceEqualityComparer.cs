using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alachisoft.NCache.OutputCacheProvider
{
    public class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        private ReferenceEqualityComparer() { }
        bool IEqualityComparer<object>.Equals(object x, object y)
        {
            return object.ReferenceEquals(x, y);
        }
        int IEqualityComparer<object>.GetHashCode(object obj)
        {
            if (obj == null) return 0;
            // Gets the identity hash code assigned by the runtime, bypassing overridden GetHashCode()
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
