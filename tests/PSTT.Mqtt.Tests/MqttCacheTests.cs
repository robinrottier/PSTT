using PSTT.Data;
using MQTTnet.Protocol;
using System.Text;

namespace PSTT.Mqtt.Tests
{
    /// <summary>
    /// Integration tests for MqttCache using a real in-process MQTT broker (MQTTnet.Server).
    ///
    /// Test groups:
    ///   1. Standalone — MqttCache used directly (no local cache layer).
    ///   2. Chain     — MqttCache wired as upstream for a local CacheWithPatterns.
    ///   3. Publish   — publishing from a downstream client propagates up to the broker.
    ///   4. Lifecycle — disconnect marks topics Stale.
    ///
    /// All tests follow the pattern:
    ///   • Start an in-process FakeBroker on a random port.
    ///   • Create and connect a MqttCache (UTF-8 string serialisation).
    ///   • Subscribe / publish / assert.
    ///   • await using disposes everything cleanly.
    ///
    /// Timing: MQTT is async and network-based even on loopback.
    /// Tests use WaitForCondition with a 3 s timeout instead of fixed delays.
    /// </summary>
    public class MqttCacheTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        private static MqttCache<string> CreateMqttDs(int brokerPort)
            => new MqttCache<string>(
                "127.0.0.1",
                brokerPort,
                bytes => Encoding.UTF8.GetString(bytes),
                value => Encoding.UTF8.GetBytes(value));

