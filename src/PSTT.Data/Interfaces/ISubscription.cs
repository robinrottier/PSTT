using System;
using System.Collections.Generic;
using System.Text;

namespace PSTT.Data
{
    public interface ISubscription<TKey, TValue> : IDisposable
    {
        TKey Key { get; }
        IStatus Status { get; }
        TValue Value { get; }
    }

}
