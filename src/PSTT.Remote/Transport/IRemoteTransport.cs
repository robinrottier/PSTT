namespace PSTT.Remote.Transport
{
    /// <summary>
    /// A bidirectional message channel to a single remote peer.
    /// Implementations include TCP streams, WebSockets, SignalR connections, etc.
    /// Messages are opaque byte sequences; framing is the transport's responsibility.
    /// </summary>
    public interface IRemoteTransport : IAsyncDisposable
    {
        /// <summary>Send a message to the peer. Must be thread-safe.</summary>
        Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

        /// <summary>Raised on the receiving loop when a complete message arrives.</summary>
        event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;

        /// <summary>Raised when the transport detects the peer has disconnected.</summary>
        event Func<Task>? Disconnected;

        /// <summary>True while the underlying connection is open.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Client-side: establish the connection to the remote endpoint.
        /// Server-side transports returned from <see cref="IRemoteServerTransport.ClientConnected"/>
        /// are already connected; calling this is a no-op for them.
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken = default);
    }
}
