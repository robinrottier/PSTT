using PSTT.Data;
using MQTTnet;
using MQTTnet.Protocol;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PSTT.Mqtt
{
    /// <summary>
    /// Fluent builder for creating configured <see cref="MqttCache{TValue}"/> instances.
    /// </summary>
    /// <example>
    /// // String values, UTF-8 encoded
    /// var mqtt = new MqttCacheBuilder&lt;string&gt;()
    ///     .WithBroker("mqtt.example.com")
    ///     .WithUtf8Encoding()
    ///     .Build();
    ///
    /// // JSON-encoded DTOs with credentials
    /// var mqtt = new MqttCacheBuilder&lt;SensorReading&gt;()
    ///     .WithBroker("mqtt.example.com", port: 8883)
    ///     .WithJsonEncoding()
    ///     .WithClientId("my-service")
    ///     .WithCredentials("user", "secret")
    ///     .WithCallbackErrorHandler((ex, msg) => Console.Error.WriteLine(msg))
    ///     .Build();
    /// </example>
    public class MqttCacheBuilder<TValue>
    {
        private string? _brokerHost;
        private int _brokerPort = 1883;
        private string? _clientId;
        private string? _username;
        private string? _password;
        private MqttQualityOfServiceLevel _qos = MqttQualityOfServiceLevel.AtLeastOnce;

        private Func<byte[], TValue>? _deserializer;
        private Func<TValue, byte[]>? _serializer;

        private readonly CacheConfig<string, TValue> _dsConfig = new();

        // ── Broker connection ─────────────────────────────────────────────────

        /// <summary>
        /// Sets the MQTT broker host and optional port (default 1883).
        /// </summary>
        public MqttCacheBuilder<TValue> WithBroker(string host, int port = 1883)
        {
            _brokerHost = host ?? throw new ArgumentNullException(nameof(host));
            _brokerPort = port;
            return this;
        }

        /// <summary>
        /// Sets the MQTT client identifier. Defaults to a random GUID if not set.
        /// </summary>
        public MqttCacheBuilder<TValue> WithClientId(string clientId)
        {
            _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            return this;
        }

        /// <summary>
        /// Sets broker credentials for authenticated connections.
        /// </summary>
        public MqttCacheBuilder<TValue> WithCredentials(string username, string? password = null)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password;
            return this;
        }

        /// <summary>
        /// Sets the MQTT QoS level for subscriptions and publications (default AtLeastOnce).
        /// </summary>
        public MqttCacheBuilder<TValue> WithQualityOfService(MqttQualityOfServiceLevel qos)
        {
            _qos = qos;
            return this;
        }

        // ── Encoding ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sets custom serializer and deserializer functions.
        /// </summary>
        /// <param name="deserializer">Converts raw MQTT payload bytes to <typeparamref name="TValue"/>.</param>
        /// <param name="serializer">Converts <typeparamref name="TValue"/> to bytes for publishing.
        /// Optional — omit if this source is receive-only.</param>
        public MqttCacheBuilder<TValue> WithEncoding(
            Func<byte[], TValue> deserializer,
            Func<TValue, byte[]>? serializer = null)
        {
            _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
            _serializer = serializer;
            return this;
        }

        /// <summary>
        /// Configures UTF-8 JSON serialisation using <see cref="System.Text.Json.JsonSerializer"/>.
        /// Suitable for any type that is JSON-serialisable.
        /// </summary>
        public MqttCacheBuilder<TValue> WithJsonEncoding(JsonSerializerOptions? options = null)
        {
            _deserializer = bytes => JsonSerializer.Deserialize<TValue>(bytes, options)!;
            _serializer = value => JsonSerializer.SerializeToUtf8Bytes(value, options);
            return this;
        }

        // ── DataSource config ─────────────────────────────────────────────────

        /// <summary>Maximum number of topics (unique keys) allowed in the local cache. 0 = unlimited.</summary>
        public MqttCacheBuilder<TValue> WithMaxTopics(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxTopics = max;
            return this;
        }

        /// <summary>Maximum number of subscriptions per topic. 0 = unlimited.</summary>
        public MqttCacheBuilder<TValue> WithMaxSubscriptionsPerTopic(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxSubscriptionsPerTopic = max;
            return this;
        }

        /// <summary>Maximum total subscriptions across all topics. 0 = unlimited.</summary>
        public MqttCacheBuilder<TValue> WithMaxSubscriptionsTotal(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxSubscriptionsTotal = max;
            return this;
        }

        /// <summary>
        /// Maximum concurrent callback invocations per subscription.
        /// -1 = unlimited, 0 = no concurrent limit, &gt;0 = explicit limit.
        /// </summary>
        public MqttCacheBuilder<TValue> WithMaxCallbackConcurrency(int max)
        {
            if (max < -1) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= -1");
            _dsConfig.MaxCallbackConcurrency = max;
            return this;
        }

        /// <summary>Configures callbacks to execute synchronously on the publishing thread.</summary>
        public MqttCacheBuilder<TValue> WithSynchronousCallbacks()
        {
            _dsConfig.Dispatcher = new SynchronousDispatcher();
            return this;
        }

        /// <summary>
        /// Configures callbacks to execute on the thread pool.
        /// </summary>
        /// <param name="waitForCompletion">When true, publishing waits for all callbacks to complete.</param>
        public MqttCacheBuilder<TValue> WithThreadPoolCallbacks(bool waitForCompletion = false)
        {
            _dsConfig.Dispatcher = new ThreadPoolDispatcher(waitForCompletion);
            return this;
        }

        /// <summary>Sets a custom callback dispatcher.</summary>
        public MqttCacheBuilder<TValue> WithDispatcher(ICallbackDispatcher dispatcher)
        {
            _dsConfig.Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            return this;
        }

        /// <summary>Sets a handler invoked when a subscriber callback throws an exception.</summary>
        public MqttCacheBuilder<TValue> WithCallbackErrorHandler(Action<Exception, string> handler)
        {
            _dsConfig.OnCallbackError = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        // ── Build ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds and returns a configured <see cref="MqttCache{TValue}"/>.
        /// Call <see cref="MqttCache{TValue}.ConnectAsync"/> on the result to connect to the broker.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="WithBroker"/> or encoding has not been configured.
        /// </exception>
        public MqttCache<TValue> Build()
        {
            if (_brokerHost == null)
                throw new InvalidOperationException("Broker host is required. Call WithBroker() before Build().");
            if (_deserializer == null)
                throw new InvalidOperationException(
                    "Encoding is required. Call WithEncoding(), WithUtf8Encoding(), or WithJsonEncoding() before Build().");

            var optBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerHost, _brokerPort);

            if (_clientId != null)
                optBuilder = optBuilder.WithClientId(_clientId);

            if (_username != null)
                optBuilder = optBuilder.WithCredentials(_username, _password);

            var options = optBuilder.Build();

            return new MqttCache<TValue>(options, _deserializer, _serializer, _dsConfig);
        }
    }

    /// <summary>
    /// Extension methods that add encoding shortcuts for <see cref="MqttCacheBuilder{TValue}"/>
    /// when <typeparamref name="TValue"/> is <see cref="string"/>.
    /// </summary>
    public static class MqttCacheBuilderStringExtensions
    {
        /// <summary>
        /// Configures UTF-8 string encoding — the most common MQTT payload format.
        /// Equivalent to <c>WithEncoding(bytes =&gt; Encoding.UTF8.GetString(bytes), value =&gt; Encoding.UTF8.GetBytes(value))</c>.
        /// </summary>
        public static MqttCacheBuilder<string> WithUtf8Encoding(
            this MqttCacheBuilder<string> builder)
            => builder.WithEncoding(
                bytes => Encoding.UTF8.GetString(bytes),
                value => Encoding.UTF8.GetBytes(value));

        /// <summary>
        /// Configures raw byte-array passthrough — payload bytes are returned as-is.
        /// Serializer stores the array directly.
        /// </summary>
        public static MqttCacheBuilder<byte[]> WithRawEncoding(
            this MqttCacheBuilder<byte[]> builder)
            => builder.WithEncoding(
                bytes => bytes,
                value => value);
    }
}
