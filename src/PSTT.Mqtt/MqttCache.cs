using PSTT.Data;
using MQTTnet;
using MQTTnet.Protocol;

namespace PSTT.Mqtt
{
    /// <summary>
    /// An ICache adapter that connects to an MQTT broker.
    /// Derives from CacheWithPatterns so all subscription management, wildcard matching,
    /// retained values, status tracking and callback dispatch are inherited.
    /// The MQTT layer only adds: broker connect/disconnect, serialization, and broker-side
    /// topic subscription management hooked via NewItem / RemoveItem overrides.
    /// Can be used standalone or as an upstream for a local DataSource / CacheWithPatterns.
    /// </summary>
    public class MqttCache<TValue> : CacheWithWildcards<string, TValue>, IAsyncDisposable
    {
        private readonly string _brokerHost;
        private readonly int _brokerPort;
        private readonly Func<byte[], TValue> _deserializer;
        private readonly Func<TValue, byte[]>? _serializer;

        private readonly IMqttClient _mqttClient;
        private readonly HashSet<string> _brokerSubscriptions = new();
        private readonly SemaphoreSlim _brokerLock = new(1, 1);
        private readonly MqttClientOptions? _preBuiltOptions;

        // Tracks how many concurrent incoming-message handlers are active.
        // When > 0, NewItem was triggered by an arriving broker message rather than
        // a user Subscribe() call, so we must NOT create a duplicate broker subscription.
        // (NewItem is called synchronously inside _cache.GetOrAdd before any await, so
        // this counter is reliably incremented before NewItem runs.)
        private int _incomingMessageDepth;

        private bool _connected;
        private bool _disposed;

        /// <summary>
        /// Create an MQTT data source that connects to the given broker.
        /// </summary>
        /// <param name="brokerHost">Broker hostname or IP address.</param>
        /// <param name="brokerPort">Broker port (default 1883).</param>
        /// <param name="deserializer">Converts raw MQTT payload bytes to TValue.</param>
        /// <param name="serializer">Converts TValue to bytes for publishing. Optional — omit if publish is not needed.</param>
        public MqttCache(
            string brokerHost,
            int brokerPort,
            Func<byte[], TValue> deserializer,
            Func<TValue, byte[]>? serializer = null)
            : base()
        {
            _brokerHost = brokerHost ?? throw new ArgumentNullException(nameof(brokerHost));
            _brokerPort = brokerPort;
            _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
            _serializer = serializer;

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        }

        /// <summary>
        /// Create an MQTT data source using pre-built options (used by <see cref="MqttCacheBuilder{TValue}"/>).
        /// </summary>
        internal MqttCache(
            MqttClientOptions preBuiltOptions,
            Func<byte[], TValue> deserializer,
            Func<TValue, byte[]>? serializer,
            CacheConfig<string, TValue>? config)
            : base(config ?? new CacheConfig<string, TValue>())
        {
            _preBuiltOptions = preBuiltOptions ?? throw new ArgumentNullException(nameof(preBuiltOptions));
            _brokerHost = string.Empty;
            _brokerPort = 0;
            _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
            _serializer = serializer;

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        }

        // ── Broker lifecycle ───────────────────────────────────────────────────

        /// <summary>Connect to the MQTT broker. Should be called before subscribing or publishing.</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var options = _preBuiltOptions ?? new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerHost, _brokerPort)
                .Build();

            await _mqttClient.ConnectAsync(options, cancellationToken);
            _connected = true;

            // Re-subscribe to any topics that were registered before connect
            await ResubscribeAllAsync(cancellationToken);
        }

