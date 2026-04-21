using PSTT.Data;
using PSTT.Remote;
using PSTT.Remote.Transport;
using PSTT.Remote.Transport.Tcp;
using System.Text;
using System.Text.Json;

namespace PSTT.Remote.Tests
{
    /// <summary>
    /// Integration tests for RemoteCache + RemoteCacheServer over an
    /// in-process TCP connection (loopback, OS-assigned port).
    ///
    /// Test groups:
    ///   1. Standalone  — RemoteCache used directly (no local cache layer)
    ///   2. Chain       — RemoteCache wired as upstream for a local CacheWithPatterns
    ///   3. Publish     — client-side publishes forwarded to server upstream
    ///   4. Lifecycle   — disconnect marks topics Stale; reconnect restores subscriptions
    ///   5. Builder     — RemoteCacheBuilder validation and end-to-end
    ///   6. MultiClient — multiple simultaneous client connections
    /// </summary>
    public class RemoteDataSourceTests : IAsyncLifetime
    {
        // ── Shared server plumbing ────────────────────────────────────────────

        /// <summary>In-process upstream DataSource used by all tests.</summary>
        private CacheWithWildcards<string, string> _upstream = null!;
        private RemoteCacheServer<string> _server = null!;
        private TcpServerTransport _serverTransport = null!;
        private int _port;

        public async Task InitializeAsync()
        {
            _upstream = new CacheWithWildcards<string, string>();

            _serverTransport = new TcpServerTransport(0); // OS-assigned port
            _server = new RemoteCacheServer<string>(
                _upstream,
                serializer:   s => Encoding.UTF8.GetBytes(s),
                deserializer: b => Encoding.UTF8.GetString(b),
                _serverTransport,
                forwardPublish: true);

            await _server.StartAsync();
            _port = _serverTransport.BoundPort;
        }

        public async Task DisposeAsync()
        {
            await _server.DisposeAsync();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private RemoteCache<string> CreateClient()
            => new RemoteCacheBuilder<string>()
                .WithTcpTransport("127.0.0.1", _port)
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

        // ────────────────────────────────────────────────────────────────────
        // Group 1: Standalone
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Standalone_Subscribe_ReceivesUpstreamPublish()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            string? received = null;
            var sub = client.Subscribe("sensors/temp", async s => { received = s.Value; });
            await Task.Delay(100);

            await _upstream.PublishAsync("sensors/temp", "22.5");

            Assert.True(await WaitForAsync(() => received == "22.5"),
                "Client did not receive upstream publish");

            client.Unsubscribe(sub);
        }

        [Fact]
        public async Task Standalone_Unsubscribe_StopsDelivery()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            int count = 0;
            var sub = client.Subscribe("sensors/hum", async s => { count++; });
            await Task.Delay(100);

            await _upstream.PublishAsync("sensors/hum", "60");
            Assert.True(await WaitForAsync(() => count == 1), "First update not received");

            client.Unsubscribe(sub);
            await Task.Delay(100);

            await _upstream.PublishAsync("sensors/hum", "65");
            await Task.Delay(200);

            Assert.Equal(1, count); // no further callbacks after unsubscribe
        }

