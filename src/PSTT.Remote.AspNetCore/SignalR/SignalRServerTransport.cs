using System.Collections.Concurrent;
using PSTT.Remote.Transport;
using Microsoft.AspNetCore.SignalR;

namespace PSTT.Remote.AspNetCore.SignalR
{
    /// <summary>
    /// Server-side <see cref="IRemoteServerTransport"/> that works with SignalR.
    /// Register as a singleton in DI and wire up <see cref="CacheHub"/> to an endpoint.
    /// </summary>
    /// <example>
    /// // In Program.cs:
    /// builder.Services.AddDataSourceSignalRServer&lt;string&gt;(upstream, ser, deser);
    /// app.MapCacheHub("/datasource");
    /// </example>
    public sealed class SignalRServerTransport : IRemoteServerTransport
    {
        private readonly ConcurrentDictionary<string, SignalRConnectionTransport> _connections = new();

        public event Func<IRemoteTransport, Task>? ClientConnected;

        /// <summary>No-op: the SignalR framework manages the listener lifecycle.</summary>
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>No-op: the SignalR framework manages the listener lifecycle.</summary>
        public Task StopAsync() => Task.CompletedTask;

        public async Task OnClientConnectedAsync(string connectionId, ISingleClientProxy caller)
        {
            var transport = new SignalRConnectionTransport(connectionId, caller);
            _connections[connectionId] = transport;
            if (ClientConnected != null)
                await ClientConnected(transport);
        }

        public async Task OnClientDisconnectedAsync(string connectionId)
        {
            if (_connections.TryRemove(connectionId, out var transport))
                await transport.TriggerDisconnectedAsync();
        }

        internal async Task OnMessageReceivedAsync(string connectionId, byte[] data)
        {
            if (_connections.TryGetValue(connectionId, out var transport))
                await transport.TriggerMessageReceivedAsync(data);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
