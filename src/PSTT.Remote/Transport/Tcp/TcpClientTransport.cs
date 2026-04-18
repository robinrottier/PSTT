using System.Net;
using System.Net.Sockets;

namespace PSTT.Remote.Transport.Tcp
{
    /// <summary>
    /// Client-side TCP transport. Connects to a remote <see cref="TcpServerTransport"/>.
    /// </summary>
    public sealed class TcpClientTransport : IRemoteTransport
    {
        private readonly string _host;
        private readonly int _port;
        private TcpTransport? _inner;
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;

        public bool IsConnected => _inner?.IsConnected ?? false;

        public TcpClientTransport(string host, int port)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            if (port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            _port = port;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(_host, _port, cancellationToken);

            _inner = new TcpTransport(tcp);
            _inner.MessageReceived += msg => MessageReceived?.Invoke(msg) ?? Task.CompletedTask;
            _inner.Disconnected    += () => Disconnected?.Invoke()        ?? Task.CompletedTask;
            _inner.StartReceiving();
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (_inner == null) throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
            return _inner.SendAsync(data, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_inner != null) await _inner.DisposeAsync();
        }
    }
}
