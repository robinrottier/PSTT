using System;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    /// <summary>
    /// Executes callbacks on the thread pool.
    /// Can optionally wait for completion or fire-and-forget.
    /// </summary>
    public class ThreadPoolDispatcher : ICallbackDispatcher
    {
        private readonly bool _waitForCompletion;

        public ThreadPoolDispatcher(bool waitForCompletion = false)
        {
            _waitForCompletion = waitForCompletion;
        }

        public string Name => _waitForCompletion ? "ThreadPool(wait)" : "ThreadPool(fire-and-forget)";

        public bool WaitsForCompletion => _waitForCompletion;

        public async Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var task = Task.Run(callback, cancellationToken);
            
            if (_waitForCompletion)
                await task;
            // else fire-and-forget
        }
    }
}
