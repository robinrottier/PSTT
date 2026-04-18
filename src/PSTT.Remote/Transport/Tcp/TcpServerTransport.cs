using System.Net;
using System.Net.Sockets;

namespace PSTT.Remote.Transport.Tcp
{
    /// <summary>
    /// Server-side TCP transport listener.  Wraps <see cref="TcpListener"/> and raises
    /// <see cref="ClientConnected"/> for each incoming connection.
    /// </summary>
    public sealed class TcpServerTransport : IRemoteServerTransport
    {
        private readonly IPEndPoint _endpoint;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public event Func<IRemoteTransport, Task>? ClientConnected;

        /// <param name="port">TCP port to listen on. 0 = OS-assigned (useful for tests).</param>
        /// <param name="address">IP address to bind. Defaults to loopback.</param>
        public TcpServerTransport(int port, IPAddress? address = null)
        {
            if (port is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            _endpoint = new IPEndPoint(address ?? IPAddress.Loopback, port);
        }

        /// <summary>The actual bound port — useful when port 0 was specified.</summary>
        public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port
                                ?? throw new InvalidOperationException("Not started.");

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _listener = new TcpListener(_endpoint);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);
            await Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var tcp = await _listener.AcceptTcpClientAsync(ct);
                    var transport = new TcpTransport(tcp);
                    transport.StartReceiving();

                    if (ClientConnected != null)
                        await ClientConnected.Invoke(transport);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch { /* individual accept failure — keep listening */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync();
        }
    }
}