        [Fact]
        public async Task Standalone_ExistingValue_DeliveredOnSubscribe()
        {
            // Publish a value that the upstream cache already holds (via a seed subscription).
            // When the remote client subscribes, it should receive the current cached value.
            ISubscription<string, string>? seedSub = null;
            try
            {
                // Seed subscription ensures the upstream creates and keeps the cache entry
                seedSub = _upstream.Subscribe("preloaded/value", async _ => { });
                await _upstream.PublishAsync("preloaded/value", "PRE-EXISTING");
                await Task.Delay(50);

                await using var client = CreateClient();
                await client.ConnectAsync();

                string? received = null;
                var sub = client.Subscribe("preloaded/value", async s =>
                {
                    if (!s.Status.IsPending)
                        received = s.Value;
                });

                Assert.True(await WaitForAsync(() => received == "PRE-EXISTING"),
                    "Pre-existing upstream value was not delivered on client subscribe");

                client.Unsubscribe(sub);
            }
            finally
            {
                if (seedSub != null) _upstream.Unsubscribe(seedSub);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 2: Wildcard subscriptions
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Wildcard_SingleLevel_ReceivesMatchingTopics()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            var received = new List<(string Key, string Val)>();
            var sub = client.Subscribe("sensors/+", async s =>
            {
                lock (received) received.Add((s.Key, s.Value));
            });
            await Task.Delay(100);

            await _upstream.PublishAsync("sensors/temp", "21");
            await _upstream.PublishAsync("sensors/hum",  "55");
            await _upstream.PublishAsync("other/topic",  "X"); // should not match

            Assert.True(await WaitForAsync(() => { lock (received) return received.Count >= 2; }),
                $"Expected ≥ 2 wildcard matches, got {received.Count}");

            lock (received)
            {
                Assert.Contains(received, r => r.Key == "sensors/temp" && r.Val == "21");
                Assert.Contains(received, r => r.Key == "sensors/hum"  && r.Val == "55");
                Assert.DoesNotContain(received, r => r.Key == "other/topic");
            }

            client.Unsubscribe(sub);
        }

        [Fact]
        public async Task Wildcard_MultiLevel_ReceivesAllDescendants()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            var keys = new List<string>();
            var sub = client.Subscribe("building/#", async s =>
            {
                lock (keys) keys.Add(s.Key);
            });
            await Task.Delay(100);

            await _upstream.PublishAsync("building/floor1/temp", "20");
            await _upstream.PublishAsync("building/floor2/temp", "21");
            await _upstream.PublishAsync("building/floor1/hum",  "50");

            Assert.True(await WaitForAsync(() => { lock (keys) return keys.Count >= 3; }),
                $"Expected 3 wildcard matches, got {keys.Count}");

            client.Unsubscribe(sub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 3: Chain — RemoteCache as upstream for a local DS
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Chain_LocalSubscriber_ReceivesRemoteUpstreamValue()
        {
            await using var remoteClient = CreateClient();
            await remoteClient.ConnectAsync();

            var localDs = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(remoteClient, supportsWildcards: true)
                .Build();

            string? received = null;
            var sub = localDs.Subscribe("chain/topic", async s =>
            {
                if (!s.Status.IsPending) received = s.Value;
            });
            await Task.Delay(200);

            await _upstream.PublishAsync("chain/topic", "chain-value");

            Assert.True(await WaitForAsync(() => received == "chain-value"),
                "Local subscriber did not receive value via remote upstream chain");

            localDs.Unsubscribe(sub);
        }

        [Fact]
        public async Task Chain_WildcardLocalSubscriber_ReceivesMultipleRemoteValues()
        {
            await using var remoteClient = CreateClient();
            await remoteClient.ConnectAsync();

            var localDs = new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(remoteClient, supportsWildcards: true)
                .Build();

            var received = new List<string>();
            var sub = localDs.Subscribe("devices/+/state", async s =>
            {
                lock (received) received.Add(s.Value);
            });
            await Task.Delay(200);

            await _upstream.PublishAsync("devices/a/state", "on");
            await _upstream.PublishAsync("devices/b/state", "off");

            Assert.True(await WaitForAsync(() => { lock (received) return received.Count >= 2; }),
                $"Chain wildcard got {received.Count} messages");

            localDs.Unsubscribe(sub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 4: Publish from client → forwarded to server upstream
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Publish_ForwardedToUpstream()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();
            await Task.Delay(100);

            string? upstreamReceived = null;
            _upstream.Subscribe("cmd/light", async s =>
            {
                if (!s.Status.IsPending) upstreamReceived = s.Value;
            });

            await client.PublishAsync("cmd/light", "ON");

            Assert.True(await WaitForAsync(() => upstreamReceived == "ON"),
                "Client publish was not forwarded to server upstream");
        }

        [Fact]
        public async Task Publish_WithoutSerializer_DoesNotThrow()
        {
            // Client with no serializer — publish should be silent no-op
            await using var client = new RemoteCache<string>(
                new TcpClientTransport("127.0.0.1", _port),
                deserializer: b => Encoding.UTF8.GetString(b),
                serializer: null);

            await client.ConnectAsync();

            // Should not throw
            await client.PublishAsync("cmd/noop", "value");
            await Task.Delay(100);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 5: Lifecycle — disconnect marks Stale; reconnect restores
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Lifecycle_Disconnect_SubscribedTopicsMarkedStale()
        {
            await using var client = CreateClient();
            await client.ConnectAsync();

            IStatus? lastStatus = null;
            var sub = client.Subscribe("status/sensor", async s => { lastStatus = s.Status; });
            await Task.Delay(100);

            await _upstream.PublishAsync("status/sensor", "ok");
            Assert.True(await WaitForAsync(() => lastStatus?.IsActive == true),
                "Topic should become Active after first publish");

            await client.DisconnectAsync();

            Assert.True(await WaitForAsync(() => lastStatus?.State == IStatus.StateValue.Stale),
                $"Topic should be Stale after disconnect, got {lastStatus?.State}");
        }

        [Fact]
        public async Task Lifecycle_SubscribeBeforeConnect_DeliveredAfterConnect()
        {
            await using var client = new RemoteCache<string>(
                new TcpClientTransport("127.0.0.1", _port),
                b => Encoding.UTF8.GetString(b),
                s => Encoding.UTF8.GetBytes(s));

            string? received = null;
            var sub = client.Subscribe("early/sub", async s =>
            {
                if (!s.Status.IsPending) received = s.Value;
            });

            // Connect AFTER subscribing — ResubscribeAllAsync should send the pending sub
            await client.ConnectAsync();
            await Task.Delay(200);

            await _upstream.PublishAsync("early/sub", "after-connect");

            Assert.True(await WaitForAsync(() => received == "after-connect"),
                "Subscribe-before-connect did not deliver message after connect");

            client.Unsubscribe(sub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 6: Multiple simultaneous clients
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task MultiClient_BothReceiveUpstreamPublish()
        {
            await using var client1 = CreateClient();
            await using var client2 = CreateClient();

            await client1.ConnectAsync();
            await client2.ConnectAsync();

            string? recv1 = null, recv2 = null;
            var sub1 = client1.Subscribe("shared/topic", async s => { recv1 = s.Value; });
            var sub2 = client2.Subscribe("shared/topic", async s => { recv2 = s.Value; });
            await Task.Delay(150);

            await _upstream.PublishAsync("shared/topic", "broadcast");

            Assert.True(await WaitForAsync(() => recv1 == "broadcast" && recv2 == "broadcast"),
                $"Not all clients received: recv1={recv1}, recv2={recv2}");

            client1.Unsubscribe(sub1);
            client2.Unsubscribe(sub2);
        }

        [Fact]
        public async Task MultiClient_DisconnectOneDoesNotAffectOther()
        {
            await using var client1 = CreateClient();
            await using var client2 = CreateClient();

            await client1.ConnectAsync();
            await client2.ConnectAsync();

            string? recv1 = null, recv2 = null;
            var sub2 = client2.Subscribe("persist/topic", async s => { recv2 = s.Value; });
            var sub1 = client1.Subscribe("persist/topic", async s => { recv1 = s.Value; });

            // Confirm both subscriptions are live on the server before disconnecting client1.
            // Re-publish until both callbacks fire — the first publish may race ahead of
            // subscription registration on the server in optimised (Release) builds.
            var subDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < subDeadline && (recv1 != "ready" || recv2 != "ready"))
            {
                await _upstream.PublishAsync("persist/topic", "ready");
                await Task.Delay(200);
            }
            Assert.True(recv1 == "ready" && recv2 == "ready",
                "Subscriptions did not become active in time");
            recv2 = null;

            // Disconnect client1 and wait for the server to process the disconnect
            await client1.DisconnectAsync();
            await Task.Delay(100);

            // Client2 should still receive. Re-publish until the callback fires to tolerate
            // any thread-pool pressure when running in parallel with other test assemblies.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && recv2 != "still-there")
            {
                await _upstream.PublishAsync("persist/topic", "still-there");
                await Task.Delay(300);
            }

            Assert.True(recv2 == "still-there",
                "Client2 stopped receiving after client1 disconnected");

            client2.Unsubscribe(sub2);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 7: Builder
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Builder_WithTcpTransport_CreatesWorkingClient()
        {
            await using var client = new RemoteCacheBuilder<string>()
                .WithTcpTransport("127.0.0.1", _port)
                .WithUtf8Encoding()
                .Build();

            await client.ConnectAsync();

            string? received = null;
            var sub = client.Subscribe("builder/topic", async s => { received = s.Value; });
            await Task.Delay(100);

            await _upstream.PublishAsync("builder/topic", "from-builder");

            Assert.True(await WaitForAsync(() => received == "from-builder"),
                "Builder-created client did not receive message");

            client.Unsubscribe(sub);
        }

        [Fact]
        public async Task Builder_WithJsonEncoding_RoundTrips()
        {
            await using var serverTransport = new TcpServerTransport(0);
            var jsonUpstream = new CacheWithWildcards<string, SensorReading>();
            await using var jsonServer = new RemoteCacheServer<SensorReading>(
                jsonUpstream,
                v => JsonSerializer.SerializeToUtf8Bytes(v),
                b => JsonSerializer.Deserialize<SensorReading>(b)!,
                serverTransport);
            await jsonServer.StartAsync();

            await using var client = new RemoteCacheBuilder<SensorReading>()
                .WithTcpTransport("127.0.0.1", serverTransport.BoundPort)
                .WithJsonEncoding()
                .Build();

            await client.ConnectAsync();

            SensorReading? received = null;
            var sub = client.Subscribe("sensor/json", async s =>
            {
                if (!s.Status.IsPending) received = s.Value;
            });
            await Task.Delay(100);

            await jsonUpstream.PublishAsync("sensor/json", new SensorReading { Name = "co2", Value = 412.5 });

            Assert.True(await WaitForAsync(() => received?.Name == "co2"),
                "JSON-encoded value was not received");
            Assert.Equal(412.5, received!.Value);

            client.Unsubscribe(sub);
        }

        [Fact]
        public void Builder_WithoutTransport_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithUtf8Encoding()
                    .Build());
        }

        [Fact]
        public void Builder_WithoutEncoding_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new RemoteCacheBuilder<string>()
                    .WithTcpTransport("localhost", 5000)
                    .Build());
        }

        [Fact]
        public void Builder_WithTransport_Null_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>().WithTransport(null!));

        [Fact]
        public void Builder_WithEncoding_NullDeserializer_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>().WithEncoding(null!));

