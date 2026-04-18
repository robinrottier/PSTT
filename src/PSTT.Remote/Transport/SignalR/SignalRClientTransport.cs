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

            _connection.Closed += async _ =>
            {
                if (Disconnected != null)
                    await Disconnected();
            };
        }

        /// <summary>
        /// Creates a transport that connects to the given hub URL using the default
        /// JSON protocol and automatic reconnect disabled.
        /// </summary>
        /// <param name="hubUrl">Absolute URL of the SignalR hub endpoint.</param>
        public SignalRClientTransport(string hubUrl)
            : this(new HubConnectionBuilder()
                .WithUrl(hubUrl ?? throw new ArgumentNullException(nameof(hubUrl)))
                .Build()) { }

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
            => await _connection.StartAsync(cancellationToken);

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
