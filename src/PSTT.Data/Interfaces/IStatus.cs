using System;
using System.Collections.Generic;
using System.Text;

namespace PSTT.Data
{
    public interface IStatus
    {
        enum StateValue
        {
            Pending,        // not in cache, request sent upstream, waiting for first value
            Active,         // actively receiving updates
            Stale,          // cached value is outdated, waiting for update from upstream...may still receive updates
            Failed          // failed to retrieve value or bad request or dropped ...no further updates
        }
        StateValue State { get; set; }
        string? Message { get; set; }
        int Code {  get; set; }

        public bool IsPending => State == IStatus.StateValue.Pending;
        public bool IsActive => State == IStatus.StateValue.Active;
        public bool IsStale => State == IStatus.StateValue.Stale;
        public bool IsFailed => State == IStatus.StateValue.Failed;

    }
}
