namespace PSTT.Remote.Transport
{
    /// <summary>
    /// Server-side transport listener.  Accepts incoming connections and exposes
    /// each one as an <see cref="IRemoteTransport"/> via <see cref="ClientConnected"/>.
    /// </summary>
    public interface IRemoteServerTransport : IAsyncDisposable
    {
        /// <summary>Start accepting connections.</summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>Stop accepting new connections (existing sessions are not forcibly closed).</summary>
        Task StopAsync();

        /// <summary>
        /// Raised for each new incoming client connection.
        /// The supplied <see cref="IRemoteTransport"/> is already connected and its
        /// receive loop has been started.
        /// </summary>
        event Func<IRemoteTransport, Task>? ClientConnected;
    }
}
