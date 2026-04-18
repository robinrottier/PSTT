using System.Net.WebSockets;
using PSTT.Remote.Transport;

namespace PSTT.Remote.Transport.WebSocket
{
    /// <summary>
    /// An <see cref="IRemoteTransport"/> that wraps an already-connected
    /// <see cref="System.Net.WebSockets.WebSocket"/>.
    ///
    /// Used internally by <see cref="WebSocketClientTransport"/> and
    /// <see cref="WebSocketServerTransport"/> (standalone), and exposed publicly so that
    /// ASP.NET Core middleware can pass in a framework-managed WebSocket directly.
    ///
    /// Call <see cref="StartReceiving"/> once after handing the transport off via
    /// <see cref="IRemoteServerTransport.ClientConnected"/> or wrapping it in a client.
    /// Await <see cref="Completion"/> to keep an ASP.NET request pipeline alive until the
    /// WebSocket is closed.
    /// </summary>
    public sealed class WebSocketConnectionTransport : IRemoteTransport
    {
        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;

        public bool IsConnected => _ws.State == WebSocketState.Open && !_disposed;

        /// <summary>Resolves when the receive loop exits (WebSocket closed or transport disposed).</summary>
        public Task Completion => _completionTcs.Task;

        /// <param name="webSocket">An already-connected WebSocket instance.</param>
        public WebSocketConnectionTransport(System.Net.WebSockets.WebSocket webSocket)
        {
            _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        }

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>Start the background receive loop. Call exactly once after construction.</summary>
        public void StartReceiving()
        {
            _cts = new CancellationTokenSource();
            _ = ReceiveLoopAsync(_cts.Token);
        }

        /// <inheritdoc/>
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsConnected) return;
                await _ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            using var ms = new System.IO.MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            // Complete the close handshake so the initiator's CloseAsync can return.
                            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); }
                            catch { }
                            goto done;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (ms.Length > 0 && MessageReceived != null)
                        await MessageReceived(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch { }
            done:
            if (Disconnected != null)
                await Disconnected.Invoke();
            _completionTcs.TrySetResult();
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
            _completionTcs.TrySetResult();
        }
    }
}
