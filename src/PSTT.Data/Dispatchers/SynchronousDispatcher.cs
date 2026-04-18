using System;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    /// <summary>
    /// Executes callbacks synchronously on the calling thread.
    /// Always waits for completion. Useful for testing or when deterministic execution is required.
    /// </summary>
    public class SynchronousDispatcher : ICallbackDispatcher
    {
        public string Name => "Synchronous";

        public bool WaitsForCompletion => true;

        public async Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await callback();
        }
    }
}
