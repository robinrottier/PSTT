using PSTT.Remote.Transport;
using PSTT.Remote.Transport.WebSocket;
using Microsoft.AspNetCore.Http;

namespace PSTT.Remote.AspNetCore.WebSocket
{
    /// <summary>
    /// Server-side <see cref="IRemoteServerTransport"/> that accepts WebSocket upgrades
    /// from the ASP.NET Core request pipeline.
    ///
    /// Register as a singleton and map to a route:
    /// <code>
    ///   builder.Services.AddDataSourceWebSocketServer&lt;string&gt;(upstream, ser, deser);
    ///   app.UseWebSockets();
    ///   app.MapDataSourceWebSocket("/datasource/ws");
    /// </code>
    /// </summary>
    public sealed class AspNetCoreWebSocketServerTransport : IRemoteServerTransport
    {
        public event Func<IRemoteTransport, Task>? ClientConnected;

        /// <summary>No-op: ASP.NET Core manages the listener lifecycle.</summary>
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>No-op: ASP.NET Core manages the listener lifecycle.</summary>
        public Task StopAsync() => Task.CompletedTask;

        /// <summary>
        /// Accept a WebSocket upgrade from an ASP.NET Core request context.
        /// The returned task completes when the WebSocket connection closes, so the
        /// middleware/endpoint handler should <c>await</c> it to keep the request alive.
        /// </summary>
        public async Task AcceptAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketConnectionTransport(ws);
            if (ClientConnected != null)
                await ClientConnected(transport);
            transport.StartReceiving();

            // Keep the ASP.NET request alive until the WebSocket is closed
            await transport.Completion;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
