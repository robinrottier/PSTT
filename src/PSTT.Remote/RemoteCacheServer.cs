using PSTT.Data;
using PSTT.Remote.Protocol;
using PSTT.Remote.Transport;

namespace PSTT.Remote
{
    /// <summary>
    /// Accepts remote client connections and proxies their subscribe/unsubscribe/publish
    /// requests to a local upstream <see cref="ICache{TKey,TValue}"/>.
    ///
    /// Multiple clients can connect simultaneously; each gets its own <see cref="ClientSession"/>.
    ///
    /// <code>
    ///   var server = new RemoteCacheServer&lt;string&gt;(
    ///       upstream, serializer, deserializer, serverTransport);
    ///   await server.StartAsync();
    /// </code>
    /// </summary>
    public sealed class RemoteCacheServer<TValue> : IAsyncDisposable
    {
        private readonly ICache<string, TValue> _upstream;
        private readonly Func<TValue, byte[]> _serializer;
        private readonly Func<byte[], TValue> _deserializer;
        private readonly IRemoteServerTransport _serverTransport;
        private readonly bool _forwardPublish;

        private readonly List<ClientSession> _sessions = new();
        private readonly object _sessionsLock = new();
        private bool _disposed;

        /// <param name="upstream">The data source to proxy.</param>
        /// <param name="serializer">Serialize TValue to bytes for wire transmission.</param>
        /// <param name="deserializer">Deserialize bytes from client publish messages.</param>
        /// <param name="serverTransport">Transport listener (e.g. TcpServerTransport).</param>
        /// <param name="forwardPublish">
        ///   When true, client "pub" messages are forwarded to the upstream via
        ///   <see cref="ICache{TKey,TValue}.PublishAsync(TKey,TValue,CancellationToken)"/>.
        /// </param>
        public RemoteCacheServer(
            ICache<string, TValue> upstream,
            Func<TValue, byte[]> serializer,
            Func<byte[], TValue> deserializer,
            IRemoteServerTransport serverTransport,
            bool forwardPublish = false)
        {
            _upstream        = upstream        ?? throw new ArgumentNullException(nameof(upstream));
            _serializer      = serializer      ?? throw new ArgumentNullException(nameof(serializer));
            _deserializer    = deserializer    ?? throw new ArgumentNullException(nameof(deserializer));
            _serverTransport = serverTransport ?? throw new ArgumentNullException(nameof(serverTransport));
            _forwardPublish  = forwardPublish;

            _serverTransport.ClientConnected += OnClientConnectedAsync;
        }

        /// <summary>Start accepting client connections.</summary>
        public Task StartAsync(CancellationToken cancellationToken = default)
            => _serverTransport.StartAsync(cancellationToken);

        /// <summary>Stop accepting new connections.</summary>
        public Task StopAsync() => _serverTransport.StopAsync();

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _serverTransport.DisposeAsync();

            List<ClientSession> sessions;
            lock (_sessionsLock) { sessions = new List<ClientSession>(_sessions); }
            foreach (var session in sessions)
                await session.DisposeAsync();
        }

        // ── Session management ─────────────────────────────────────────────────

        private async Task OnClientConnectedAsync(IRemoteTransport transport)
        {
            var session = new ClientSession(transport, _upstream, _serializer, _deserializer, _forwardPublish);
            lock (_sessionsLock) _sessions.Add(session);

            transport.Disconnected += async () =>
            {
                lock (_sessionsLock) _sessions.Remove(session);
                await session.DisposeAsync();
            };

            await Task.CompletedTask;
        }

        // ── Per-client session ─────────────────────────────────────────────────

        private sealed class ClientSession : IAsyncDisposable
        {
            private readonly IRemoteTransport _transport;
            private readonly ICache<string, TValue> _upstream;
            private readonly Func<TValue, byte[]> _serializer;
            private readonly Func<byte[], TValue> _deserializer;
            private readonly bool _forwardPublish;

            // Map: subscribed key → upstream ISubscription (for clean unsubscription)
            private readonly Dictionary<string, ISubscription<string, TValue>> _upstreamSubs = new();
            private readonly SemaphoreSlim _subLock = new(1, 1);
            private bool _disposed;

            internal ClientSession(
                IRemoteTransport transport,
                ICache<string, TValue> upstream,
                Func<TValue, byte[]> serializer,
                Func<byte[], TValue> deserializer,
                bool forwardPublish)
            {
                _transport      = transport;
                _upstream       = upstream;
                _serializer     = serializer;
                _deserializer   = deserializer;
                _forwardPublish = forwardPublish;

                _transport.MessageReceived += OnMessageReceivedAsync;
            }

            private async Task OnMessageReceivedAsync(ReadOnlyMemory<byte> data)
            {
                var message = RemoteProtocol.Decode(data);
                if (message == null) return;

                switch (message.Type)
                {
                    case RemoteMessageTypes.Subscribe:
                        await HandleSubscribeAsync(message.Key);
                        break;

                    case RemoteMessageTypes.Unsubscribe:
                        await HandleUnsubscribeAsync(message.Key);
                        break;

                    case RemoteMessageTypes.Publish when _forwardPublish:
                        await HandlePublishAsync(message);
                        break;
                }
            }

            private async Task HandleSubscribeAsync(string key)
            {
                await _subLock.WaitAsync();
                try
                {
                    if (_upstreamSubs.ContainsKey(key))
                        return; // already subscribed for this client

                    var sub = _upstream.Subscribe(key, async s =>
                    {
                        if (s.Status.IsPending) return; // no value yet

                        byte[] payload;
                        try { payload = _serializer(s.Value); }
                        catch { return; }

                        var msg = RemoteProtocol.Update(s.Key, payload, s.Status);
                        try
                        {
                            await _transport.SendAsync(RemoteProtocol.Encode(msg));
                        }
                        catch { /* client disconnected */ }
                    });

                    _upstreamSubs[key] = sub;
                }
                finally
                {
                    _subLock.Release();
                }
            }

            private async Task HandleUnsubscribeAsync(string key)
            {
                await _subLock.WaitAsync();
                try
                {
                    if (!_upstreamSubs.TryGetValue(key, out var sub))
                        return;
                    _upstream.Unsubscribe(sub);
                    _upstreamSubs.Remove(key);
                }
                finally
                {
                    _subLock.Release();
                }
            }

            private async Task HandlePublishAsync(RemoteMessage message)
            {
                var payloadBytes = RemoteProtocol.DecodePayload(message);
                if (payloadBytes == null) return;

                TValue value;
                try { value = _deserializer(payloadBytes); }
                catch { return; }

                await _upstream.PublishAsync(message.Key, value);
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;

                await _subLock.WaitAsync();
                try
                {
                    foreach (var sub in _upstreamSubs.Values)
                        _upstream.Unsubscribe(sub);
                    _upstreamSubs.Clear();
                }
                finally
                {
                    _subLock.Release();
                }
            }
        }
    }
}
