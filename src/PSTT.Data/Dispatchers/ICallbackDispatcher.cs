using System;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    /// <summary>
    /// Abstraction for how callbacks are dispatched to subscribers.
    /// Allows different execution strategies: synchronous, thread pool, GUI thread, batched, etc.
    /// </summary>
    public interface ICallbackDispatcher
    {
        /// <summary>
        /// Dispatches a callback for execution.
        /// </summary>
        /// <param name="callback">The callback to execute</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>Task representing the dispatch operation (may not wait for callback completion)</returns>
        Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken = default);

        /// <summary>
        /// Name/description of this dispatcher for debugging/logging
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether this dispatcher waits for callbacks to complete before returning
        /// </summary>
        bool WaitsForCompletion { get; }
    }
}
