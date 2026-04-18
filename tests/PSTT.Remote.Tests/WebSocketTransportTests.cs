using PSTT.Data;
using PSTT.Remote.Transport.WebSocket;
using System.Text;

namespace PSTT.Remote.Tests
{
    /// <summary>
    /// Integration tests for WebSocket transports.
    /// Uses a standalone <see cref="WebSocketServerTransport"/> (HttpListener) on a
    /// dynamically-assigned port, and connects with <see cref="WebSocketClientTransport"/>.
    /// </summary>
    [Collection("WebSocket")]
    public class WebSocketTransportTests : IAsyncDisposable
    {
        private readonly CacheWithWildcards<string, string> _upstream;
        private readonly WebSocketServerTransport _serverTransport;
        private readonly RemoteCacheServer<string> _server;
        private readonly string _uriPrefix;
        private readonly string _wsUri;

        public WebSocketTransportTests()
        {
            // Pick a high-numbered port to avoid conflicts
            var port = GetFreePort();
            _uriPrefix = $"http://localhost:{port}/dsws/";
            _wsUri = $"ws://localhost:{port}/dsws/";

            _upstream = new CacheWithWildcards<string, string>();
            _serverTransport = new WebSocketServerTransport(_uriPrefix);
            _server = new RemoteCacheServer<string>(
                _upstream,
                serializer:   s => Encoding.UTF8.GetBytes(s),
                deserializer: b => Encoding.UTF8.GetString(b),
                _serverTransport,
                forwardPublish: true);

            _server.StartAsync().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private RemoteCache<string> CreateClient()
            => new RemoteCacheBuilder<string>()
                .WithWebSocketTransport(_wsUri)
                .WithUtf8Encoding()
                .Build();

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return false;
        }

        // ── Tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task WebSocket_Subscribe_ReceivesUpstreamPublish()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            string? received = null;
            var sub = client.Subscribe("ws/temp", async s => { received = s.Value; });
            await Task.Delay(100);

            await _upstream.PublishAsync("ws/temp", "36.6");

            Assert.True(await WaitForAsync(() => received == "36.6"),
                "WebSocket client did not receive upstream publish");

            client.Unsubscribe(sub);
        }

        [Fact]
        public async Task WebSocket_Wildcard_ReceivesMultipleTopics()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            var received = new List<string>();
            var sub = client.Subscribe("ws/sensors/+", async s =>
            {
                lock (received) received.Add(s.Value);
            });
            await Task.Delay(100);

            await _upstream.PublishAsync("ws/sensors/a", "1");
            await _upstream.PublishAsync("ws/sensors/b", "2");

            Assert.True(await WaitForAsync(() => { lock (received) return received.Count >= 2; }),
                $"Expected 2 wildcard messages, got {received.Count}");

            client.Unsubscribe(sub);
        }

        [Fact]
        public async Task WebSocket_Publish_ForwardedToUpstream()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();
            await Task.Delay(100);

            string? upstreamReceived = null;
            _upstream.Subscribe("ws/cmd", async s =>
            {
                if (!s.Status.IsPending) upstreamReceived = s.Value;
            });

            await client.PublishAsync("ws/cmd", "HELLO");

            Assert.True(await WaitForAsync(() => upstreamReceived == "HELLO"),
                "WebSocket publish not forwarded to upstream");
        }

        [Fact]
        public async Task WebSocket_MultipleClients_BothReceive()
        {
            await using var c1 = CreateClient();
            await using var c2 = CreateClient();

            // Subscribe BEFORE ConnectAsync so ResubscribeAllAsync awaits each sub
            // round-trip — guaranteeing server-side registration before publishing.
            string? r1 = null, r2 = null;
            var s1 = c1.Subscribe("ws/shared", async s => { r1 = s.Value; });
            var s2 = c2.Subscribe("ws/shared", async s => { r2 = s.Value; });

            await c1.ConnectAsync();
            await c2.ConnectAsync();

            await _upstream.PublishAsync("ws/shared", "broadcast");

            Assert.True(await WaitForAsync(() => r1 == "broadcast" && r2 == "broadcast"),
                $"Not both received: r1={r1}, r2={r2}");

            c1.Unsubscribe(s1);
            c2.Unsubscribe(s2);
        }

        [Fact]
        public async Task WebSocket_Disconnect_TopicsMarkedStale()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            PSTT.Data.IStatus? lastStatus = null;
            var sub = client.Subscribe("ws/status", async s => { lastStatus = s.Status; });
            await Task.Delay(100);

            await _upstream.PublishAsync("ws/status", "ok");
            Assert.True(await WaitForAsync(() => lastStatus?.IsActive == true));

            await client.DisconnectAsync();

            Assert.True(await WaitForAsync(() =>
                lastStatus?.State == PSTT.Data.IStatus.StateValue.Stale),
                $"Expected Stale, got {lastStatus?.State}");
        }

        // ── Builder validation ────────────────────────────────────────────────

        [Fact]
        public void Builder_WithWebSocketTransport_Uri_Null_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithWebSocketTransport((Uri)null!));

        [Fact]
        public void Builder_WithWebSocketTransport_String_Null_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithWebSocketTransport((string)null!));

        [Fact]
        public void Builder_WithSignalRTransport_NullUrl_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithSignalRTransport((string)null!));

        [Fact]
        public void Builder_WithSignalRTransport_NullConnection_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithSignalRTransport(
                        (Microsoft.AspNetCore.SignalR.Client.HubConnection)null!));

        // ── WebSocketServerTransport validation ────────────────────────────────

        [Fact]
        public void WebSocketServerTransport_EmptyPrefix_Throws()
            => Assert.Throws<ArgumentException>(() =>
                new WebSocketServerTransport(""));

        [Fact]
        public void WebSocketClientTransport_NullUri_String_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new WebSocketClientTransport((string)null!));

        [Fact]
        public void WebSocketClientTransport_NullUri_Typed_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new WebSocketClientTransport((Uri)null!));

        [Fact]
        public void WebSocketConnectionTransport_NullSocket_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new WebSocketConnectionTransport(null!));
    }
}
