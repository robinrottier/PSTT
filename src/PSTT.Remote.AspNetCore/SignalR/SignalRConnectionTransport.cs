using PSTT.Remote.Transport;
using Microsoft.AspNetCore.SignalR;
using PSTT.Remote.Transport.SignalR;

namespace PSTT.Remote.AspNetCore.SignalR
{
    /// <summary>
    /// Per-connection server-side transport.  One instance is created for each SignalR
    /// client connection by <see cref="SignalRServerTransport"/>.
    /// </summary>
    internal sealed class SignalRConnectionTransport : IRemoteTransport
    {
        private readonly string _connectionId;
        private readonly ISingleClientProxy _caller;
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;

        public bool IsConnected => !_disposed;

        internal SignalRConnectionTransport(string connectionId, ISingleClientProxy caller)
        {
            _connectionId = connectionId;
            _caller = caller;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (_disposed) return;
            await _caller.SendAsync(SignalRProtocol.ServerToClientMethod, data.ToArray(), cancellationToken);
        }

        internal async Task TriggerMessageReceivedAsync(byte[] data)
        {
            if (MessageReceived != null)
                await MessageReceived(data);
        }

        internal async Task TriggerDisconnectedAsync()
        {
            _disposed = true;
            if (Disconnected != null)
                await Disconnected();
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
