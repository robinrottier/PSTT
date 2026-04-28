using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    public interface ICache<TKey, TValue>
        where TKey : notnull
    {
        // client side value retrieval operations
        // -- snapshot retrieval
        TValue? GetValue(TKey key);
        bool TryGetValue(TKey key, out TValue? value);

        // -- subscribe and continue to get updates
        ISubscription<TKey, TValue> Subscribe(TKey key, Func<ISubscription<TKey, TValue>, Task> callback);
        void Unsubscribe(ISubscription<TKey, TValue> subscription);

        /// <summary>
        /// Subscribes to <paramref name="key"/> and returns a task that completes once the first
        /// non-Pending callback has fired (i.e. the subscription has an <em>Active</em>, <em>Stale</em>
        /// or <em>Failed</em> status). The subscription remains active after the task completes and
        /// continues to invoke <paramref name="callback"/> on subsequent updates.
        /// </summary>
        /// <param name="key">The topic key to subscribe to.</param>
        /// <param name="callback">Callback invoked on every update, including the first one.</param>
        /// <param name="cancellationToken">Optional token to cancel the wait for the first value.</param>
        /// <returns>The subscription handle. Dispose or pass to <see cref="Unsubscribe"/> to cancel.</returns>
        Task<ISubscription<TKey, TValue>> SubscribeAsync(TKey key, Func<ISubscription<TKey, TValue>, Task> callback, CancellationToken cancellationToken = default);

        // management operations
        void Clear();
        int Count { get; }

        /// <summary>Returns a point-in-time snapshot of all retained/active key-value pairs.</summary>
        IReadOnlyDictionary<TKey, TValue> GetSnapshot();

        /// <summary>
        /// Returns a point-in-time snapshot of all retained/active key-value pairs whose key
        /// matches <paramref name="pattern"/>. Wildcard patterns (e.g. <c>sensors/#</c>,
        /// <c>room/+/temp</c>) are resolved by the cache's configured matcher. Exact keys return
        /// at most one entry. Implementations without wildcard support fall back to exact-key lookup.
        /// </summary>
        IReadOnlyDictionary<TKey, TValue> GetSnapshot(TKey pattern);

        // pub operations
        // -- publish a value and trigger updates to subscribers
        // -- retain flag means that value will stay in cache even if no current subscribers
        Task PublishAsync(TKey key, TValue value, IStatus? status, bool retain = false, CancellationToken cancellationToken = default);
        Task PublishAsync(TKey key, TValue value, CancellationToken cancellationToken = default);
        Task PublishAsync(TKey key, IStatus status, CancellationToken cancellationToken = default);

        /// <summary>
        /// Registers a producer handle for <paramref name="key"/>. Each call returns a new independent
        /// handle backed by a shared per-topic reference count. When the last handle is disposed,
        /// <paramref name="disposeStatus"/> is automatically published (defaults to <c>Stale</c>).
        /// </summary>
        IPublisher<TKey, TValue> RegisterPublisher(TKey key, IStatus? disposeStatus = null);
    }

}
