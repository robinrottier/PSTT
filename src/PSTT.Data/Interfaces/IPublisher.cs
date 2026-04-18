using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Data
{
    /// <summary>
    /// Represents a producer handle for a single topic in an <see cref="ICache{TKey,TValue}"/>.
    /// Each call to <see cref="ICache{TKey,TValue}.RegisterPublisher"/> returns a new, independent
    /// handle. A shared per-topic reference count is incremented for each handle; when the last handle
    /// is disposed, an optional <em>dispose status</em> is automatically published to the topic.
    /// </summary>
    /// <typeparam name="TKey">Key type of the owning data source.</typeparam>
    /// <typeparam name="TValue">Value type of the owning data source.</typeparam>
    /// <example>
    /// Long-lived producer pattern:
    /// <code>
    /// await using var pub = dataSource.RegisterPublisher("sensors/temp");
    /// await pub.PublishAsync(42.5);
    /// // On dispose: publishes Stale status automatically
    /// </code>
    /// </example>
    public interface IPublisher<TKey, TValue> : System.IAsyncDisposable
    {
        /// <summary>The topic key this publisher publishes to.</summary>
        TKey Key { get; }

        /// <summary>Publishes a value with an optional status. Retain keeps the value in cache after all subscribers leave.</summary>
        Task PublishAsync(TValue value, IStatus? status = null, bool retain = false, CancellationToken cancellationToken = default);

        /// <summary>Publishes a value with the default Active status.</summary>
        Task PublishAsync(TValue value, CancellationToken cancellationToken = default);

        /// <summary>Publishes a status update only (does not change the cached value).</summary>
        Task PublishAsync(IStatus status, CancellationToken cancellationToken = default);
    }
}
