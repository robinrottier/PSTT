using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PSTT.Remote.Transport;

namespace PSTT.Remote.Transport.Tcp
{
    /// <summary>
    /// TCP transport shared by both client and server sides.
    /// Frames: [4-byte little-endian length][payload bytes]
    /// </summary>
    internal sealed class TcpTransport : IRemoteTransport
    {
        private readonly TcpClient _tcp;
        private NetworkStream? _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationTokenSource? _receiveCts;
        private bool _disposed;

        public event Func<ReadOnlyMemory<byte>, Task>? MessageReceived;
        public event Func<Task>? Disconnected;

        public bool IsConnected => _tcp.Connected && !_disposed;

        internal TcpTransport(TcpClient connectedClient)
        {
            _tcp = connectedClient;
            _stream = connectedClient.GetStream();
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        internal void StartReceiving()
        {
            _receiveCts = new CancellationTokenSource();
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_stream == null || !IsConnected)
                    return;

                // Write 4-byte LE length header
                var header = new byte[4];
                MemoryMarshal.Write(header, (uint)data.Length);
                await _stream.WriteAsync(header, cancellationToken);
                await _stream.WriteAsync(data, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var header = new byte[4];
            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    if (!await ReadExactAsync(_stream, header, ct))
                        break;

                    var length = (int)MemoryMarshal.Read<uint>(header);
                    if (length is <= 0 or > 10 * 1024 * 1024)
                        break; // sanity guard

                    var buffer = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        if (!await ReadExactAsync(_stream, buffer.AsMemory(0, length), ct))
                            break;

                        if (MessageReceived != null)
                            await MessageReceived.Invoke(buffer.AsMemory(0, length));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* connection dropped */ }
            finally
            {
                if (Disconnected != null)
                    await Disconnected.Invoke();
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer[offset..], ct);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _stream?.Dispose();
            _tcp.Dispose();
            await Task.CompletedTask;
        }
    }
}
