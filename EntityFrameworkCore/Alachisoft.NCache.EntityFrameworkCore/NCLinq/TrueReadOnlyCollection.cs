using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alachisoft.NCache.EntityFrameworkCore.NCLinq
{
    internal sealed class TrueReadOnlyCollection<T> : ReadOnlyCollection<T>
    {
        /// <summary>
        /// Creates instance of TrueReadOnlyCollection, wrapping passed in array.
        /// </summary>
        public TrueReadOnlyCollection(params T[] list)
            : base(list)
        {
        }
    }
}
