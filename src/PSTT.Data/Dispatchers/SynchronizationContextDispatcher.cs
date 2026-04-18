using System;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    /// <summary>
    /// Dispatches callbacks to a specific SynchronizationContext (e.g., GUI thread in WPF/WinForms/MAUI).
    /// Always waits for completion.
    /// </summary>
    public class SynchronizationContextDispatcher : ICallbackDispatcher
    {
        private readonly SynchronizationContext _context;

        public SynchronizationContextDispatcher(SynchronizationContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Creates a dispatcher for the current synchronization context.
        /// Throws if there is no current context.
        /// </summary>
        public static SynchronizationContextDispatcher ForCurrentContext()
        {
            var context = SynchronizationContext.Current 
                ?? throw new InvalidOperationException("No current SynchronizationContext available");
            return new SynchronizationContextDispatcher(context);
        }

        public string Name => $"SyncContext({_context.GetType().Name})";

        public bool WaitsForCompletion => true;

        public Task DispatchAsync(Func<Task> callback, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<bool>();

            _context.Post(async _ =>
            {
                try
                {
                    await callback();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);

            return tcs.Task;
        }
    }
}