        /// <summary>Poll condition every 20 ms for up to timeoutMs before failing.</summary>
        private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000)
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
        // Group 1: Standalone MqttCache
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Basic smoke test: broker publishes a single message and the MqttCache
        /// subscriber receives it.
        /// </summary>
        [Fact]
        public async Task Standalone_SingleTopic_BrokerPublish_ReceivedBySubscriber()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            string? received = null;
            var sub = ds.Subscribe("sensors/room1/temp", async s => { received = s.Value; });

            // Allow broker subscription to propagate
            await Task.Delay(150);

            await broker.PublishAsync("sensors/room1/temp", "21.5");

            Assert.True(await WaitForConditionAsync(() => received != null),
                "Subscriber did not receive the broker message");
            Assert.Equal("21.5", received);

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Publishing via MqttCache sends the value to the broker.
        /// Verified by a watcher client connected directly to the broker.
        /// </summary>
        [Fact]
        public async Task Standalone_SingleTopic_MqttDsPublish_ReachesBroker()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            // Watch the topic via the broker's internal watcher client
            await broker.WatchTopicAsync("sensors/room1/temp");
            await Task.Delay(150); // let watcher subscription settle

            await ds.PublishAsync("sensors/room1/temp", "22.0");

            Assert.True(
                await WaitForConditionAsync(() => broker.ReceivedMessages.Any(m =>
                    m.Topic == "sensors/room1/temp" && m.Payload == "22.0")),
                "Broker watcher did not receive the published message");
        }

        /// <summary>
        /// Single-level wildcard '+': subscribing to "sensors/+/temp" receives messages
        /// for "sensors/room1/temp" and "sensors/room2/temp".
        /// </summary>
        [Fact]
        public async Task Standalone_WildcardPlus_BrokerPublishesMatchingTopics_AllReceived()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            var received = new List<(string Key, string Value)>();
            var sub = ds.Subscribe("sensors/+/temp", async s => { lock (received) received.Add((s.Key, s.Value)); });

            await Task.Delay(150);

            await broker.PublishAsync("sensors/room1/temp", "20.0");
            await broker.PublishAsync("sensors/room2/temp", "25.0");

            Assert.True(
                await WaitForConditionAsync(() => { lock (received) return received.Count >= 2; }),
                $"Expected 2 wildcard messages, got {received.Count}");

            lock (received)
            {
                Assert.Contains(received, r => r.Key == "sensors/room1/temp" && r.Value == "20.0");
                Assert.Contains(received, r => r.Key == "sensors/room2/temp" && r.Value == "25.0");
            }

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Multi-level wildcard '#': subscribing to "sensors/#" receives all messages
        /// published under "sensors/".
        /// </summary>
        [Fact]
        public async Task Standalone_WildcardHash_BrokerPublishesNestedTopics_AllReceived()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            var received = new List<(string Key, string Value)>();
            var sub = ds.Subscribe("sensors/#", async s => { lock (received) received.Add((s.Key, s.Value)); });

            await Task.Delay(150);

            await broker.PublishAsync("sensors/room1/temp", "20.0");
            await broker.PublishAsync("sensors/room2/humidity", "55");
            await broker.PublishAsync("sensors/outdoor/temp", "15.0");

            Assert.True(
                await WaitForConditionAsync(() => { lock (received) return received.Count >= 3; }),
                $"Expected 3 '#' wildcard messages, got {received.Count}");

            lock (received)
            {
                Assert.Contains(received, r => r.Key == "sensors/room1/temp");
                Assert.Contains(received, r => r.Key == "sensors/room2/humidity");
                Assert.Contains(received, r => r.Key == "sensors/outdoor/temp");
            }

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Overlapping wildcard + exact subscriptions:
        /// Both "sensors/+" and "sensors/room1" are subscribed.
        /// When "sensors/room1" is published, BOTH subscriber callbacks must fire.
        /// </summary>
        [Fact]
        public async Task Standalone_OverlappingWildcardAndExact_BothCallbacksFire()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            int wildcardCount = 0;
            int exactCount = 0;

            var subWild = ds.Subscribe("sensors/+", async s => { Interlocked.Increment(ref wildcardCount); });
            var subExact = ds.Subscribe("sensors/room1", async s => { Interlocked.Increment(ref exactCount); });

            await Task.Delay(150);

            await broker.PublishAsync("sensors/room1", "hot");

            Assert.True(await WaitForConditionAsync(() => exactCount >= 1),
                "Exact subscriber did not fire");
            Assert.True(await WaitForConditionAsync(() => wildcardCount >= 1),
                "Wildcard subscriber did not fire");

            ds.Unsubscribe(subWild);
            ds.Unsubscribe(subExact);
        }

        /// <summary>
        /// Multiple overlapping wildcard subscriptions: '+' and '#' both covering the same
        /// published topic. Both callbacks must receive the message.
        /// </summary>
        [Fact]
        public async Task Standalone_MultipleWildcards_OverlappingCoverage_AllCallbacksFire()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            int plusCount = 0;
            int hashCount = 0;
            int exactCount = 0;

            var subPlus = ds.Subscribe("data/+/value", async s => { Interlocked.Increment(ref plusCount); });
            var subHash = ds.Subscribe("data/#", async s => { Interlocked.Increment(ref hashCount); });
            var subExact = ds.Subscribe("data/sensor1/value", async s => { Interlocked.Increment(ref exactCount); });

            await Task.Delay(150);

            await broker.PublishAsync("data/sensor1/value", "42");

            Assert.True(await WaitForConditionAsync(() => exactCount >= 1), "Exact callback did not fire");
            Assert.True(await WaitForConditionAsync(() => plusCount >= 1), "'+' wildcard callback did not fire");
            Assert.True(await WaitForConditionAsync(() => hashCount >= 1), "'#' wildcard callback did not fire");

            ds.Unsubscribe(subPlus);
            ds.Unsubscribe(subHash);
            ds.Unsubscribe(subExact);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 2: MqttCache as upstream (chain)
        // ────────────────────────────────────────────────────────────────────

        private static Cache<string, string> BuildLocalWithMqttUpstream(
            MqttCache<string> mqttDs,
            bool supportsWildcards = false,
            bool forwardPublish = false)
            => new CacheBuilder<string, string>()
                .WithWildcards()
                .WithUpstream(mqttDs, supportsWildcards: supportsWildcards, forwardPublish: forwardPublish)
                .Build();

        /// <summary>
        /// Exact key via chain: broker publishes → mqttDs upstream fires →
        /// local DataSource subscriber receives the value.
        /// </summary>
        [Fact]
        public async Task Chain_ExactKey_BrokerPublish_FlowsToLocalSubscriber()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            var localDs = BuildLocalWithMqttUpstream(mqttDs);

            string? received = null;
            var sub = localDs.Subscribe("sensors/temp", async s => { received = s.Value; });

            // Allow MQTT broker subscription to propagate (fire-and-forget in NewItem)
            await Task.Delay(200);

            await broker.PublishAsync("sensors/temp", "19.0");

            Assert.True(await WaitForConditionAsync(() => received != null),
                "Local subscriber did not receive the broker value");
            Assert.Equal("19.0", received);

            localDs.Unsubscribe(sub);
        }

        /// <summary>
        /// Multiple exact-key subscriptions via chain: each local subscriber sees only
        /// updates for its own topic.
        /// </summary>
        [Fact]
        public async Task Chain_MultipleExactKeys_EachSubscriberReceivesOwnTopic()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            var localDs = BuildLocalWithMqttUpstream(mqttDs);

            string? tempVal = null, humVal = null;
            var subTemp = localDs.Subscribe("env/temp", async s => { tempVal = s.Value; });
            var subHum = localDs.Subscribe("env/humidity", async s => { humVal = s.Value; });

            await Task.Delay(200);

            await broker.PublishAsync("env/temp", "23.0");
            await broker.PublishAsync("env/humidity", "60");

            Assert.True(await WaitForConditionAsync(() => tempVal != null && humVal != null),
                $"Not all local subscribers received updates (temp={tempVal}, humidity={humVal})");
            Assert.Equal("23.0", tempVal);
            Assert.Equal("60", humVal);

            localDs.Unsubscribe(subTemp);
            localDs.Unsubscribe(subHum);
        }

        /// <summary>
        /// Wildcard via chain (upstream supports wildcards):
        /// local DS subscribes "sensors/+" → forwarded to mqttDs →
        /// broker publishes to "sensors/room1" and "sensors/room2" →
        /// local wildcard subscriber receives both.
        /// </summary>
        [Fact]
        public async Task Chain_WildcardUpstream_BrokerPublish_FlowsToLocalWildcardSubscriber()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: true);

            var received = new List<(string Key, string Value)>();
            var sub = localDs.Subscribe("sensors/+", async s => { lock (received) received.Add((s.Key, s.Value)); });

            await Task.Delay(200);

            await broker.PublishAsync("sensors/room1", "warm");
            await broker.PublishAsync("sensors/room2", "cool");

            Assert.True(
                await WaitForConditionAsync(() => { lock (received) return received.Count >= 2; }),
                $"Expected 2 chained wildcard messages, got {received.Count}");

            lock (received)
            {
                Assert.Contains(received, r => r.Key == "sensors/room1" && r.Value == "warm");
                Assert.Contains(received, r => r.Key == "sensors/room2" && r.Value == "cool");
            }

            localDs.Unsubscribe(sub);
        }

        /// <summary>
        /// Mixed chain: wildcard subscription stays local (supportsWildcards: false) while
        /// exact-key subscriptions still forward to mqttDs / broker.
        /// </summary>
        [Fact]
        public async Task Chain_WildcardLocalOnly_ExactKeyStillForwardsUpstream()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            // supportsWildcards: false — wildcards stay local
            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: false);

            string? exactReceived = null;
            string? wildcardReceived = null;

            var subExact = localDs.Subscribe("data/sensor1", async s => { exactReceived = s.Value; });
            var subWild = localDs.Subscribe("data/+", async s => { wildcardReceived = s.Value; });

            await Task.Delay(200);

            // Broker publishes to the exact topic — should reach the exact subscriber via upstream
            await broker.PublishAsync("data/sensor1", "100");

            Assert.True(await WaitForConditionAsync(() => exactReceived != null),
                "Exact subscriber did not receive broker update");
            Assert.Equal("100", exactReceived);

            // The wildcard subscriber also fires because an exact-key publish propagates to wildcards
            Assert.True(await WaitForConditionAsync(() => wildcardReceived != null),
                "Wildcard subscriber did not fire from the exact key publish");

            localDs.Unsubscribe(subExact);
            localDs.Unsubscribe(subWild);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 3: Publish downstream → up chain → broker
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ForwardPublishToUpstream: publishing on the local DataSource with
        /// forwardPublish: true also sends the value to the MqttCache which
        /// forwards it to the broker.
        /// </summary>
        [Fact]
        public async Task Chain_ForwardPublish_LocalPublish_ReachesBroker()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            // forwardPublish: true — local publishes propagate upstream to the broker
            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: true, forwardPublish: true);

            // Watch at broker level to verify the message arrives
            await broker.WatchTopicAsync("cmd/actuator1");
            await Task.Delay(100);

            // Publish locally — should be forwarded to mqttDs → broker
            await localDs.PublishAsync("cmd/actuator1", "ON");

            Assert.True(
                await WaitForConditionAsync(() => broker.ReceivedMessages.Any(m =>
                    m.Topic == "cmd/actuator1" && m.Payload == "ON")),
                "Broker did not receive the locally published message");
        }

        /// <summary>
        /// ForwardPublishToUpstream with wildcards: the local subscriber (wildcard)
        /// receives values published locally AND the broker receives them too.
        /// </summary>
        [Fact]
        public async Task Chain_ForwardPublish_LocalWildcardSubscriber_AndBrokerBothReceive()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: true, forwardPublish: true);

            // Watch broker
            await broker.WatchTopicAsync("actuators/#");
            await Task.Delay(100);

            // Local wildcard subscriber
            var received = new List<(string Key, string Value)>();
            var sub = localDs.Subscribe("actuators/+", async s => { lock (received) received.Add((s.Key, s.Value)); });
            await Task.Delay(150);

            await localDs.PublishAsync("actuators/light1", "on");
            await localDs.PublishAsync("actuators/fan1", "off");

            // Local subscriber should see both (from local update)
            Assert.True(
                await WaitForConditionAsync(() => { lock (received) return received.Count >= 2; }),
                $"Local wildcard subscriber got {received.Count} messages, expected ≥ 2");

            // Broker should have received both via forwardPublish
            Assert.True(
                await WaitForConditionAsync(() => broker.ReceivedMessages.Count(m =>
                    m.Topic == "actuators/light1" || m.Topic == "actuators/fan1") >= 2),
                "Broker did not receive both forwarded publishes");

            localDs.Unsubscribe(sub);
        }

        /// <summary>
        /// ForwardPublishToUpstream disabled (default): publishing on the local DataSource
        /// does NOT reach the broker.
        /// </summary>
        [Fact]
        public async Task Chain_NoForwardPublish_LocalPublish_DoesNotReachBroker()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var mqttDs = CreateMqttDs(broker.Port);
            await mqttDs.ConnectAsync();

            // forwardPublish: false (default) — local publishes stay local
            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: false, forwardPublish: false);

            await broker.WatchTopicAsync("local/only");
            await Task.Delay(150);

            string? localReceived = null;
            var sub = localDs.Subscribe("local/only", async s => { localReceived = s.Value; });
            await Task.Delay(150);

            // Publish locally without forward
            await localDs.PublishAsync("local/only", "secret");

            // Local subscriber should receive it
            Assert.True(await WaitForConditionAsync(() => localReceived != null),
                "Local subscriber did not receive local publish");
            Assert.Equal("secret", localReceived);

            // Wait a moment then verify broker did NOT receive it
            await Task.Delay(300);
            var brokerMessages = broker.ReceivedMessages;
            Assert.DoesNotContain(brokerMessages, m => m.Topic == "local/only");

            localDs.Unsubscribe(sub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 4: Lifecycle
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Disconnecting from the broker marks all subscribed topics as Stale.
        /// </summary>
        [Fact]
        public async Task Lifecycle_Disconnect_SubscribedTopicsMarkedStale()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            IStatus? lastStatus = null;
            var sub = ds.Subscribe("device/status", async s => { lastStatus = s.Status; });

            // Wait for subscription to establish, then publish a value to move out of Pending
            await Task.Delay(200);
            await broker.PublishAsync("device/status", "online");
            Assert.True(await WaitForConditionAsync(() => lastStatus?.IsActive == true),
                "Topic should be Active before disconnect");

            // Disconnect — OnDisconnectedAsync publishes Stale status for all tracked topics
            await ds.DisconnectAsync();

            Assert.True(await WaitForConditionAsync(() => lastStatus?.State == IStatus.StateValue.Stale),
                $"Topic should be Stale after disconnect, got {lastStatus?.State}");

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// After reconnecting, new broker messages are delivered to existing subscribers.
        /// </summary>
        [Fact]
        public async Task Lifecycle_Reconnect_SubscriptionsRestored()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);
            await ds.ConnectAsync();

            string? received = null;
            var sub = ds.Subscribe("device/value", async s => { received = s.Value; });
            await Task.Delay(200);

            // Disconnect
            await ds.DisconnectAsync();
            await Task.Delay(100);

            // Reconnect — ResubscribeAllAsync re-registers broker subscriptions
            await ds.ConnectAsync();
            await Task.Delay(200);

            await broker.PublishAsync("device/value", "restored");

            Assert.True(await WaitForConditionAsync(() => received == "restored"),
                "Subscriber did not receive message after reconnect");

            ds.Unsubscribe(sub);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 5: MqttCacheBuilder
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builder with UTF-8 encoding creates a working MqttCache.
        /// </summary>
        [Fact]
        public async Task Builder_WithUtf8Encoding_CreatesWorkingDataSource()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1", broker.Port)
                .WithUtf8Encoding()
                .Build();

            await ds.ConnectAsync();

            string? received = null;
            var sub = ds.Subscribe("builder/topic", async s => { received = s.Value; });
            await Task.Delay(150);

            await broker.PublishAsync("builder/topic", "hello-builder");

            Assert.True(await WaitForConditionAsync(() => received == "hello-builder"),
                "Builder-created source did not receive message");

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Builder with JSON encoding round-trips a value object correctly.
        /// </summary>
        [Fact]
        public async Task Builder_WithJsonEncoding_RoundTripsValue()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds = new MqttCacheBuilder<SensorReading>()
                .WithBroker("127.0.0.1", broker.Port)
                .WithJsonEncoding()
                .Build();

            await ds.ConnectAsync();

            SensorReading? received = null;
            var sub = ds.Subscribe("sensors/temp", async s => { received = s.Value; });
            await Task.Delay(150);

            var expected = new SensorReading { Name = "temp", Value = 42.5 };
            await broker.PublishAsync("sensors/temp",
                System.Text.Json.JsonSerializer.Serialize(expected));

            Assert.True(await WaitForConditionAsync(() => received?.Name == "temp"),
                "JSON-encoded value was not received");
            Assert.Equal(42.5, received!.Value);

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Builder with custom serializer can publish a value that the broker receives as expected bytes.
        /// </summary>
        [Fact]
        public async Task Builder_WithEncoding_CustomSerializer_PublishesCorrectly()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var ds = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1", broker.Port)
                .WithUtf8Encoding()
                .Build();

            await ds.ConnectAsync();

            await broker.WatchTopicAsync("cmd/value");
            await Task.Delay(150);

            await ds.PublishAsync("cmd/value", "on");

            Assert.True(await WaitForConditionAsync(() =>
                broker.ReceivedMessages.Any(m => m.Topic == "cmd/value" && m.Payload == "on")),
                "Published value did not arrive at broker");
        }

        /// <summary>
        /// Builder wired as upstream for a local CacheWithPatterns works end-to-end.
        /// </summary>
        [Fact]
        public async Task Builder_UsedAsUpstream_DeliversBrokerMessagesToLocalSubscriber()
        {
            await using var broker = await FakeBroker.StartAsync();

            await using var mqttDs = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1", broker.Port)
                .WithUtf8Encoding()
                .Build();

            await mqttDs.ConnectAsync();

            var localDs = BuildLocalWithMqttUpstream(mqttDs, supportsWildcards: true, forwardPublish: false);

            string? received = null;
            var sub = localDs.Subscribe("sensors/+", async s => { received = s.Value; });
            await Task.Delay(200);

            await broker.PublishAsync("sensors/pressure", "99.1");

            Assert.True(await WaitForConditionAsync(() => received == "99.1"),
                "Builder-wired upstream did not deliver message to local subscriber");

            localDs.Unsubscribe(sub);
        }

        /// <summary>Builder throws when broker has not been configured.</summary>
        [Fact]
        public void Builder_Build_WithoutBroker_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new MqttCacheBuilder<string>()
                    .WithUtf8Encoding()
                    .Build());
        }

        /// <summary>Builder throws when encoding has not been configured.</summary>
        [Fact]
        public void Builder_Build_WithoutEncoding_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new MqttCacheBuilder<string>()
                    .WithBroker("127.0.0.1")
                    .Build());
        }

        /// <summary>Builder WithClientId, WithCredentials, and WithQualityOfService can all be set without error.</summary>
        [Fact]
        public async Task Builder_SetAllOptions_DoesNotThrow()
        {
            var builder = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1")
                .WithClientId("test-client")
                .WithCredentials("user", "pass")
                .WithQualityOfService(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .WithMaxTopics(100)
                .WithMaxSubscriptionsPerTopic(5)
                .WithMaxSubscriptionsTotal(50)
                .WithMaxCallbackConcurrency(2)
                .WithSynchronousCallbacks()
                .WithCallbackErrorHandler((ex, msg) => { })
                .WithUtf8Encoding();

            // Build must succeed without throwing
            await using var ds = builder.Build();
            Assert.NotNull(ds);
        }

        /// <summary>Builder WithThreadPoolCallbacks (both overloads) succeeds.</summary>
        [Fact]
        public async Task Builder_WithThreadPoolCallbacks_Succeeds()
        {
            await using var ds1 = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1").WithUtf8Encoding()
                .WithThreadPoolCallbacks(waitForCompletion: false)
                .Build();

            await using var ds2 = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1").WithUtf8Encoding()
                .WithThreadPoolCallbacks(waitForCompletion: true)
                .Build();

            Assert.NotNull(ds1);
            Assert.NotNull(ds2);
        }

        /// <summary>Builder null host throws ArgumentNullException.</summary>
        [Fact]
        public void Builder_WithBroker_NullHost_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MqttCacheBuilder<string>().WithBroker(null!));
        }

        /// <summary>Builder null deserializer throws ArgumentNullException.</summary>
        [Fact]
        public void Builder_WithEncoding_NullDeserializer_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MqttCacheBuilder<string>().WithEncoding(null!));
        }

        /// <summary>Builder WithDispatcher(null) throws ArgumentNullException.</summary>
        [Fact]
        public void Builder_WithDispatcher_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MqttCacheBuilder<string>().WithDispatcher(null!));
        }

        /// <summary>Builder WithDispatcher with a valid dispatcher succeeds and builds.</summary>
        [Fact]
        public async Task Builder_WithDispatcher_Custom_Succeeds()
        {
            await using var ds = new MqttCacheBuilder<string>()
                .WithBroker("127.0.0.1")
                .WithUtf8Encoding()
                .WithDispatcher(new SynchronousDispatcher())
                .Build();
            Assert.NotNull(ds);
        }

        // ────────────────────────────────────────────────────────────────────
        // Group 6: Edge-case behaviour
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Subscribing before ConnectAsync still delivers messages received after connect.
        /// (Exercises the "subscribe before connected → queue for ResubscribeAllAsync" path.)
        /// </summary>
        [Fact]
        public async Task Standalone_SubscribeBeforeConnect_DeliversMessageAfterConnect()
        {
            await using var broker = await FakeBroker.StartAsync();
            await using var ds = CreateMqttDs(broker.Port);

            string? received = null;
            var sub = ds.Subscribe("early/topic", async s => { received = s.Value; });

            // Connect AFTER subscribing — ConnectAsync calls ResubscribeAllAsync internally
            await ds.ConnectAsync();
            await Task.Delay(200);

            await broker.PublishAsync("early/topic", "late-value");

            Assert.True(await WaitForConditionAsync(() => received == "late-value"),
                "Subscriber registered before ConnectAsync should still receive messages");

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// When the deserializer throws, the malformed broker message is silently discarded
        /// and no subscriber callback is invoked.
        /// </summary>
        [Fact]
        public async Task Standalone_MalformedPayload_IsIgnored()
        {
            await using var broker = await FakeBroker.StartAsync();

            int callCount = 0;
            await using var ds = new MqttCache<string>(
                "127.0.0.1", broker.Port,
                _ => throw new InvalidOperationException("bad payload"));

            await ds.ConnectAsync();

            var sub = ds.Subscribe("broken/topic", async s => { callCount++; });
            await Task.Delay(200);

            await broker.PublishAsync("broken/topic", "garbage");

            await Task.Delay(300);
            Assert.Equal(0, callCount);

            ds.Unsubscribe(sub);
        }

        /// <summary>
        /// Publishing on a MqttCache that has no serializer is a silent no-op:
        /// no exception is thrown and nothing reaches the broker.
        /// </summary>
        [Fact]
        public async Task Standalone_PublishAsync_WithoutSerializer_DoesNotThrow()
        {
            await using var broker = await FakeBroker.StartAsync();

            // No serializer supplied
            await using var ds = new MqttCache<string>(
                "127.0.0.1", broker.Port,
                bytes => Encoding.UTF8.GetString(bytes),
                serializer: null);

            await ds.ConnectAsync();
            await broker.WatchTopicAsync("out/topic");
            await Task.Delay(100);

            // Should not throw
            await ds.PublishAsync("out/topic", "hello");

            await Task.Delay(200);
            Assert.DoesNotContain(broker.ReceivedMessages, m => m.Topic == "out/topic");
        }
    }

    // ── Test DTOs ────────────────────────────────────────────────────────────

    public record SensorReading
    {
        public string Name { get; init; } = string.Empty;
        public double Value { get; init; }
    }
}
