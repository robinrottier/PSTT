using System.Net;
using PSTT.Remote.Transport;

namespace PSTT.Remote.Transport.WebSocket
{
    /// <summary>
    /// Standalone server-side <see cref="IRemoteServerTransport"/> that listens for
    /// WebSocket upgrade requests using <see cref="HttpListener"/>.
    ///
    /// Suitable for console apps, Windows Services, or any scenario that does not use
    /// ASP.NET Core.  For ASP.NET Core hosts use
    /// <c>PSTT.Remote.AspNetCore.AspNetCoreWebSocketServerTransport</c> instead.
    /// </summary>
    /// <example>
    /// var serverTransport = new WebSocketServerTransport("http://localhost:5001/datasource/");
    /// var server = new RemoteCacheServer&lt;string&gt;(upstream, ser, deser, serverTransport);
    /// await server.StartAsync();
    /// </example>
    public sealed class WebSocketServerTransport : IRemoteServerTransport
    {
        private readonly HttpListener _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public event Func<IRemoteTransport, Task>? ClientConnected;

        /// <param name="uriPrefix">
        ///   HTTP listener prefix, e.g. <c>"http://localhost:5001/datasource/"</c>.
        ///   Must end with a slash.
        /// </param>
        public WebSocketServerTransport(string uriPrefix)
        {
            if (string.IsNullOrWhiteSpace(uriPrefix))
                throw new ArgumentException("A URI prefix is required.", nameof(uriPrefix));
            _listener = new HttpListener();
            _listener.Prefixes.Add(uriPrefix);
        }

        /// <inheritdoc/>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _listener.Start();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = AcceptLoopAsync(_cts.Token);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StopAsync()
        {
            _listener.Stop();
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = HandleContextAsync(ctx);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext ctx)
        {
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            System.Net.WebSockets.HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
            catch { return; }

            var transport = new WebSocketConnectionTransport(wsCtx.WebSocket);
            if (ClientConnected != null) await ClientConnected(transport);
            transport.StartReceiving();
            // Keep the connection alive until the WebSocket closes
            await transport.Completion;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _listener.Close();
            await Task.CompletedTask;
        }
    }
}
