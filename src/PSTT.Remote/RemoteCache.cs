using PSTT.Data;
using PSTT.Remote.Protocol;
using PSTT.Remote.Transport;

namespace PSTT.Remote
{
    /// <summary>
    /// An <see cref="CacheWithWildcards{TKey,TValue}"/> that transparently proxies
    /// subscriptions and publishes to a remote <see cref="RemoteCacheServer{TValue}"/>
    /// over any pluggable <see cref="IRemoteTransport"/>.
    ///
    /// Usage is identical to any other upstream data source:
    /// <code>
    ///   var remote = new RemoteCache&lt;string&gt;(transport, deserializer);
    ///   await remote.ConnectAsync();
    ///   var local = new CacheBuilder&lt;string, string&gt;()
    ///       .WithWildcards()
    ///       .WithUpstream(remote, supportsWildcards: true)
    ///       .Build();
    /// </code>
    /// </summary>
    public class RemoteCache<TValue> : CacheWithWildcards<string, TValue>, IAsyncDisposable
    {
        private readonly IRemoteTransport _transport;
        private readonly Func<byte[], TValue> _deserializer;
        private readonly Func<TValue, byte[]>? _serializer;

        // Set of keys currently subscribed at the server level (for reconnect).
        private readonly HashSet<string> _serverSubscriptions = new();
        private readonly SemaphoreSlim _subLock = new(1, 1);

        // Guards against NewItem creating a duplicate server subscription when
        // it's called from within an incoming-message handler.
        private int _incomingMessageDepth;

        private bool _connected;
        private bool _disposed;

        /// <param name="transport">
        ///   Transport to the server. Call <see cref="ConnectAsync"/> to open it.
        /// </param>
        /// <param name="deserializer">Convert raw payload bytes to TValue.</param>
        /// <param name="serializer">Convert TValue to bytes for publishing. Optional.</param>
        /// <param name="config">Optional DataSource configuration.</param>
        public RemoteCache(
            IRemoteTransport transport,
            Func<byte[], TValue> deserializer,
            Func<TValue, byte[]>? serializer = null,
            CacheConfig<string, TValue>? config = null)
            : base(config ?? new CacheConfig<string, TValue>())
        {
            _transport    = transport    ?? throw new ArgumentNullException(nameof(transport));
            _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
            _serializer   = serializer;

            _transport.MessageReceived += OnMessageReceivedAsync;
            _transport.Disconnected    += OnDisconnectedAsync;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>Connect to the remote server. Re-sends all pending subscriptions.</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _transport.ConnectAsync(cancellationToken);
            _connected = true;
            await ResubscribeAllAsync(cancellationToken);
        }

        /// <summary>Disconnect from the server.</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            await _transport.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectAsync();
        }

        // ── Publish ────────────────────────────────────────────────────────────

        public override async Task PublishAsync(
            string key, TValue value, IStatus? status, bool retain = false,
            CancellationToken cancellationToken = default)
        {
            await SendToServerAsync(key, value, retain, cancellationToken);
            await base.PublishAsync(key, value, status, retain, cancellationToken);
        }

        public override Task PublishAsync(string key, TValue value, CancellationToken cancellationToken = default)
            => PublishAsync(key, value, null, retain: false, cancellationToken);

        // ── NewItem / RemoveItem ───────────────────────────────────────────────

        internal override CacheItem<string, TValue> NewItem(string key)
        {
            var col = base.NewItem(key);
            if (_incomingMessageDepth == 0)
                _ = SubscribeServerTopicAsync(key);
            return col;
        }

        internal override void RemoveItem(CacheItem<string, TValue> col)
        {
            _ = UnsubscribeServerTopicAsync(col.Key);
            base.RemoveItem(col);
        }

        // ── Server subscription management ────────────────────────────────────

        private async Task SubscribeServerTopicAsync(string key)
        {
            await _subLock.WaitAsync();
            try
            {
                if (!_serverSubscriptions.Add(key))
                    return; // already registered

                if (!_connected)
                    return; // will be sent by ResubscribeAllAsync on next ConnectAsync
            }
            finally
            {
                _subLock.Release();
            }

            await SendMessageAsync(RemoteProtocol.Subscribe(key));
        }

        private async Task UnsubscribeServerTopicAsync(string key)
        {
            await _subLock.WaitAsync();
            try
            {
                if (!_serverSubscriptions.Remove(key))
                    return;

                if (!_connected)
                    return;
            }
            finally
            {
                _subLock.Release();
            }

            await SendMessageAsync(RemoteProtocol.Unsubscribe(key));
        }

        private async Task ResubscribeAllAsync(CancellationToken cancellationToken)
        {
            List<string> keys;
            await _subLock.WaitAsync(cancellationToken);
            try { keys = new List<string>(_serverSubscriptions); }
            finally { _subLock.Release(); }

            foreach (var key in keys)
                await SendMessageAsync(RemoteProtocol.Subscribe(key), cancellationToken);
        }

        // ── Sending ───────────────────────────────────────────────────────────

        private async Task SendToServerAsync(string key, TValue value, bool retain, CancellationToken ct)
        {
            if (_serializer == null || !_connected)
                return;

            var payload = _serializer(value);
            await SendMessageAsync(RemoteProtocol.Publish(key, payload, retain), ct);
        }

        private async Task SendMessageAsync(Protocol.RemoteMessage message,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var bytes = RemoteProtocol.Encode(message);
                await _transport.SendAsync(bytes, cancellationToken);
            }
            catch { /* transport failure — server will detect disconnect */ }
        }

        // ── Incoming messages from server ──────────────────────────────────────

        private async Task OnMessageReceivedAsync(ReadOnlyMemory<byte> data)
        {
            var message = RemoteProtocol.Decode(data);
            if (message?.Type != RemoteMessageTypes.Update || message.Key == null)
                return;

            var payloadBytes = RemoteProtocol.DecodePayload(message);
            if (payloadBytes == null)
                return;

            TValue value;
            try { value = _deserializer(payloadBytes); }
            catch { return; } // ignore malformed payload

            var status = new Status
            {
                State   = (IStatus.StateValue)message.StatusState,
                Message = message.StatusMessage,
                Code    = message.StatusCode,
            };

            Interlocked.Increment(ref _incomingMessageDepth);
            try
            {
                await base.PublishAsync(message.Key, value, status);
            }
            finally
            {
                Interlocked.Decrement(ref _incomingMessageDepth);
            }
        }

        private async Task OnDisconnectedAsync()
        {
            _connected = false;

            List<string> keys;
            await _subLock.WaitAsync();
            try { keys = new List<string>(_serverSubscriptions); }
            finally { _subLock.Release(); }

            var stale = new Status { State = IStatus.StateValue.Stale, Message = "Remote server disconnected" };
            foreach (var key in keys)
                await base.PublishAsync(key, stale);
        }
    }
}
