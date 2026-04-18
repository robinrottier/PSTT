using System.Net.WebSockets;
using PSTT.Remote.Transport;

namespace PSTT.Remote.Transport.WebSocket
{
    /// <summary>
    /// Client-side <see cref="IRemoteTransport"/> that connects to a WebSocket server
    /// identified by a <c>ws://</c> or <c>wss://</c> URI.
    /// </summary>
    /// <example>
    /// var client = new RemoteCacheBuilder&lt;string&gt;()
    ///     .WithWebSocketTransport("ws://localhost:5001/datasource")
    ///     .WithUtf8Encoding()
    ///     .Build();
    /// await client.ConnectAsync();
    /// </example>
    public sealed class WebSocketClientTransport : IRemoteTransport
    {
        private readonly Uri _uri;
        private WebSocketConnectionTransport? _inner;
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;

        public bool IsConnected => _inner?.IsConnected ?? false;

        /// <param name="uri">WebSocket server URI, e.g. <c>ws://localhost:5001/Cache</c>.</param>
        public WebSocketClientTransport(Uri uri)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        }

        /// <param name="uri">WebSocket server URI string.</param>
        public WebSocketClientTransport(string uri)
            : this(new Uri(uri ?? throw new ArgumentNullException(nameof(uri)))) { }

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(_uri, cancellationToken);
            _inner = new WebSocketConnectionTransport(ws);
            _inner.MessageReceived += data => MessageReceived?.Invoke(data) ?? Task.CompletedTask;
            _inner.Disconnected += () => Disconnected?.Invoke() ?? Task.CompletedTask;
            _inner.StartReceiving();
        }

        /// <inheritdoc/>
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => _inner?.SendAsync(data, cancellationToken) ?? Task.CompletedTask;

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_inner != null) await _inner.DisposeAsync();
        }
    }
}
