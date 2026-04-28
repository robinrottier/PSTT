using PSTT.Data;
using PSTT.Remote.Transport;
using PSTT.Remote.Transport.Tcp;
using System.Text;
using System.Text.Json;

namespace PSTT.Remote
{
    /// <summary>
    /// Fluent builder for creating configured <see cref="RemoteCache{TValue}"/> instances.
    /// </summary>
    /// <example>
    /// // String values, UTF-8 encoded, connecting via TCP
    /// var client = new RemoteCacheBuilder&lt;string&gt;()
    ///     .WithTcpTransport("localhost", 5000)
    ///     .WithUtf8Encoding()
    ///     .Build();
    /// await client.ConnectAsync();
    ///
    /// // JSON-encoded DTOs
    /// var client = new RemoteCacheBuilder&lt;SensorReading&gt;()
    ///     .WithTcpTransport("server.example.com", 5000)
    ///     .WithJsonEncoding()
    ///     .WithCallbackErrorHandler((ex, msg) => Console.Error.WriteLine(msg))
    ///     .Build();
    /// </example>
    public class RemoteCacheBuilder<TValue>
    {
        private IRemoteTransport? _transport;
        private Func<byte[], TValue>? _deserializer;
        private Func<TValue, byte[]>? _serializer;
        private readonly CacheConfig<string, TValue> _dsConfig = new();
        private bool _autoReconnect;
        private TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);

        // ── Transport ─────────────────────────────────────────────────────────

        /// <summary>
        /// Connect via TCP to a <see cref="RemoteCacheServer{TValue}"/> listening on
        /// the given host and port.
        /// </summary>
        public RemoteCacheBuilder<TValue> WithTcpTransport(string host, int port)
        {
            _transport = new TcpClientTransport(
                host ?? throw new ArgumentNullException(nameof(host)),
                port);
            return this;
        }

        /// <summary>
        /// Use a custom transport (WebSocket, SignalR, in-process, etc.).
        /// </summary>
        public RemoteCacheBuilder<TValue> WithTransport(IRemoteTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            return this;
        }

        // ── Encoding ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sets custom serializer and deserializer functions.
        /// </summary>
        /// <param name="deserializer">Converts raw payload bytes to <typeparamref name="TValue"/>.</param>
        /// <param name="serializer">Converts <typeparamref name="TValue"/> to bytes for publishing.
        /// Optional — omit if this source is receive-only.</param>
        public RemoteCacheBuilder<TValue> WithEncoding(
            Func<byte[], TValue> deserializer,
            Func<TValue, byte[]>? serializer = null)
        {
            _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
            _serializer = serializer;
            return this;
        }

        /// <summary>
        /// Configures UTF-8 JSON serialization using <see cref="System.Text.Json.JsonSerializer"/>.
        /// Suitable for any type that is JSON-serializable.
        /// </summary>
        public RemoteCacheBuilder<TValue> WithJsonEncoding(JsonSerializerOptions? options = null)
        {
            _deserializer = bytes => JsonSerializer.Deserialize<TValue>(bytes, options)!;
            _serializer = value => JsonSerializer.SerializeToUtf8Bytes(value, options);
            return this;
        }

        // ── DataSource config ─────────────────────────────────────────────────

        /// <summary>Maximum number of topics (unique keys) allowed in the local cache. 0 = unlimited.</summary>
        public RemoteCacheBuilder<TValue> WithMaxTopics(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxTopics = max;
            return this;
        }

        /// <summary>Maximum number of subscriptions per topic. 0 = unlimited.</summary>
        public RemoteCacheBuilder<TValue> WithMaxSubscriptionsPerTopic(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxSubscriptionsPerTopic = max;
            return this;
        }

        /// <summary>Maximum total subscriptions across all topics. 0 = unlimited.</summary>
        public RemoteCacheBuilder<TValue> WithMaxSubscriptionsTotal(int max)
        {
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= 0");
            _dsConfig.MaxSubscriptionsTotal = max;
            return this;
        }

        /// <summary>
        /// Maximum concurrent callback invocations per subscription.
        /// -1 = unlimited, 0 = no concurrent limit, &gt;0 = explicit limit.
        /// </summary>
        public RemoteCacheBuilder<TValue> WithMaxCallbackConcurrency(int max)
        {
            if (max < -1) throw new ArgumentOutOfRangeException(nameof(max), "Must be >= -1");
            _dsConfig.MaxCallbackConcurrency = max;
            return this;
        }

        /// <summary>Configures callbacks to execute synchronously on the publishing thread.</summary>
        public RemoteCacheBuilder<TValue> WithSynchronousCallbacks()
        {
            _dsConfig.Dispatcher = new SynchronousDispatcher();
            return this;
        }

        /// <summary>
        /// Configures callbacks to execute on the thread pool.
        /// </summary>
        /// <param name="waitForCompletion">When true, publishing waits for all callbacks to complete.</param>
        public RemoteCacheBuilder<TValue> WithThreadPoolCallbacks(bool waitForCompletion = false)
        {
            _dsConfig.Dispatcher = new ThreadPoolDispatcher(waitForCompletion);
            return this;
        }

