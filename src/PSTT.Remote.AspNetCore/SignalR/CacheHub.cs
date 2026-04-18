using Microsoft.AspNetCore.SignalR;
using PSTT.Remote.Transport.SignalR;

namespace PSTT.Remote.AspNetCore.SignalR
{
    /// <summary>
    /// ASP.NET Core SignalR Hub that bridges connected clients to a
    /// <see cref="SignalRServerTransport"/> / <see cref="PSTT.Remote.RemoteCacheServer{TValue}"/>.
    ///
    /// Map this hub in your application startup:
    /// <code>
    ///   // Program.cs
    ///   builder.Services.AddDataSourceSignalRServer&lt;string&gt;(upstream, serializer, deserializer);
    ///   app.MapCacheHub("/datasource");
    /// </code>
    /// </summary>
    public sealed class CacheHub : Hub
    {
        private readonly SignalRServerTransport _serverTransport;

        public CacheHub(SignalRServerTransport serverTransport)
        {
            _serverTransport = serverTransport;
        }

        public override async Task OnConnectedAsync()
        {
            // OnConnectedAsync IS awaited by the SignalR framework before the client can
            // invoke hub methods. Awaiting setup here guarantees the ClientSession and its
            // MessageReceived handler are wired up before any "Send" message can arrive.
            await _serverTransport.OnClientConnectedAsync(Context.ConnectionId, Clients.Caller);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await _serverTransport.OnClientDisconnectedAsync(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>Receives a raw protocol message from the client.</summary>
        public async Task Send(byte[] data)
            => await _serverTransport.OnMessageReceivedAsync(Context.ConnectionId, data);
    }
}
