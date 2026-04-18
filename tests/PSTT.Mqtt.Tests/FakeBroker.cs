using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using System.Text;

namespace PSTT.Mqtt.Tests
{
    /// <summary>
    /// In-process MQTT broker for integration tests.
    ///
    /// Wraps a MQTTnet in-process server on a random free port.
    /// Provides:
    ///   PublishAsync   — inject a message directly into the broker (simulates an external publisher).
    ///   WatchTopicAsync — subscribe via an internal watcher client to capture messages at the broker.
    ///   ReceivedMessages — all messages seen by the watcher client.
    ///
    /// Usage:
    ///   await using var broker = await FakeBroker.StartAsync();
    ///   var mqttDs = new MqttCache&lt;string&gt;("127.0.0.1", broker.Port, ...);
    ///   await mqttDs.ConnectAsync();
    /// </summary>
    internal sealed class FakeBroker : IAsyncDisposable
    {
        private readonly MqttServer _server;
        private readonly IMqttClient _watcherClient;
        private readonly List<(string Topic, string Payload)> _received = new();
        private readonly SemaphoreSlim _receivedLock = new(1, 1);

        public int Port { get; }

        private FakeBroker(MqttServer server, IMqttClient watcherClient, int port)
        {
            _server = server;
            _watcherClient = watcherClient;
            Port = port;
        }

        // ── Factory ──────────────────────────────────────────────────────────

        public static async Task<FakeBroker> StartAsync()
        {
            int port = FindFreePort();

            var serverFactory = new MqttServerFactory();
            var serverOptions = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(port)
                .Build();

            var server = serverFactory.CreateMqttServer(serverOptions);
            await server.StartAsync();

            // Watcher client — connects and records any messages we ask it to watch
            var clientFactory = new MqttClientFactory();
            var watcher = clientFactory.CreateMqttClient();
            var broker = new FakeBroker(server, watcher, port);

            watcher.ApplicationMessageReceivedAsync += broker.OnWatcherMessageReceivedAsync;

            await watcher.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("fake-broker-watcher")
                .Build());

            return broker;
        }

        // ── Publish helper ───────────────────────────────────────────────────

        /// <summary>Inject a message into the broker, simulating an external MQTT publisher.</summary>
        public async Task PublishAsync(string topic, string payload)
        {
            await _server.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(
                    new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(Encoding.UTF8.GetBytes(payload))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build())
                { SenderClientId = "fake-broker-injector" });
        }

        // ── Watcher subscription ─────────────────────────────────────────────

        /// <summary>
        /// Subscribe the internal watcher client to a topic filter.
        /// All matching messages will accumulate in ReceivedMessages.
        /// </summary>
        public async Task WatchTopicAsync(string topicFilter)
        {
            var filter = new MqttTopicFilterBuilder()
                .WithTopic(topicFilter)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _watcherClient.SubscribeAsync(filter);
        }

        /// <summary>Returns a snapshot of all messages captured by the watcher.</summary>
        public IReadOnlyList<(string Topic, string Payload)> ReceivedMessages
        {
            get
            {
                _receivedLock.Wait();
                try { return new List<(string, string)>(_received); }
                finally { _receivedLock.Release(); }
            }
        }

        private async Task OnWatcherMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payloadSeq = e.ApplicationMessage.Payload;
            var payload = payloadSeq.IsEmpty ? "" : Encoding.UTF8.GetString(payloadSeq.FirstSpan);

            await _receivedLock.WaitAsync();
            try { _received.Add((topic, payload)); }
            finally { _receivedLock.Release(); }
        }

        // ── Dispose ──────────────────────────────────────────────────────────

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_watcherClient.IsConnected)
                    await _watcherClient.DisconnectAsync();
            }
            catch { /* best effort */ }
            _watcherClient.Dispose();

            await _server.StopAsync();
            _server.Dispose();
        }

        // ── Port helper ──────────────────────────────────────────────────────

        private static int FindFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