        /// <summary>Sets a custom callback dispatcher.</summary>
        public RemoteCacheBuilder<TValue> WithDispatcher(ICallbackDispatcher dispatcher)
        {
            _dsConfig.Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            return this;
        }

        /// <summary>Sets a handler invoked when a subscriber callback throws an exception.</summary>
        public RemoteCacheBuilder<TValue> WithCallbackErrorHandler(Action<Exception, string> handler)
        {
            _dsConfig.OnCallbackError = handler ?? throw new ArgumentNullException(nameof(handler));
            return this;
        }

        /// <summary>
        /// Enables automatic reconnection when the server connection drops unexpectedly.
        /// The cache will re-subscribe to all topics after each successful reconnect.
        /// </summary>
        /// <param name="delay">Delay between reconnect attempts. Defaults to 5 seconds.</param>
        public RemoteCacheBuilder<TValue> WithAutoReconnect(TimeSpan? delay = null)
        {
            _autoReconnect = true;
            if (delay.HasValue) _reconnectDelay = delay.Value;
            return this;
        }

        // ── Build ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds and returns a configured <see cref="RemoteCache{TValue}"/>.
        /// Call <see cref="RemoteCache{TValue}.ConnectAsync"/> on the result to connect.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when transport or encoding has not been configured.
        /// </exception>
        public RemoteCache<TValue> Build()
        {
            if (_transport == null)
                throw new InvalidOperationException(
                    "A transport is required. Call WithTcpTransport() or WithTransport() before Build().");
            if (_deserializer == null)
                throw new InvalidOperationException(
                    "Encoding is required. Call WithEncoding(), WithUtf8Encoding(), or WithJsonEncoding() before Build().");

            return new RemoteCache<TValue>(_transport, _deserializer, _serializer, _dsConfig, _autoReconnect, _reconnectDelay);
        }
    }

    /// <summary>
    /// Extension methods that add encoding shortcuts for <see cref="RemoteCacheBuilder{TValue}"/>
    /// when <typeparamref name="TValue"/> is <see cref="string"/>.
    /// </summary>
    public static class RemoteCacheBuilderStringExtensions
    {
        /// <summary>
        /// Configure UTF-8 encoding: strings are transmitted as UTF-8 bytes.
        /// </summary>
        public static RemoteCacheBuilder<string> WithUtf8Encoding(
            this RemoteCacheBuilder<string> builder)
            => builder.WithEncoding(
                deserializer: bytes => Encoding.UTF8.GetString(bytes),
                serializer:   value => Encoding.UTF8.GetBytes(value));

        /// <summary>
        /// Configure raw (pass-through) encoding: payloads are transmitted as-is.
        /// </summary>
        public static RemoteCacheBuilder<byte[]> WithRawEncoding(
            this RemoteCacheBuilder<byte[]> builder)
            => builder.WithEncoding(
                deserializer: bytes => bytes,
                serializer:   value => value);
    }

    /// <summary>
    /// Extension methods that add WebSocket transport shortcuts to
    /// <see cref="RemoteCacheBuilder{TValue}"/>.
    /// </summary>
    public static class RemoteCacheBuilderWebSocketExtensions
    {
        /// <summary>
        /// Connect via WebSocket to a <c>ws://</c> or <c>wss://</c> URI.
        /// </summary>
        public static RemoteCacheBuilder<TValue> WithWebSocketTransport<TValue>(
            this RemoteCacheBuilder<TValue> builder, Uri uri)
            => builder.WithTransport(new Transport.WebSocket.WebSocketClientTransport(uri));

        /// <summary>
        /// Connect via WebSocket to a URI string.
        /// </summary>
        public static RemoteCacheBuilder<TValue> WithWebSocketTransport<TValue>(
            this RemoteCacheBuilder<TValue> builder, string uri)
            => builder.WithTransport(new Transport.WebSocket.WebSocketClientTransport(uri));
    }

    /// <summary>
    /// Extension methods that add SignalR transport shortcuts to
    /// <see cref="RemoteCacheBuilder{TValue}"/>.
    /// </summary>
    public static class RemoteCacheBuilderSignalRExtensions
    {
        /// <summary>
        /// Connect via SignalR to the hub at the given URL.
        /// </summary>
        public static RemoteCacheBuilder<TValue> WithSignalRTransport<TValue>(
            this RemoteCacheBuilder<TValue> builder, string hubUrl)
            => builder.WithTransport(new Transport.SignalR.SignalRClientTransport(hubUrl));

        /// <summary>
        /// Connect via SignalR using a pre-built <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/>.
        /// Use this overload in Blazor WASM where you build the connection yourself.
        /// </summary>
        public static RemoteCacheBuilder<TValue> WithSignalRTransport<TValue>(
            this RemoteCacheBuilder<TValue> builder,
            Microsoft.AspNetCore.SignalR.Client.HubConnection connection)
            => builder.WithTransport(new Transport.SignalR.SignalRClientTransport(connection));
    }
}
