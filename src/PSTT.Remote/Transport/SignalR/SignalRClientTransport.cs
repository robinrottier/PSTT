using Microsoft.AspNetCore.SignalR.Client;
using PSTT.Remote.Transport;

namespace PSTT.Remote.Transport.SignalR
{
    /// <summary>
    /// Client-side <see cref="IRemoteTransport"/> backed by a SignalR
    /// <see cref="HubConnection"/>.
    ///
    /// Works in all .NET runtimes including Blazor WebAssembly.  On the server side,
    /// pair with <c>CacheHub</c> from the <c>PSTT.Remote.AspNetCore</c> package.
    /// </summary>
    /// <example>
    /// // Build a HubConnection (e.g. in Blazor WASM where the connection is pre-created):
    /// var hub = new HubConnectionBuilder()
    ///     .WithUrl(NavigationManager.ToAbsoluteUri("/datasource"))
    ///     .Build();
    ///
    /// var client = new RemoteCacheBuilder&lt;string&gt;()
    ///     .WithSignalRTransport(hub)
    ///     .WithUtf8Encoding()
    ///     .Build();
    ///
    /// await client.ConnectAsync();
    /// </example>
    public sealed class SignalRClientTransport : IRemoteTransport
    {
        private readonly HubConnection _connection;
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;
        public event Func<Task>? Reconnected;

        public bool IsConnected => _connection.State == HubConnectionState.Connected && !_disposed;

        /// <summary>
        /// Creates a transport wrapping an existing <see cref="HubConnection"/>.
        /// The connection is NOT started here; call <see cref="ConnectAsync"/> to start it.
        /// </summary>
        public SignalRClientTransport(HubConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

            _connection.On<byte[]>(SignalRProtocol.ServerToClientMethod, async data =>
            {
                if (MessageReceived != null)
                    await MessageReceived(data);
            });

            // Closed fires when the connection is permanently lost (all retries exhausted).
            _connection.Closed += async _ =>
            {
                if (Disconnected != null)
                    await Disconnected();
            };

            // Reconnected fires when SignalR's own auto-reconnect succeeds.
            // Fire the Reconnected event (not Disconnected) so RemoteCache re-sends subscriptions
            // WITHOUT marking data as stale — the connection was only briefly lost.
            _connection.Reconnected += async _ =>
            {
                if (Reconnected != null)
                    await Reconnected();
            };
        }

        /// <summary>
        /// Creates a transport that connects to the given hub URL using the default
        /// JSON protocol and automatic reconnect enabled.
        /// </summary>
        /// <param name="hubUrl">Absolute URL of the SignalR hub endpoint.</param>
        public SignalRClientTransport(string hubUrl)
            : this(new HubConnectionBuilder()
                .WithUrl(hubUrl ?? throw new ArgumentNullException(nameof(hubUrl)))
                .WithAutomaticReconnect()
                .Build()) { }

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            // If SignalR already reconnected on its own, skip StartAsync (which would throw)
            // but still let RemoteCache call ResubscribeAllAsync by returning normally.
            if (_connection.State == HubConnectionState.Connected)
                return;

            // If SignalR's own reconnect is in progress, wait for it rather than racing StartAsync.
            if (_connection.State == HubConnectionState.Reconnecting ||
                _connection.State == HubConnectionState.Connecting)
            {
                while (_connection.State != HubConnectionState.Connected &&
                       _connection.State != HubConnectionState.Disconnected)
                    await Task.Delay(200, cancellationToken);

                // If it ended up connected, we're done; otherwise fall through to StartAsync.
                if (_connection.State == HubConnectionState.Connected)
                    return;
            }

            await _connection.StartAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => await _connection.InvokeAsync(SignalRProtocol.ClientToServerMethod, data.ToArray(), cancellationToken);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _connection.DisposeAsync();
        }
    }
}