        [Fact]
        public void Builder_WithDispatcher_Null_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>().WithDispatcher(null!));

        [Fact]
        public void Builder_WithCallbackErrorHandler_Null_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheBuilder<string>().WithCallbackErrorHandler(null!));

        [Fact]
        public void Builder_WithMaxTopics_Negative_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RemoteCacheBuilder<string>().WithMaxTopics(-1));

        [Fact]
        public void Builder_WithMaxSubscriptionsPerTopic_Negative_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RemoteCacheBuilder<string>().WithMaxSubscriptionsPerTopic(-1));

        [Fact]
        public void Builder_WithMaxSubscriptionsTotal_Negative_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RemoteCacheBuilder<string>().WithMaxSubscriptionsTotal(-1));

        [Fact]
        public void Builder_WithMaxCallbackConcurrency_TooLow_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RemoteCacheBuilder<string>().WithMaxCallbackConcurrency(-2));

        // ────────────────────────────────────────────────────────────────────
        // Group 8: Constructor validation
        // ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Client_NullTransport_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCache<string>(null!, b => Encoding.UTF8.GetString(b)));

        [Fact]
        public void Client_NullDeserializer_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCache<string>(
                    new TcpClientTransport("localhost", 5000),
                    null!));

        [Fact]
        public void Server_NullUpstream_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheServer<string>(null!, s => Encoding.UTF8.GetBytes(s),
                    b => Encoding.UTF8.GetString(b), new TcpServerTransport(0)));

        [Fact]
        public void Server_NullSerializer_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheServer<string>(new CacheWithWildcards<string, string>(),
                    null!, b => Encoding.UTF8.GetString(b), new TcpServerTransport(0)));

        [Fact]
        public void Server_NullDeserializer_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheServer<string>(new CacheWithWildcards<string, string>(),
                    s => Encoding.UTF8.GetBytes(s), null!, new TcpServerTransport(0)));

        [Fact]
        public void Server_NullTransport_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new RemoteCacheServer<string>(new CacheWithWildcards<string, string>(),
                    s => Encoding.UTF8.GetBytes(s), b => Encoding.UTF8.GetString(b), null!));

        [Fact]
        public void TcpClient_NullHost_Throws()
            => Assert.Throws<ArgumentNullException>(() => new TcpClientTransport(null!, 5000));

        [Fact]
        public void TcpClient_InvalidPort_Throws()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new TcpClientTransport("host", 0));
    }

    // ── Test DTOs ────────────────────────────────────────────────────────────

    public record SensorReading
    {
        public string Name { get; init; } = string.Empty;
        public double Value { get; init; }
    }
}