        /// <summary>Disconnect from the broker.</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectAsync();
            _mqttClient.Dispose();
        }

        // ── Publish — send to broker as well as updating the local cache ───────

        public override async Task PublishAsync(string key, TValue value, IStatus? status, bool retain = false, CancellationToken cancellationToken = default)
        {
            await SendToBrokerAsync(key, value, retain, cancellationToken);
            await base.PublishAsync(key, value, status, retain, cancellationToken);
        }

        public override Task PublishAsync(string key, TValue value, CancellationToken cancellationToken = default)
            => PublishAsync(key, value, null, retain: false, cancellationToken);

        // ── NewItem / RemoveItem hooks — manage broker-side subscriptions ──────

        internal override CacheItem<string, TValue> NewItem(string key)
        {
            var col = base.NewItem(key);
            // Only subscribe at the broker when triggered by a user Subscribe() call.
            // When _incomingMessageDepth > 0 the cache entry is being created as a side-effect of
            // routing an arriving broker message — we already receive this topic via the existing
            // wildcard (e.g. '#') subscription, so adding an exact subscription would cause the broker
            // to re-deliver any retained message and produce a duplicate callback.
            if (_incomingMessageDepth == 0)
                _ = SubscribeBrokerTopicAsync(key);
            return col;
        }

        internal override void RemoveItem(CacheItem<string, TValue> col)
        {
            _ = UnsubscribeBrokerTopicAsync(col.Key);
            base.RemoveItem(col);
        }

        // ── Broker subscription management ────────────────────────────────────

        private async Task SubscribeBrokerTopicAsync(string topic)
        {
            await _brokerLock.WaitAsync();
            try
            {
                if (!_brokerSubscriptions.Add(topic))
                    return; // already subscribed

                if (!_connected)
                    return; // will subscribe on next ConnectAsync

                var filter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _mqttClient.SubscribeAsync(filter);
            }
            finally
            {
                _brokerLock.Release();
            }
        }

        private async Task UnsubscribeBrokerTopicAsync(string topic)
        {
            await _brokerLock.WaitAsync();
            try
            {
                if (!_brokerSubscriptions.Remove(topic))
                    return;

                if (!_connected)
                    return;

                await _mqttClient.UnsubscribeAsync(topic);
            }
            catch
            {
                // Best-effort unsubscribe
            }
            finally
            {
                _brokerLock.Release();
            }
        }

        private async Task ResubscribeAllAsync(CancellationToken cancellationToken)
        {
            List<string> topics;
            await _brokerLock.WaitAsync(cancellationToken);
            try
            {
                topics = new List<string>(_brokerSubscriptions);
            }
            finally
            {
                _brokerLock.Release();
            }

            foreach (var topic in topics)
            {
                var filter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _mqttClient.SubscribeAsync(filter, cancellationToken);
            }
        }

        private async Task SendToBrokerAsync(string key, TValue value, bool retain, CancellationToken cancellationToken)
        {
            if (_serializer == null || !_connected)
                return;

            var payload = _serializer(value);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(key)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);
        }

        // ── Incoming broker messages ───────────────────────────────────────────

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payloadSeq = e.ApplicationMessage.Payload;
            byte[] payload = payloadSeq.IsEmpty ? Array.Empty<byte>() : payloadSeq.FirstSpan.ToArray();

            TValue value;
            try
            {
                value = _deserializer(payload);
            }
            catch
            {
                return; // Ignore malformed messages
            }

            // Route through CacheWithPatterns — handles wildcard fan-out automatically.
            // Calling base.PublishAsync bypasses the broker send override above (no echo-back loop).
            // Raise the depth counter so NewItem (called synchronously inside GetOrAdd before any await)
            // knows not to create a redundant exact-topic broker subscription.
            Interlocked.Increment(ref _incomingMessageDepth);
            try
            {
                await base.PublishAsync(topic, value);
            }
            finally
            {
                Interlocked.Decrement(ref _incomingMessageDepth);
            }
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            _connected = false;

            List<string> topics;
            await _brokerLock.WaitAsync();
            try { topics = new List<string>(_brokerSubscriptions); }
            finally { _brokerLock.Release(); }

            var staleStatus = new Status { State = IStatus.StateValue.Stale, Message = "MQTT broker disconnected" };
            foreach (var topic in topics)
                await base.PublishAsync(topic, staleStatus);
        }
    }
}
